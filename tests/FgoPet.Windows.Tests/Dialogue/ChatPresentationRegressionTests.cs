using System.IO;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.UiSdk;
using Microsoft.Web.WebView2.Wpf;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatPresentationRegressionTests
{
    [Fact]
    public Task User_copy_control_is_below_bubble_and_inside_narrow_reading_area() =>
        StaRunner.RunAsync(async () =>
        {
            var vm = DialogueWindowIntegrationTests.CreateViewModel();
            vm.Conversation.SetActiveServant("synthetic");
            vm.Conversation.Turns.Add(new("short", ChatMessageRole.User, "Hello"));
            vm.Conversation.Turns.Add(new("long", ChatMessageRole.User, new string('文', 180)));
            vm.Conversation.Turns.Add(new("reply", ChatMessageRole.Assistant, "Synthetic reply"));
            var factory = new ChatWebSurfaceFactory(new ChatWebSessionFactory(vm.Conversation),
                Path.Combine(Path.GetTempPath(), "fgopet-chat-layout-" + Guid.NewGuid().ToString("N")));
            using var window = new DialogueWindow(vm, webFactory: factory)
                { ShowInTaskbar = false, Left = -10000, Top = -10000 };
            try
            {
                window.Show();
                var host = Assert.IsType<WebView2SurfaceHost>(((ContentControl)window.FindName("ChatSurface")).Content);
                await MarginWebIntegrationTests.WaitUntil(() => Task.FromResult(host.State == WebSurfaceState.Ready));
                var web = Assert.IsType<WebView2>(host.Children[0]);
                await MarginWebIntegrationTests.WaitUntil(async () => await web.ExecuteScriptAsync(
                    "!!document.querySelector('button[aria-label=\"展开完整对话\"]')") == "true");
                await web.ExecuteScriptAsync("document.querySelector('button[aria-label=\"展开完整对话\"]').click()");
                await MarginWebIntegrationTests.WaitUntil(async () => await web.ExecuteScriptAsync(
                    "document.querySelectorAll('.message-user > .message-actions button').length===2") == "true");
                foreach (var width in new[] { 520d, 360d })
                {
                    window.Width = width;
                    await MarginWebIntegrationTests.WaitUntil(async () => await web.ExecuteScriptAsync("innerWidth<=" + width) == "true");
                    Assert.Equal("true", await web.ExecuteScriptAsync("""
                        [...document.querySelectorAll('.message-user')].every(m => {
                          const bubble=m.getBoundingClientRect(), actions=m.querySelector('.message-actions').getBoundingClientRect();
                          const next=m.nextElementSibling?.getBoundingClientRect();
                          return actions.top>=bubble.bottom && actions.left>=0 && actions.right<=innerWidth
                            && (!next || next.top>=actions.bottom) && getComputedStyle(m).backgroundColor!=='rgba(0, 0, 0, 0)';
                        })
                        """));
                    Assert.Equal("true", await web.ExecuteScriptAsync(
                        "getComputedStyle(document.querySelector('.message-assistant .message-actions')).position!=='absolute'"));
                }
            }
            finally { window.Dispose(); vm.Conversation.Dispose(); }
        });

}
