using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatWebWindowTests
{
    [Fact]
    public async Task Application_composition_uses_the_registered_Web_surface_and_single_conversation_owner()
    {
        await StaRunner.RunAsync(async () =>
        {
            // WindowsTestState gives this process its own state root and pipe suffix.
            var services = new ServiceCollection();
            FgoPet.App.Bootstrap.ServiceRegistration.AddFgoPet(services, [], includeTodo: false,
                includeFocus: false, includeMemory: false, includeSpeech: false, includeAgentBackend: false);
            await using var provider = services.BuildServiceProvider();
            var window = provider.GetRequiredService<DialogueWindow>();
            var vm = provider.GetRequiredService<DialogueWindowViewModel>();
            var conversation = provider.GetRequiredService<ConversationViewModel>();
            Assert.Same(conversation, vm.Conversation);
            Assert.Same(vm, window.DataContext);
            Assert.IsType<FgoPet.UiSdk.WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
            Assert.Same(provider.GetRequiredService<FgoPet.App.Windowing.WorkspaceWindowCoordinator>(),
                provider.GetRequiredService<FgoPet.UiSdk.IWorkspaceLauncher>());
            window.Dispose();
            conversation.InputText = "装配仍使用原会话";
            Assert.Equal("装配仍使用原会话", vm.Conversation.InputText);
        });
    }

    [Fact]
    public async Task Actual_chat_WebView_retains_surface_draft_and_reading_position_across_hidden_updates()
    {
        await StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            vm.Conversation.SetActiveServant("800100");
            vm.Conversation.InputText = "真实 WebView 草稿（合成）";
            for (var i = 0; i < 20; i++) vm.Conversation.Turns.Add(new("native-" + i, ChatMessageRole.Assistant,
                "第" + i + "条" + new string('文', 400)));
            var factory = new ChatWebSurfaceFactory(new ChatWebSessionFactory(vm.Conversation),
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgopet-chat-runtime-" + Guid.NewGuid().ToString("N")));
            using var window = new DialogueWindow(vm, webFactory: factory)
                { ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Show();
            var surface = Assert.IsType<FgoPet.UiSdk.WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (surface.State == FgoPet.UiSdk.WebSurfaceState.Initializing && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal(FgoPet.UiSdk.WebSurfaceState.Ready, surface.State);
            var web = Assert.IsType<Microsoft.Web.WebView2.Wpf.WebView2>(surface.Children[0]);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var ready = await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#messages article').length === 20");
                if (ready == "true") break;
                await Task.Delay(25);
            }
            Assert.Equal("20", await web.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('#messages article').length"));
            await web.CoreWebView2.ExecuteScriptAsync("var scroll=document.getElementById('message-scroll');scroll.scrollTop=500;scroll.dispatchEvent(new Event('scroll'));document.getElementById('composer').focus();");
            await Task.Delay(100);
            Assert.Equal("500", await web.CoreWebView2.ExecuteScriptAsync("Math.round(scroll.scrollTop)"));
            window.Hide();
            await Task.Delay(100);
            Assert.Equal(FgoPet.UiSdk.WebSurfaceState.Ready, surface.State);
            vm.Conversation.Turns[^1].Text += new string('续', 600);
            await Task.Delay(100);
            window.Show();
            await Task.Delay(200);
            Assert.Same(surface, ((ContentControl)window.FindName("ChatSurface")).Content);
            Assert.Equal("500", await web.CoreWebView2.ExecuteScriptAsync("Math.round(document.getElementById('message-scroll').scrollTop)"));
            Assert.Equal("真实 WebView 草稿（合成）", System.Text.Json.JsonSerializer.Deserialize<string>(
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').value")));
            // The owner clears a sent draft even if the composer keeps keyboard focus.
            vm.Conversation.InputText = "";
            await Task.Delay(100);
            Assert.Equal("", System.Text.Json.JsonSerializer.Deserialize<string>(
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('composer').value")));
            window.Dispose();
            Assert.Equal(FgoPet.UiSdk.WebSurfaceState.Closed, surface.State);
            vm.Conversation.InputText = "原会话仍可继续使用";
            Assert.True(vm.Conversation.CanSend);
            vm.Conversation.Dispose();
        });
    }

    [Fact]
    public void Missing_web_host_shows_a_retryable_error_inside_the_single_chat_surface()
    {
        StaRunner.Run(() =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            var window = new DialogueWindow(vm);
            try
            {
                var surface = Assert.IsType<ContentControl>(window.FindName("ChatSurface"));
                var failure = Assert.IsType<StackPanel>(surface.Content);
                Assert.Contains("聊天页面暂时无法打开", Assert.IsType<TextBlock>(failure.Children[0]).Text);
                Assert.False(Assert.IsType<Button>(failure.Children[1]).IsEnabled);
                Assert.Null(window.FindName("InputBox"));
                Assert.Null(window.FindName("MessagesList"));
                Assert.Null(window.FindName("HistoryList"));
            }
            finally
            {
                window.Dispose();
                vm.Conversation.Dispose();
            }
        });
    }

    [Fact]
    public void Hiding_and_reopening_preserves_the_same_draft_turns_and_active_generation()
    {
        StaRunner.Run(() =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            vm.Conversation.SetActiveServant("800100");
            vm.Conversation.InputText = "未发送草稿";
            vm.Conversation.Turns.Add(new ConversationTurnViewModel("turn-1", ChatMessageRole.Assistant, "已经收到"));
            vm.Conversation.IsStreaming = true;
            var window = new DialogueWindow(vm);
            try
            {
                window.Show();
                window.Hide();

                Assert.False(window.IsVisible);
                Assert.Equal("未发送草稿", vm.Conversation.InputText);
                Assert.True(vm.Conversation.IsStreaming);

                vm.RequestOpen();

                Assert.Same(vm, window.DataContext);
                Assert.True(window.IsVisible);
                Assert.Equal("未发送草稿", vm.Conversation.InputText);
                Assert.Equal("turn-1", Assert.Single(vm.Conversation.Turns).MessageId);
                Assert.True(vm.Conversation.IsStreaming);
            }
            finally
            {
                window.Dispose();
                vm.Conversation.Dispose();
            }
        });
    }

    [Fact]
    public void Close_hides_for_reuse_and_a_lost_focus_does_not_hide_the_chat()
    {
        StaRunner.Run(() =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            var window = new DialogueWindow(vm);
            var other = new Window { Width = 120, Height = 80, ShowInTaskbar = false };
            try
            {
                window.Show();
                window.Close();
                Assert.False(window.IsVisible);

                window.Show();
                other.Show();
                StaRunner.Pump(window.Dispatcher);

                Assert.True(window.IsVisible, "Deactivation must leave the window open until the user hides it.");
                window.Hide();
                other.Close();
            }
            finally
            {
                if (other.IsVisible) other.Close();
                window.Dispose();
                vm.Conversation.Dispose();
            }
        });
    }

    [Fact]
    public void Open_requests_delegate_once_to_the_shell_presentation_owner()
    {
        StaRunner.Run(() =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            var window = new DialogueWindow(vm);
            var presentations = 0;
            try
            {
                window.PresentationRequestedHandler = () => presentations++;

                vm.RequestOpen();

                Assert.Equal(1, presentations);
                Assert.False(window.IsVisible);

                window.PresentationRequestedHandler = () =>
                {
                    presentations++;
                    window.Show();
                };
                vm.RequestOpen();

                Assert.Equal(2, presentations);
                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Dispose();
                vm.Conversation.Dispose();
            }
        });
    }

    [Fact]
    public void Disposing_the_window_detaches_presentation_without_disposing_the_shared_conversation()
    {
        StaRunner.Run(() =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            vm.Conversation.SetActiveServant("800100");
            var window = new DialogueWindow(vm);
            var presentations = 0;
            window.PresentationRequestedHandler = () => presentations++;

            window.Dispose();
            vm.RequestOpen();
            vm.Conversation.InputText = "会话仍由原所有者持有";

            Assert.Equal(0, presentations);
            Assert.Equal("会话仍由原所有者持有", vm.Conversation.InputText);
            Assert.True(vm.Conversation.SendCommand.CanExecute(null));

            vm.Conversation.Dispose();
        });
    }
}
