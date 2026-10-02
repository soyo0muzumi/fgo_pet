using System.IO;
using System.IO.Compression;
using FgoPet.App.Memory;
using FgoPet.App.Privacy;
using FgoPet.Infrastructure.Persistence;
using FgoPet.App.Bootstrap;
using FgoPet.Character.Settings;
using FgoPet.Core.Agents;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Agents;
using FgoPet.Memory.Settings;
using FgoPet.SettingsHost;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FgoPet.Extensibility;
using FgoPet.Kernel.Conversation;
using FgoPet.App.Lifetime;
using FgoPet.UiSdk;
using FgoPet.App.Portraits.Live2D;
using FgoPet.Kernel.Presentation;
using Xunit;

namespace FgoPet.App.Tests.Bootstrap;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public async Task Application_composition_and_smoke_startup_work_without_the_Todo_capability()
    {
        var lifetime = new SmokeLifetime();
        var services = new ServiceCollection().AddFgoPet([], includeTodo: false, includeFocus: false, includeMemory: false, includeSpeech: false, includeAgentBackend: false);
        services.RemoveAll<IAppLifetime>();
        services.AddSingleton<IAppLifetime>(lifetime);
        await using var provider = services.BuildServiceProvider();
        Assert.Equal(new[] { "firstparty.content", "firstparty.portrait-static" },
            provider.GetRequiredService<PluginCatalog>().Plugins.Select(plugin => plugin.Manifest.Id));
        var portrait = Assert.IsType<Live2DPortraitController>(provider.GetRequiredService<IPortraitBackend>());
        Assert.Same(portrait, provider.GetRequiredService<IPortraitSurface>());
        Assert.True((await provider.GetRequiredService<PluginRuntime>().StartAsync(default)).Succeeded);
        Assert.Empty(provider.GetRequiredService<ConversationCapabilityRouter>().Tools);
        Assert.Empty(provider.GetRequiredService<IWorkspaceCatalog>().Workspaces);
        Assert.Empty(provider.GetRequiredService<ITransientSurfaceCatalog>().TransientSurfaces);
        var settings = provider.GetRequiredService<SettingsPageCatalog>();
        Assert.True(settings.Contains("Privacy"));
        Assert.True(settings.Contains("ModelConnection"));
        Assert.False(settings.Contains("Speech"));
        Assert.False(settings.Contains("ConversationMemory"));
        Assert.False(settings.Contains("AgentConnection"));
        Assert.Empty(settings.Search("语音"));
        Assert.Empty(settings.Search("Agent"));
        Assert.Empty(settings.Search("记忆"));
        Assert.Equal("ModelConnection", Assert.Single(settings.Search("聊天")).Id);
        Assert.Equal(["开始使用", "能力", "管理"], settings.Search(null)
            .Select(page => page.Group).Distinct());
        Assert.DoesNotContain(provider.GetServices<ISettingsWebPage>(), page =>
            page.SettingsPageId is "Speech" or "ConversationMemory" or "AgentConnection");
        Assert.Null(provider.GetService<FgoPet.App.Services.TodoApplicationService>());
        await provider.GetRequiredService<AppStartup>().StartAsync(["--smoke-test"]);
        Assert.Equal(0, lifetime.ExitCode);
    }

    [Fact]
    public async Task Focus_can_be_removed_while_Todo_and_generic_conversation_remain_active()
    {
        var services = new ServiceCollection().AddFgoPet([], includeFocus: false);
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<FgoPet.App.Focus.IFocusSessionService>());
        var catalog = provider.GetRequiredService<PluginCatalog>();
        Assert.DoesNotContain(catalog.Plugins, entry => entry.Manifest.Id == "firstparty.focus");
        Assert.Contains(catalog.Plugins, entry => entry.Manifest.Id == "firstparty.todo");
        Assert.True((await provider.GetRequiredService<PluginRuntime>().StartAsync(default)).Succeeded);
        Assert.NotEmpty(provider.GetRequiredService<ConversationCapabilityRouter>().Tools);
        Assert.Single(provider.GetRequiredService<IWorkspaceCatalog>().Workspaces);
        Assert.Contains(provider.GetRequiredService<ITransientSurfaceCatalog>().TransientSurfaces,
            surface => surface.Id == "todo.peek");
    }

    [Fact]
    public async Task Memory_can_be_removed_without_changing_other_capability_registration()
    {
        await using var provider = new ServiceCollection().AddFgoPet([], includeMemory: false).BuildServiceProvider();
        var catalog = provider.GetRequiredService<PluginCatalog>();
        Assert.DoesNotContain(catalog.Plugins, entry => entry.Manifest.Id == "firstparty.memory");
        Assert.Contains(catalog.Plugins, entry => entry.Manifest.Id == "firstparty.todo");
        Assert.Contains(catalog.Plugins, entry => entry.Manifest.Id == "firstparty.focus");
        Assert.True((await provider.GetRequiredService<PluginRuntime>().StartAsync(default)).Succeeded);
        Assert.NotNull(provider.GetRequiredService<FgoPet.App.Dialogue.ConversationOrchestrator>());
        Assert.NotEmpty(provider.GetRequiredService<ConversationCapabilityRouter>().Tools);
        await provider.GetRequiredService<PluginRuntime>().StopAsync();
    }

    [Fact]
    public async Task Removing_IndexTts_keeps_other_speech_providers_and_capabilities_available()
    {
        await using var provider = new ServiceCollection().AddFgoPet([], includeIndexTts: false).BuildServiceProvider();
        var providers = provider.GetServices<FgoPet.Core.Speech.ISpeechProvider>().ToArray();
        Assert.Single(providers);
        Assert.DoesNotContain(providers, item => item.Provider == FgoPet.Core.Speech.SpeechProviderKind.IndexTts);
        Assert.Contains(providers, item => item.Provider == FgoPet.Core.Speech.SpeechProviderKind.OpenAiCompatible);
        Assert.DoesNotContain(providers, item => item.Provider.ToString() == "GptSoVits");
        Assert.True((await provider.GetRequiredService<PluginRuntime>().StartAsync(default)).Succeeded);
        Assert.Contains(provider.GetRequiredService<PluginCatalog>().Plugins, item => item.Manifest.Id == "firstparty.speech");
        await provider.GetRequiredService<PluginRuntime>().StopAsync();
    }

    [Fact]
    public void Registers_the_agent_gateway_and_projection_services()
    {
        var services = new ServiceCollection();
        services.AddFgoPet(Array.Empty<string>());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAgentGateway));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AgentEventProjector));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(AgentReconnectService));
    }

    [Fact]
    public void Settings_section_ports_and_document_facade_alias_one_coordinator_singleton()
    {
        using var provider = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ApplicationSettingsCoordinator>();

        Assert.Same(coordinator, provider.GetRequiredService<IApplicationSettingsDocument>());
        Assert.Same(coordinator, provider.GetRequiredService<ICharacterSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IDialogueSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IMemorySettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IWorkExecutionSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<ISpeechSettingsStore>());
        Assert.Same(coordinator, provider.GetRequiredService<IThemeSettingsStore>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Privacy_export_uses_the_registered_service_even_without_Memory_contributions(bool includeMemory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fgo-privacy-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = TestRuntimeDatabase.Create(Path.Combine(directory, "runtime.db"));
            new RuntimeDatabaseMigrator(database).Migrate();
            var services = new ServiceCollection().AddFgoPet([], includeMemory: includeMemory,
                includeFocus: false, includeSpeech: false, includeAgentBackend: false);
            services.RemoveAll<RuntimeDatabase>();
            services.AddSingleton(database);
            await using var provider = services.BuildServiceProvider();
            var model = provider.GetRequiredService<MemoryViewModel>();
            model.ExportPath = Path.Combine(directory, "export.zip");

            await model.ExportCommand.ExecuteAsync(null);

            Assert.True(File.Exists(model.ExportPath), model.StatusText);
            using var archive = ZipFile.OpenRead(model.ExportPath);
            Assert.NotNull(archive.GetEntry("data.json"));
            Assert.Same(provider.GetRequiredService<UserDataDeletionService>(), provider.GetRequiredService<IUserDataDeleter>());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private sealed class SmokeLifetime : IAppLifetime
    {
        public int? ExitCode { get; private set; }
        public bool IsPetVisible => false;
        public void Shutdown(int exitCode) => ExitCode = exitCode;
        public void RequestNormalExit() => Shutdown(0);
        public void ShowOrHidePet() { }
        public void AttachPetWindow(System.Windows.Window window) { }
    }
}
