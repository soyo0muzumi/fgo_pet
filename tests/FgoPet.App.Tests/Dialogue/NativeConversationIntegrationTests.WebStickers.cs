using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Authorized_collection_runs_through_command_policy_and_enters_a_later_final_catalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "fgopet-native-web-sticker-" + Guid.NewGuid().ToString("N"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4)));
        using var output = new MemoryStream(); encoder.Save(output);
        var png = "data:image/png;base64," + Convert.ToBase64String(output.ToArray());
        var library = new ManagedStickerLibrary(root, (_, _) => Task.FromResult<string?>(png));
        using var plugin = new WebStickerPlugin(library, true, _ => true, _ => () => true, () => new(true));
        const string role = "mash_kyrielight";
        var key = new ContentContextKey(role, "official.mash", "1", "casual", "1", "1");
        var binding = new ContentBinding(key, new PersonaBundle(role, "official.mash", "1", "1", "synthetic identity", []), [], [], "persona", "knowledge");
        try
        {
            var provider = new ScriptedProvider(ProviderTurn.Call("sticker.add", "collect",
                """{"url":"https://images.example/reaction.png","label":"大笑","intent":"laugh"}"""),
                ProviderTurn.Call("response.finalize", "finish", "{}"), ProviderTurn.Final("收藏好了。"));
            await using (var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(),
                settings: TestSettingsStore.Create(32768), contentResolver: new BoundPersonaResolver(binding), approvalPlugin: plugin,
                composer: new PromptComposer(null, null, ExpressionMode.Off, true, library), stickerCatalog: library))
            {
                var result = await host.Orchestrator.SendAsync(role, "把这张图收藏成表情。", default, new(ProjectId));
                Assert.Equal(ConversationSendStatus.Completed, result.Status);
                Assert.Single(library.ReadCollected());
                Assert.Equal(3, provider.Requests.Count);
                Assert.Null(host.Conversations.LoadMessages(result.ConversationId, role).Last().Sticker);
            }
            var saved = Assert.Single(new ManagedStickerLibrary(root).ReadCollected());
            var raw = JsonSerializer.Serialize(new { text = "这个确实好笑。", emotion = "happy", sticker = new {
                assetId = saved.Id, intent = saved.Intent, utility = "enhance", salience = "high", context = "playful", position = "after" } });
            var nextProvider = new ScriptedProvider(ProviderTurn.Final(raw));
            await using var next = await TestHost.CreateAsync(nextProvider, null, new RecordingObserver(), settings: TestSettingsStore.Create(32768),
                contentResolver: new BoundPersonaResolver(binding), composer: new PromptComposer(null, null, ExpressionMode.Off, true, library), stickerCatalog: library);
            var reply = await next.Orchestrator.SendAsync(role, "哈哈，刚才那张真有意思。", default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, reply.Status);
            Assert.Contains(Assert.Single(nextProvider.Requests).ModelMessages, m => m.Content.Contains(saved.Id, StringComparison.Ordinal));
            Assert.NotNull(await library.LoadAsync(saved.Id, default));
            Assert.Equal("这个确实好笑。", next.Conversations.LoadMessages(reply.ConversationId, role).Last().Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
