using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FgoPet.Testing.Persona;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public static class FinishDiagnosticsEntry
{
    public static async Task<int> Main()
    {
        var tests = new NativeConversationIntegrationTests();
        var cases = new (string Name, Func<Task> Run)[]
        {
            ("wire-summary-and-utf8-offset", tests.Finish_v2_wire_summary_and_utf8_offset),
            ("native-repair-parent-link", tests.Finish_v2_native_repair_parent_link),
            ("cross-layer-node-dotnet", tests.Finish_v2_cross_layer_node_dotnet),
            ("missing-conflicting-response-id", tests.Finish_v2_missing_conflicting_response_id),
            ("scope-isolation", tests.Finish_v2_scope_isolation),
            ("incomplete-wire-is-prefix", tests.Finish_v2_incomplete_wire_is_prefix),
            ("sink-failure-unknown-usage", tests.Finish_v2_sink_failure_and_unknown_usage),
            ("private-candidate-lossless-template-acl", tests.Finish_private_candidate_lossless_template_acl),
            ("private-candidate-failure-no-overwrite", tests.Finish_private_candidate_failure_no_overwrite),
            ("private-candidate-concurrent-scopes", tests.Finish_private_candidate_concurrent_scopes),
        };
        var results = new List<object>();
        var failed = 0;
        foreach (var test in cases)
        {
            try { await test.Run(); results.Add(new { test.Name, Passed = true, ErrorType = (string?)null }); }
            catch (Exception error) { failed++; results.Add(new { test.Name, Passed = false, ErrorType = error.GetType().Name }); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { RealModelCalls = 0, Failed = failed, Results = results }));
        return failed == 0 ? 0 : 1;
    }
}

public sealed partial class NativeConversationIntegrationTests
{
    private const string FinishCanary = "synthetic-private-body-canary-finish-v2";
    private static string FinishEvidence => Environment.GetEnvironmentVariable("FGO_PET_FINISH_V2_EVIDENCE")
        ?? throw new InvalidOperationException("FINISH_OFFLINE_EVIDENCE_REQUIRED");

    private static ChatRequest CaptureFixtureRequest(bool repair = false) => ChatRequest.CreateAgent("fixture", "conversation",
        [new(ModelMessageRole.System, "private-persona-not-to-retain", []),
         new(ModelMessageRole.System, FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions, []),
         new(ModelMessageRole.User, "private-user-not-to-retain", []),
         new(ModelMessageRole.Assistant, "", [new("capture-call", "fixture.read", "{\"secret\":\"private-args-not-to-retain\"}")]),
         new(ModelMessageRole.Tool, "private-tool-not-to-retain", [], "capture-call"),
         new(ModelMessageRole.System, repair ? P2FailedCandidateCapture.RepairInstructions : "private-history-not-to-retain", [])], maxOutputTokens: 2048);

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public Task Finish_private_candidate_lossless_template_acl()
    {
        var report = Path.Combine(FinishEvidence, "capture-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report);
        var scope = P2ResponseDiagnostics.Hash("capture-scope");
        var raw = new[] { "\uFEFF答复\r\n中文🍵\"\\\0", "bad\ud800" };
        using (var capture = new P2FailedCandidateCapture(report))
            for (var i = 0; i < raw.Length; i++)
            {
                Assert.True(capture.ObserveRequest(scope, i + 1, CaptureFixtureRequest(repair: true), FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions));
                Assert.False(capture.TryCapture("read-retry", P2ResponseDiagnostics.Hash("other-scope"), i + 1, raw[i], "json-invalid", null));
                Assert.True(capture.TryCapture("read-retry", scope, i + 1, raw[i], "json-invalid", null));
                Assert.False(capture.TryCapture("read-retry", scope, i + 1, raw[i], "json-invalid", null));
            }
        var path = Path.Combine(report, P2FailedCandidateCapture.FileName);
        var rows = File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(raw.Length, rows.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var units = Convert.FromBase64String(rows[i].GetProperty("RawUtf16LeBase64").GetString()!);
            var restored = new string(Enumerable.Range(0, units.Length / 2).Select(n => (char)(units[n * 2] | units[n * 2 + 1] << 8)).ToArray());
            Assert.Equal(raw[i], restored);
            Assert.Equal(P2ResponseDiagnostics.Hash(raw[i]), rows[i].GetProperty("Body").GetProperty("Sha256").GetString());
            var templates = rows[i].GetProperty("Templates").EnumerateArray().ToArray();
            Assert.Equal(new[] { 1, 5 }, templates.Select(x => x.GetProperty("MessageIndex").GetInt32()));
            Assert.Equal(P2FailedCandidateCapture.RepairInstructions, templates[1].GetProperty("Text").GetString());
        }
        Assert.Equal(raw[0], rows[0].GetProperty("RawText").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("RawText").ValueKind);
        Assert.DoesNotContain("not-to-retain", File.ReadAllText(path));
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent();
        var allowed = new[] { identity.User!.Value, "S-1-5-18", "S-1-5-32-544" };
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => Assert.Contains(rule.IdentityReference.Value, allowed));
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public Task Finish_private_candidate_failure_no_overwrite()
    {
        var report = Path.Combine(FinishEvidence, "capture-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report);
        var scope = P2ResponseDiagnostics.Hash("capture-scope");
        var capture = new P2FailedCandidateCapture(report);
        Assert.True(capture.ObserveRequest(scope, 1, CaptureFixtureRequest(), FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions));
        capture.Dispose();
        Assert.False(capture.TryCapture("read-retry", scope, 1, FinishCanary, "json-invalid", null));
        Assert.False(capture.ObserveRequest(scope, 2, CaptureFixtureRequest(), FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions));
        Assert.Throws<IOException>(() => new P2FailedCandidateCapture(report));
        Assert.Empty(File.ReadAllBytes(Path.Combine(report, P2FailedCandidateCapture.FileName)));
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public Task Finish_private_candidate_concurrent_scopes()
    {
        var report = Path.Combine(FinishEvidence, "capture-scopes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(report);
        using (var capture = new P2FailedCandidateCapture(report))
        {
            Parallel.For(1, 13, request => Assert.True(capture.ObserveRequest(P2ResponseDiagnostics.Hash("scope-" + request), request,
                CaptureFixtureRequest(), FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions)));
            Parallel.For(1, 13, request => Assert.True(capture.TryCapture("read-retry", P2ResponseDiagnostics.Hash("scope-" + request),
                request, "bad候选-" + request, "json-invalid", null)));
        }
        var rows = File.ReadAllLines(Path.Combine(report, P2FailedCandidateCapture.FileName)).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(12, rows.Length);
        Assert.Equal(12, rows.Select(x => x.GetProperty("Request").GetInt32()).Distinct().Count());
        Assert.All(rows, row =>
        {
            var request = row.GetProperty("Request").GetInt32();
            Assert.Equal(P2ResponseDiagnostics.Hash("scope-" + request), row.GetProperty("ScopeHash").GetString());
            Assert.Equal("bad候选-" + request, row.GetProperty("RawText").GetString());
        });
        return Task.CompletedTask;
    }

    private static HttpResponseMessage FinishWire(string body, string id) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new { id,
            choices = new[] { new { delta = new { content = body }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 100, completion_tokens = 203 } }) + "\n\ndata: [DONE]\n\n")
    };

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_wire_summary_and_utf8_offset()
    {
        var snapshots = new List<object>();
        foreach (var body in new[] { "{\n\"text\":\"中🍵" + FinishCanary + "\"", "{\n\"text\":]}", "{\"text\":\"中🍵\",\"emotion\":\"neutral\"}" })
        {
            using var fixture = new BatchFixture(subscription: true);
            var events = new List<P2RequestDiagnostic>();
            fixture.Batch.Diagnostic = events.Add;
            fixture.Respond = (_, _) => Task.FromResult(FinishWire(body, "chatcmpl-offline-" + FinishCanary));
            await fixture.Send();
            var record = JsonSerializer.SerializeToElement(events.Last());
            Assert.True(record.TryGetProperty("Response", out var response), "响应诊断应包含固定字段的 Response 摘要");
            Assert.Equal(P2Hash("chatcmpl-offline-" + FinishCanary).ToLowerInvariant(), response.GetProperty("RequestHash").GetString());
            Assert.Equal(P2Hash(body).ToLowerInvariant(), response.GetProperty("Body").GetProperty("Sha256").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(body), response.GetProperty("Body").GetProperty("Utf8Bytes").GetInt32());
            Assert.Equal(body.Length, response.GetProperty("Body").GetProperty("Utf16Units").GetInt32());
            try { using var json = JsonDocument.Parse(body); Assert.Equal("object", response.GetProperty("Json").GetProperty("Kind").GetString()); }
            catch (JsonException error)
            {
                var meta = response.GetProperty("Json");
                Assert.Equal(error.LineNumber, meta.GetProperty("ErrorLine").GetInt64());
                Assert.Equal(error.BytePositionInLine, meta.GetProperty("ErrorByteInLine").GetInt64());
                Assert.Equal(body.EndsWith('"'), meta.GetProperty("ErrorAtEof").GetBoolean());
                Assert.InRange(meta.GetProperty("ErrorByteAbsolute").GetInt64(), 0, Encoding.UTF8.GetByteCount(body));
            }
            Assert.DoesNotContain(FinishCanary, JsonSerializer.Serialize(events));
            Assert.Equal(1, fixture.Sends);
            snapshots.Add(record);
        }
        Directory.CreateDirectory(FinishEvidence);
        File.WriteAllText(Path.Combine(FinishEvidence, "consumer-samples.json"), JsonSerializer.Serialize(snapshots));
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_native_repair_parent_link()
    {
        // Correlation-only synthetic matrix; the separate gate regressions keep the 20-send cap.
        using var fixture = new BatchFixture(subscription: true, maxRequests: null);
        var turns = new Queue<HttpResponseMessage>();
        const string good = "{\"text\":\"仅合成结果。\",\"emotion\":\"neutral\"}";
        foreach (var scene in ContractScenes)
        {
            turns.Enqueue(ContractToolResponse("skill.load", "load", JsonSerializer.Serialize(new { name = scene.Skill })));
            if (scene.Id == "permission")
            {
                turns.Enqueue(ContractToolResponse("user.ask", "ask", "{\"questions\":[{\"id\":\"title\",\"question\":\"合成标题？\"}]}"));
                turns.Enqueue(ContractToolResponse("fixture.command", "denied", "{}"));
                turns.Enqueue(P2TextResponse(good));
            }
            else
            {
                turns.Enqueue(ContractToolResponse(scene.Id == "read-retry" ? "todo.list" : "fixture.receipt", "read", "{}"));
                if (scene.Id == "read-retry") turns.Enqueue(P2TextResponse("{\"text\":\"" + FinishCanary));
                turns.Enqueue(P2TextResponse(good));
                if (scene.Id == "read-retry") turns.Enqueue(ContractToolResponse("todo.list", "retry", "{}"));
                turns.Enqueue(P2TextResponse(good));
            }
        }
        var sequence = 0;
        fixture.Respond = async (_, _) =>
        {
            using var original = turns.Dequeue();
            var wire = await original.Content.ReadAsStringAsync();
            var id = "chatcmpl-offline-native-" + ++sequence;
            var lines = wire.Split('\n').Select(line =>
            {
                if (!line.StartsWith("data: {", StringComparison.Ordinal)) return line;
                var frame = JsonNode.Parse(line[6..])!.AsObject();
                frame["id"] = id;
                return "data: " + frame.ToJsonString();
            });
            return new(HttpStatusCode.OK) { Content = new StringContent(string.Join('\n', lines)) };
        };
        var report = Path.Combine(FinishEvidence, "native-" + Guid.NewGuid().ToString("N"));
        var results = await RunContractScenes(fixture.Batch, report, ResolveSourcePersona(), false, enforceRequestLimits: false);
        Assert.Empty(turns);
        Assert.Equal(25, fixture.Sends);
        Assert.All(results, result => Assert.Equal(0, result.CommandInvocations));
        var finals = File.ReadAllLines(Path.Combine(report, "model-finals.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var bad = finals.Single(x => x.GetProperty("Request").GetInt32() == 4);
        var repaired = finals.Single(x => x.GetProperty("Request").GetInt32() == 5);
        Assert.True(repaired.TryGetProperty("Correlation", out var correlation), "修复候选应带直接响应 hash 和父请求关联");
        Assert.Equal(P2Hash("chatcmpl-offline-native-4").ToLowerInvariant(), correlation.GetProperty("RequestHash").GetString());
        Assert.Equal(4, correlation.GetProperty("RepairParentRequest").GetInt32());
        Assert.Equal(P2Hash("chatcmpl-offline-native-3").ToLowerInvariant(), correlation.GetProperty("RepairParentHash").GetString());
        Assert.Equal("json-invalid", bad.GetProperty("Output").GetProperty("StrictValidation").GetString());
        var delivery = JsonDocument.Parse(File.ReadLines(Path.Combine(report, "delivery.jsonl")).First()).RootElement;
        Assert.Equal(repaired.GetProperty("Output").GetProperty("RawHash").GetString(), delivery.GetProperty("Diagnostic").GetProperty("Output").GetProperty("RawHash").GetString());
        Assert.DoesNotContain(FinishCanary, File.ReadAllText(Path.Combine(report, "provider-diagnostics.jsonl")));
        Assert.DoesNotContain(FinishCanary, File.ReadAllText(Path.Combine(report, "model-finals.jsonl")));
        var privateFile = Path.Combine(report, "failed-candidates.private.jsonl");
        Assert.True(File.Exists(privateFile), "失败候选应在格式修复前单独留存在受限本地文件");
        var captured = JsonDocument.Parse(Assert.Single(File.ReadAllLines(privateFile))).RootElement;
        Assert.Equal(4, captured.GetProperty("Request").GetInt32());
        Assert.Equal("{\"text\":\"" + FinishCanary, captured.GetProperty("RawText").GetString());
        Assert.Equal(bad.GetProperty("Correlation").GetProperty("RequestHash").GetString(), captured.GetProperty("RequestHash").GetString());
        Assert.Equal(bad.GetProperty("Output").GetProperty("RawHash").GetString(), captured.GetProperty("Body").GetProperty("Sha256").GetString(), ignoreCase: true);
        Assert.Equal(FgoPet.App.Dialogue.StructuredOutputValidator.FinalStepInstructions,
            Assert.Single(captured.GetProperty("Templates").EnumerateArray()).GetProperty("Text").GetString());
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_cross_layer_node_dotnet()
    {
        var file = Environment.GetEnvironmentVariable("FGO_PET_FINISH_V2_FIXTURES")
            ?? throw new InvalidOperationException("FINISH_CROSS_LAYER_FIXTURES_REQUIRED");
        using var fixtures = JsonDocument.Parse(File.ReadAllText(file));
        var observations = new List<object>();
        foreach (var input in fixtures.RootElement.EnumerateArray())
        {
            using var fixture = new BatchFixture(subscription: true);
            var events = new List<P2RequestDiagnostic>();
            fixture.Batch.Diagnostic = events.Add;
            fixture.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(input.GetProperty("wire").GetString()!) });
            var consumed = new StringBuilder();
            await foreach (var chunk in fixture.Batch.StreamAsync(new ChatRequest("fixture", "conversation",
                [new(ChatMessageRole.User, "合成输入")], maxOutputTokens: 2048), default)) consumed.Append(chunk.TextDelta);
            var response = Assert.IsType<P2ResponseDiagnostic>(events.Last().Response);
            var body = P2ResponseDiagnostics.Summarize(consumed.ToString());
            Assert.Equal(input.GetProperty("requestHash").GetString(), response.RequestHash);
            foreach (var stage in new[] { "upstreamContent", "gatewayContent" })
            {
                var summary = input.GetProperty(stage);
                Assert.Equal(summary.GetProperty("sha256").GetString(), body.Sha256);
                Assert.Equal(summary.GetProperty("utf8Bytes").GetInt32(), body.Utf8Bytes);
                Assert.Equal(summary.GetProperty("utf16Units").GetInt32(), body.Utf16Units);
            }
            Assert.Equal(body, response.Body);
            Assert.Equal("stop", response.Finish);
            Assert.Equal("consistent", response.ResponseIdState);
            Assert.Equal(input.GetProperty("name").GetString() == "valid" ? "object" : "invalid", response.Json.Kind);
            if (input.GetProperty("name").GetString() != "valid")
                Assert.Equal(input.GetProperty("name").GetString() == "eof", response.Json.ErrorAtEof);
            observations.Add(new { Name = input.GetProperty("name").GetString(), Response = response, Consumer = body });
        }
        File.WriteAllText(Path.Combine(FinishEvidence, "cross-layer-summaries.json"), JsonSerializer.Serialize(observations));
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_missing_conflicting_response_id()
    {
        foreach (var mode in new[] { "missing", "conflicting", "invalid" })
        {
            using var fixture = new BatchFixture(subscription: true);
            var events = new List<P2RequestDiagnostic>();
            fixture.Batch.Diagnostic = events.Add;
            fixture.Respond = async (_, _) =>
            {
                using var original = FinishWire("{\"text\":\"合成结果\"}", "id-a");
                var wire = await original.Content.ReadAsStringAsync();
                var frame = JsonNode.Parse(wire.Split('\n')[0][6..])!.AsObject();
                if (mode == "missing") frame.Remove("id");
                if (mode == "invalid") frame["id"] = new string('x', 257) + FinishCanary;
                var first = "data: " + frame.ToJsonString() + "\n\n";
                var extra = mode == "conflicting" ? "data: {\"id\":\"id-b\",\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":203}}\n\n" : "";
                return new(HttpStatusCode.OK) { Content = new StringContent(first + extra + "data: [DONE]\n\n") };
            };
            await fixture.Send();
            var diagnostic = Assert.IsType<P2ResponseDiagnostic>(events.Last().Response);
            Assert.Equal(mode, diagnostic.ResponseIdState);
            Assert.Null(diagnostic.RequestHash);
            Assert.DoesNotContain(FinishCanary, JsonSerializer.Serialize(events));
        }
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_scope_isolation()
    {
        var links = new P2RepairLinker();
        var a = new string('a', 64);
        var b = new string('b', 64);
        const string bad = "{\"text\":\"合成";
        const string good = "{\"text\":\"合成结果\"}";
        async Task Record(string scope, int request, string body)
        {
            links.BeginRequest(scope, request);
            using var wire = FinishWire(body, "scope-id-" + request);
            var response = P2ResponseDiagnostics.ReadWire(await wire.Content.ReadAsByteArrayAsync());
            links.Observe(new(DateTimeOffset.UtcNow, new string('c', 64), request, "settlement", "completed", P2FailureReason.None, 200, response));
        }
        await Record(a, 1, bad);
        Assert.NotNull(links.CaptureFinal(a, 1, bad));
        links.MarkRepair(b, 1); // 错误 scope 不能登记父请求。
        links.MarkRepair(a, 1);
        await Record(b, 2, good);
        Assert.Null(links.CaptureFinal(b, 2, good)!.RepairParentRequest);
        await Record(a, 3, good);
        var child = links.CaptureFinal(a, 3, good)!;
        Assert.Equal(1, child.RepairParentRequest);
        Assert.Equal(P2ResponseDiagnostics.Hash("scope-id-1"), child.RepairParentHash);
        Assert.True(child.WireBodyMatches);
        await Record(a, 4, good);
        Assert.Null(links.CaptureFinal(a, 4, good)!.RepairParentRequest); // 标记只消费一次。
        Assert.Null(links.CaptureFinal(b, 4, good));
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public Task Finish_v2_incomplete_wire_is_prefix()
    {
        const string first = "data: {\"id\":\"prefix-id\",\"choices\":[{\"delta\":{\"content\":\"合成前缀\"},\"finish_reason\":\"stop\"}]}\n\n";
        foreach (var suffix in new[] { "data: [DONE]\n\ndata: {}\n\n", "data: {broken\n\ndata: [DONE]\n\n" })
        {
            var response = Assert.IsType<P2ResponseDiagnostic>(P2ResponseDiagnostics.ReadWire(Encoding.UTF8.GetBytes(first + suffix)));
            Assert.False(response.Body.Complete);
            Assert.Equal(P2ResponseDiagnostics.Hash("合成前缀"), response.Body.Sha256);
            Assert.Equal(suffix.StartsWith("data: [DONE]", StringComparison.Ordinal) ? "after-done" : "frame-invalid", response.ParseState);
        }
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "OfflineFinishDiagnostics")]
    public async Task Finish_v2_sink_failure_and_unknown_usage()
    {
        using (var normal = new BatchFixture(subscription: true))
        {
            normal.Batch.Diagnostic = _ => throw new IOException(FinishCanary);
            await normal.Send();
            Assert.Equal(1, normal.Sends);
            Assert.Null(normal.Batch.Snapshot.StopCode);
        }
        using var unknown = new BatchFixture(subscription: true);
        var events = new List<P2RequestDiagnostic>();
        unknown.Batch.Diagnostic = events.Add;
        unknown.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("data: {\"id\":\"usage-unknown\",\"choices\":[{\"delta\":{\"content\":\"合成\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n") });
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => unknown.Send())).Code);
        Assert.Equal("P2_USAGE_UNKNOWN", (await Assert.ThrowsAsync<P2BudgetException>(() => unknown.Send())).Code);
        Assert.Equal(1, unknown.Sends);
        Assert.Equal(P2FailureReason.UsageMissingFinal, events.Last().Reason);
        Assert.NotNull(events.Last().Response); // 用量失败仍可取证，不能因此释放预算。
    }
}
