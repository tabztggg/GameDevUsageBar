using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using GameDevUsageBar.Core;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Infrastructure;

namespace GameDevUsageBar.App;

public partial class AccountsWindow : Window
{
    private readonly ApplicationHost host;
    private readonly ProviderStateHub hub;
    private readonly string provider;
    private bool busy,rebinding,closed;
    private Guid? pendingSelection;
    private string feedback="";
    private readonly HashSet<Guid> unknownLogins=[];
    private CardModel? Selected=>AccountList.SelectedItem as CardModel;
    private bool IsClaudeProfile=>provider=="claude"&&!string.IsNullOrWhiteSpace(Selected?.State.Config.ClaudeConfigDirectory);
    private bool IsLoginUnknown(Guid slot)=>unknownLogins.Contains(slot)||host.IsClaudeLoginUnknown(slot);
    public AccountsWindow(ApplicationHost host,ProviderStateHub hub,string provider)
    {
        this.host=host;this.hub=hub;this.provider=provider;InitializeComponent();
        Height=Math.Min(Height,SystemParameters.WorkArea.Height-30);MinHeight=Math.Min(MinHeight,Height);
        NativeActions.Visibility=provider is "codex" or "claude"?Visibility.Visible:Visibility.Collapsed;
        AccountNameLabel.Visibility=provider=="claude"?Visibility.Visible:Visibility.Collapsed;
        Loaded+=(_,_)=>{if(provider=="claude"&&!host.Settings.ReadOnly)NewLabel.Focus();};
        hub.Changed+=Rebind;L.Changed+=LanguageChanged;Closed+=(_,_)=>{closed=true;hub.Changed-=Rebind;L.Changed-=LanguageChanged;};
        Rebind();LanguageChanged();
    }
    private void LanguageChanged()
    {
        if(closed)return;
        Heading.Text=L.T(host.Adapters.Single(a=>a.Definition.Id==provider).Definition.Name)+" · "+L.T("Accounts");Feedback.Text=L.T(feedback);
        if(provider=="claude")
        {
            AddButton.Content=L.T("Add Claude account");NewLabel.ToolTip=L.T("Account name (required)");AutomationProperties.SetName(NewLabel,L.T("Account name (required)"));
            NativeIntro.Text=L.T("Adding or signing in to an account opens a CLI terminal with a fresh Firefox profile. Complete the authorization with the intended Claude account. Other browser logins are not reused.");
            NativeBoundary.Text=L.T("The name is your own label, so check the account on the authorization page. Usage is read after login completes. Edit account can rename the label.");
        }
        UpdateActions();
    }
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
        LoginButton.Visibility=IsClaudeProfile?Visibility.Visible:Visibility.Collapsed;
        LoginButton.IsEnabled=editable&&IsClaudeProfile&&Selected is {} selected&&!IsLoginUnknown(selected.SlotId);
        CaptureButton.Content=L.T(IsClaudeProfile?"Read account login":"Save current CLI login");
        SwitchButton.IsEnabled=editable&&!IsClaudeProfile&&Selected?.State.Config.NativeAuthRef is not null;
        SwitchButton.ToolTip=IsClaudeProfile?L.T("This account uses an isolated CLI config. Auth-file switching does not change that config."):null;
    }
    private void SetFeedback(string message){if(closed)return;feedback=message;Feedback.Text=L.T(message);}
    private async Task Operate(Func<Task> action)
    {
        if(busy||closed)return;busy=true;UpdateActions();
        try{await action();}
        catch(QueryException error){host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback(GameDevUsageBar.Core.Presentation.CardPresentation.Failure(error.Kind));}
        catch(Exception error){host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback("The account operation could not finish. No credentials were logged.");}
        finally{busy=false;if(!closed)Rebind();}
    }
    private async void Add_Click(object sender,RoutedEventArgs e)
    {
        if(busy||closed||host.Settings.ReadOnly)return;
        var label=NewLabel.Text.Trim();
        if(provider=="claude"&&string.IsNullOrWhiteSpace(label)){SetFeedback("Enter an account name before signing in.");NewLabel.Focus();NewLabel.SelectAll();return;}
        await Operate(async()=>{
            var pending=provider=="claude"?host.GetAccounts("claude").FirstOrDefault(account=>!string.IsNullOrWhiteSpace(account.ClaudeConfigDirectory)&&string.Equals(account.Label,label,StringComparison.Ordinal)&&(IsLoginUnknown(account.SlotId)||!account.Enabled&&account.NativeIdentity is null)):null;
            var config=pending??(provider=="claude"?await host.AddClaudeAccountAsync(label):await host.AddAccountAsync(provider,label));
            pendingSelection=config.SlotId;
            if(closed)return;
            NewLabel.Clear();Rebind();
            if(provider=="claude")await SignIn(config.SlotId);
            else SetFeedback("Account added. Configure its credentials before querying.");
        });
        if(!closed&&provider=="claude"&&LoginButton.IsEnabled)LoginButton.Focus();
    }
    private async Task SignIn(Guid slot)
    {
        if(IsLoginUnknown(slot)){SetFeedback("The previous sign-in result is unknown. Do not repeat sign-in. You can read the current account login.");return;}
        SetFeedback("Opening an isolated CLI login with a fresh Firefox profile. Authorize the intended Claude account in the opened terminal; this account stays pending until login is verified.");
        var result=await host.LoginClaudeAccountAsync(slot);
        if(result.Status==NativeAccountStatus.Unknown)unknownLogins.Add(slot);
        else if(result.Succeeded)unknownLogins.Remove(slot);
        SetFeedback(result.MessageKey);
    }
    private async void Login_Click(object sender,RoutedEventArgs e)
    {
        if(!IsClaudeProfile||Selected is not {} account||busy||closed||host.Settings.ReadOnly||IsLoginUnknown(account.SlotId))return;
        await Operate(()=>SignIn(account.SlotId));
        if(!closed&&LoginButton.IsEnabled)LoginButton.Focus();
    }
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
        await Operate(async()=>{
            var result=IsClaudeProfile?await host.CompleteClaudeAccountLoginAsync(account.SlotId):await host.CaptureCurrentLoginAsync(provider,account.SlotId);
            if(result.Succeeded)unknownLogins.Remove(account.SlotId);
            SetFeedback(result.MessageKey);
        });
    }
    private async void Switch_Click(object sender,RoutedEventArgs e)
    {
        if(IsClaudeProfile||Selected is not {} account||busy||closed||host.Settings.ReadOnly)return;
        await Operate(async()=>{var result=await host.SwitchCliLoginAsync(provider,account.SlotId);SetFeedback(result.MessageKey);});
    }
}
