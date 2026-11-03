using FgoPet.App.Dialogue;
using FgoPet.App.Bootstrap;
using FgoPet.App.Main;
using FgoPet.App.Settings;
using FgoPet.App.Tray;
using FgoPet.App.Windowing;
using FgoPet.Core.Geometry;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Xunit;

namespace FgoPet.Windows.Tests.Dialogue;

public sealed class ChatPresentationWindowTests
{
    [Fact]
    public Task Chat_and_owned_preview_do_not_inherit_the_portraits_topmost_setting() => StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        using var chat = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var portrait = new PortraitWindow { ShowInTaskbar = false, Left = -10000, Top = -10000, Topmost = true };
        using var tray = new TrayService();
        using var ui = new DesktopAppUi(tray, null!, new SettingsViewModel(), portrait,
            null!, null!, null!, null!, dialogueWindow: chat, dialogue: vm);
        Window? preview = null;
        try
        {
            portrait.Show();
            ui.ShowDialogueWindow();
            Assert.True(portrait.Topmost);
            Assert.False(chat.Topmost);
            Assert.Null(chat.Owner);
            // ShowInTaskbar=false may supply a framework HWND owner. The companion
            // must not be that owner; actual native topmost flags are checked below.
            Assert.NotEqual(new WindowInteropHelper(portrait).Handle,
                GetWindow(new WindowInteropHelper(chat).Handle, 4));
            preview = new Window { Owner = chat, ShowInTaskbar = false, Width = 120, Height = 120,
                Left = -10000, Top = -10000 };
            preview.Show();
            Assert.Equal(new WindowInteropHelper(chat).Handle,
                GetWindow(new WindowInteropHelper(preview).Handle, 4));
            Assert.True(IsNativeTopmost(portrait));
            Assert.False(IsNativeTopmost(chat));
            Assert.False(IsNativeTopmost(preview));
            preview.Hide();
            chat.Hide();
            ui.ShowDialogueWindow();
            Assert.False(chat.Topmost);
            Assert.False(IsNativeTopmost(chat));
            Assert.True(portrait.Topmost);
        }
        finally { preview?.Close(); chat.Hide(); portrait.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Native_minimum_size_keeps_the_conversation_usable_without_enlarging_the_composer() => StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        Assert.Equal(328, window.MinWidth);
        Assert.Equal(182, window.MinHeight);
        window.Show();
        window.SetPresentation("conversation", false);
        Assert.Equal(Math.Min(328, window.Width), window.MinWidth);
        Assert.Equal(Math.Min(388, window.Height), window.MinHeight);
        window.SetPresentation("composer", false, 160);
        Assert.Equal(Math.Min(328, window.Width), window.MinWidth);
        Assert.Equal(Math.Min(182, window.Height), window.MinHeight);
        window.SetExpanded(false);
        Assert.Equal(Math.Min(328, window.Width), window.MinWidth);
        Assert.Equal(Math.Min(388, window.Height), window.MinHeight);
        window.Hide();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Streaming_height_changes_keep_the_native_bottom_right_and_the_users_width() => StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        window.SetPresentation("composer", false, 160);
        var area = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(window);
        var marginX = Math.Min((area.Width - 1) / 2, (int)Math.Round(16 * dpi.DpiScaleX));
        var marginY = Math.Min((area.Height - 1) / 2, (int)Math.Round(16 * dpi.DpiScaleY));
        var widthPx = Math.Min(area.Width - 2 * marginX, (int)Math.Ceiling(520 * dpi.DpiScaleX));
        var heightPx = (int)Math.Ceiling(window.Height * dpi.DpiScaleY);
        var anchor = new DeviceRect(area.Right - marginX - widthPx, area.Bottom - marginY - heightPx,
            widthPx, heightPx);
        // Simulate a user resizing and positioning the compact window, then
        // deliver several measured heights from a streaming response.
        window.Width = widthPx / dpi.DpiScaleX;
        window.Left = anchor.Left / dpi.DpiScaleX;
        window.Top = anchor.Top / dpi.DpiScaleY;
        TransientSurfacePlacement.ApplyNativeBounds(window, anchor);
        foreach (var height in new[] { 200, 260, 360, 220 })
        {
            window.SetPresentation("composer", false, height);
            Assert.True(TransientSurfacePlacement.TryReadNativeBounds(window, out var actual));
            Assert.InRange(Math.Abs(actual.Right - anchor.Right), 0, 1);
            Assert.InRange(Math.Abs(actual.Bottom - anchor.Bottom), 0, 1);
            Assert.InRange(Math.Abs(actual.Width - widthPx), 0, 1);
            Assert.True(actual.Left >= area.Left + marginX && actual.Top >= area.Top + marginY);
            Assert.True(actual.Right <= area.Right - marginX && actual.Bottom <= area.Bottom - marginY);
            Assert.Equal(height, window.WindowPresentation.ComposerHeight);
        }
        window.Hide();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Repeated_content_height_does_not_recreate_chrome_or_reapply_native_layout() => StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        window.Show();
        window.SetPresentation("composer", false, 220);
        var chrome = WindowChrome.GetWindowChrome(window);
        Assert.NotNull(chrome);
        var layouts = 0;
        window.LayoutRequestedHandler = () => layouts++;
        for (var i = 0; i < 5; i++) window.SetPresentation("composer", false, 220);
        Assert.Equal(0, layouts);
        Assert.Same(chrome, WindowChrome.GetWindowChrome(window));
        Assert.Equal(220, window.WindowPresentation.ComposerHeight);
        window.Hide();
        return Task.CompletedTask;
    });

    private static bool IsNativeTopmost(Window window) =>
        (GetWindowLong(new WindowInteropHelper(window).Handle, -20) & 0x8) != 0;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);

    [Fact]
    public Task Presentation_survives_hide_restore_and_rejects_invalid_geometry() => StaRunner.RunAsync(() =>
    {
        var vm = DialogueWindowIntegrationTests.CreateViewModel();
        using var conversation = vm.Conversation;
        using var window = new DialogueWindow(vm) { ShowInTaskbar = false, Left = -10000, Top = -10000 };
        conversation.InputText = "synthetic unsent draft";
        window.Show();
        Assert.Equal("composer", window.WindowPresentation.View);
        window.SetPresentation("conversation", true);
        Assert.Equal("conversation", window.WindowPresentation.View);
        Assert.True(window.WindowPresentation.Wide);
        Assert.True(double.IsFinite(window.Left) && double.IsFinite(window.Top));
        window.Hide(); window.RestorePresentation();
        Assert.True(window.WindowPresentation.Wide);
        Assert.Equal("synthetic unsent draft", conversation.InputText);
        window.SetPresentation("composer", false, 240);
        Assert.Equal(240, window.WindowPresentation.ComposerHeight);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetPresentation("invalid", false));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetPresentation("composer", false, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.SetPresentation("composer", false, 361));
        window.Hide();
        return Task.CompletedTask;
    });
}
