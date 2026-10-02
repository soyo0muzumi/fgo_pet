using System.IO;
using System.Windows;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.Windows.Tests.Shell;

public sealed class WebView2SurfaceHostTests
{
    [Fact]
    public async Task Local_fake_page_reaches_ready_without_an_external_navigation()
    {
        await StaRunner.RunAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "fgopet-web-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "index.html"),
                "<html><body>fixture<script>window.chrome.webview.postMessage({type:'ready'});window.chrome.webview.postMessage({type:'ping',requestId:'req-1',payload:{}});</script></body></html>");
            var policy = new WebSurfacePolicy("https://fixture.fgopet.invalid/index.html", ["ping"]);
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new WebView2SurfaceHost(policy, root, Path.Combine(root, "profile"),
                (message, _) =>
                {
                    received.TrySetResult(message.Type);
                    return ValueTask.FromResult(new WebSurfaceCommandResult(true));
                });
            var owner = new Window { Width = 320, Height = 400, Content = host,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            owner.Show();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (host.State == WebSurfaceState.Initializing && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.Equal(WebSurfaceState.Ready, host.State);
            Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            host.SetThemeVersion(1, new Dictionary<string, string> { ["--accent"] = "#123456" });
            var ackDeadline = DateTime.UtcNow.AddSeconds(4);
            while (!host.ThemeAckTimedOut && DateTime.UtcNow < ackDeadline)
                await Task.Delay(25);
            Assert.True(host.ThemeAckTimedOut);
            owner.Close();
            host.Dispose();
            Assert.Equal(WebSurfaceState.Closed, host.State);
        });
    }

    [Fact]
    public async Task Missing_local_assets_fail_without_crashing_the_window_and_dispose_is_final()
    {
        await StaRunner.RunAsync(async () =>
        {
            var policy = new WebSurfacePolicy("https://fixture.fgopet.invalid/index.html", ["ping"]);
            using var host = new WebView2SurfaceHost(policy,
                Path.Combine(Path.GetTempPath(), "missing-ui-" + Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(), "fgopet-webview-test-profile"),
                (_, _) => ValueTask.FromResult(new WebSurfaceCommandResult(true)));

            await host.InitializeAsync();

            Assert.Equal(WebSurfaceState.Failed, host.State);
            Assert.Equal("WEB_ASSETS_MISSING", host.ErrorCode);
            host.Dispose();
            Assert.Equal(WebSurfaceState.Closed, host.State);
        });
    }
}
