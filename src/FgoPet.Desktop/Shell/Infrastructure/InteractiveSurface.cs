using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FgoPet.App.Windowing;

/// <summary>Classifies controls whose pointer input must never start portrait/window dragging.</summary>
internal static class InteractiveSurface
{
    public static bool Contains(DependencyObject? source)
    {
        for (var current = source; current is not null; current = ParentOf(current))
        {
            if (current is ButtonBase
                or TextBoxBase
                or PasswordBox
                or Selector
                or RangeBase
                or ScrollViewer)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject current) => current switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(current),
        FrameworkContentElement content => content.Parent ?? ContentOperations.GetParent(content),
        _ => null,
    };
}
