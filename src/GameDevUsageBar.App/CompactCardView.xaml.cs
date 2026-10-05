using System.Windows;
using System.Windows.Controls;

namespace GameDevUsageBar.App;

public partial class CompactCardView : UserControl
{
    public static readonly DependencyProperty ShowActionsProperty = DependencyProperty.Register(nameof(ShowActions), typeof(bool), typeof(CompactCardView), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowResetProperty = DependencyProperty.Register(nameof(ShowReset), typeof(bool), typeof(CompactCardView), new PropertyMetadata(true));
    public bool ShowReset { get => (bool)GetValue(ShowResetProperty); set => SetValue(ShowResetProperty, value); }
    public bool ShowActions { get => (bool)GetValue(ShowActionsProperty); set => SetValue(ShowActionsProperty, value); }
    public event RoutedEventHandler? RefreshRequested;
    public event RoutedEventHandler? SetupRequested;
    public CompactCardView() => InitializeComponent();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, e);
    private void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke(this, e);
}
