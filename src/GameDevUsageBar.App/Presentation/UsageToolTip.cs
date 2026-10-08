using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GameDevUsageBar.App.Presentation;
public static class UsageToolTip
{
    public static ToolTip Create(ProviderStateHub hub,string providerId)
    {
        var view=new ProviderAccountsView {ShowActions=false};view.SetProvider(hub,providerId);
        var scroll=new ScrollViewer {Content=view,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,MaxHeight=Math.Min(680,SystemParameters.WorkArea.Height-24)};
        var border=new Border {Child=scroll,Padding=new Thickness(12),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)};
        border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");border.SetResourceReference(Border.BorderBrushProperty,"EdgeBrush");
        return new ToolTip {Content=border,Width=Math.Min(500,SystemParameters.WorkArea.Width-16),Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Placement=PlacementMode.Bottom,HorizontalOffset=-8,VerticalOffset=5,HasDropShadow=false,Focusable=false};
    }
    public static ToolTip Create(CardModel model) => new()
    {
        Content = new CompactCardView { DataContext = model, Width = 320, ShowActions = false, ShowReset = true },
        Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Placement = PlacementMode.Bottom,
        HorizontalOffset = -8, VerticalOffset = 6, HasDropShadow = false,
        MaxWidth = 322, Focusable = false
    };
}
