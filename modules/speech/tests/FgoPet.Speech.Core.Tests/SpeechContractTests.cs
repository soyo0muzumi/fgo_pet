using FgoPet.Core.Speech;
using Xunit;

namespace FgoPet.Speech.Core.Tests;

public sealed class SpeechContractTests
{
    [Fact]
    public void Normalize_bounds_playback_and_auto_read_settings()
    {
        var settings = new SpeechConnectionSettings
        {
            AutoReadLimit = 999,
            Rate = double.NaN,
            Volume = 2,
        }.Normalize();

        Assert.Equal(300, settings.AutoReadLimit);
        Assert.Equal(1.0, settings.Rate);
        Assert.Equal(1.0, settings.Volume);
    }

    [Fact]
    public void Normalize_preserves_metadata_only_voice_and_selection()
    {
        var voice = new ReferenceVoice("restored-voice", "Restored Voice", string.Empty);

        var normalized = new SpeechConnectionSettings
        {
            IndexTtsVoiceId = voice.Id,
            IndexTtsVoices = [voice],
        }.Normalize();

        Assert.Equal("restored-voice", normalized.IndexTtsVoiceId);
        Assert.Equal(voice, Assert.Single(normalized.IndexTtsVoices));
    }

    [Fact]
    public void Normalize_rejects_null_or_whitespace_voice_paths()
    {
        var normalized = new SpeechConnectionSettings
        {
            IndexTtsVoices =
            [
                new ReferenceVoice("null-path", "Null Path", null!),
                new ReferenceVoice("whitespace-path", "Whitespace Path", "   "),
            ],
        }.Normalize();

        Assert.Empty(normalized.IndexTtsVoices);
    }

    [Fact]
    public void IndexTts_request_rejects_selected_metadata_only_voice()
    {
        var voice = new ReferenceVoice("restored-voice", "Restored Voice", string.Empty);
        var settings = new SpeechConnectionSettings
        {
            Enabled = true,
            Provider = SpeechProviderKind.IndexTts,
            IndexTtsVoiceId = voice.Id,
            IndexTtsVoices = [voice],
        };

        var error = Assert.Throws<SpeechSynthesisException>(() => settings.CreateRequest());

        Assert.Equal(SpeechFailureCategory.Configuration, error.Category);
        Assert.Equal("请先导入并选择参考音色。", error.Message);
    }

    [Fact]
    public void IndexTts_request_keeps_selected_live_voice_path()
    {
        const string path = "C:/voices/live-reference.wav";
        var voice = new ReferenceVoice("live-voice", "Live Voice", path);
        var settings = new SpeechConnectionSettings
        {
            Enabled = true,
            Provider = SpeechProviderKind.IndexTts,
            IndexTtsVoiceId = voice.Id,
            IndexTtsVoices = [voice],
        };

        var request = settings.CreateRequest();

        Assert.Equal(path, request.ReferenceAudioPath);
    }

    [Fact]
    public void Synthesis_result_rejects_non_wave_payloads()
    {
        Assert.Throws<ArgumentException>(() => new SpeechSynthesisResult([1, 2, 3, 4]));
    }
}
