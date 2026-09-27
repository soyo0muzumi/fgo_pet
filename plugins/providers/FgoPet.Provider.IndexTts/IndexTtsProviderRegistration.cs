using FgoPet.Core.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Infrastructure.Speech;

public static class IndexTtsProviderRegistration
{
    public static IServiceCollection AddIndexTtsProvider(this IServiceCollection services)
    {
        services.AddKeyedSingleton<HttpClient>("speech.indextts", (_, _) => new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        }) { Timeout = TimeSpan.FromMinutes(5) });
        services.AddSingleton<IndexTtsSpeechSynthesizer>(p => new(p.GetRequiredKeyedService<HttpClient>("speech.indextts")));
        services.AddSingleton<ISpeechProvider>(p => p.GetRequiredService<IndexTtsSpeechSynthesizer>());
        return services;
    }
}
