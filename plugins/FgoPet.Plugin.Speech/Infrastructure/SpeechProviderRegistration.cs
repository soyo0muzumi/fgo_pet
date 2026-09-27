using FgoPet.Core.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Infrastructure.Speech;

public static class SpeechProviderRegistration
{
    public static IServiceCollection AddBuiltInSpeechProviders(this IServiceCollection services)
    {
        services.AddSingleton<OpenAiCompatibleSpeechSynthesizer>();
        services.AddSingleton<ISpeechProvider>(p => p.GetRequiredService<OpenAiCompatibleSpeechSynthesizer>());
        services.AddSingleton<GptSoVitsSpeechSynthesizer>();
        services.AddSingleton<ISpeechProvider>(p => p.GetRequiredService<GptSoVitsSpeechSynthesizer>());
        return services;
    }
}
