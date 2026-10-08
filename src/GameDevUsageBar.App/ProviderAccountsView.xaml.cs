using System.Windows;
using System.Windows.Controls;
using GameDevUsageBar.App.Presentation;

namespace GameDevUsageBar.App;

// One provider owns this list. The models are keyed by saved slot, never copied
// from the selected provider summary; background updates keep account boundaries.
public partial class ProviderAccountsView : UserControl
{
    private ProviderStateHub? hub;
    private bool subscribed;
    public string? ProviderId {get;private set;}
    public IReadOnlyList<CardModel> AccountModels {get;private set;}=[];
    public static readonly DependencyProperty ShowActionsProperty=DependencyProperty.Register(nameof(ShowActions),typeof(bool),typeof(ProviderAccountsView),new PropertyMetadata(false,(owner,_)=>((ProviderAccountsView)owner).UpdateHint()));
    public bool ShowActions {get=>(bool)GetValue(ShowActionsProperty);set=>SetValue(ShowActionsProperty,value);}
    public ProviderAccountsView()
    {
        InitializeComponent();
        Loaded+=(_,_)=>Subscribe();Unloaded+=(_,_)=>Unsubscribe();
        IsVisibleChanged+=(_,_)=>UpdateHint();
    }
    public void SetProvider(ProviderStateHub providerHub,string providerId)
    {
        if(!ReferenceEquals(hub,providerHub))Unsubscribe();
        hub=providerHub;ProviderId=providerId;
        if(IsLoaded)Subscribe();
        RefreshAccounts();
    }
    private void Subscribe(){if(subscribed || hub is null)return;hub.Changed+=RefreshAccounts;subscribed=true;RefreshAccounts();}
    private void Unsubscribe(){if(subscribed && hub is not null)hub.Changed-=RefreshAccounts;subscribed=false;}
    private void RefreshAccounts()
    {
        DataContext=hub?.Models.FirstOrDefault(model=>model.Id==ProviderId);
        var next=ProviderId is not null && hub is not null?hub.GetAccountModels(ProviderId):[];
        if(!AccountModels.SequenceEqual(next)){AccountModels=next;Accounts.ItemsSource=AccountModels;}
        EmptyAccounts.Visibility=AccountModels.Count==0?Visibility.Visible:Visibility.Collapsed;
        UpdateHint();
    }
    private void UpdateHint(){if(HoverHint is not null)HoverHint.Visibility=ShowActions?Visibility.Collapsed:Visibility.Visible;}
    private void Manage_Click(object sender,RoutedEventArgs e){if(ShowActions)RaiseEvent(new RoutedEventArgs(AccountPickerView.ManageAccountsEvent,this));}
}
