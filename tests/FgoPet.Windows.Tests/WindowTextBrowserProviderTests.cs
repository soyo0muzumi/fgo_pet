using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using FgoPet.Platform.WindowText;
using FgoPet.Platform.Windows.WindowText;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.Windows.Tests;

public sealed class WindowTextBrowserProviderTests(ITestOutputHelper output)
{
    [Fact]
    public Task Owned_offline_Chromium_page_exposes_body_without_password_or_hidden_text() =>
        StaRunner.RunAsync(async () =>
        {
            const string firstParagraph = "合成浏览器正文第一段：苹果三个。";
            const string secondParagraph = "合成浏览器正文第二段：橙子五个。";
            const string password = "synthetic-browser-password-never-collect";
            const string hidden = "synthetic-browser-hidden-never-collect";
            var correlation = Guid.NewGuid().ToString("N")[..16];
            var profile = Path.Combine(Path.GetTempPath(), "fgopet-window-text-browser-" + Guid.NewGuid().ToString("N"));
            var web = new WebView2();
            var window = new Window
            {
                Title = "Synthetic offline browser-provider test",
                Content = web,
                Width = 600,
                Height = 440,
                Left = 40,
                Top = 40,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            try
            {
                Record("fixture-created");
                window.Show();
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile,
                    options: new CoreWebView2EnvironmentOptions(
                        "--force-renderer-accessibility --disable-background-networking --disable-component-update --no-first-run"));
                await web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(15));
                // This fixture has no remote assets or navigations. Also deny any accidental requests.
                web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                web.CoreWebView2.WebResourceRequested += (_, args) =>
                {
                    var scheme = GetScheme(args.Request.Uri);
                    if (scheme is "about" or "data") return;
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
                };
                var allowSyntheticNavigation = true;
                web.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    // NavigateToString may report its initial internal data:text/html URI.
                    var isSynthetic = string.Equals(args.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)
                        || args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);
                    var allowed = allowSyntheticNavigation && isSynthetic;
                    if (allowed) allowSyntheticNavigation = false;
                    args.Cancel = !allowed;
                    RecordNavigation("navigation-starting", GetScheme(args.Uri), allowed, null);
                };
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                web.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    RecordNavigation("navigation-completed", null, args.IsSuccess, args.WebErrorStatus.ToString());
                    loaded.TrySetResult(args.IsSuccess);
                };
                web.NavigateToString($$"""
                    <!doctype html><html lang="zh-CN"><head><meta charset="utf-8">
                    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
                    <style>body{font:18px sans-serif} input{display:block;margin:12px}</style></head>
                    <body><main><p>{{firstParagraph}}</p><p><strong>{{secondParagraph}}</strong></p>
                    <input aria-label="合成普通输入框" value="合成普通输入文字">
                    <input aria-label="合成密码输入框" type="password" value="{{password}}">
                    <p style="display:none">{{hidden}}</p></main></body></html>
                    """);
                Assert.True(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal("\"complete\"", await web.CoreWebView2.ExecuteScriptAsync("document.readyState"));
                window.UpdateLayout();
                Record("local-page-ready");
                var handle = new WindowInteropHelper(window).Handle;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await Task.Run(() =>
                {
                    // Authorize only renderer subtrees anchored to a native child of this fixture.
                    var contentRoot = WindowTextReadRoot.Select(handle, deadline.Token);
                    Assert.NotEqual(handle, contentRoot);
                    var root = AutomationElement.FromHandle(contentRoot);
                    RecordProviderShape(root, handle, deadline.Token);
                    var scope = new WindowTextNativeScope(handle, Environment.ProcessId, contentRoot);
                    return WindowTextCollector.Collect(new AutomationWindowTextNode(root, scope),
                        Environment.ProcessId, deadline.Token);
                }).WaitAsync(TimeSpan.FromSeconds(10));
                Record("collection-completed", result.Status, result.Text.Length, result.VisitedNodes);
                Assert.Contains(firstParagraph, result.Text);
                Assert.Contains(secondParagraph, result.Text);
                Assert.DoesNotContain(password, result.Text);
                Assert.DoesNotContain(hidden, result.Text);
                Assert.Equal(WindowTextReadStatus.Complete, result.Status);
            }
            finally
            {
                web.Dispose();
                window.Close();
                Record("fixture-closed");
            }

            static string GetScheme(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                ? parsed.Scheme : "invalid";

            void RecordNavigation(string stage, string? scheme, bool success, string? errorCode) =>
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    requestHash = correlation,
                    stage,
                    scheme,
                    success,
                    errorCode,
                }));

            void RecordProviderShape(AutomationElement root, nint fixtureHandle, CancellationToken token)
            {
                var count = 0;
                Visit(root, 0, -1);

                void Visit(AutomationElement element, int depth, int parent)
                {
                    token.ThrowIfCancellationRequested();
                    if (count >= 100 || depth >= 16) return;
                    var index = count++;
                    var current = element.Current;
                    var sameProcess = current.ProcessId == Environment.ProcessId;
                    var isPassword = current.IsPassword;
                    var nativeHandle = new nint(current.NativeWindowHandle);
                    var hasNativeHandle = nativeHandle != nint.Zero;
                    var nativeWindowIsChildOfFixture = hasNativeHandle && IsChild(fixtureHandle, nativeHandle);
                    var nativeOwnerMatchesNode = hasNativeHandle
                        && GetWindowThreadProcessId(nativeHandle, out var ownerProcess) != 0
                        && ownerProcess == (uint)current.ProcessId;
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        timestampUtc = DateTimeOffset.UtcNow,
                        requestHash = correlation,
                        stage = "provider-shape",
                        index,
                        parent,
                        depth,
                        controlType = current.ControlType.ProgrammaticName,
                        sameProcess,
                        isPassword,
                        isOffscreen = current.IsOffscreen,
                        hasNativeHandle,
                        nativeWindowIsChildOfFixture,
                        nativeOwnerMatchesNode,
                    }));
                    if (!sameProcess || isPassword) return;
                    var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                    while (child is not null && count < 100)
                    {
                        token.ThrowIfCancellationRequested();
                        Visit(child, depth + 1, index);
                        child = TreeWalker.RawViewWalker.GetNextSibling(child);
                    }
                }
            }

            void Record(string stage, WindowTextReadStatus? status = null, int characterCount = 0, int visitedNodes = 0) =>
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    requestHash = correlation,
                    stage,
                    status,
                    characterCount,
                    visitedNodes,
                }));
        }, TimeSpan.FromSeconds(45));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint child);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
