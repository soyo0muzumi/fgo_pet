using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Privacy;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed class UnderstandingPrivacyIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgo-understanding-privacy-" + Guid.NewGuid().ToString("N"));
    [Fact] public async Task Explicit_export_includes_derived_entries_and_conversation_forget_removes_every_revision()
    {
        var database = new RuntimeDatabase(Path.Combine(_root, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(database).Migrate();
        var conversations = new SqliteConversationRepository(database); var now = DateTimeOffset.UtcNow;
        var context = new ContentContextKey("mash", "fixture", "1.0.0", "default", "p1", "k1");
        conversations.CreateConversation("c", "mash", context, now);
        conversations.Append(new("u", "c", "mash", ChatMessageRole.User, "synthetic original", ChatMessageStatus.Completed, now, context, 1));
        var sources = new ConversationSourceReader(conversations); var owner = new UnderstandingService(database, sources);
        var scope = new ConversationScope("mash", null);
        owner.Configure(scope, 0, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true));
        var source = sources.Read(new("c", "mash", null), "u", ConversationSourceKind.User);
        owner.Apply(owner.Begin(scope, "candidate", [source]), [new("derived", UnderstandingDomain.User, UnderstandingKind.Interpretation, "user", "synthetic interpretation", "only fixture", [source])]);
        owner.Review(scope, "approve", "derived", 1, UnderstandingAction.Approve);
        var exportPath = Path.Combine(_root, "export.zip");
        await new UserDataExportService(database, [owner]).ExportAsync(exportPath, default);
        using (var archive = ZipFile.OpenRead(exportPath))
        using (var stream = archive.GetEntry("data.json")!.Open())
        using (var json = await JsonDocument.ParseAsync(stream))
        {
            var entry = Assert.Single(json.RootElement.GetProperty("derived_conversation_data")[0].GetProperty("Entries").EnumerateArray());
            Assert.Equal("synthetic interpretation", entry.GetProperty("Content").GetString());
        }
        await new UserDataDeletionService(database, conversations, new SqliteMemoryRepository(database),
            dialogueLifetime: new DialogueContextLifetime(), derivedData: [owner]).DeleteConversationAsync("c", "mash", default);
        Assert.Empty(owner.List(scope, true)); Assert.Empty(owner.List(scope, false)); Assert.Empty(owner.Changes(scope, "derived"));
        Assert.Null(conversations.GetConversation("c", "mash"));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
