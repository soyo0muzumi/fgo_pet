using System.IO;
using System.Text;
using System.Text.Json;
using FgoPet.App.Speech;
using FgoPet.App.Settings;
using FgoPet.Core.Secrets;
using FgoPet.Core.Speech;
using FgoPet.Speech.Settings;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class SpeechWebPageTests
{
    private static byte[] MakeWaveHeader() => "RIFFxxxxWAVE"u8.ToArray();

    private static string WriteTempFile(string directory, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Get_returns_full_snapshot_and_does_not_write()
    {
        var settings = new FakeSettings();
        var page = new SpeechWebPage(CreateViewModel(settings, new FakeCredentials()), new FakePicker(null));

        var result = await Send(page, "speech.get", "{\"pageId\":\"Speech\"}");

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        var root = document.RootElement;
        Assert.Equal("OpenAiCompatible", root.GetProperty("provider").GetString());
        Assert.Equal("", root.GetProperty("selectedVoiceId").GetString());
        Assert.Equal(2, root.GetProperty("providers").GetArrayLength());
        var providerIds = root.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray();
        Assert.Contains("OpenAiCompatible", providerIds);
        Assert.Contains("IndexTts", providerIds);
        Assert.False(root.GetProperty("isKeySaved").GetBoolean());
        Assert.Equal(32, root.GetProperty("voiceLimit").GetInt32());
        Assert.Equal("", root.GetProperty("errorText").GetString());
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task Get_never_leaks_credential_or_audio_path()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/speech/openai"] = "super-secret-key";
        var voiceDir = Path.Combine(Path.GetTempPath(), "fgo-pet-speech-test", Guid.NewGuid().ToString("N"));
        var vm = CreateViewModel(settings, credentials, voiceDir);
        await vm.InitializeAsync();
        var page = new SpeechWebPage(vm, new FakePicker(null));

        var result = await Send(page, "speech.get", "{\"pageId\":\"Speech\"}");
        var serialized = JsonSerializer.Serialize(result.Payload);
        Assert.DoesNotContain("super-secret-key", serialized);
        Assert.DoesNotContain(voiceDir, serialized);
    }

    [Fact]
    public async Task SetDraft_updates_whitelisted_fields_and_survives_across_sessions()
    {
        var settings = new FakeSettings();
        var vm = CreateViewModel(settings, new FakeCredentials());
        var page = new SpeechWebPage(vm, new FakePicker(null));

        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"enabled\",\"value\":true}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"provider\",\"value\":\"IndexTts\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"openAiBaseUrl\",\"value\":\"https://api.example.com/v1\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"openAiModel\",\"value\":\"tts-1\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"openAiVoice\",\"value\":\"nova\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"apiKey\",\"value\":\"top-secret\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"indexTtsBaseUrl\",\"value\":\"http://127.0.0.1:7860\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"voiceName\",\"value\":\"我的音色\"}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"autoReadEnabled\",\"value\":true}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"doNotDisturb\",\"value\":true}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"autoReadLimit\",\"value\":120}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"rate\",\"value\":1.5}")).Success);
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"volume\",\"value\":0.5}")).Success);

        // apiKey must not echo in snapshot
        var reread = await Send(page, "speech.get", "{\"pageId\":\"Speech\"}");
        var serialized = JsonSerializer.Serialize(reread.Payload);
        Assert.DoesNotContain("top-secret", serialized);

        var session = Assert.IsType<SpeechWebPage>(page.CreateSession());
        Assert.NotSame(page, session);
        var sessionRead = await Send(session, "speech.get", "{\"pageId\":\"Speech\"}");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(sessionRead.Payload));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("IndexTts", root.GetProperty("provider").GetString());
        Assert.Equal("https://api.example.com/v1", root.GetProperty("openAiBaseUrl").GetString());
        Assert.Equal("tts-1", root.GetProperty("openAiModel").GetString());
        Assert.Equal("nova", root.GetProperty("openAiVoice").GetString());
        Assert.Equal("http://127.0.0.1:7860", root.GetProperty("indexTtsBaseUrl").GetString());
        Assert.Equal("我的音色", root.GetProperty("voiceName").GetString());
        Assert.True(root.GetProperty("autoReadEnabled").GetBoolean());
        Assert.True(root.GetProperty("doNotDisturb").GetBoolean());
        Assert.Equal(120, root.GetProperty("autoReadLimit").GetInt32());
        Assert.Equal(1.5, root.GetProperty("rate").GetDouble());
        Assert.Equal(0.5, root.GetProperty("volume").GetDouble());
    }

    [Fact]
    public async Task SetDraft_rejects_unknown_field_extra_property_and_wrong_value_type()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));

        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"other\",\"value\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"openAiBaseUrl\",\"value\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"provider\",\"value\":\"GptSoVits\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"enabled\",\"value\":\"yes\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"enabled\",\"value\":true,\"extra\":1}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT", (await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\"}")).ErrorCode);
    }

    [Fact]
    public async Task SelectVoice_unknown_id_is_invalid_and_known_id_selects()
    {
        var settings = new FakeSettings();
        var vm = CreateViewModel(settings, new FakeCredentials());
        var page = new SpeechWebPage(vm, new FakePicker(null));

        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.selectVoice", "{\"pageId\":\"Speech\",\"voiceId\":\"missing\"}")).ErrorCode);

        vm.Voices.Add(new ReferenceVoice("v1", "音色一", string.Empty));
        var ok = await Send(page, "speech.selectVoice", "{\"pageId\":\"Speech\",\"voiceId\":\"v1\"}");
        Assert.True(ok.Success);
        Assert.NotNull(vm.SelectedVoice);
        Assert.Equal("v1", vm.SelectedVoice!.Id);
    }

    [Fact]
    public async Task Save_commits_draft_without_playing_audio()
    {
        var settings = new FakeSettings();
        var page = new SpeechWebPage(CreateViewModel(settings, new FakeCredentials()), new FakePicker(null));
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"enabled\",\"value\":true}")).Success);

        var save = await Send(page, "speech.save", "{\"pageId\":\"Speech\"}");
        Assert.True(save.Success);
        Assert.NotNull(settings.Saved);
        Assert.True(settings.Saved!.Connection.Enabled);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public async Task Preview_completes_for_openai_without_writing_settings()
    {
        var settings = new FakeSettings();
        var page = new SpeechWebPage(CreateViewModel(settings, new FakeCredentials(), synthesizer: new OkSynthesizer()), new FakePicker(null));

        var preview = await Send(page, "speech.preview", "{\"pageId\":\"Speech\"}");
        Assert.True(preview.Success);
        Assert.Equal(0, settings.SaveCount);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(preview.Payload));
        Assert.Equal("试听完成。", doc.RootElement.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task Preview_saves_pending_credential_but_not_settings_document()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        var page = new SpeechWebPage(CreateViewModel(settings, credentials, synthesizer: new OkSynthesizer()), new FakePicker(null));

        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"apiKey\",\"value\":\"pending-key\"}")).Success);
        var preview = await Send(page, "speech.preview", "{\"pageId\":\"Speech\"}");
        Assert.True(preview.Success);
        Assert.True(credentials.Values.ContainsKey("fgo-pet/speech/openai"));
        Assert.Equal("pending-key", credentials.Values["fgo-pet/speech/openai"]);
        Assert.Equal(0, settings.SaveCount);

        var reread = await Send(page, "speech.get", "{\"pageId\":\"Speech\"}");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(reread.Payload));
        Assert.True(doc.RootElement.GetProperty("isKeySaved").GetBoolean());
    }

    [Fact]
    public async Task Preview_without_index_tts_voice_reports_test_failed()
    {
        var settings = new FakeSettings();
        var page = new SpeechWebPage(CreateViewModel(settings, new FakeCredentials()), new FakePicker(null));
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"provider\",\"value\":\"IndexTts\"}")).Success);

        var preview = await Send(page, "speech.preview", "{\"pageId\":\"Speech\"}");
        Assert.False(preview.Success);
        Assert.Equal("SETTINGS_TEST_FAILED", preview.ErrorCode);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(preview.Payload));
        Assert.Contains("请先导入", doc.RootElement.GetProperty("snapshot").GetProperty("errorText").GetString());
    }

    [Fact]
    public async Task StopPreview_updates_status_without_error()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));
        var result = await Send(page, "speech.stopPreview", "{\"pageId\":\"Speech\"}");
        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        Assert.Contains("已停止", doc.RootElement.GetProperty("statusText").GetString());
    }

    [Fact]
    public async Task ClearKey_removes_credential_and_marks_unsaved()
    {
        var settings = new FakeSettings();
        var credentials = new FakeCredentials();
        credentials.Values["fgo-pet/speech/openai"] = "stored";
        var vm = CreateViewModel(settings, credentials);
        await vm.InitializeAsync();
        var page = new SpeechWebPage(vm, new FakePicker(null));

        var clear = await Send(page, "speech.clearKey", "{\"pageId\":\"Speech\"}");
        Assert.True(clear.Success);
        Assert.False(credentials.Values.ContainsKey("fgo-pet/speech/openai"));
        Assert.False(vm.IsKeySaved);
    }

    [Fact]
    public async Task ImportVoice_with_host_picker_saves_voice_immediately()
    {
        var settings = new FakeSettings();
        var voiceDir = Path.Combine(Path.GetTempPath(), "fgo-pet-speech-test", Guid.NewGuid().ToString("N"));
        var vm = CreateViewModel(settings, new FakeCredentials(), voiceDir);
        var wavPath = WriteTempFile(Path.Combine(Path.GetTempPath(), "fgo-pet-speech-src"), MakeWaveHeader());
        var page = new SpeechWebPage(vm, new FakePicker(wavPath));
        vm.VoiceName = "导入音色";

        var import = await Send(page, "speech.importVoice", "{\"pageId\":\"Speech\"}");
        Assert.True(import.Success);
        Assert.Single(vm.Voices);
        Assert.NotNull(settings.Saved);
        Assert.Single(settings.Saved!.Connection.IndexTtsVoices);
        Assert.Equal("导入音色", settings.Saved.Connection.IndexTtsVoices[0].Name);
    }

    [Fact]
    public async Task ImportVoice_rejects_any_path_sent_from_web()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.importVoice", "{\"pageId\":\"Speech\",\"path\":\"C:\\\\evil.wav\"}")).ErrorCode);
    }

    [Fact]
    public async Task ImportVoice_cancelled_picker_does_not_change_state()
    {
        var settings = new FakeSettings();
        var page = new SpeechWebPage(CreateViewModel(settings, new FakeCredentials()), new FakePicker(null));
        var result = await Send(page, "speech.importVoice", "{\"pageId\":\"Speech\"}");
        Assert.True(result.Success);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task ImportVoice_with_non_wav_reports_test_failed()
    {
        var settings = new FakeSettings();
        var voiceDir = Path.Combine(Path.GetTempPath(), "fgo-pet-speech-test", Guid.NewGuid().ToString("N"));
        var vm = CreateViewModel(settings, new FakeCredentials(), voiceDir);
        vm.VoiceName = "坏文件";
        var badPath = WriteTempFile(Path.Combine(Path.GetTempPath(), "fgo-pet-speech-src"), Encoding.ASCII.GetBytes("this is not a wave file"));
        var page = new SpeechWebPage(vm, new FakePicker(badPath));

        var import = await Send(page, "speech.importVoice", "{\"pageId\":\"Speech\"}");
        Assert.False(import.Success);
        Assert.Equal("SETTINGS_TEST_FAILED", import.ErrorCode);
        Assert.Empty(vm.Voices);
    }

    [Fact]
    public async Task DeleteVoice_removes_voice_and_updates_settings()
    {
        var settings = new FakeSettings();
        var voiceDir = Path.Combine(Path.GetTempPath(), "fgo-pet-speech-test", Guid.NewGuid().ToString("N"));
        var vm = CreateViewModel(settings, new FakeCredentials(), voiceDir);
        var wavPath = WriteTempFile(Path.Combine(Path.GetTempPath(), "fgo-pet-speech-src"), MakeWaveHeader());
        vm.VoiceName = "要删除的音色";
        await vm.ImportVoiceAsync(wavPath);
        var id = vm.Voices[0].Id;

        var page = new SpeechWebPage(vm, new FakePicker(null));
        var delete = await Send(page, "speech.deleteVoice", $"{{\"pageId\":\"Speech\",\"voiceId\":\"{id}\"}}");
        Assert.True(delete.Success);
        Assert.Empty(vm.Voices);
        Assert.NotNull(settings.Saved);
        Assert.Empty(settings.Saved!.Connection.IndexTtsVoices);
        Assert.Equal("", settings.Saved.Connection.IndexTtsVoiceId);
    }

    [Fact]
    public async Task DeleteVoice_unknown_id_is_invalid()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.deleteVoice", "{\"pageId\":\"Speech\",\"voiceId\":\"nope\"}")).ErrorCode);
    }

    [Fact]
    public async Task Unknown_command_returns_unknown_command()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));
        Assert.Equal("SETTINGS_UNKNOWN_COMMAND",
            (await Send(page, "speech.bogus", "{\"pageId\":\"Speech\"}")).ErrorCode);
    }

    [Fact]
    public async Task Page_id_mismatch_or_missing_returns_invalid_input()
    {
        var page = new SpeechWebPage(CreateViewModel(new FakeSettings(), new FakeCredentials()), new FakePicker(null));
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.get", "{\"pageId\":\"Personalization\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.get", "{\"pageId\":\"\"}")).ErrorCode);
        Assert.Equal("SETTINGS_INVALID_INPUT",
            (await Send(page, "speech.get", "null")).ErrorCode);
    }

    [Fact]
    public async Task Concurrent_setDraft_and_save_preserve_both_fields()
    {
        var settings = new FakeSettings();
        var vm = CreateViewModel(settings, new FakeCredentials());
        var page = new SpeechWebPage(vm, new FakePicker(null));
        Assert.True((await Send(page, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"enabled\",\"value\":true}")).Success);

        var tasks = new List<Task>(16);
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var session = page.CreateSession();
                await Send(session, "speech.setDraft", "{\"pageId\":\"Speech\",\"field\":\"provider\",\"value\":\"IndexTts\"}");
            }));
            tasks.Add(Task.Run(async () =>
            {
                var session = page.CreateSession();
                await Send(session, "speech.save", "{\"pageId\":\"Speech\"}");
            }));
        }

        await Task.WhenAll(tasks);
        Assert.NotNull(settings.Saved);
        Assert.True(settings.Saved!.Connection.Enabled);
    }

    private static async Task<WebSurfaceCommandResult> Send(ISettingsWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(new WebSurfaceMessage(command, null, document.RootElement.Clone()), CancellationToken.None);
    }

    private static SpeechConnectionViewModel CreateViewModel(
        ISpeechSettingsStore settings, ICredentialStore credentials, string? voiceDirectory = null,
        ISpeechSynthesizer? synthesizer = null)
    {
        var coordinator = new SpeechPlaybackCoordinator(
            new SpeechSynthesisCoordinator(synthesizer ?? new OkSynthesizer()),
            new NoopAudioPlayer(),
            settings);
        return new SpeechConnectionViewModel(settings, credentials, coordinator, voiceDirectory);
    }

    private sealed class FakeSettings : ISpeechSettingsStore
    {
        public SpeechSettings Current { get; set; } = SpeechSettings.Defaults;
        public SpeechSettings? Saved { get; private set; }
        public int SaveCount { get; private set; }

        public SpeechSettings Load() => Current;
        public void Save(SpeechSettings settings) { Current = settings; Saved = settings; SaveCount++; }
    }

    private sealed class FakeCredentials : ICredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task SaveAsync(string target, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values[target] = secret;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.ContainsKey(target));
        }

        public Task DeleteAsync(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Values.Remove(target);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePicker : ISpeechVoiceFilePicker
    {
        private readonly string? _path;
        public FakePicker(string? path) => _path = path;
        public ValueTask<string?> PickWavFileAsync(CancellationToken cancellationToken) => new(_path);
    }

    private sealed class OkSynthesizer : ISpeechSynthesizer
    {
        public SpeechProviderKind Provider => SpeechProviderKind.OpenAiCompatible;
        public Task<SpeechSynthesisResult> SynthesizeAsync(SpeechSynthesisRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SpeechSynthesisResult(MakeWaveHeader()));
        }
    }

    private sealed class NoopAudioPlayer : ISpeechAudioPlayer
    {
        public Task PlayAsync(SpeechSynthesisResult audio, SpeechPlaybackOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Stop() { }
        public void Dispose() { }
    }
}
