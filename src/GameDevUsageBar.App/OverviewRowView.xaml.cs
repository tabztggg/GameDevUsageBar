using System.Windows;
using System.Windows.Controls;
namespace GameDevUsageBar.App;
public partial class OverviewRowView : UserControl
{
 public event RoutedEventHandler? RefreshRequested;
 public event RoutedEventHandler? SetupRequested;
 public event RoutedEventHandler? MoveUpRequested;
 public event RoutedEventHandler? MoveDownRequested;
 public OverviewRowView()=>InitializeComponent();
 private void Refresh_Click(object sender,RoutedEventArgs e)=>RefreshRequested?.Invoke(this,e);
 private void Setup_Click(object sender,RoutedEventArgs e)=>SetupRequested?.Invoke(this,e);
 private void Up_Click(object sender,RoutedEventArgs e)=>MoveUpRequested?.Invoke(this,e);
 private void Down_Click(object sender,RoutedEventArgs e)=>MoveDownRequested?.Invoke(this,e);
 private void Details_Click(object sender,RoutedEventArgs e)=>ExpandedDetails.Visibility=ExpandedDetails.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
 private void Row_SizeChanged(object sender,SizeChangedEventArgs e)
 {
  var compact=ActualWidth<1020;Grid.SetColumn(RowMetrics,compact?0:1);Grid.SetRow(RowMetrics,compact?1:0);Grid.SetColumnSpan(RowMetrics,compact?2:1);RowMetrics.Margin=compact?new Thickness(32,8,0,0):new Thickness(0);
 }
}
