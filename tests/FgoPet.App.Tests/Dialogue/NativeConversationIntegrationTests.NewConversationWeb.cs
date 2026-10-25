using System.IO;
using System.Windows;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Infrastructure.Providers;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Native_new_conversation_button_sends_first_turn_without_old_history(bool showOldConversation) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("Synthetic new answer"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        SeedOldSession(host);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        if (showOldConversation) model.SetActiveConversation("old-capacity-session");
        var storage = Path.Combine(Path.GetTempPath(), "fgopet-new-session-" + Guid.NewGuid().ToString("N"));
        using var surface = new ChatWebSurfaceFactory(new ChatWebSessionFactory(model), storage)
            .CreateView(new NewConversationWebHost());
        var window = new Window { Content = surface, Width = 500, Height = 700,
            ShowInTaskbar = false, Left = -10000, Top = -10000 };
        try
        {
            window.Show();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            async Task Wait(Func<Task<bool>> condition)
            {
                while (!await condition()) await Task.Delay(20, timeout.Token);
            }
            await Wait(() => Task.FromResult(surface.State == WebSurfaceState.Ready));
            var web = Assert.IsType<WebView2>(surface.Children[0]);
            async Task<bool> Js(string code) => await web.CoreWebView2.ExecuteScriptAsync(code) == "true";
            await Wait(() => Js("!!document.querySelector('button[aria-label=\"对话历史\"]')"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('button[aria-label=\"对话历史\"]').click()");
            await Wait(() => Js("!!document.querySelector('.new-conversation-button')"));
            await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('.new-conversation-button').click()");
            await Wait(() => Task.FromResult(host.Conversations.ReadState("LastActiveConversationId:" + RoleId) is null));
            Assert.True(string.IsNullOrEmpty(model.CurrentConversationId));
            Assert.Empty(model.Turns);
            Assert.Null(host.Conversations.ReadState("LastActiveConversationId:" + RoleId));
            model.InputText = "Hello from a synthetic new conversation";
            await model.SendCommand.ExecuteAsync(null);
            Assert.NotEqual("old-capacity-session", model.CurrentConversationId);
            Assert.NotEmpty(provider.Requests);
            Assert.All(provider.Requests, request => Assert.DoesNotContain("OLD_SESSION_RAW_CANARY",
                ChatRequestInputEnvelope.Write("fixture", request)));
        }
        finally { window.Close(); }
    });

    private sealed class NewConversationWebHost : IChatWebHostActions
    {
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic role", null, [], [],
            Window: new("conversation", false));
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken token)
            => ValueTask.FromResult(new WebSurfaceCommandResult(true));
    }
}
