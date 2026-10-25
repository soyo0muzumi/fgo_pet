using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FgoPet.Testing.Understanding;

// Does not call arbitrary formatters or serialize exception/parameter objects.
internal sealed class MetadataLog(string path,Action<string>? checkpoint=null):ILoggerProvider
{
    private readonly object _gate=new();
    public ILogger CreateLogger(string category)=>new Logger(this,category.Contains("Understanding",StringComparison.Ordinal));
    public void Dispose() { }
    internal void Event(string correlation,string stage,string? code=null)
    {
        lock(_gate)File.AppendAllText(path,JsonSerializer.Serialize(new{atUtc=DateTimeOffset.UtcNow,
            correlation=ValidationHost.Digest(correlation)[..16],stage=Safe(stage),code=code is null?null:Safe(code)})+Environment.NewLine);
        checkpoint?.Invoke(stage);
    }
    private static string Safe(string value)=>value.Length<=100&&value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_'or'-'or'.')?value:"UNSAFE_METADATA_OMITTED";
    private sealed class Logger(MetadataLog sink,bool enabled):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>enabled;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(!enabled||state is not IEnumerable<KeyValuePair<string,object?>> fields)return;
            var values=fields.Where(p=>p.Key is "Correlation"or"Stage"or"Code").ToDictionary(p=>p.Key,p=>p.Value?.ToString());
            if(values.GetValueOrDefault("Stage")is{} stage)sink.Event(values.GetValueOrDefault("Correlation")??"worker",stage,values.GetValueOrDefault("Code"));
        }
    }
}
