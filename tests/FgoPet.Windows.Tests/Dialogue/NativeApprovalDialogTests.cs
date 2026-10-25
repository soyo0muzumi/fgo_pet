using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class NativeApprovalDialogTests
{
    [Fact]
    public Task Native_dialog_has_full_inert_details_no_default_allow_and_close_is_cancel() => StaRunner.RunAsync(() =>
    {
        const string summary = "Synthetic reminder: take a short break\nScheduled for tomorrow at 09:00 UTC\nReminder: synthetic-id · version 17\nThis reminder will no longer notify you.";
        var details = "<script>alert('synthetic')</script>\n" + new string('文', 20000) + "\nEND: version 17 UTC";
        var dialog = new NativeApprovalDialog(new("run", "request", 3, DateTimeOffset.UtcNow.AddMinutes(5),
            "reminder.cancel", details, false, "") { ActionTitle = "Cancel reminder", PlainTextSummary = summary });
        Assert.Equal(details, dialog.Details.Text); Assert.True(dialog.Details.IsReadOnly);
        Assert.Equal(summary, dialog.Summary.Text);
        Assert.False(dialog.DetailsSection.IsExpanded);
        Assert.True(dialog.DenyButton.IsDefault); Assert.False(dialog.AllowButton.IsDefault);
        Assert.True(dialog.CancelButton.IsCancel); Assert.Equal(ChatApprovalDecision.Cancel, dialog.Decision);
        var evidence = Environment.GetEnvironmentVariable("FGO_PET_UI_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence); dialog.ShowInTaskbar = false; dialog.Left = dialog.Top = -10000;
            dialog.Show(); dialog.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(dialog); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(evidence, "native-approval-synthetic.png")); encoder.Save(stream);
        }
        dialog.Close(); Assert.Equal(ChatApprovalDecision.Cancel, dialog.Decision);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Missing_summary_falls_back_to_all_frozen_details() => StaRunner.RunAsync(() =>
    {
        var dialog = new NativeApprovalDialog(new("run", "request", 3, DateTimeOffset.UtcNow.AddMinutes(5),
            "unknown", "All original details", false, ""));
        Assert.Equal("All original details", dialog.Details.Text);
        Assert.True(dialog.DetailsSection.IsExpanded);
        Assert.True(dialog.DenyButton.IsDefault);
        dialog.Close(); return Task.CompletedTask;
    });
}
