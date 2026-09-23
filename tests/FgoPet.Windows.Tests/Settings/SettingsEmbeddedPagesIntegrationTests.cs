using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.App.Privacy;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Work.Execution.Settings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class SettingsEmbeddedPagesIntegrationTests
{
    [Fact]
    public void Model_connection_page_hosts_all_controls_inside_the_settings_shell()
    {
        StaRun(() =>
        {
            var catalog = new FgoPet.Infrastructure.Providers.ProviderCatalog();
            var viewModel = new ModelConnectionViewModel(
                new FakeSettingsStore(DialogueSettings.Defaults),
                new FakeCredentials(),
                catalog,
                new FgoPet.App.Providers.ChatProviderFactory(catalog, new FakeCredentials(), new HttpClient()));
            var page = new ModelConnectionPage(viewModel);

            Assert.NotNull(page.ProviderComboBox);
            Assert.NotNull(page.ApiKeyBox);
            Assert.NotNull(page.BaseUrlBox);
            Assert.NotNull(page.ModelTextBox);
            Assert.NotNull(page.ModelPickerToggle);
            Assert.NotNull(page.ModelPickerPopup);
            Assert.NotNull(page.ModelList);
            Assert.NotNull(page.ModelListEmptyState);
            Assert.NotNull(page.RefreshModelsButton);
            Assert.NotNull(page.TestConnectionButton);
            Assert.NotNull(page.SaveButton);
            Assert.NotNull(page.ClearKeyButton);
            Assert.NotNull(page.OfflineButton);
            Assert.Null(page.FindName("AddressSection"));
        });
    }

    [Fact]
    public void Model_connection_provider_selector_renders_display_name()
    {
        StaRun(() =>
        {
            var catalog = new FgoPet.Infrastructure.Providers.ProviderCatalog();
            var credentials = new FakeCredentials();
            var viewModel = new ModelConnectionViewModel(
                new FakeSettingsStore(DialogueSettings.Defaults),
                credentials,
                catalog,
                new FgoPet.App.Providers.ChatProviderFactory(catalog, credentials, new HttpClient()));
            var page = new ModelConnectionPage(viewModel);

            var selectedTemplate = Assert.IsType<DataTemplate>(page.ProviderComboBox.ItemTemplate);
            var selectedLabel = Assert.IsType<TextBlock>(selectedTemplate.LoadContent());
            var displayBinding = System.Windows.Data.BindingOperations.GetBinding(selectedLabel, TextBlock.TextProperty);

            Assert.Equal("DisplayName", displayBinding?.Path.Path);
            Assert.Equal("OpenAI", viewModel.Providers.Single(provider => provider.ProviderId == viewModel.SelectedProviderId).DisplayName);
        });
    }

    [Fact]
    public void Conversation_memory_page_hosts_review_controls_without_a_top_level_window()
    {
        StaRun(() =>
        {
            var viewModel = new MemoryViewModel(new FakeMemoryHolder().Service);
            var page = new ConversationMemoryPage(viewModel);

            Assert.NotNull(page.CandidatesList);
            Assert.NotNull(page.StoredMemoriesList);
            Assert.NotNull(page.CandidatesEmptyState);
            Assert.NotNull(page.StoredMemoriesEmptyState);
            Assert.NotNull(page.MemoryEnabledCheck);
            Assert.NotNull(page.CandidateEditBox);
            Assert.NotNull(page.MemoryEditBox);
            Assert.NotNull(page.RefreshButton);
            Assert.Equal("对话与记忆", ConversationMemoryPage.SectionTitle);
        });
    }

    [Fact]
    public void Privacy_page_keeps_bulk_data_controls_without_a_duplicate_history_list()
    {
        StaRun(() =>
        {
            var viewModel = new MemoryViewModel(new FakeMemoryHolder().Service);
            var page = new PrivacyPage(viewModel);

            Assert.Null(page.FindName("ConversationsList"));
            Assert.Null(page.FindName("ConversationsEmptyState"));
            Assert.NotNull(page.ExportPathBox);
            Assert.NotNull(page.ExportButton);
            Assert.NotNull(page.CreatePrivateBackupButton);
            Assert.NotNull(page.RestorePrivateBackupButton);
            Assert.NotNull(page.PrivateBackupStatusText);
            Assert.Equal("创建私有备份", System.Windows.Automation.AutomationProperties.GetName(page.CreatePrivateBackupButton));
            Assert.Equal("恢复私有备份", System.Windows.Automation.AutomationProperties.GetName(page.RestorePrivateBackupButton));
            Assert.Null(page.FindName("DeleteConversationButton"));
            Assert.NotNull(page.DeleteAllButton);
            Assert.Equal("数据与隐私", PrivacyPage.SectionTitle);
        });
    }

    [Fact]
    public void Delete_all_confirmation_is_presented_by_the_page_before_executing()
    {
        StaRun(() =>
        {
            var confirmed = false;
            var viewModel = new MemoryViewModel(new FakeMemoryHolder().Service);
            var page = new ConversationMemoryPage(viewModel);
            page.DeleteAllRequested += (_, _) => confirmed = true;

            page.RequestDeleteAll();

            Assert.True(confirmed);
        });
    }

    [Fact]
    public void Service_registration_resolves_all_embedded_pages_without_legacy_windows()
    {
        StaRun(() =>
        {
            var services = FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore());
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore());
            using var provider = services.BuildServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();
            var shell = provider.GetRequiredService<SettingsViewModel>();
            try
            {
                shell.Select(SettingsSection.ModelConnection);
                Assert.IsType<ModelConnectionPage>(window.SettingsContent.Content);

                // Memory review is presented inside the "通用与数据" category.
                shell.Select(SettingsSection.ConversationMemory);
                Assert.IsType<PrivacyPage>(window.SettingsContent.Content);

                shell.Select(SettingsSection.Privacy);
                Assert.IsType<PrivacyPage>(window.SettingsContent.Content);

                Assert.Null(typeof(SettingsWindow).Assembly.GetType("FgoPet.App.Settings.ModelConnectionWindow"));
                Assert.Null(typeof(SettingsWindow).Assembly.GetType("FgoPet.App.Memory.MemoryWindow"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Navigation_across_model_memory_privacy_pages_preserves_session_state()
        // One shell survives model → memory → privacy navigation without resetting package selection.
        => StaRun(() =>
        {
            var services = FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<ICharacterSettingsStore>(new FakeCharacterSettingsStore());
            services.AddSingleton<IWorkExecutionSettingsStore>(new FakeWorkSettingsStore());
            using var provider = services.BuildServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();
            var shell = provider.GetRequiredService<SettingsViewModel>();
            try
            {
                shell.OpenPackageCommand.Execute(new PackageDetailRoute("official.mash", "玛修"));
                shell.Select(SettingsSection.ModelConnection);
                var modelPage = Assert.IsType<ModelConnectionPage>(window.SettingsContent.Content);
                modelPage.BaseUrlBox.Text = "session-preserved-base-url";

                shell.Select(SettingsSection.ConversationMemory);
                shell.Select(SettingsSection.ModelConnection);

                Assert.Equal("session-preserved-base-url", modelPage.BaseUrlBox.Text);
                Assert.Equal(new PackageDetailRoute("official.mash", "玛修"), shell.PackageDetail);
            }
            finally
            {
                window.Hide();
            }
        });

    private static void StaRun(Action action) => StaRunner.Run(action);

    private sealed class FakeSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    {
        public DialogueSettings Load() => initial;
        public void Save(DialogueSettings settings) { }
    }

    private sealed class FakeCharacterSettingsStore : ICharacterSettingsStore
    {
        public CharacterSettings Load() => CharacterSettings.Defaults;
        public void Save(CharacterSettings settings) { }
    }

    private sealed class FakeWorkSettingsStore : IWorkExecutionSettingsStore
    {
        public WorkExecutionSettings Load() => WorkExecutionSettings.Defaults;
        public void Save(WorkExecutionSettings settings) { }
    }

    private sealed class FakeCredentials : FgoPet.Infrastructure.Secrets.ICredentialStore, FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task DeleteAsync(string target, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FakeMemoryHolder
    {
        public MemoryCandidateService Service { get; } =
            new(new SqliteMemoryRepository(new RuntimeDatabase(":memory:")), TimeProvider.System);
    }
}
