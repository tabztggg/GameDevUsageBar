using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GameDevUsageBar.App;

public sealed class AccountSelectionEventArgs(RoutedEvent routedEvent,string providerId,Guid slotId) : RoutedEventArgs(routedEvent)
{
    public string ProviderId {get;}=providerId;
    public Guid SlotId {get;}=slotId;
    protected override void InvokeEventHandler(Delegate handler,object target)=>((EventHandler<AccountSelectionEventArgs>)handler)(target,this);
}

// Selection changes the account displayed and queried by the bar. Hosts own
// persistence, management, and the separate explicit CLI-login operation.
public partial class AccountPickerView : UserControl
{
    public static readonly RoutedEvent SelectAccountEvent=EventManager.RegisterRoutedEvent(nameof(SelectAccount),RoutingStrategy.Bubble,typeof(EventHandler<AccountSelectionEventArgs>),typeof(AccountPickerView));
    public static readonly RoutedEvent ManageAccountsEvent=EventManager.RegisterRoutedEvent(nameof(ManageAccounts),RoutingStrategy.Bubble,typeof(RoutedEventHandler),typeof(AccountPickerView));
    public event EventHandler<AccountSelectionEventArgs> SelectAccount {add=>AddHandler(SelectAccountEvent,value);remove=>RemoveHandler(SelectAccountEvent,value);}
    public event RoutedEventHandler ManageAccounts {add=>AddHandler(ManageAccountsEvent,value);remove=>RemoveHandler(ManageAccountsEvent,value);}
    private ContextMenu? menu;
    public AccountPickerView()
    {
        InitializeComponent();
        DataContextChanged+=(_,_)=> {if(menu is not null)menu.IsOpen=false;};
        Unloaded+=(_,_)=> {if(menu is not null)menu.IsOpen=false;};
    }
    private TextBlock Text(string value,string brush,double size,bool bold=false)
    {
        var text=new TextBlock{Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=300};
        text.SetResourceReference(TextBlock.ForegroundProperty,brush);
        return text;
    }
    private void Picker_Click(object sender,RoutedEventArgs e)
    {
        if(menu?.IsOpen==true){menu.IsOpen=false;return;}
        if(DataContext is not CardModel model){PickerButton.IsChecked=false;return;}
        var next=new ContextMenu{PlacementTarget=PickerButton,Placement=PlacementMode.Bottom,MinWidth=250,MaxWidth=360};
        foreach(var account in model.AccountChoices)
        {
            var content=new StackPanel{Margin=new Thickness(0,2,0,2)};
            content.Children.Add(Text(account.Label,"TextBrush",12,true));
            if(account.Summary.Length>0)
            {
                var summary=Text(account.Summary,"TextBrush",11);summary.TextWrapping=TextWrapping.Wrap;summary.TextTrimming=TextTrimming.None;
                content.Children.Add(summary);
            }
            var status=string.Join(" · ",new[]{account.Status,account.IsCurrent?L.T("Current account"):""}.Where(s=>s.Length>0));
            if(status.Length>0)content.Children.Add(Text(status,"MutedBrush",10));
            var item=new MenuItem{Header=content,IsCheckable=true,IsChecked=account.IsCurrent,IsEnabled=!account.IsCurrent,Tag=account.SlotId};
            System.Windows.Automation.AutomationProperties.SetName(item,string.Join(". ",new[]{account.Label,account.Summary,status}.Where(s=>s.Length>0)));
            item.Click+=(_,_)=>RaiseEvent(new AccountSelectionEventArgs(SelectAccountEvent,model.Id,account.SlotId));
            next.Items.Add(item);
        }
        if(next.Items.Count>0)next.Items.Add(new Separator());
        var manage=new MenuItem{Header=L.T("Manage accounts")};
        manage.Click+=(_,_)=>RaiseEvent(new RoutedEventArgs(ManageAccountsEvent,this));
        next.Items.Add(manage);
        next.Closed+=(_,_)=>PickerButton.IsChecked=false;
        menu=next;PickerButton.ContextMenu=next;PickerButton.IsChecked=true;next.IsOpen=true;
    }
}
