using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

// Explicit subscription experiment only. Durable accounting has no artificial call ceiling.
internal sealed class SubscriptionCallLedger : IReviewCallBudget, IDisposable
{
    private readonly FileStream _file;
    private readonly string _route;
    private readonly int _inputCapacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _trials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewCallRecord> _calls = new(StringComparer.Ordinal);
    private sealed record Event(string Stage, ReviewCallRecord Call);

    internal SubscriptionCallLedger(string path, string route, int inputCapacity)
    {
        if(route.Length!=64||!route.All(char.IsAsciiHexDigit)||inputCapacity<=0)
            throw new InvalidOperationException("SUBSCRIPTION_LEDGER_BINDING_INVALID");
        _route=route;_inputCapacity=inputCapacity;
        _file=new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
        try
        {
            using var reader=new StreamReader(_file,Encoding.UTF8,false,1024,leaveOpen:true);
            while(reader.ReadLine() is {} line)
            {
                var item=JsonSerializer.Deserialize<Event>(line)??throw new InvalidOperationException("SUBSCRIPTION_LEDGER_CORRUPT");
                if(item.Stage=="reserved")
                {
                    if(item.Call.Outcome!=ReviewCallOutcome.ReservedOrUnknown||!_calls.TryAdd(item.Call.RequestHash,item.Call))
                        throw new InvalidOperationException("SUBSCRIPTION_LEDGER_CORRUPT");
                }
                else if(item.Stage=="result"&&_calls.TryGetValue(item.Call.RequestHash,out var before)&&
                    before.Outcome==ReviewCallOutcome.ReservedOrUnknown&&item.Call.Outcome!=ReviewCallOutcome.ReservedOrUnknown&&
                    item.Call with{Outcome=before.Outcome,InputTokens=before.InputTokens,OutputTokens=before.OutputTokens}==before)
                    _calls[item.Call.RequestHash]=item.Call;
                else throw new InvalidOperationException("SUBSCRIPTION_LEDGER_CORRUPT");
            }
            _file.Seek(0,SeekOrigin.End);
        }
        catch { _file.Dispose();throw; }
    }

    internal void Register(string trial,string scope)
    {
        lock(_gate)
        {
            if(_trials.TryGetValue(trial,out var previous)&&previous!=scope)
                throw new InvalidOperationException("SUBSCRIPTION_TRIAL_SCOPE_CHANGED");
            _trials[trial]=scope;
        }
    }
    public ReviewCallReservation? TryReserve(ReviewCallPlan plan)
    {
        lock(_gate)
        {
            if(plan.RouteHash!=_route||plan.Batch!=ReviewBatch.Formal||!_trials.TryGetValue(plan.TrialId,out var scope)||
                scope!=plan.ScopeKey||plan.EstimatedInputTokens<1||plan.EstimatedInputTokens>_inputCapacity||
                plan.InputHash.Length!=64||!plan.InputHash.All(char.IsAsciiHexDigit)||
                plan.Purpose is not("reply"or"review"or"classify"or"summary")||!Enum.IsDefined(plan.Mode)||plan.PolicyGeneration<0)
                return null;
            var request=ValidationHost.Digest(plan.RequestId);if(_calls.ContainsKey(request))return null;
            var trial=ValidationHost.Digest(plan.TrialId);
            var attempt=_calls.Values.Count(c=>c.TrialHash==trial&&c.Purpose==plan.Purpose)+1;
            var call=new ReviewCallRecord(request,plan.Batch,trial,ValidationHost.Digest(scope),plan.Purpose,attempt,
                plan.Mode,plan.PolicyGeneration,plan.InputHash,plan.EstimatedInputTokens,ReviewCallOutcome.ReservedOrUnknown);
            Append(new("reserved",call));_calls.Add(request,call);
            return new(request,attempt,_calls.Count,_calls.Count,_calls.Values.Count(c=>c.TrialHash==trial));
        }
    }
    public void Complete(string requestHash,ReviewCallOutcome outcome,int? inputTokens=null,int? outputTokens=null)
    {
        lock(_gate)
        {
            if(!Enum.IsDefined(outcome)||outcome==ReviewCallOutcome.ReservedOrUnknown||inputTokens<0||outputTokens<0||
                (inputTokens is null)!=(outputTokens is null))throw new InvalidOperationException("SUBSCRIPTION_LEDGER_RESULT_INVALID");
            if(!_calls.TryGetValue(requestHash,out var before)||before.Outcome!=ReviewCallOutcome.ReservedOrUnknown)return;
            var after=before with{Outcome=outcome,InputTokens=inputTokens,OutputTokens=outputTokens};
            Append(new("result",after));_calls[requestHash]=after;
        }
    }
    internal ReviewBudgetSnapshot Snapshot()
    {
        lock(_gate)return new(_calls.Count,Enum.GetValues<ReviewBatch>().ToDictionary(b=>b,b=>_calls.Values.Count(c=>c.Batch==b)),_calls.Values.ToArray());
    }
    private void Append(Event item)
    {
        var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item)+"\n");
        _file.Write(bytes);_file.Flush(flushToDisk:true);
    }
    public void Dispose()=>_file.Dispose();
}
