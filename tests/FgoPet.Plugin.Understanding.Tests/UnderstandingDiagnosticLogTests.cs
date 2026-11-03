using System.Text.Json;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class UnderstandingDiagnosticLogTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"fgo-understanding-diagnostics-"+Guid.NewGuid().ToString("N"));
    private string LogPath=>Path.Combine(_directory,"understanding-diagnostics.jsonl");
    private static UnderstandingDiagnostic Event(string correlation="private-scope",string? code=null)=>
        new(DateTimeOffset.UtcNow,correlation,"budget_check_completed",code);

    [Fact] public void Log_redacts_payloads_and_preserves_structured_reason_and_session_across_concurrent_writes()
    {
        var log=new UnderstandingDiagnosticLog(LogPath);
        log.Record(Event(code:"private prompt\nkey=secret"));
        Parallel.For(0,40,i=>log.Record(Event("private-scope-"+i,"UNDERSTANDING_BUDGET_UNAUTHORIZED")));
        var lines=File.ReadAllLines(LogPath);Assert.Equal(41,lines.Length);
        Assert.DoesNotContain("private",File.ReadAllText(LogPath));Assert.DoesNotContain("secret",File.ReadAllText(LogPath));
        var sessions=new HashSet<string>();
        foreach(var line in lines)
        {
            using var json=JsonDocument.Parse(line);var value=json.RootElement;
            Assert.Matches("^[A-F0-9]{16}$",value.GetProperty("Correlation").GetString()!);
            sessions.Add(value.GetProperty("Session").GetString()!);
            Assert.True(value.GetProperty("At").TryGetDateTimeOffset(out _));
        }
        Assert.Single(sessions);
        Assert.Contains("UNDERSTANDING_DIAGNOSTIC_REDACTED",lines[0]);
        new UnderstandingDiagnosticLog(LogPath).Record(Event());
        using var last=JsonDocument.Parse(File.ReadAllLines(LogPath)[^1]);Assert.DoesNotContain(last.RootElement.GetProperty("Session").GetString()!,sessions);
    }
    [Fact] public void Rotation_keeps_one_previous_file_and_write_failure_is_non_fatal()
    {
        Directory.CreateDirectory(_directory);var log=new UnderstandingDiagnosticLog(LogPath);
        for(var i=0;i<2;i++)
        {
            using(var file=File.OpenWrite(LogPath))file.SetLength(UnderstandingDiagnosticLog.MaximumBytes);
            log.Record(Event("scope-"+i));
            Assert.Equal(UnderstandingDiagnosticLog.MaximumBytes,new FileInfo(LogPath+".previous").Length);
            Assert.InRange(new FileInfo(LogPath).Length,1,UnderstandingDiagnosticLog.MaximumBytes);
        }
        Assert.Equal(2,Directory.GetFiles(_directory).Length);
        new UnderstandingDiagnosticLog(Path.Combine(LogPath,"invalid-child")).Record(Event());
        Assert.Single(File.ReadAllLines(LogPath));
    }
    [Fact] public void Repeated_states_are_sampled_but_changed_reasons_and_minute_heartbeats_are_retained()
    {
        var clock=new Clock();var log=new UnderstandingDiagnosticLog(LogPath,clock);
        log.Record(Event(code:"UNDERSTANDING_BUDGET_UNAUTHORIZED"));
        log.Record(Event(code:"UNDERSTANDING_BUDGET_UNAUTHORIZED"));Assert.Single(File.ReadAllLines(LogPath));
        log.Record(Event(code:"UNDERSTANDING_BUDGET_LIMITED"));Assert.Equal(2,File.ReadAllLines(LogPath).Length);
        clock.Ticks=TimeSpan.FromMinutes(1).Ticks;
        log.Record(Event(code:"UNDERSTANDING_BUDGET_LIMITED"));Assert.Equal(3,File.ReadAllLines(LogPath).Length);
        log.Record(Event(code:"UNDERSTANDING_WORKER_FAILED") with{Stage="budget_check_failed"});
        log.Record(Event(code:"UNDERSTANDING_BUDGET_LIMITED"));Assert.Equal(5,File.ReadAllLines(LogPath).Length);
    }
    private sealed class Clock:TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp()=>Ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
