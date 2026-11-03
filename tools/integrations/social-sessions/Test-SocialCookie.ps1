param([string] $Cookie)
# Reports which expected cookies are present by NAME ONLY. Never prints values.
$names = $Cookie.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { ($_ -split '=', 2)[0].Trim() } | Where-Object { $_ }
$wanted = @{
    bilibili    = @('SESSDATA', 'DedeUserID')
    xiaohongshu = @('web_session', 'a1')
    xiaoheihe   = @('heybox_id', 'pkey')
}
foreach ($platform in $wanted.Keys) {
    $hits = $wanted[$platform] | Where-Object { $names -contains $_ }
    Write-Host ("{0,-12} 命中 {1}/{2}: {3}" -f $platform, $hits.Count, $wanted[$platform].Count, ($hits -join ', '))
}
Write-Host ""
Write-Host ("共 {0} 个 cookie 名: {1}" -f $names.Count, ($names -join ', '))
