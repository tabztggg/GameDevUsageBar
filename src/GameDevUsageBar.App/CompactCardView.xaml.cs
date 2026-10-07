using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GameDevUsageBar.App;

public partial class CompactCardView : UserControl
{
    public static readonly RoutedEvent SwitchCliAccountEvent=EventManager.RegisterRoutedEvent(nameof(SwitchCliAccount),RoutingStrategy.Bubble,typeof(EventHandler<AccountSelectionEventArgs>),typeof(CompactCardView));
    public event EventHandler<AccountSelectionEventArgs> SwitchCliAccount {add=>AddHandler(SwitchCliAccountEvent,value);remove=>RemoveHandler(SwitchCliAccountEvent,value);}
    private ContextMenu? accountMenu;
    public static readonly DependencyProperty ShowActionsProperty = DependencyProperty.Register(nameof(ShowActions), typeof(bool), typeof(CompactCardView), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowResetProperty = DependencyProperty.Register(nameof(ShowReset), typeof(bool), typeof(CompactCardView), new PropertyMetadata(true));
    public bool ShowReset { get => (bool)GetValue(ShowResetProperty); set => SetValue(ShowResetProperty, value); }
    public bool ShowActions { get => (bool)GetValue(ShowActionsProperty); set => SetValue(ShowActionsProperty, value); }
    public event RoutedEventHandler? RefreshRequested;
    public event RoutedEventHandler? SetupRequested;
    public CompactCardView()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=>{if(accountMenu is not null)accountMenu.IsOpen=false;};
        Unloaded+=(_,_)=>{if(accountMenu is not null)accountMenu.IsOpen=false;};
    }
    private void SwitchAccount_Click(object sender,RoutedEventArgs e)
    {
        if(!ShowActions || DataContext is not CardModel model || !model.CanSwitchAccount)return;
        if(accountMenu?.IsOpen==true){accountMenu.IsOpen=false;return;}
        var native=model.SupportsCliAccountSwitch;
        var menu=new ContextMenu{PlacementTarget=SwitchAccountButton,Placement=PlacementMode.Bottom,MinWidth=250,MaxWidth=360};
        if(native){
            var hint=new TextBlock{Text=L.T("Switch CLI login. Close CLI sessions first."),TextWrapping=TextWrapping.Wrap,MaxWidth=300,FontSize=11};
            hint.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");
            menu.Items.Add(new MenuItem{Header=hint,IsEnabled=false});
            menu.Items.Add(new Separator());
        }
        foreach(var account in native?model.CliAccountChoices:model.AccountChoices){
            var content=new StackPanel{Margin=new Thickness(0,2,0,2)};
            var label=new TextBlock{Text=account.Label,FontWeight=FontWeights.SemiBold,FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=300};
            label.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");content.Children.Add(label);
            var detail=string.Join(" · ",new[]{account.Summary,account.Status,account.IsCurrent?L.T("Displayed account"):""}.Where(s=>s.Length>0));
            if(detail.Length>0){var text=new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,FontSize=10,MaxWidth=300};text.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");content.Children.Add(text);}
            var item=new MenuItem{Header=content,Tag=account.SlotId,IsCheckable=true,IsChecked=account.IsCurrent,IsEnabled=native || !account.IsCurrent};
            System.Windows.Automation.AutomationProperties.SetName(item,account.Label+". "+detail);
            item.Click+=(_,_)=>{
                menu.IsOpen=false;
                RaiseEvent(new AccountSelectionEventArgs(native?SwitchCliAccountEvent:AccountPickerView.SelectAccountEvent,model.Id,account.SlotId));
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var manage=new MenuItem{Header=L.T("Manage accounts")};
        manage.Click+=(_,_)=>{menu.IsOpen=false;RaiseEvent(new RoutedEventArgs(AccountPickerView.ManageAccountsEvent,this));};
        menu.Items.Add(manage);
        accountMenu=menu;SwitchAccountButton.ContextMenu=menu;menu.IsOpen=true;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, e);
    private void Setup_Click(object sender, RoutedEventArgs e) => SetupRequested?.Invoke(this, e);
}
