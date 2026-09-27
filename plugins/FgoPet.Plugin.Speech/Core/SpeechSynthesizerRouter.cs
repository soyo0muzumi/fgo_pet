using System.Collections.Immutable;
using FgoPet.Core.Speech;

namespace FgoPet.Infrastructure.Speech;

public sealed class SpeechSynthesizerRouter : ISpeechSynthesizer
{
    private readonly ImmutableDictionary<SpeechProviderKind, ISpeechProvider> _providers;
    public SpeechSynthesizerRouter(IEnumerable<ISpeechProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var builder = ImmutableDictionary.CreateBuilder<SpeechProviderKind, ISpeechProvider>();
        foreach (var provider in providers)
        {
            if (provider is null || !Enum.IsDefined(provider.Provider) || !builder.TryAdd(provider.Provider, provider))
                throw new ArgumentException("Invalid or duplicate speech provider.", nameof(providers));
        }
        _providers = builder.ToImmutable();
    }
    public SpeechProviderKind Provider => throw new InvalidOperationException("A provider must be selected by the request.");
    public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_providers.TryGetValue(request.Provider, out var provider))
            throw new SpeechSynthesisException(SpeechFailureCategory.Configuration, "所选语音服务未注册。请调整朗读设置。");
        return provider.SynthesizeAsync(request, cancellationToken);
    }
}
