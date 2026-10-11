using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FgoPet.Windows.Tests.Settings;

/// <summary>Opt-in capture of synthetic character fixtures only, never the desktop or a user profile.</summary>
internal static class MarginCharacterCapture
{
    internal static async Task Save(WebView2 web, string scene)
    {
        if (Environment.GetEnvironmentVariable("FGO_PET_MARGIN_CHARACTER_CAPTURE_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        await Task.Delay(300);
        using var file = File.Create(Path.Combine(directory, scene + "-webview.png"));
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        File.AppendAllText(Path.Combine(directory, "character-verification.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, request = "synthetic-character",
                stage = scene, result = "captured", errorCode = (string?)null }) + Environment.NewLine);
    }
}
