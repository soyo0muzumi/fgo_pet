using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace FgoPet.Windows.Tests.Theming;

public sealed class ChatButtonTemplateTests
{
    [Fact]
    public void Borderless_button_keeps_a_visible_keyboard_focus_indicator()
    {
        StaRunner.Run(() =>
        {
            var button = new Button { Content = "查看", BorderThickness = new Thickness(0) };
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/UiFoundation/ThemeTokens.xaml", UriKind.Relative),
            });
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Themes/FgoLight.xaml", UriKind.Relative),
            });
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Ui/Shell/ChatControls.xaml", UriKind.Relative),
            });
            button.Style = (Style)button.FindResource("ChatTextButton");
            var window = new Window { Content = button, Width = 180, Height = 100 };
            try
            {
                window.Show();
                button.Focus();
                StaRunner.Pump();
                Assert.True(button.IsKeyboardFocused);
                var outline = Assert.IsType<Border>(button.Template.FindName("FocusOutline", button));
                Assert.Equal(Visibility.Visible, outline.Visibility);
                Assert.Equal(new Thickness(1), outline.BorderThickness);
                Assert.True(Assert.IsType<SolidColorBrush>(outline.BorderBrush).Color.A > 0);
                Assert.False(outline.IsHitTestVisible);
                Assert.Equal(new Thickness(0), Assert.IsType<Border>(button.Template.FindName("Chrome", button)).BorderThickness);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("FgoLight")]
    [InlineData("ModernGray")]
    public void Chat_button_honors_consumer_border_thickness_in_the_real_template(string theme)
    {
        StaRunner.Run(() =>
        {
            var button = new Button { Content = "查看", BorderThickness = new Thickness(0) };
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/UiFoundation/ThemeTokens.xaml", UriKind.Relative),
            });
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/FgoPet.App;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            button.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Ui/Shell/ChatControls.xaml", UriKind.Relative),
            });
            button.Style = (Style)button.FindResource("ChatTextButton");
            button.ApplyTemplate();
            var chrome = Assert.IsType<Border>(button.Template.FindName("Chrome", button));

            Assert.Equal(new Thickness(0), chrome.BorderThickness);
            button.BorderThickness = new Thickness(2);
            Assert.Equal(new Thickness(2), chrome.BorderThickness);
        });
    }
}
