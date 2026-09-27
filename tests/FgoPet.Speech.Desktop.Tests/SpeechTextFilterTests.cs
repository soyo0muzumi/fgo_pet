using FgoPet.App.Speech;
using FgoPet.Core.Speech;
using Xunit;

namespace FgoPet.Speech.Desktop.Tests;

public sealed class SpeechTextFilterTests
{
    [Fact]
    public void Filter_removes_code_technical_fields_and_urls()
    {
        var filtered = SpeechTextFilter.Filter(
            "好的，我们先整理一下。\n"
            + new string((char)96, 3) + "json\n{\"task_id\":\"secret\"}\n"
            + new string((char)96, 3) + "\n"
            + "task_id: dispatch-123\n"
            + "参考 https://example.test/private");

        Assert.Equal("好的，我们先整理一下。 参考 链接", filtered);
        Assert.DoesNotContain("dispatch-123", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_read_keeps_long_replies_manual_without_truncating()
    {
        var accepted = SpeechTextFilter.TryGetAutoReadText(new string('长', 301), 300, out var autoReadText);

        Assert.False(accepted);
        Assert.Empty(autoReadText);
    }

    [Fact]
    public async Task Coordinator_uses_filtered_text_and_keeps_cancellation_independent()
    {
        var synthesizer = new RecordingSynthesizer();
        using var coordinator = new SpeechSynthesisCoordinator(synthesizer);
        var request = new SpeechSynthesisRequest(
            "原始",
            SpeechProviderKind.GptSoVits,
            new Uri("http://127.0.0.1:9880"));

        var codeFence = new string((char)96, 3);
        var result = await coordinator.SynthesizeAsync(
            request,
            "回答 " + codeFence + "code" + codeFence + " 后继续。",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("回答 后继续。", synthesizer.LastText);
        Assert.Equal(SpeechSessionState.Ready, coordinator.State);
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.GptSoVits;
        public string? LastText { get; private set; }

        public Task<SpeechSynthesisResult> SynthesizeAsync(
            SpeechSynthesisRequest request,
            CancellationToken cancellationToken = default)
        {
            LastText = request.Text;
            return Task.FromResult(new SpeechSynthesisResult([
                (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0,
                (byte)'W', (byte)'A', (byte)'V', (byte)'E']));
        }
    }
}