using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.Core.Settings;

namespace FgoPet.Testing.Understanding;

internal static class ValidationGate
{
    internal static string FileHash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    internal static string RouteHash(ModelConnectionSettings connection)=>ValidationHost.Digest(JsonSerializer.Serialize(connection));
    internal static string RequireEngineering(string report,string repository,ModelConnectionSettings connection,bool repair=false)
    {
        using var authority=JsonDocument.Parse(File.ReadAllText(Path.Combine(report,"authority.json")));
        var a=authority.RootElement;
        if(a.GetProperty("totalLimit").GetInt32()!=252||a.GetProperty("hardGateWaiver").GetBoolean()||a.GetProperty("reviewReply").GetString()!="确认")
            throw new InvalidOperationException("VALIDATION_AUTHORITY_INVALID");
        var path=Path.Combine(report,repair?"engineering-gates-repair.json":"engineering-gates.json");
        using var document=JsonDocument.Parse(File.ReadAllText(path));var gates=document.RootElement;
        if(!gates.GetProperty("passed").GetBoolean())throw new InvalidOperationException("VALIDATION_ENGINEERING_GATE_OPEN");
        if(gates.GetProperty("routeSha256").GetString()!=RouteHash(connection))throw new InvalidOperationException("VALIDATION_PARAMETERS_CHANGED");
        VerifyFiles(repository,gates.GetProperty("sourceHashes"));
        VerifyFiles(repository,gates.GetProperty("binaryHashes"));
        VerifyFiles(repository,gates.GetProperty("evidenceHashes"));
        var sourcePaths=SourcePaths(repository);
        if(!sourcePaths.SequenceEqual(gates.GetProperty("sourceHashes").EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("VALIDATION_SOURCE_MANIFEST_CHANGED");
        var baseDirectory=Path.GetFullPath(AppContext.BaseDirectory);
        var expected=gates.GetProperty("binaryHashes").EnumerateObject().Select(p=>Path.GetFullPath(Path.Combine(repository,p.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual=Directory.GetFiles(baseDirectory,"*",SearchOption.AllDirectories);
        if(actual.Any(p=>!expected.Contains(p))||!expected.Contains(Path.GetFullPath(typeof(Program).Assembly.Location)))
            throw new InvalidOperationException("VALIDATION_UNVERIFIED_DRIVER_BINARY");
        using var recovery=JsonDocument.Parse(File.ReadAllText(Path.Combine(report,"default-store-recovery-verification.json")));
        if(!recovery.RootElement.GetProperty("verified").GetBoolean())throw new InvalidOperationException("VALIDATION_ISOLATION_INCIDENT_OPEN");
        var defaultRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FgoPet");
        if(!Same(FileHash(Path.Combine(defaultRoot,"settings.json")),recovery.RootElement.GetProperty("settingsSha256").GetString()!))
            throw new InvalidOperationException("VALIDATION_PRODUCTION_SETTINGS_CHANGED");
        return FileHash(path);
    }
    internal static string Freeze(string report,string repository,ModelConnectionSettings connection,bool repair,string? reason=null)
    {
        var gatesHash=RequireEngineering(report,repository,connection,repair);
        var live=Path.Combine(report,"live");
        if(!Directory.Exists(live))throw new InvalidOperationException("VALIDATION_SMOKE_REQUIRED");
        var path=Path.Combine(live,repair?"freeze-repair.json":"freeze-formal.json");
        if(File.Exists(path))return RequireFrozen(report,repository,connection,repair);
        if(repair)
        {
            if(reason is null||reason.Length is <1 or >80||!reason.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))
                throw new InvalidOperationException("VALIDATION_REPAIR_REASON_REQUIRED");
            var formal=ValidationRun.ReadManifest(Path.Combine(live,"formal","manifest.json"));
            if(formal.Trials.Count!=21||formal.Trials.Any(t=>t.Status is not("COMPLETED"or"PROTOCOL_FAILED"or"UNKNOWN_STOP")))
                throw new InvalidOperationException("VALIDATION_FORMAL_BATCH_INCOMPLETE");
        }
        else
        {
            var smoke=ValidationRun.ReadManifest(Path.Combine(live,"smoke","manifest.json"));
            if(smoke.Trials.Count!=3||smoke.Trials.Any(t=>t.Status!="COMPLETED"||t.Implementation!=gatesHash))
                throw new InvalidOperationException("VALIDATION_CURRENT_SMOKE_REQUIRED");
        }
        ValidationHost.Write(path,new{schema=1,atUtc=DateTimeOffset.UtcNow,batch=repair?"repair":"formal",reason,
            engineeringGatesSha256=gatesHash,preregisteredSha256=FileHash(Path.Combine(report,"preregistered-trials.json")),
            routeSha256=RouteHash(connection),protocolRevision=false,serverRevision="Cannot be pinned by this client",humanSemanticApproval=false});
        return FileHash(path);
    }
    internal static string RequireFrozen(string report,string repository,ModelConnectionSettings connection,bool repair)
    {
        var gatesHash=RequireEngineering(report,repository,connection,repair);
        var path=Path.Combine(report,"live",repair?"freeze-repair.json":"freeze-formal.json");
        using var document=JsonDocument.Parse(File.ReadAllText(path));var freeze=document.RootElement;
        if(freeze.GetProperty("engineeringGatesSha256").GetString()!=gatesHash||
            freeze.GetProperty("preregisteredSha256").GetString()!=FileHash(Path.Combine(report,"preregistered-trials.json"))||
            freeze.GetProperty("routeSha256").GetString()!=RouteHash(connection))throw new InvalidOperationException("VALIDATION_FROZEN_VERSION_CHANGED");
        return FileHash(path);
    }
    private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
    private static void VerifyFiles(string repository,JsonElement hashes)
    {
        var prefix=Path.GetFullPath(repository)+Path.DirectorySeparatorChar;
        foreach(var file in hashes.EnumerateObject())
        {
            var path=Path.GetFullPath(Path.Combine(repository,file.Name));
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!File.Exists(path)||!Same(FileHash(path),file.Value.GetString()!))
                throw new InvalidOperationException("VALIDATION_EVIDENCE_OR_SOURCE_CHANGED");
        }
    }
    private static string[] SourcePaths(string repository)
    {
        var info=new ProcessStartInfo("git"){WorkingDirectory=repository,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var arg in new[]{"ls-files","-c","-o","--exclude-standard","-z"})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new InvalidOperationException("VALIDATION_GIT_START_FAILED");
        var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();process.WaitForExit();
        if(process.ExitCode!=0)throw new InvalidOperationException("VALIDATION_SOURCE_INVENTORY_FAILED");
        _=error;return output.Split('\0',StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
