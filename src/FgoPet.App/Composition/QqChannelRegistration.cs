using System.IO;
using System.Security.Cryptography;
using System.Text;
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
        .AddSingleton<QqImageDownloader>()
        .AddSingleton(provider => new QqStateStore(Path.Combine(storageRoot, "Qq", "channel.protected"),
            provider.GetRequiredService<IProtectedStateProtector>()))
        .AddSingleton(provider => new QqChatPlugin(provider.GetRequiredService<QqStateStore>(),
            provider.GetRequiredService<ICredentialStore>(),
            () => new QqBotTransport(provider.GetRequiredService<ICredentialReader>(), provider.GetRequiredService<TimeProvider>()),
            async token =>
            {
                var binding = await provider.GetRequiredService<IIndependentConversationGateway>()
                    .CaptureCurrentScopeAsync(token).ConfigureAwait(false);
                return binding is null ? null : new(binding.RoleId, binding.ConversationId, binding.ProjectId, Independent: true);
            },
            async (binding, text, token) =>
            {
                var result = await provider.GetRequiredService<IExternalConversationGateway>().ExchangeTextAsync(
                    new(binding.RoleId, binding.ConversationId, binding.ProjectId), text, token).ConfigureAwait(false);
                return new(result.Text, result.ErrorCode);
            }, provider.GetRequiredService<TimeProvider>(),
            diagnostic => provider.GetRequiredService<ILogger<QqChatPlugin>>().LogInformation(
                "QQ {Timestamp} {Correlation} {Stage} {ErrorCode}", diagnostic.Timestamp, diagnostic.Correlation,
                diagnostic.Stage, diagnostic.ErrorCode),
            exchangeMessage: async (binding, message, token) =>
            {
                ExternalConversationImage? image = null;
                if (message.Image is { } attachment)
                {
                    var correlation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.MessageId)))[..12];
                    var logger = provider.GetRequiredService<ILogger<QqChatPlugin>>();
                    logger.LogInformation("QQ {Timestamp} {Correlation} {Stage}", DateTimeOffset.UtcNow, correlation, "image_download_started");
                    var downloaded = await provider.GetRequiredService<QqImageDownloader>().DownloadAsync(attachment, token).ConfigureAwait(false);
                    logger.LogInformation("QQ {Timestamp} {Correlation} {Stage} {ErrorCode}", DateTimeOffset.UtcNow, correlation,
                        "image_download_completed", downloaded is null ? "QQ_IMAGE_DOWNLOAD_FAILED" : null);
                    if (downloaded is null) return new("图片下载失败或格式不受支持，请重新发送单张 PNG/JPEG 图片。", "QQ_IMAGE_DOWNLOAD_FAILED");
                    image = new(downloaded.Bytes, downloaded.ContentType);
                }
                var result = await provider.GetRequiredService<IIndependentConversationGateway>().ExchangeAsync(
                    new(binding.RoleId, binding.ConversationId, binding.ProjectId), message.Content, image, token).ConfigureAwait(false);
                return new(result.Text, result.ErrorCode);
            }))
        .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<QqChatPlugin>())
        .AddQqSettings();
}
