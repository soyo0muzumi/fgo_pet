using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Core.Secrets;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Platform.Secrets;
using FgoPet.Plugin.Qq;
using FgoPet.Plugin.Qq.Desktop;
using FgoPet.Plugin.Qq.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Bootstrap;

internal static class QqChannelRegistration
{
    internal static IServiceCollection AddQqChannel(this IServiceCollection services, string storageRoot) => services
        .AddExternalConversationGateway()
        .AddSingleton(provider => new QqStateStore(Path.Combine(storageRoot, "Qq", "channel.protected"),
            provider.GetRequiredService<IProtectedStateProtector>()))
        .AddSingleton(provider => new QqChatPlugin(provider.GetRequiredService<QqStateStore>(),
            provider.GetRequiredService<ICredentialStore>(),
            () => new QqBotTransport(provider.GetRequiredService<ICredentialReader>(), provider.GetRequiredService<TimeProvider>()),
            async token =>
            {
                var binding = await provider.GetRequiredService<IExternalConversationGateway>()
                    .CaptureCurrentBindingAsync(token).ConfigureAwait(false);
                return binding is null ? null : new(binding.RoleId, binding.ConversationId, binding.ProjectId);
            },
            async (binding, text, token) =>
            {
                var result = await provider.GetRequiredService<IExternalConversationGateway>().ExchangeTextAsync(
                    new(binding.RoleId, binding.ConversationId, binding.ProjectId), text, token).ConfigureAwait(false);
                return new(result.Text, result.ErrorCode);
            }, provider.GetRequiredService<TimeProvider>(),
            diagnostic => provider.GetRequiredService<ILogger<QqChatPlugin>>().LogInformation(
                "QQ {Timestamp} {Correlation} {Stage} {ErrorCode}", diagnostic.Timestamp, diagnostic.Correlation,
                diagnostic.Stage, diagnostic.ErrorCode)))
        .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<QqChatPlugin>())
        .AddQqSettings();
}
