using FgoPet.Core.Speech;
using FgoPet.Core.Secrets;
using FgoPet.App.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiSdk;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Speech;

public static class SpeechServiceCollectionExtensions
{
    public static IServiceCollection AddSpeechSettings(this IServiceCollection services, string voicesDirectory, bool enabled)
    {
        if (!enabled) return services;
        services.AddSingleton(provider => new SpeechConnectionViewModel(
            provider.GetRequiredService<ISpeechSettingsStore>(), provider.GetRequiredService<ICredentialStore>(),
            provider.GetRequiredService<SpeechPlaybackCoordinator>(), voicesDirectory));
        services.AddSingleton<SpeechConnectionPage>();
        services.AddSingleton<ISettingsPageViewFactory>(provider => new SettingsPageViewFactory(
            "Speech", _ => provider.GetRequiredService<SpeechConnectionPage>(), "语音朗读", "选择角色的声音，调整朗读与播放偏好。"));
        return services;
    }

    /// <summary>Owns speech orchestration and audio playback. Providers are selected by host composition.</summary>
    public static IServiceCollection AddSpeechServices(this IServiceCollection services, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISpeechSynthesizer, SpeechSynthesizerRouter>();
        services.AddSingleton<ISpeechAudioPlayer, WpfSpeechAudioPlayer>();
        services.AddSingleton<SpeechPlaybackCoordinator>();
        services.AddSingleton<SpeechSynthesisCoordinator>();
        if (enabled)
        {
            services.AddSingleton<SpeechDesktopPlugin>();
            services.AddSingleton<IFgoPetPlugin>(p => p.GetRequiredService<SpeechDesktopPlugin>());
        }
        return services;
    }
}
