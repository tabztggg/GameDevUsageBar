using System.Windows;
using System.Windows.Controls;

namespace GameDevUsageBar.App;

public partial class OverviewCardView : UserControl
{
    public event RoutedEventHandler? RefreshRequested;
    public event RoutedEventHandler? SetupRequested;
    public OverviewCardView()=>InitializeComponent();
    private void Refresh_Click(object sender,RoutedEventArgs e)=>RefreshRequested?.Invoke(this,e);
    private void Setup_Click(object sender,RoutedEventArgs e)=>SetupRequested?.Invoke(this,e);
}
