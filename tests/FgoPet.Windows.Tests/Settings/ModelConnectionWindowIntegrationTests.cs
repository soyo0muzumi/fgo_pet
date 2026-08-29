using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using FgoPet.App.Providers;
using FgoPet.App.Settings;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class ModelConnectionWindowIntegrationTests
{
    [Fact]
    public void Login_window_shows_provider_model_and_key_controls_without_servant_settings()
    {
        StaRun(() =>
        {
            var settings = new Settings();
            var credentials = new Credentials();
            var catalog = new ProviderCatalog();
            var factory = new ChatProviderFactory(catalog, credentials, new HttpClient());
            var viewModel = new ModelConnectionViewModel(settings, credentials, catalog, factory);
            var window = new ModelConnectionWindow(viewModel);
            try
            {
                Assert.Equal("模型连接", window.Title);
                Assert.NotNull(window.ProviderComboBox);
                Assert.NotNull(window.ApiKeyBox);
                Assert.NotNull(window.ModelTextBox);
                Assert.DoesNotContain("称呼", window.Content.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("角色包", window.Content.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Settings : IAppSettingsStore
    {
        public string Location => "memory";
        public AppSettings Load() => AppSettings.Defaults;
        public void Save(AppSettings settings) { }
    }

    private sealed class Credentials : ICredentialStore, ICredentialReader
    {
        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task DeleteAsync(string target, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
