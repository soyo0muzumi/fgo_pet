using FgoPet.Core.Speech;
using FgoPet.Infrastructure.Speech;
using Xunit;

namespace FgoPet.Speech.Core.Tests;

public sealed class SpeechProviderRouterTests
{
    [Fact]
    public async Task Missing_provider_fails_configuration_without_calling_another_provider()
    {
        var provider = new TestProvider(SpeechProviderKind.OpenAiCompatible);
        var router = new SpeechSynthesizerRouter([provider]);
        var error = await Assert.ThrowsAsync<SpeechSynthesisException>(() => router.SynthesizeAsync(
            new("正文", SpeechProviderKind.IndexTts, new Uri("http://127.0.0.1:7860"))));
        Assert.Equal(SpeechFailureCategory.Configuration, error.Category);
        Assert.Equal(0, provider.Calls);
        await router.SynthesizeAsync(new("正文", SpeechProviderKind.OpenAiCompatible, new Uri("https://example.test")));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void Duplicate_provider_kind_is_rejected_before_activation()
    {
        Assert.Throws<ArgumentException>(() => new SpeechSynthesizerRouter([
            new TestProvider(SpeechProviderKind.IndexTts), new TestProvider(SpeechProviderKind.IndexTts)]));
    }

    private sealed class TestProvider(SpeechProviderKind kind) : ISpeechProvider
    {
        public SpeechProviderKind Provider => kind;
        public int Calls { get; private set; }
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SpeechSynthesisResult("RIFF0000WAVE"u8.ToArray()));
        }
    }
}
