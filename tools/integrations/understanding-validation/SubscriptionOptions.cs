using System.IO;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Understanding.Contracts;

namespace FgoPet.Testing.Understanding;

internal sealed record SubscriptionOptions(ModelConnectionSettings Connection,ReviewRequestLimits Limits,
    SubscriptionCallLedger Ledger)
{
    internal static string Stage(string report)=>Path.Combine(report,"subscription-scenarios-20261021");
    internal static ModelConnectionSettings ReadConnection(string stage)
    {
        using var approval=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"authority.json")));
        var root=approval.RootElement;
        if(!root.GetProperty("authorized").GetBoolean()||
            root.GetProperty("routeAnswer").GetString()!="本机 Buddy / DSH 的 DeepSeek 通道")
            throw new InvalidOperationException("SUBSCRIPTION_AUTHORITY_REQUIRED");
        using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(stage,"model-capacity.json")));
        var model=metadata.RootElement;
        var source=model.GetProperty("adapterSourcePath").GetString()!;
        if(ValidationGate.FileHash(source)!=model.GetProperty("adapterSourceSha256").GetString()||
            model.GetProperty("model").GetString()!="buddy/deepseek-v4.1-flash"||
            model.GetProperty("contextWindowTokens").GetInt32()!=1_000_000||
            model.GetProperty("maxOutputTokens").GetInt32()!=128_000)
            throw new InvalidOperationException("SUBSCRIPTION_MODEL_CAPACITY_CHANGED");
        return new("custom-openai-compatible","http://127.0.0.1:8326/v1","buddy/deepseek-v4.1-flash",
            contextWindowOverride:1_000_000,maxOutputTokens:128_000);
    }
    internal static ReviewRequestLimits Capacity(ModelConnectionSettings connection)
    {
        var context=new ModelContextLimit(ModelRouteKey.From(connection),connection.ContextWindowOverride!.Value,
            connection.MaxOutputTokens,ContextLimitSource.Override,"installed Buddy adapter fallback; not gateway-reported metadata");
        var budget=PromptBudget.Resolve(context,connection.MaxOutputTokens);
        return new(budget.InputTokens,budget.OutputTokens);
    }
    internal sealed class Credentials : FgoPet.Infrastructure.Secrets.ICredentialReader,FgoPet.Core.Secrets.ICredentialReader
    {
        public async Task<string?> ReadAsync(string target,CancellationToken token)
        {
            var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".dsh","openai-gateway","api-key");
            var value=(await File.ReadAllTextAsync(path,token).ConfigureAwait(false)).Trim();
            if(value.Length<16||value.Contains('\n')||value.Contains('\r'))throw new InvalidOperationException("SUBSCRIPTION_GATEWAY_CREDENTIAL_INVALID");
            return value;
        }
    }
}
