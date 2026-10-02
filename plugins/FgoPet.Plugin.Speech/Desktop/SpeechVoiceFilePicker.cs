using Microsoft.Win32;

namespace FgoPet.App.Speech;

/// <summary>Selects a WAV file on the host. The Web surface never supplies a path.</summary>
public interface ISpeechVoiceFilePicker
{
    /// <summary>Opens the host-native file picker restricted to WAV. Returns the chosen path or null if cancelled.</summary>
    ValueTask<string?> PickWavFileAsync(CancellationToken cancellationToken);
}

/// <summary>Default host picker using the WPF <see cref="OpenFileDialog"/>.</summary>
public sealed class WpfSpeechVoiceFilePicker : ISpeechVoiceFilePicker
{
    public ValueTask<string?> PickWavFileAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "WAV 音频|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };
        var result = dialog.ShowDialog();
        return new ValueTask<string?>(result == true ? dialog.FileName : null);
    }
}
