using System.Text.Json;
using FgoPet.Character.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Platform.Settings;
using FgoPet.Work.Execution.Settings;
using Xunit;

namespace FgoPet.SettingsHost.Tests;

public sealed class ApplicationSettingsCoordinatorTests
{
    [Fact]
    public void Saving_dialogue_preserves_every_other_section()
    {
        var json = ReadFixture("settings-v2-complete.json");
        var document = new MemoryDocumentStore(json);
        var coordinator = new ApplicationSettingsCoordinator(document);
        var dialogue = (IDialogueSettingsStore)coordinator;

        dialogue.Save(dialogue.Load() with { ShowReasoning = true });

        var decoded = new SettingsDocumentV2Codec().Deserialize(document.Read()!);
        Assert.True(decoded.Dialogue.ShowReasoning);
        Assert.Equal("fixture.package", decoded.Character.Selection!.PackageId);
        Assert.True(decoded.WorkExecution.AgentConnection.Enabled);
    }

    [Fact]
    public void Invalid_live_document_is_quarantined_but_invalid_restore_is_not()
    {
        var live = new MemoryDocumentStore("{");
        var coordinator = new ApplicationSettingsCoordinator(live);

        Assert.Equal(CharacterSettings.Defaults, ((ICharacterSettingsStore)coordinator).Load());
        Assert.Equal(1, live.QuarantineCount);
        Assert.Throws<JsonException>(() => ((IApplicationSettingsDocument)coordinator).ValidateForRestore("{"));
        Assert.Equal(1, live.QuarantineCount);
    }

    [Fact]
    public void Failed_section_write_preserves_the_previous_document()
    {
        var json = ReadFixture("settings-v2-complete.json");
        var document = new MemoryDocumentStore(json) { FailWrites = true };
        var coordinator = new ApplicationSettingsCoordinator(document);
        var memory = (IMemorySettingsStore)coordinator;

        Assert.Throws<IOException>(() => memory.Save(new MemorySettings(true)));
        Assert.Equal(json, document.Read());
    }

    [Fact]
    public async Task Concurrent_section_writes_read_the_latest_document_without_lost_updates()
    {
        var document = new CoordinatedDocumentStore(ReadFixture("settings-v2-complete.json"));
        var coordinator = new ApplicationSettingsCoordinator(document);
        var memory = (IMemorySettingsStore)coordinator;
        var dialogue = (IDialogueSettingsStore)coordinator;

        var first = Task.Run(() => memory.Save(new MemorySettings(false)));
        Assert.True(document.FirstReadEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => dialogue.Save(new DialogueSettings(null, true)));

        if (document.SecondReadEntered.Wait(TimeSpan.FromMilliseconds(500)))
        {
            Assert.True(document.WriteCompleted.Wait(TimeSpan.FromSeconds(5)));
        }

        document.ReleaseFirstRead.Set();
        await Task.WhenAll(first, second);

        var decoded = new SettingsDocumentV2Codec().Deserialize(document.Read()!);
        Assert.False(decoded.Memory.Enabled);
        Assert.True(decoded.Dialogue.ShowReasoning);
    }

    [Fact]
    public void Document_facade_exports_the_live_document_and_reports_restore_pairing()
    {
        var document = new MemoryDocumentStore(ReadFixture("settings-v2-complete.json"));
        var facade = (IApplicationSettingsDocument)new ApplicationSettingsCoordinator(document);

        var exported = new SettingsDocumentV2Codec().Deserialize(facade.Export());
        var metadata = facade.ValidateForRestore(ReadFixture("settings-v2-complete.json"));

        Assert.Equal("memory://settings.json", facade.Location);
        Assert.Equal("fixture.package", exported.Character.Selection!.PackageId);
        Assert.True(metadata.AgentPairingRequired);
    }

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private class MemoryDocumentStore(string? document) : ISettingsDocumentStore
    {
        private string? _document = document;

        public string Location => "memory://settings.json";
        public int QuarantineCount { get; private set; }
        public bool FailWrites { get; init; }
        public virtual string? Read() => _document;

        public virtual void Write(string value)
        {
            if (FailWrites)
            {
                throw new IOException("fixture");
            }

            _document = value;
        }

        public void Quarantine()
        {
            QuarantineCount++;
            _document = null;
        }
    }

    private sealed class CoordinatedDocumentStore(string document) : MemoryDocumentStore(document)
    {
        private int _readCount;

        public ManualResetEventSlim FirstReadEntered { get; } = new(false);
        public ManualResetEventSlim SecondReadEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstRead { get; } = new(false);
        public ManualResetEventSlim WriteCompleted { get; } = new(false);

        public override string? Read()
        {
            var snapshot = base.Read();
            var readCount = Interlocked.Increment(ref _readCount);
            if (readCount == 1)
            {
                FirstReadEntered.Set();
                Assert.True(ReleaseFirstRead.Wait(TimeSpan.FromSeconds(5)));
            }
            else if (readCount == 2)
            {
                SecondReadEntered.Set();
            }

            return snapshot;
        }

        public override void Write(string value)
        {
            base.Write(value);
            WriteCompleted.Set();
        }
    }
}
