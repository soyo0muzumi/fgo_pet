using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Images;

public sealed class SelectedImageTests
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    internal static readonly ToolScope Scope = new("conversation", "role", "project");
    internal static readonly ModelRouteKey Route = ModelRouteKey.From(new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4.1-mini"));
    internal static void Grant(SelectedImageStore store, ToolScope scope, ModelRouteKey route, ChatImageInput image)
    { store.Grant(scope, route, image, "user"); store.Bind(scope, route, "user", "run", default); }
    private static ChatImageInput Image() => new(Png, 1, 1);
    [Fact]
    public void Browser_pixels_keep_their_source_and_cannot_be_replayed_after_release()
    {
        var store = new SelectedImageStore(_ => true);
        Grant(store, Scope, Route, new(Png, 1, 1, isBrowserImage: true));
        var request = Request(store.Read(Scope, Route, "run", default));
        ChatImageInputs.Prepare(request, Route, store, true, default);
        var wire = ChatRequestInputEnvelope.Write(Route.ModelId, request);
        Assert.Contains("Authorized rendered browser capture", wire);
        Assert.DoesNotContain("User-selected image", wire);
        Assert.Contains("base64", wire);
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(request.ModelMessages));
        store.Clear();
        Assert.Throws<SelectedImageException>(() => ChatImageInputs.Validate(request, Route, store, default));
    }
    internal static ChatRequest Request(ToolImageReference reference) => ChatRequest.CreateAgent("role", "conversation", [
        new(ModelMessageRole.User, "inspect the selected image", []),
        new(ModelMessageRole.Assistant, "", [new("image-call", "image.read", "{}"), new("other-call", "fixture.read", "{}")]),
        new(ModelMessageRole.Tool, "{\"success\":true}", [], "image-call") { Image = reference },
        new(ModelMessageRole.Tool, "{}", [], "other-call")
    ]);
    [Fact]
    public void Only_current_scope_route_reference_and_live_authority_can_resolve()
    {
        var active = true;
        var store = new SelectedImageStore(s => active && s == Scope);
        Assert.Equal("IMAGE_SELECTION_REQUIRED", Assert.Throws<SelectedImageException>(() => store.Read(Scope, Route, "run", default)).Code);
        Grant(store, Scope, Route, Image());
        var reference = store.Read(Scope, Route, "run", default);
        Assert.Equal(1, store.Resolve(reference, "role", "conversation", Route, default).Width);
        Assert.Equal("IMAGE_SCOPE_DENIED", Assert.Throws<SelectedImageException>(() => store.Read(Scope with { ProjectId = "other" }, Route, "run", default)).Code);
        Assert.Throws<SelectedImageException>(() => store.Resolve(reference, "other", "conversation", Route, default));
        Assert.Throws<SelectedImageException>(() => store.Resolve(reference, "role", "other", Route, default));
        Assert.Throws<SelectedImageException>(() => store.Resolve(new(Guid.NewGuid().ToString("N")), "role", "conversation", Route, default));
        Assert.Equal("IMAGE_MODEL_CHANGED", Assert.Throws<SelectedImageException>(() => store.Read(Scope, Route with { ModelId = "other" }, "run", default)).Code);
        active = false;
        Assert.Throws<SelectedImageException>(() => store.Read(Scope, Route, "run", default));
    }
    [Fact]
    public void Expiry_revocation_reselection_and_cancellation_fail_closed()
    {
        var clock = new Clock(); var store = new SelectedImageStore(_ => true, clock);
        Grant(store, Scope, Route, Image()); var original = store.Read(Scope, Route, "run", default);
        Grant(store, Scope, Route, Image());
        Assert.Throws<SelectedImageException>(() => store.Resolve(original, "role", "conversation", Route, default));
        Assert.Throws<OperationCanceledException>(() => store.Read(Scope, Route, "run", new(true)));
        clock.Now += SelectedImageStore.Lifetime;
        Assert.Equal("IMAGE_SELECTION_EXPIRED", Assert.Throws<SelectedImageException>(() => store.Read(Scope, Route, "run", default)).Code);
        Grant(store, Scope, Route, Image()); store.Clear();
        Assert.Throws<SelectedImageException>(() => store.Read(Scope, Route, "run", default));
    }
    [Fact]
    public void Native_wire_appends_real_image_after_complete_tool_batch_but_checkpoint_has_only_reference()
    {
        var store = new SelectedImageStore(_ => true); Grant(store, Scope, Route, Image());
        var reference = store.Read(Scope, Route, "run", default); var request = Request(reference);
        Assert.Throws<SelectedImageException>(() => ChatRequestInputEnvelope.Write(Route.ModelId, request));
        ChatImageInputs.Prepare(request, Route, store, true, default);
        using var json = JsonDocument.Parse(ChatRequestInputEnvelope.Write(Route.ModelId, request));
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("user", messages[4].GetProperty("role").GetString());
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), messages[4].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("low", messages[4].GetProperty("content")[1].GetProperty("image_url").GetProperty("detail").GetString());
        var persisted = JsonSerializer.Serialize(request.ModelMessages);
        Assert.Contains(reference.Id, persisted);
        Assert.DoesNotContain("base64", persisted);
        Assert.DoesNotContain(Convert.ToBase64String(Png), persisted);
        ChatImageInputs.Validate(request, Route, store, default);
        store.Clear();
        Assert.Throws<SelectedImageException>(() => ChatImageInputs.Validate(request, Route, store, default));
    }
    [Fact]
    public void Meter_reserves_image_budget_without_counting_base64_as_text_and_usage_stays_exact()
    {
        var store = new SelectedImageStore(_ => true); Grant(store, Scope, Route, Image());
        var request = Request(store.Read(Scope, Route, "run", default)); ChatImageInputs.Prepare(request, Route, store, true, default);
        var meter = new RequestTokenMeter(); var measurement = meter.Measure(Route, request);
        Assert.InRange(measurement.InputTokens, 8192, 12000);
        Assert.DoesNotContain("base64", ChatRequestInputEnvelope.WriteForMeasurement(Route.ModelId, request));
        meter.RecordUsage(Route, request, new(1234, 1)); Assert.Equal(1234, meter.Measure(Route, request).InputTokens);
    }
    [Fact]
    public void Unsupported_unprepared_multi_image_and_non_tool_images_are_rejected()
    {
        var store = new SelectedImageStore(_ => true); Grant(store, Scope, Route, Image());
        var reference = store.Read(Scope, Route, "run", default); var request = Request(reference);
        Assert.Equal("IMAGE_MODEL_UNSUPPORTED", Assert.Throws<SelectedImageException>(() => ChatImageInputs.Prepare(request, Route, store, false, default)).Code);
        Assert.Throws<OperationCanceledException>(() => ChatImageInputs.Prepare(request, Route, store, true, new(true)));
        Assert.Throws<AgentProtocolException>(() => ChatRequest.CreateAgent("role", "conversation", [new(ModelMessageRole.User, "fixture", []) { Image = reference }]));
        Assert.Throws<AgentProtocolException>(() => Request(new("/private/fixture.png")));
        var messages = request.ModelMessages.SetItem(3, request.ModelMessages[3] with { Image = new(Guid.NewGuid().ToString("N")) });
        Assert.Equal("IMAGE_COUNT_LIMIT", Assert.Throws<SelectedImageException>(() => ChatImageInputs.Prepare(ChatRequest.CreateAgent("role", "conversation", messages), Route, store, true, default)).Code);
    }
    [Fact]
    public void Capability_allow_list_does_not_trust_custom_endpoints_or_similar_model_names()
    {
        Assert.True(ImageModelCapabilities.Supports(Route));
        Assert.True(ImageModelCapabilities.Supports(Route with { ModelId = "gpt-4.1-mini-2025-04-14" }));
        Assert.False(ImageModelCapabilities.Supports(Route with { ModelId = "gpt-4.1-mini-custom" }));
        Assert.False(ImageModelCapabilities.Supports(ModelRouteKey.From(new("custom-openai-compatible", "https://fixture.test/v1", "gpt-4.1-mini"))));
        Assert.False(ImageModelCapabilities.Supports(ModelRouteKey.From(new("openai", "https://fixture.test/v1", "gpt-4.1-mini"))));
    }
    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1")]
    public void Official_deepseek_flash_is_supported_with_conservative_image_budget_and_exact_usage(string endpoint)
    {
        var route = ModelRouteKey.From(new("deepseek", endpoint, "deepseek-flash"));
        Assert.True(ImageModelCapabilities.Supports(route));
        var store = new SelectedImageStore(s => s == Scope); Grant(store, Scope, route, Image());
        var reference = store.Read(Scope, route, "run", default); var request = Request(reference);
        ChatImageInputs.Prepare(request, route, store, ImageModelCapabilities.Supports(route), default);
        var meter = new RequestTokenMeter(); var measurement = meter.Measure(route, request);
        // DeepSeek documents a 1024 image-token maximum; retain the stricter 8192 reserve
        // with text/envelope overhead charged separately, not as an asserted exact token cost.
        Assert.True(measurement.InputTokens >= 8192 + System.Text.Encoding.UTF8.GetByteCount(
            ChatRequestInputEnvelope.WriteForMeasurement(route.ModelId, request)));
        using var wire = JsonDocument.Parse(ChatRequestInputEnvelope.Write(route.ModelId, request));
        Assert.Equal("user", wire.RootElement.GetProperty("messages")[4].GetProperty("role").GetString());
        Assert.Equal("low", wire.RootElement.GetProperty("messages")[4].GetProperty("content")[1].GetProperty("image_url").GetProperty("detail").GetString());
        meter.RecordUsage(route, request, new(1498, 1));
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(route, request).Kind);
        Assert.Equal(1498, meter.Measure(route, request).InputTokens);
        Assert.Throws<SelectedImageException>(() => store.Resolve(reference, "role", "different-conversation", route, default));
        Assert.Throws<SelectedImageException>(() => store.Resolve(reference, "role", "conversation", Route, default));
        store.Clear();
        Assert.Throws<SelectedImageException>(() => ChatImageInputs.Dispatch(request, route, store, () => true, default));
    }

    [Theory]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-v4-pro")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-flash-custom")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "DeepSeek-V4.1-Flash-1017")]
    [InlineData("deepseek", "https://api.deepseek.com/v1", "deepseek-v4-flash-vision-exp")]
    [InlineData("deepseek", "https://api.deepseek.com.evil.invalid/v1", "deepseek-flash")]
    [InlineData("deepseek", "https://api.deepseek.com/v1?proxy=1", "deepseek-flash")]
    [InlineData("deepseek", "https://api.deepseek.com/anthropic", "deepseek-flash")]
    [InlineData("deepseek", "http://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("custom-openai-compatible", "https://api.deepseek.com/v1", "deepseek-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8327/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "http://localhost:8326/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1?proxy=1", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/other", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "https://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("openai", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash")]
    [InlineData("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash-custom")]
    public void Deepseek_capability_does_not_guess_other_model_names_proxies_or_provider_ids(string provider, string endpoint, string model)
    {
        Assert.False(ImageModelCapabilities.Supports(ModelRouteKey.From(new(provider, endpoint, model))));
    }

    [Fact]
    public void Exact_buddy_subscription_route_prepares_pixels_with_conservative_budget_and_exact_usage()
    {
        var route = ModelRouteKey.From(new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash"));
        Assert.True(ImageModelCapabilities.Supports(route));
        var store = new SelectedImageStore(s => s == Scope); Grant(store, Scope, route, Image());
        var reference = store.Read(Scope, route, "run", default); var request = Request(reference);
        ChatImageInputs.Prepare(request, route, store, ImageModelCapabilities.Supports(route), default);
        var meter = new RequestTokenMeter();
        Assert.True(meter.Measure(route, request).InputTokens >= 8192 + System.Text.Encoding.UTF8.GetByteCount(
            ChatRequestInputEnvelope.WriteForMeasurement(route.ModelId, request)));
        Assert.Contains("data:image/png;base64,", ChatRequestInputEnvelope.Write(route.ModelId, request));
        meter.RecordUsage(route, request, new(259, 84));
        Assert.Equal(259, meter.Measure(route, request).InputTokens);
        Assert.Equal(TokenCountKind.ProviderUsage, meter.Measure(route, request).Kind);
        store.Clear();
        Assert.Throws<SelectedImageException>(() => ChatImageInputs.Dispatch(request, route, store, () => true, default));
    }

    [Fact]
    public void Header_checks_type_size_and_dimension_bombs_before_pixel_decode()
    {
        Assert.Equal((1, 1), SelectedImageFormat.Inspect(Png, ".png"));
        Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(Png, ".jpg"));
        Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(Png, ".svg"));
        Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(new byte[SelectedImageFormat.MaxSourceBytes + 1], ".png"));
        var bomb = Png.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16), uint.MaxValue);
        Assert.Equal("IMAGE_DIMENSION_LIMIT", Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(bomb, ".png")).Code);
        BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(16), 4096); BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(20), 4096);
        Assert.Equal("IMAGE_DIMENSION_LIMIT", Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(bomb, ".png")).Code);
        Assert.Throws<SelectedImageException>(() => SelectedImageFormat.Inspect(new byte[24], ".png"));
        Assert.Throws<SelectedImageException>(() => new ChatImageInput(Png, 0, 1));
        Assert.Throws<SelectedImageException>(() => new ChatImageInput(new byte[ChatImageInput.MaxBytes + 1], 1, 1));
    }
    [Fact]
    public void Image_result_must_be_successful_readonly_with_opaque_identifier()
    {
        var result = new ToolResult(true, JsonSerializer.SerializeToElement(new { })) { Image = new(Guid.NewGuid().ToString("N")) };
        Assert.NotNull(ToolResultNormalizer.Normalize(ToolEffect.ReadOnly, result).Result!.Image);
        Assert.Equal("TOOL_INVALID_OUTPUT", ToolResultNormalizer.Normalize(ToolEffect.Proposal, result).Result!.ErrorCode);
        Assert.Equal("TOOL_INVALID_OUTPUT", ToolResultNormalizer.Normalize(ToolEffect.ReadOnly, result with { Success = false }).Result!.ErrorCode);
        Assert.Equal("TOOL_INVALID_OUTPUT", ToolResultNormalizer.Normalize(ToolEffect.ReadOnly, result with { Image = new("sensitive-path") }).Result!.ErrorCode);
    }
    [Fact]
    public async Task Revocation_and_dispatch_are_linearized_on_the_same_lease()
    {
        var store = new SelectedImageStore(_ => true); Grant(store, Scope, Route, Image());
        var request = Request(store.Read(Scope, Route, "run", default)); ChatImageInputs.Prepare(request, Route, store, true, default);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var clearing = new ManualResetEventSlim();
        var order = new List<string>();
        var dispatch = Task.Run(() => ChatImageInputs.Dispatch(request, Route, store, () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            lock (order) order.Add("send");
            return true;
        }, default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var clear = Task.Run(() => { clearing.Set(); store.Clear(); lock (order) order.Add("clear"); });
        Assert.True(clearing.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(clear.IsCompleted);
        release.Set();
        Assert.True(await dispatch); await clear;
        Assert.Equal(new[] { "send", "clear" }, order);
        var sent = false;
        Assert.Throws<SelectedImageException>(() => ChatImageInputs.Dispatch(request, Route, store, () => sent = true, default));
        Assert.False(sent);
    }
    [Fact]
    public void Config_change_during_credential_wait_is_checked_again_at_dispatch()
    {
        var current = Route;
        var store = new SelectedImageStore(_ => true, currentRoute: () => current);
        Grant(store, Scope, Route, Image());
        var request = Request(store.Read(Scope, Route, "run", default)); ChatImageInputs.Prepare(request, Route, store, true, default);
        current = Route with { ConfigurationRevision = "changed" };
        var sent = false;
        Assert.Equal("IMAGE_MODEL_CHANGED", Assert.Throws<SelectedImageException>(() => ChatImageInputs.Dispatch(request, Route, store,
            () => sent = true, default)).Code);
        Assert.False(sent);
    }
    [Fact]
    public void Png_metadata_is_removed_without_decompressing_and_invalid_crc_animation_are_rejected()
    {
        var metadata = PngChunk("zTXt", System.Text.Encoding.ASCII.GetBytes("private-synthetic-note\0\0not-a-zlib-stream"));
        var decorated = Png[..33].Concat(metadata).Concat(Png[33..]).ToArray();
        Assert.Equal(Png, SelectedImageSanitizer.Prepare(decorated, ".png"));
        var animated = Png[..33].Concat(PngChunk("acTL", new byte[8])).Concat(Png[33..]).ToArray();
        Assert.Equal("IMAGE_TYPE_UNSUPPORTED", Assert.Throws<SelectedImageException>(() => SelectedImageSanitizer.Prepare(animated, ".png")).Code);
        decorated[decorated.Length - 1] ^= 1;
        Assert.Throws<SelectedImageException>(() => SelectedImageSanitizer.Prepare(decorated, ".png"));
    }
    [Fact]
    public void Jpeg_metadata_is_removed_while_coding_transform_is_bounded_and_cmyk_rejected()
    {
        byte[] core = [0xff,0xd8, 0xff,0xc0,0,11,8,0,1,0,1,1,1,0x11,0, 0xff,0xda,0,8,1,1,0,0,63,0, 0x12,0xff,0,0x34, 0xff,0xd9];
        byte[] metadata = [0xff,0xe1,0,8,69,120,105,102,0,0];
        var decorated = core[..2].Concat(metadata).Concat(core[2..]).ToArray();
        Assert.Equal(core, SelectedImageSanitizer.Prepare(decorated, ".jpg"));
        var cmyk = core.ToArray(); cmyk[11] = 4;
        Assert.Equal("IMAGE_TYPE_UNSUPPORTED", Assert.Throws<SelectedImageException>(() => SelectedImageSanitizer.Prepare(cmyk, ".jpg")).Code);
        byte[] badAdobe = [0xff,0xee,0,14,65,100,111,98,101,0,100,0,0,0,0,2];
        Assert.Throws<SelectedImageException>(() => SelectedImageSanitizer.Prepare(core[..2].Concat(badAdobe).Concat(core[2..]).ToArray(), ".jpg"));
    }
    internal static byte[] PngChunk(string type, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4); data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
        return chunk;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
