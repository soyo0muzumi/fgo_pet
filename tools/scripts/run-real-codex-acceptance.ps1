[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CodexExe,
    [Parameter(Mandatory = $true)][string]$AcceptanceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$fullDirectory = [IO.Path]::GetFullPath($AcceptanceDirectory)
[IO.Directory]::CreateDirectory($fullDirectory) | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $CodexExe
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('app-server')
$start.ArgumentList.Add('--stdio')
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
if (-not $process.Start()) { throw 'Codex app-server did not start.' }
$input = $process.StandardInput
$input.WriteLine('{"id":1,"method":"initialize","params":{"clientInfo":{"name":"fgo-pet-acceptance","title":"FGO Pet acceptance","version":"1.0.0"}}}')
$input.WriteLine('{"method":"initialized","params":{}}')
$input.WriteLine((ConvertTo-Json -Compress @{ id = 2; method = 'thread/start'; params = @{ cwd = $fullDirectory; approvalPolicy = 'never'; sandbox = 'read-only' } }))
$input.Flush()
$lines = [Collections.Generic.List[string]]::new()
$deadline = [DateTime]::UtcNow.AddSeconds(45)
$threadId = $null
$turnStarted = $false
$completed = $false
while ([DateTime]::UtcNow -lt $deadline -and -not $completed) {
    if ($process.StandardOutput.Peek() -lt 0) { Start-Sleep -Milliseconds 50; continue }
    $line = $process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $lines.Add($line)
    try { $message = $line | ConvertFrom-Json } catch { continue }
    if ($message.id -eq 2 -and $null -ne $message.result.thread.id) {
        $threadId = [string]$message.result.thread.id
        $input.WriteLine((ConvertTo-Json -Compress @{ id = 3; method = 'turn/start'; params = @{ threadId = $threadId; input = @(@{ type = 'text'; text = 'Return exactly this text and nothing else: FGO_PET_REAL_CODEX_ACCEPTANCE_OK' }); cwd = $fullDirectory; approvalPolicy = 'never'; sandboxPolicy = @{ type = 'readOnly'; networkAccess = $false } } }))
        $input.Flush()
        continue
    }
    if ($message.method -eq 'turn/started') { $turnStarted = $true }
    if ($message.method -eq 'turn/completed') { $completed = $true }
}
$input.Close()
if (-not $completed) { try { $process.Kill($true) } catch {} ; throw 'Codex real acceptance timed out before turn/completed.' }
$process.WaitForExit(5000)
[pscustomobject]@{ ThreadId = $threadId; TurnStarted = $turnStarted; TurnCompleted = $completed; Output = @($lines) } | ConvertTo-Json -Depth 12
$process.Dispose()
