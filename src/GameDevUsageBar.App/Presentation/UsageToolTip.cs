using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GameDevUsageBar.App.Presentation;
public static class UsageToolTip
{
    public static ToolTip Create(CardModel model) => new()
    {
        Content = new CompactCardView { DataContext = model, Width = 320, ShowActions = false, ShowReset = true },
        Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Placement = PlacementMode.Bottom,
        HorizontalOffset = -8, VerticalOffset = 6, HasDropShadow = false,
        MaxWidth = 322, Focusable = false
    };
}
