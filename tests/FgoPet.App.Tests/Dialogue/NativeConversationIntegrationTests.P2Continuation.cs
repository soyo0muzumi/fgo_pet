using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string ContinuationVariable = "FGO_PET_P2_CONTINUATION_MANIFEST";
    private const string ContinuationAuthorization = "user-authorized-final-delivery-diagnostic-continuation-20261014";
    private sealed record P2ContinuationBudget(string Authorization, string BatchId, string FixtureHash, string ParentManifestHash,
        string ParentLedgerHash, int ParentRequests, int MaxRequests, DateTimeOffset ExpiresUtc);
    private sealed record P2ContinuationPhase(string BudgetHash, string Phase, int StartingRequests, int MaxRequests,
        string SceneHash, Dictionary<string, string> BinaryHashes);

    private static P2Scene[] ContinuationScenes(string phase) => phase switch
    {
        "diagnostic" => [SubscriptionScenes.Single(scene => scene.Id == "history-reopen") with { Second = null }],
        "verification" => SubscriptionScenes.Where(scene => scene.Id is not ("correction" or "explicit-help")).ToArray(),
        _ => throw new P2BudgetException("P2_PHASE_INVALID"),
    };
    private static string ContinuationSceneHash(string phase) => P2Hash(JsonSerializer.Serialize(ContinuationScenes(phase)));

    private sealed class P2ContinuationFactAttribute : FactAttribute
    {
        public P2ContinuationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ContinuationVariable)))
                Skip = "Explicit frozen continuation manifest required; no real provider in default tests.";
        }
    }

    [Fact]
    public void P2_continuation_fixture_contract_has_one_probe_and_sixteen_verification_sends()
    {
        var profiles = new[] { "diagnostic", "verification" }.Select(phase => new
        {
            Phase = phase, SceneHash = ContinuationSceneHash(phase), Scenes = ContinuationScenes(phase),
            ExpectedRequests = ContinuationScenes(phase).Sum(scene => SceneInputs(scene).Length +
                Enumerable.Range(0, SceneInputs(scene).Length).Sum(turn => ExpectedReads(scene, turn))),
        }).ToArray();
        Assert.Equal(new[] { 1, 16 }, profiles.Select(profile => profile.ExpectedRequests));
        var capture = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(capture)) return;
        Directory.CreateDirectory(capture);
        File.WriteAllText(Path.Combine(capture, "continuation-contract.json"), JsonSerializer.Serialize(new
        { FixtureHash = SubscriptionFixtureHash, Profiles = profiles }));
    }

    [P2ContinuationFact]
    [Trait("Category", "LiveP2Subscription")]
    public async Task P2_continuation_explicit_frozen_live_phase()
    {
        // There is exactly one child budget for this failed parent. Changing a phase filename,
        // BatchId or destination cannot create another allowance from the same three sends.
        var root = P2RepositoryRoot();
        var report = Path.Combine(root, "docs", "internal", "reports", "personal-p2-local-20261014", "delivery-diagnostics");
        var parent = Path.Combine(Path.GetDirectoryName(report)!, "final-output");
        var manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable(ContinuationVariable)!);
        using var phaseFile = new P2FrozenFile(manifestPath);
        var phase = phaseFile.Read<P2ContinuationPhase>();
        if (P2BatchPolicy.CanonicalPath(manifestPath) != P2BatchPolicy.CanonicalPath(Path.Combine(report, phase.Phase, "live-manifest.json")))
            throw new P2BudgetException("P2_PHASE_PATH");
        using var budgetFile = new P2FrozenFile(Path.Combine(report, "continuation-budget.json"));
        var budget = budgetFile.Read<P2ContinuationBudget>();
        using var parentFile = new P2FrozenFile(Path.Combine(parent, "live-manifest.json"));
        var original = parentFile.Read<P2LiveManifest>();
        if (budgetFile.Hash != phase.BudgetHash || parentFile.Hash != budget.ParentManifestHash ||
            original.Authorization != SubscriptionAuthorization || original.FixtureHash != SubscriptionFixtureHash)
            throw new P2BudgetException("P2_PREDECESSOR_CHANGED");
        var parentLedger = Path.Combine(parent, "live-ledger.jsonl");
        using var predecessor = P2BatchProvider.Audit(parentLedger,
            new(original.BatchId, P2Hash(parentFile.Text), 20, 0, original.ExpiresUtc, parentLedger) { LocalSubscription = true }, budget.ParentLedgerHash);
        ValidateContinuation(budget, phase, predecessor.Snapshot);
        var binaries = Directory.GetFiles(AppContext.BaseDirectory, "FgoPet*.dll");
        if (binaries.Length != phase.BinaryHashes.Count || binaries.Any(path =>
                !phase.BinaryHashes.TryGetValue(Path.GetFileName(path), out var expected) || HashFile(path) != expected))
            throw new P2BudgetException("P2_BINARY_CHANGED");
        var binding = ResolveSourcePersona();
        if (original.PersonaHash != FrozenPersonaHash || original.PersonaTextHash != FrozenPersonaTextHash ||
            HashFile(Path.Combine(root, "content", "packs", "official.mash", "persona", "core_persona.json")) != FrozenPersonaHash ||
            P2Hash(binding.Persona!.CoreText) != FrozenPersonaTextHash)
            throw new P2BudgetException("P2_PERSONA_CHANGED");

        var ledger = Path.Combine(report, "continuation-ledger.jsonl");
        var policy = new P2BatchPolicy(budget.BatchId, budgetFile.Hash, budget.MaxRequests, 0, budget.ExpiresUtc, ledger) { LocalSubscription = true };
        var credentials = new WindowsCredentialStore();
        using var batch = OpenContinuation(policy, phase, credentials, P2BatchProvider.CreateDirectTransport, TimeProvider.System);
        var saved = await credentials.ReadAsync("fgo-pet/provider/custom-openai-compatible", default);
        var gateway = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "openai-gateway", "api-key");
        if (string.IsNullOrWhiteSpace(saved) || !File.Exists(gateway) || saved != File.ReadAllText(gateway).Trim())
            throw new P2BudgetException("P2_CREDENTIAL_ROUTE_MISMATCH");
        saved = null;
        var complete = await RunSubscriptionScenesAsync(batch, Path.GetDirectoryName(manifestPath)!, true, binding,
            ContinuationScenes(phase.Phase), phase.StartingRequests + phase.MaxRequests, budget.ParentRequests);
        Assert.True(complete, "P2_PHASE_INCOMPLETE_SEE_SAFE_EVIDENCE");
    }

    private static void ValidateContinuation(P2ContinuationBudget budget, P2ContinuationPhase phase, P2BatchSnapshot parent)
    {
        if (budget.Authorization != ContinuationAuthorization || budget.FixtureHash != SubscriptionFixtureHash ||
            !Guid.TryParseExact(budget.BatchId, "N", out _) || budget.ParentRequests != 3 || parent.Requests != budget.ParentRequests ||
            parent.StopCode is not null || budget.MaxRequests != 20 - budget.ParentRequests ||
            budget.ExpiresUtc <= DateTimeOffset.UtcNow || budget.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(2))
            throw new P2BudgetException("P2_CONTINUATION_INVALID");
        var diagnostic = phase.Phase == "diagnostic";
        if (phase.StartingRequests != (diagnostic ? 0 : 1) || phase.MaxRequests != (diagnostic ? 1 : 16) ||
            phase.StartingRequests + phase.MaxRequests > budget.MaxRequests || phase.SceneHash != ContinuationSceneHash(phase.Phase))
            throw new P2BudgetException("P2_PHASE_INVALID");
    }

    private static P2BatchProvider OpenContinuation(P2BatchPolicy policy, P2ContinuationPhase phase, ICredentialReader credentials,
        Func<HttpMessageHandler> transport, TimeProvider clock)
    {
        string approval;
        // A diagnostic restart cannot create a new ledger; a verification restart cannot refund
        // earlier sends. Opening and checking the durable starting count precedes credentials.
        using (var prepared = phase.Phase == "diagnostic"
            ? P2BatchProvider.CreateUnapproved(policy.LedgerPath, policy, credentials, transport, clock)
            : P2BatchProvider.Open(policy.LedgerPath, policy, null, credentials, transport, clock)) approval = prepared.ApprovalFingerprint;
        var batch = P2BatchProvider.Open(policy.LedgerPath, policy, approval, credentials, transport, clock);
        if (batch.Snapshot.Requests != phase.StartingRequests || batch.Snapshot.StopCode is not null)
        {
            batch.Dispose();
            throw new P2BudgetException("P2_PHASE_STATE_CHANGED");
        }
        return batch;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private sealed class P2FrozenFile : IDisposable
    {
        private readonly FileStream file;
        public string Hash { get; }
        public string Text { get; }
        public P2FrozenFile(string path)
        {
            file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (file.Length is 0 or > 1_048_576) throw new P2BudgetException("P2_MANIFEST_INVALID");
                Hash = Convert.ToHexString(SHA256.HashData(file));
                file.Position = 0;
                using var reader = new StreamReader(file, new UTF8Encoding(false, true), leaveOpen: true);
                Text = reader.ReadToEnd();
            }
            catch { file.Dispose(); throw; }
        }
        public T Read<T>() => JsonSerializer.Deserialize<T>(Text) ?? throw new P2BudgetException("P2_MANIFEST_INVALID");
        public void Dispose() => file.Dispose();
    }

    [Fact]
    public async Task P2_continuation_phase_gate_stops_before_second_send_or_credential_read()
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(P2TextResponse("{\"text\":\"合成答复。\",\"emotion\":\"neutral\"}"));
        var report = Path.Combine(fixture.DirectoryPath, "phase-gate");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, false, scenes: [SubscriptionScenes[1]], phaseRequestLimit: 1, previousRequests: 3);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.False(complete);
        Assert.Equal("P2_PHASE_REQUEST_LIMIT", result.RootElement.GetProperty("BatchStopCode").GetString());
        Assert.Equal(4, result.RootElement.GetProperty("CumulativeGatewayRequests").GetInt32());
        Assert.Equal(1, fixture.Sends);
        Assert.Equal(1, fixture.Credentials.Reads);
    }

    [Theory]
    [InlineData(2, null, 17, 0, 1)]
    [InlineData(3, "P2_USAGE_UNKNOWN", 17, 0, 1)]
    [InlineData(3, null, 18, 0, 1)]
    [InlineData(3, null, 17, 1, 1)]
    [InlineData(3, null, 17, 0, 2)]
    public void P2_continuation_rejects_wrong_parent_budget_and_phase(int parentRequests, string? stop, int max, int start, int phaseMax)
    {
        var budget = new P2ContinuationBudget(ContinuationAuthorization, Guid.NewGuid().ToString("N"), SubscriptionFixtureHash,
            new string('a', 64), new string('b', 64), 3, max, DateTimeOffset.UtcNow.AddHours(1));
        var phase = new P2ContinuationPhase("unused", "diagnostic", start, phaseMax, ContinuationSceneHash("diagnostic"), []);
        Assert.Throws<P2BudgetException>(() => ValidateContinuation(budget, phase, new(parentRequests, null, stop)));
    }

    [Fact]
    public async Task P2_continuation_unique_ledger_keeps_remaining_seventeen_across_phases_and_restart()
    {
        using var fixture = new BatchFixture(subscription: true);
        var ledger = Path.Combine(fixture.DirectoryPath, "continuation.jsonl");
        var policy = fixture.Policy with { LedgerPath = ledger, MaxRequests = 17 };
        var diagnostic = new P2ContinuationPhase("unused", "diagnostic", 0, 1, "unused", []);
        var verification = diagnostic with { Phase = "verification", StartingRequests = 1, MaxRequests = 16 };
        var sends = 0;
        HttpMessageHandler Transport() => new P2ContinuationHandler(() => { sends++; return P2BatchProviderTests.CompleteResponse(); });
        async Task Send(P2BatchProvider batch)
        {
            await foreach (var _ in batch.StreamAsync(new("fixture", "conversation", [new(ChatMessageRole.User, "合成")], maxOutputTokens: 2048), default)) { }
        }
        try
        {
            Assert.Throws<FileNotFoundException>(() => OpenContinuation(policy, verification, fixture.Credentials, Transport, fixture.Clock));
            using (var first = OpenContinuation(policy, diagnostic, fixture.Credentials, Transport, fixture.Clock)) await Send(first);
            Assert.Throws<IOException>(() => OpenContinuation(policy, diagnostic, fixture.Credentials, Transport, fixture.Clock));
            string approval;
            using (var second = OpenContinuation(policy, verification, fixture.Credentials, Transport, fixture.Clock))
            {
                for (var index = 1; index < 17; index++) await Send(second);
                approval = second.ApprovalFingerprint;
            }
            using var reopened = P2BatchProvider.Open(ledger, policy, approval, fixture.Credentials, Transport, fixture.Clock);
            Assert.Equal("P2_REQUEST_LIMIT", (await Assert.ThrowsAsync<P2BudgetException>(() => Send(reopened))).Code);
            Assert.Equal(17, sends);
            Assert.Equal(17, fixture.Credentials.Reads);
        }
        finally { File.Delete(ledger); }
    }

    private sealed class P2ContinuationHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond());
    }
}
