using System.Windows;
using System.Windows.Controls;

namespace GameDevUsageBar.App;

public partial class CompactAccountView : UserControl
{
    public static readonly DependencyProperty ShowActionsProperty=DependencyProperty.Register(nameof(ShowActions),typeof(bool),typeof(CompactAccountView),new PropertyMetadata(false));
    public bool ShowActions {get=>(bool)GetValue(ShowActionsProperty);set=>SetValue(ShowActionsProperty,value);}
    public static readonly RoutedEvent RefreshAccountRequestedEvent=EventManager.RegisterRoutedEvent(nameof(RefreshAccountRequested),RoutingStrategy.Bubble,typeof(EventHandler<AccountSelectionEventArgs>),typeof(CompactAccountView));
    public static readonly RoutedEvent SetupAccountRequestedEvent=EventManager.RegisterRoutedEvent(nameof(SetupAccountRequested),RoutingStrategy.Bubble,typeof(EventHandler<AccountSelectionEventArgs>),typeof(CompactAccountView));
    public event EventHandler<AccountSelectionEventArgs> RefreshAccountRequested {add=>AddHandler(RefreshAccountRequestedEvent,value);remove=>RemoveHandler(RefreshAccountRequestedEvent,value);}
    public event EventHandler<AccountSelectionEventArgs> SetupAccountRequested {add=>AddHandler(SetupAccountRequestedEvent,value);remove=>RemoveHandler(SetupAccountRequestedEvent,value);}
    public CompactAccountView()=>InitializeComponent();
    private void SelectAccount_Click(object sender,RoutedEventArgs e){if(ShowActions && DataContext is CardModel {CanSelectDisplayedAccount:true} model)RaiseEvent(new AccountSelectionEventArgs(AccountPickerView.SelectAccountEvent,model.Id,model.SlotId));}
    private void SwitchAccount_Click(object sender,RoutedEventArgs e){if(ShowActions && DataContext is CardModel {CanSwitchThisCliAccount:true} model)RaiseEvent(new AccountSelectionEventArgs(CompactCardView.SwitchCliAccountEvent,model.Id,model.SlotId));}
    private void Refresh_Click(object sender,RoutedEventArgs e){if(ShowActions && DataContext is CardModel {CanRefresh:true} model)RaiseEvent(new AccountSelectionEventArgs(RefreshAccountRequestedEvent,model.Id,model.SlotId));}
    private void Setup_Click(object sender,RoutedEventArgs e){if(ShowActions && DataContext is CardModel model)RaiseEvent(new AccountSelectionEventArgs(SetupAccountRequestedEvent,model.Id,model.SlotId));}
}
