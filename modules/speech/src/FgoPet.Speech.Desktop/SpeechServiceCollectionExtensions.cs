using System;
using System.Net.Http;
using FgoPet.Core.Speech;
using FgoPet.Infrastructure.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.App.Speech;

/// <summary>
/// speech 模块自有的服务注册入口。
/// </summary>
/// <remarks>
/// 宿主（<c>host/DesktopShell</c>）只调用这一个方法，不直接认识本模块的任何一个具体实现类型。
/// 这与四层六模块设计里「模块拥有自己的 DI 组合」一致：speech 的表所有权为 0，
/// 但仍有 7 个需要注册到容器的服务（3 个合成器 + 1 个路由器 + 1 个音频播放器 + 2 个协调器）。
/// <para>
/// 这 7 条对应 <c>host/DesktopShell/Application/ServiceRegistration.cs</c> 里原先散写的
/// 7 行 <c>AddSingleton</c>（见 git HEAD 版 <c>src/FgoPet.App/Bootstrap/ServiceRegistration.cs</c>
/// 第 251–257 行）——迁移时被收拢成本方法一次调用，注册内容保持等价。
/// </para>
/// <para>
/// 注意：<c>SpeechConnectionViewModel</c> 与 <c>SpeechConnectionPage</c> **不在**此注册，
/// 它们的构造依赖宿主提供的 <c>StorageRoot</c> 路径，由宿主自行注册。
/// </para>
/// </remarks>
public static class SpeechServiceCollectionExtensions
{
    /// <summary>注册 speech 模块内部的服务（合成器路由、音频播放与两个协调器）。</summary>
    public static IServiceCollection AddSpeechServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OpenAiCompatibleSpeechSynthesizer>();
        services.AddSingleton<GptSoVitsSpeechSynthesizer>();
        services.AddSingleton(provider => new IndexTtsSpeechSynthesizer(
            new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
            })
            {
                Timeout = TimeSpan.FromMinutes(5),
            }));
        services.AddSingleton<ISpeechSynthesizer, SpeechSynthesizerRouter>();
        services.AddSingleton<ISpeechAudioPlayer, WpfSpeechAudioPlayer>();
        services.AddSingleton<SpeechPlaybackCoordinator>();
        services.AddSingleton<SpeechSynthesisCoordinator>();

        return services;
    }
}
