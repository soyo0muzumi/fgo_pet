using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.App.Tray;
using FgoPet.Character.Settings;
using FgoPet.Core.Geometry;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.UiFoundation.Theming;
using FgoPet.UiSdk;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class SettingsWindowIntegrationTests
{
    [Fact]
    public void Service_registration_uses_one_settings_shell_instance()
    {
        StaRun(() =>
        {
            using var provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), []).BuildServiceProvider();

            var first = provider.GetRequiredService<SettingsWindow>();
            var second = provider.GetRequiredService<SettingsWindow>();

            Assert.Same(first, second);
            first.Close();
        });
    }

    [Fact]
    public void Tray_menu_exposes_settings_without_direct_model_or_library_entries()
    {
        StaRun(() =>
        {
            using var tray = new TrayService();
            var requested = false;
            tray.SettingsRequested += (_, _) => requested = true;

            var texts = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                .Select(item => item.Text).ToArray();
            Assert.Contains("设置", texts);
            Assert.DoesNotContain(texts, text => text is "模型连接" or "从者库与设置");

            var settingsItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                .Single(item => item.Text == "设置");
            settingsItem.PerformClick();

            Assert.True(requested);
        });
    }

    [Fact]
    public void Portrait_menu_exposes_settings_without_direct_model_or_library_entries()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = CreateSettingsWindow(viewModel);
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeCharacterSettingsStore(CharacterSettings.Defaults),
                _ => { });
            var dialogueViewModel = new DialogueWindowViewModel(CreateConversationViewModel());
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                var headers = ui.PortraitMenu.Items.OfType<MenuItem>()
                    .Where(item => item.Header is string)
                    .Select(item => (string)item.Header)
                    .ToArray();
                Assert.Contains("设置", headers);
                Assert.DoesNotContain(headers, text => text is "模型连接" or "从者库与设置");

                var settingsItem = ui.PortraitMenu.Items.OfType<MenuItem>()
                    .Single(item => (string)item.Header == "设置");
                settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(window.IsVisible);
                Assert.False(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Null(window.Owner);
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    [Fact]
    public void Tray_settings_request_opens_the_settings_shell()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = CreateSettingsWindow(viewModel);
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeCharacterSettingsStore(CharacterSettings.Defaults),
                _ => { });
            var dialogueViewModel = new DialogueWindowViewModel(CreateConversationViewModel());
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                var settingsItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "设置");
                settingsItem.PerformClick();

                Assert.True(window.IsVisible);
                Assert.False(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Null(window.Owner);

                window.WindowState = WindowState.Minimized;
                settingsItem.PerformClick();

                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Same(window, ui.GetType().GetField("_settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ui));
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_hide_request_can_show_portrait_before_startup_show()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore(CharacterSettings.Defaults with { Selection = selection }));
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore(WorkExecutionSettings.Defaults));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();
                var showHideItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏");

                showHideItem.PerformClick();
                StaRunner.Pump(portrait.Dispatcher);

                Assert.True(portrait.IsVisible);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_hide_request_marshals_background_callback_to_portrait_dispatcher()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore(CharacterSettings.Defaults with { Selection = selection }));
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore(WorkExecutionSettings.Defaults));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();
                var showHideItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏");

                var callback = Task.Run(showHideItem.PerformClick);
                while (!callback.IsCompleted)
                {
                    portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                Assert.Null(callback.Exception);
                Assert.True(portrait.IsVisible);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_request_uses_owned_portrait_when_lifetime_visibility_state_cannot_show_it()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<FgoPet.App.Lifetime.IAppLifetime, NonDisplayingLifetime>();
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore(CharacterSettings.Defaults with { Selection = selection }));
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore(WorkExecutionSettings.Defaults));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();

                tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏")
                    .PerformClick();

                Assert.True(portrait.IsVisible);
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(portrait).Handle);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_request_reactivates_saved_selection_when_portrait_state_is_cold()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            PortraitSelection? activated = null;
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore(CharacterSettings.Defaults with { Selection = selection }));
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore(WorkExecutionSettings.Defaults));
            services.AddSingleton<PortraitActivation>((requested, _) =>
            {
                activated = requested;
                return Task.CompletedTask;
            });
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();

                tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏")
                    .PerformClick();
                StaRunner.Pump(portrait.Dispatcher);

                Assert.Equal(selection, activated);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Dialogue_settings_request_opens_the_model_connection_section()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = CreateSettingsWindow(viewModel);
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeCharacterSettingsStore(CharacterSettings.Defaults),
                _ => { });
            var conversation = CreateConversationViewModel();
            var dialogueViewModel = new DialogueWindowViewModel(conversation);
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!, conversation,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                dialogueWindow.Show();
                conversation.OpenSettingsCommand.Execute(null);

                Assert.True(window.IsVisible);
                Assert.True(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.ModelConnection, viewModel.SelectedSection);
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    private static ConversationViewModel CreateConversationViewModel()
    {
        var settingsStore = new DialogueSettingsStore(DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        });
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new SqliteConversationRepository(new RuntimeDatabase(":memory:")),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new ConversationViewModel(orchestrator, settingsStore);
    }

    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }

    private static SettingsWindow CreateSettingsWindow(SettingsViewModel vm) => new(vm,
        new SettingsWebRootFactory(new SettingsPageCatalog([]), [],
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgo-settings-shell-tests")));

    [Fact]
    public void Settings_catalog_fences_new_views_after_shutdown_without_constructing_a_page()
    {
        using var stopping = new CancellationTokenSource();
        var created = false;
        var catalog = new SettingsPageCatalog([new SettingsPageViewFactory("sample.settings", _ =>
        {
            created = true;
            return new Border();
        })], stopping.Token);
        Assert.True(catalog.Contains("sample.settings"));
        stopping.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.CreateView("sample.settings"));
        Assert.False(created);
    }

    private static ServantLibraryViewModel CreateLibrary() => new(
        new PackageRepository(),
        new PackageInstaller(),
        new PortraitController(),
        new FakeCharacterSettingsStore(CharacterSettings.Defaults),
        _ => { });

    private static void StaRun(Action action) => StaRunner.Run(action);

    private static Task StaRun(Func<Task> action) => StaRunner.RunAsync(action);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class PackageRepository : IArtPackageRepository
    {
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>
            ([
                new InstalledServant(
                    "preview.mash",
                    "mash_kyrielight",
                    "玛修",
                    "C:\\packs\\mash\\previews\\library.png",
                    "community",
                    [new ServantAppearance("casual", "1.0.0", "C:\\packs\\mash", null)]),
            ]);

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PackCatalog([]));
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class PackageInstaller : IPackInstaller
    {
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken) =>
            Task.FromResult(new PackInstallResult(true, new PackIdentity("preview.mash", "1.0.0"), null));
    }

    private sealed class PortraitController : IPortraitController
    {
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class FakeThemeSettingsStore(ThemeSettings initial) : IThemeSettingsStore
    {
        private ThemeSettings _settings = initial;

        public ThemeSettings Load() => _settings;

        public void Save(ThemeSettings settings) => _settings = settings;
    }

    private sealed class DialogueSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    {
        public DialogueSettings Current { get; private set; } = initial;
        public DialogueSettings Load() => Current;
        public void Save(DialogueSettings settings) => Current = settings;
    }

    private sealed class FakeCharacterSettingsStore(CharacterSettings initial) : ICharacterSettingsStore
    {
        private CharacterSettings _settings = initial;

        public CharacterSettings Load() => _settings;

        public void Save(CharacterSettings settings) => _settings = settings;
    }

    private sealed class FakeWorkSettingsStore(WorkExecutionSettings initial) : IWorkExecutionSettingsStore
    {
        private WorkExecutionSettings _settings = initial;

        public WorkExecutionSettings Load() => _settings;

        public void Save(WorkExecutionSettings settings) => _settings = settings;
    }

    private sealed class NonDisplayingLifetime : FgoPet.App.Lifetime.IAppLifetime
    {
        public bool IsPetVisible => false;
        public void AttachPetWindow(Window window) { }
        public void ShowOrHidePet() { }
        public void RequestNormalExit() { }
        public void Shutdown(int exitCode) { }
    }
}
