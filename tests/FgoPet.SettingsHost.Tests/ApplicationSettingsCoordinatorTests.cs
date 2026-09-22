using System.Text.Json;
using FgoPet.Character.Settings;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
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
    public static TheoryData<string> MalformedPackageSettings => new()
    {
        { "{\"mash_kyrielight\":null}" },
        { "{\"\":{\"show_status\":\"true\"}}" },
        { "{\"invalid servant\":{\"show_status\":\"true\"}}" },
        { $"{{\"{new string('s', 129)}\":{{\"show_status\":\"true\"}}}}" },
        { "{\"mash_kyrielight\":{\"\":\"true\"}}" },
        { "{\"mash_kyrielight\":{\"Invalid key\":\"true\"}}" },
        { $"{{\"mash_kyrielight\":{{\"{new string('s', 65)}\":\"true\"}}}}" },
        { "{\"mash_kyrielight\":{\"show_status\":null}}" },
        { $"{{\"mash_kyrielight\":{{\"greeting\":\"{new string('x', 257)}\"}}}}" },
    };

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

    [Theory]
    [MemberData(nameof(MalformedPackageSettings))]
    public void Malformed_package_settings_are_quarantined_and_live_reads_return_literal_defaults(
        string packageSettings)
    {
        var live = new MemoryDocumentStore($$"""
            {
              "schema_version": 2,
              "scale": 0.75,
              "topmost": false,
              "auto_collapse": false,
              "memory_enabled": false,
              "show_reasoning": false,
              "theme": "modern_gray",
              "package_settings": {{packageSettings}}
            }
            """);
        var coordinator = new ApplicationSettingsCoordinator(live);

        var character = ((ICharacterSettingsStore)coordinator).Load();

        Assert.Null(character.Selection);
        Assert.Equal(0.5, character.Scale);
        Assert.True(character.Topmost);
        Assert.True(character.AutoCollapseExpandedPanel);
        Assert.Empty(character.ServantPreferences);
        Assert.Null(character.UserProfile);
        Assert.Empty(character.PackageSettings);
        Assert.Equal(1, live.QuarantineCount);
        Assert.Null(live.Read());

        Assert.True(((IDialogueSettingsStore)coordinator).Load().ShowReasoning);
        Assert.True(((IMemorySettingsStore)coordinator).Load().Enabled);
        Assert.Equal(AppTheme.FgoLight, ((IThemeSettingsStore)coordinator).Load().Theme);
        Assert.Equal(1, live.QuarantineCount);
    }

    [Fact]
    public void Unknown_theme_falls_back_to_light_without_quarantining_the_live_document()
    {
        const string json = """
            {
              "schema_version": 2,
              "scale": 0.5,
              "topmost": true,
              "auto_collapse": true,
              "theme": "unknown_theme"
            }
            """;
        var live = new MemoryDocumentStore(json);
        var coordinator = new ApplicationSettingsCoordinator(live);

        var theme = ((IThemeSettingsStore)coordinator).Load();

        Assert.Equal(AppTheme.FgoLight, theme.Theme);
        Assert.Equal(0, live.QuarantineCount);
        Assert.Equal(json, live.Read());
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

    [Fact]
    public void Document_facade_export_sanitizes_all_local_speech_paths_without_mutating_live_settings()
    {
        const string gptSoVitsPath = "C:/Users/Task12/Private/gpt-sovits-reference-audio.wav";
        const string indexTtsPathOne = "C:/Users/Task12/Private/index-tts-reference-one.wav";
        const string indexTtsPathTwo = "D:/Task12/Private/index-tts-reference-two.wav";
        var document = new MemoryDocumentStore($$"""
            {
              "schema_version": 2,
              "speech_connection": {
                "enabled": true,
                "provider": "GptSoVits",
                "openai_credential_target": "fgo-pet/speech/openai",
                "gpt_sovits_reference_audio_path": "{{gptSoVitsPath}}",
                "index_tts_voice_id": "voice-two",
                "index_tts_voices": [
                  { "Id": "voice-one", "Name": "Voice One", "AudioPath": "{{indexTtsPathOne}}" },
                  { "Id": "voice-two", "Name": "Voice Two", "AudioPath": "{{indexTtsPathTwo}}" }
                ],
                "auto_read_enabled": true,
                "auto_read_limit": 240,
                "rate": 1.25,
                "volume": 0.75
              }
            }
            """);
        var coordinator = new ApplicationSettingsCoordinator(document);

        var live = ((ISpeechSettingsStore)coordinator).Load().Connection;
        using var exported = JsonDocument.Parse(((IApplicationSettingsDocument)coordinator).Export());
        var speech = exported.RootElement.GetProperty("speech_connection");
        var voices = speech.GetProperty("index_tts_voices").EnumerateArray().ToArray();
        var exportedJson = exported.RootElement.GetRawText();

        Assert.Equal(gptSoVitsPath, live.GptSoVitsReferenceAudioPath);
        Assert.Equal([indexTtsPathOne, indexTtsPathTwo], live.IndexTtsVoices.Select(voice => voice.AudioPath));
        Assert.Equal(string.Empty, speech.GetProperty("gpt_sovits_reference_audio_path").GetString());
        Assert.Equal(2, voices.Length);
        Assert.Equal(["voice-one", "voice-two"], voices.Select(voice => voice.GetProperty(nameof(ReferenceVoice.Id)).GetString()));
        Assert.Equal(["Voice One", "Voice Two"], voices.Select(voice => voice.GetProperty(nameof(ReferenceVoice.Name)).GetString()));
        Assert.All(voices, voice => Assert.Equal(string.Empty, voice.GetProperty(nameof(ReferenceVoice.AudioPath)).GetString()));
        Assert.DoesNotContain(gptSoVitsPath, exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(indexTtsPathOne, exportedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(indexTtsPathTwo, exportedJson, StringComparison.Ordinal);
        Assert.Equal("fgo-pet/speech/openai", speech.GetProperty("openai_credential_target").GetString());
        Assert.Equal("voice-two", speech.GetProperty("index_tts_voice_id").GetString());
        Assert.True(speech.GetProperty("enabled").GetBoolean());
        Assert.True(speech.GetProperty("auto_read_enabled").GetBoolean());
        Assert.Equal(240, speech.GetProperty("auto_read_limit").GetInt32());
        Assert.Equal(1.25, speech.GetProperty("rate").GetDouble());
        Assert.Equal(0.75, speech.GetProperty("volume").GetDouble());
    }

    [Fact]
    public void Backup_export_restore_and_unrelated_save_preserve_metadata_only_IndexTts_voices()
    {
        const string firstPath = "C:/Users/Task12/Private/restored-index-one.wav";
        const string secondPath = "D:/Task12/Private/restored-index-two.wav";
        var source = new ApplicationSettingsCoordinator(new MemoryDocumentStore(null));
        ((ISpeechSettingsStore)source).Save(new SpeechSettings(SpeechConnectionSettings.Defaults with
        {
            Enabled = true,
            Provider = SpeechProviderKind.IndexTts,
            IndexTtsVoiceId = "voice-two",
            IndexTtsVoices =
            [
                new ReferenceVoice("voice-one", "Voice One", firstPath),
                new ReferenceVoice("voice-two", "Voice Two", secondPath),
            ],
        }));

        var backupDocument = ((IApplicationSettingsDocument)source).Export();
        Assert.DoesNotContain(firstPath, backupDocument, StringComparison.Ordinal);
        Assert.DoesNotContain(secondPath, backupDocument, StringComparison.Ordinal);
        ((IApplicationSettingsDocument)source).ValidateForRestore(backupDocument);

        var restoredStore = new MemoryDocumentStore(backupDocument);
        var restored = new ApplicationSettingsCoordinator(restoredStore);
        AssertMetadataOnlyVoices(((ISpeechSettingsStore)restored).Load().Connection);

        ((IMemorySettingsStore)restored).Save(new MemorySettings(false));

        AssertMetadataOnlyVoices(((ISpeechSettingsStore)restored).Load().Connection);
        Assert.DoesNotContain(firstPath, restoredStore.Read(), StringComparison.Ordinal);
        Assert.DoesNotContain(secondPath, restoredStore.Read(), StringComparison.Ordinal);
    }

    private static void AssertMetadataOnlyVoices(SpeechConnectionSettings connection)
    {
        Assert.Equal("voice-two", connection.IndexTtsVoiceId);
        Assert.Equal(["voice-one", "voice-two"], connection.IndexTtsVoices.Select(voice => voice.Id));
        Assert.Equal(["Voice One", "Voice Two"], connection.IndexTtsVoices.Select(voice => voice.Name));
        Assert.All(connection.IndexTtsVoices, voice => Assert.Equal(string.Empty, voice.AudioPath));
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
