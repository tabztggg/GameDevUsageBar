using System.Windows;
using System.Windows.Controls;
using GameDevUsageBar.Core;
using GameDevUsageBar.App.Presentation;

namespace GameDevUsageBar.App;

public partial class AccountsWindow : Window
{
    private readonly ApplicationHost host;
    private readonly ProviderStateHub hub;
    private readonly string provider;
    private bool busy,rebinding,closed;
    private Guid? pendingSelection;
    private string feedback="";
    private CardModel? Selected=>AccountList.SelectedItem as CardModel;
    public AccountsWindow(ApplicationHost host,ProviderStateHub hub,string provider)
    {
        this.host=host;this.hub=hub;this.provider=provider;InitializeComponent();
        Height=Math.Min(Height,SystemParameters.WorkArea.Height-30);MinHeight=Math.Min(MinHeight,Height);
        NativeActions.Visibility=provider is "codex" or "claude"?Visibility.Visible:Visibility.Collapsed;
        hub.Changed+=Rebind;L.Changed+=LanguageChanged;Closed+=(_,_)=>{closed=true;hub.Changed-=Rebind;L.Changed-=LanguageChanged;};
        Rebind();LanguageChanged();
    }
    private void LanguageChanged(){Heading.Text=L.T(host.Adapters.Single(a=>a.Definition.Id==provider).Definition.Name)+" · "+L.T("Accounts");Feedback.Text=L.T(feedback);}
    private void Rebind()
    {
        if(closed)return;
        var slot=pendingSelection??Selected?.SlotId??host.GetActiveAccount(provider).SlotId;
        rebinding=true;
        var accounts=hub.GetAccountModels(provider);AccountList.ItemsSource=accounts;
        AccountList.SelectedItem=accounts.FirstOrDefault(a=>a.SlotId==slot)??accounts.FirstOrDefault();
        if(accounts.Any(a=>a.SlotId==pendingSelection))pendingSelection=null;
        rebinding=false;UpdateActions();
    }
    private void Account_Changed(object sender,SelectionChangedEventArgs e){if(!rebinding)UpdateActions();}
    private void UpdateActions()
    {
        var editable=!busy&&!host.Settings.ReadOnly;
        AddButton.IsEnabled=editable;NewLabel.IsEnabled=editable;AccountList.IsEnabled=!busy;
        SelectButton.IsEnabled=EditButton.IsEnabled=DeleteButton.IsEnabled=editable&&Selected is not null;
        RefreshButton.IsEnabled=!busy&&Selected?.CanRefresh==true;
        CaptureButton.IsEnabled=editable&&Selected is not null;
        SwitchButton.IsEnabled=editable&&Selected?.State.Config.NativeAuthRef is not null;
    }
    private void SetFeedback(string message){feedback=message;Feedback.Text=L.T(message);}
    private async Task Operate(Func<Task> action)
    {
        if(busy)return;busy=true;UpdateActions();
        try{await action();}
        catch(QueryException error){SetFeedback(GameDevUsageBar.Core.Presentation.CardPresentation.Failure(error.Kind));}
        catch{SetFeedback("The account operation could not finish. No credentials were logged.");}
        finally{busy=false;if(!closed)Rebind();}
    }
    private async void Add_Click(object sender,RoutedEventArgs e)=>await Operate(async()=>{
        var config=await host.AddAccountAsync(provider,NewLabel.Text.Trim());pendingSelection=config.SlotId;NewLabel.Clear();Rebind();
        SetFeedback("Account added. Configure its credentials before querying.");
    });
    private async void Select_Click(object sender,RoutedEventArgs e){if(Selected is not {} account)return;await Operate(async()=>{await host.SelectAccountAsync(provider,account.SlotId);SetFeedback("Displayed account changed. The CLI login is unchanged.");});}
    private async void Refresh_Click(object sender,RoutedEventArgs e){if(Selected is not {} account)return;await Operate(()=>hub.RequestManualRefresh(provider,account.SlotId));}
    private async void Edit_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} account||busy)return;
        var definition=host.Adapters.Single(a=>a.Definition.Id==provider).Definition;
        var dialog=new AccountWindow(host,definition,host.GetAccounts(provider).Single(a=>a.SlotId==account.SlotId)){Owner=this};
        dialog.ShowDialog();if(dialog.Saved&&host.GetAccounts(provider).FirstOrDefault(a=>a.SlotId==account.SlotId) is {Enabled:true})await Operate(()=>hub.RequestManualRefresh(provider,account.SlotId));
    }
    private async void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} account||busy)return;
        if(System.Windows.MessageBox.Show(this,L.T("Delete this saved account and its app credentials? CLI login files stay unchanged."),L.T("Delete account"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        await Operate(async()=>{await host.RemoveAccountAsync(provider,account.SlotId);SetFeedback("Saved account deleted. CLI login files are unchanged.");});
    }
    private async void Capture_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} account)return;
        await Operate(async()=>{var result=await host.CaptureCurrentLoginAsync(provider,account.SlotId);SetFeedback(result.MessageKey);});
    }
    private async void Switch_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} account)return;
        await Operate(async()=>{var result=await host.SwitchCliLoginAsync(provider,account.SlotId);SetFeedback(result.MessageKey);});
    }
}
