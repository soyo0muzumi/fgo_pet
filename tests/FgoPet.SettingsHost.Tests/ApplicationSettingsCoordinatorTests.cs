using System.Text.Json;
using FgoPet.Character.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Platform.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
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
    public void Saving_null_rejects_before_touching_the_live_document()
    {
        Action<ApplicationSettingsCoordinator>[] saveNull =
        [
            coordinator => ((ICharacterSettingsStore)coordinator).Save(null!),
            coordinator => ((IDialogueSettingsStore)coordinator).Save(null!),
            coordinator => ((IMemorySettingsStore)coordinator).Save(null!),
            coordinator => ((IWorkExecutionSettingsStore)coordinator).Save(null!),
            coordinator => ((ISpeechSettingsStore)coordinator).Save(null!),
            coordinator => ((IThemeSettingsStore)coordinator).Save(null!),
        ];

        foreach (var save in saveNull)
        {
            var document = new ObservingDocumentStore("{");
            var coordinator = new ApplicationSettingsCoordinator(document);

            Assert.Throws<ArgumentNullException>(() => save(coordinator));
            Assert.Equal(0, document.ReadCount);
            Assert.Equal(0, document.WriteCount);
            Assert.Equal(0, document.QuarantineCount);
        }
    }

    [Fact]
    public void Concurrent_section_writes_block_before_the_store_and_preserve_both_updates()
    {
        var document = new BlockingFirstReadDocumentStore(ReadFixture("settings-v2-complete.json"));
        var coordinator = new ApplicationSettingsCoordinator(document);
        Exception? firstFailure = null;
        Exception? secondFailure = null;
        using var secondOperationAttempting = new ManualResetEventSlim(false);
        var first = new Thread(() =>
        {
            try
            {
                ((IMemorySettingsStore)coordinator).Save(new MemorySettings(false));
            }
            catch (Exception error)
            {
                firstFailure = error;
            }
        }) { IsBackground = true };
        var second = new Thread(() =>
        {
            secondOperationAttempting.Set();
            try
            {
                ((IDialogueSettingsStore)coordinator).Save(new DialogueSettings(null, true));
            }
            catch (Exception error)
            {
                secondFailure = error;
            }
        }) { IsBackground = true };

        first.Start();
        var firstEntered = document.FirstReadEntered.Wait(TimeSpan.FromSeconds(5));
        var secondStarted = false;
        if (firstEntered)
        {
            second.Start();
            secondStarted = true;
        }

        var secondAttempting = secondStarted
            && secondOperationAttempting.Wait(TimeSpan.FromSeconds(5));
        var contentionObserved = secondAttempting
            && SpinWait.SpinUntil(
                () => IsWaiting(second) || document.ReadCount > 1 || !second.IsAlive,
                TimeSpan.FromSeconds(5));
        var secondBlocked = secondStarted && IsWaiting(second);
        var readsWhileFirstBlocked = document.ReadCount;

        document.ReleaseFirstRead.Set();
        var firstCompleted = first.Join(TimeSpan.FromSeconds(5));
        var secondCompleted = !secondStarted || second.Join(TimeSpan.FromSeconds(5));

        Assert.True(firstEntered, "First write did not enter the document read.");
        Assert.True(secondAttempting, "Second write did not start attempting the coordinator.");
        Assert.True(contentionObserved, "Second write neither blocked nor reached the document store.");
        Assert.True(secondBlocked, "Second write was not blocked by the coordinator lock.");
        Assert.Equal(1, readsWhileFirstBlocked);
        Assert.True(firstCompleted, "First write did not complete after release.");
        Assert.True(secondCompleted, "Second write did not complete after the first write.");
        Assert.Null(firstFailure);
        Assert.Null(secondFailure);

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

    private static bool IsWaiting(Thread thread) =>
        (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;

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

    private sealed class ObservingDocumentStore(string? document) : ISettingsDocumentStore
    {
        public string Location => "memory://settings.json";
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int QuarantineCount { get; private set; }

        public string? Read()
        {
            ReadCount++;
            return document;
        }

        public void Write(string value)
        {
            WriteCount++;
            document = value;
        }

        public void Quarantine()
        {
            QuarantineCount++;
            document = null;
        }
    }

    private sealed class BlockingFirstReadDocumentStore(string document) : MemoryDocumentStore(document)
    {
        private int _readCount;

        public ManualResetEventSlim FirstReadEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseFirstRead { get; } = new(false);
        public int ReadCount => Volatile.Read(ref _readCount);

        public override string? Read()
        {
            var snapshot = base.Read();
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadEntered.Set();
                if (!ReleaseFirstRead.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("First document read was not released by the test.");
                }
            }

            return snapshot;
        }
    }
}
