using System.Windows;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using System.Diagnostics;

namespace GameDevUsageBar.App;
public partial class AccountWindow : Window
{
    private readonly ApplicationHost host;
    private readonly ProviderDefinition definition;
    private AccountConfig config;
    private string feedback="";
    private sealed record DemoChoice(string Id,string Label);
    private sealed record RegionChoice(string Id,string Label);
    private bool choosingRegion;
    private bool choosingSource;
    private bool choosingBrowser;
    private bool importingCookie;
    private bool closed;
    private FirefoxProfile[]? firefoxProfiles;
    private string SelectedSource=>SourceSelector.SelectedValue as string ?? config.SourceMode;
    private string SelectedRegion=>RegionSelector.SelectedValue as string ?? (definition.Id=="tripo"?config.TripoRegion:config.QueryRegion);
    public bool Saved {get;private set;}
    public AccountWindow(ApplicationHost host,ProviderDefinition definition,AccountConfig config,FirefoxProfile[]? profiles=null)
    {
        this.host=host; this.definition=definition; this.config=config;firefoxProfiles=profiles; InitializeComponent();
        Height=Math.Min(Height,SystemParameters.WorkArea.Height-30); MinHeight=Math.Min(420,Height);
        AccountLabel.Text=config.Label; Interval.Text=config.IntervalMinutes.ToString(); Enabled.IsChecked=config.Enabled;
        RegionFields.Visibility=definition.Id is "tripo" or "grsai" ? Visibility.Visible : Visibility.Collapsed;
        SourceFields.Visibility=ProviderSources.IsExtended(definition.Id)?Visibility.Visible:Visibility.Collapsed;
        ProjectFields.Visibility=definition.Id=="gemini"?Visibility.Visible:Visibility.Collapsed;
        ProjectInput.Text=config.ProjectId;OrganizationInput.Text=config.OrganizationId;AccountIdInput.Text=config.AccountId;
        if(definition.IsDemo)
        {
            SecretFields.Visibility=Visibility.Collapsed; DemoFields.Visibility=Visibility.Visible;
            Scenario.SelectedValuePath="Id";
        }
        else if(definition.Endpoint is null) {Fields.Visibility=Visibility.Collapsed; Save.Visibility=Visibility.Collapsed;}
        Remove.Visibility=config.CredentialRef==null&&config.NativeAuthRef==null&&config.NativeIdentity==null&&config.SourceMode!="browser-session" ? Visibility.Collapsed : Visibility.Visible;
        if(host.Settings.ReadOnly) { Fields.IsEnabled=false; Save.IsEnabled=false; Remove.IsEnabled=false; feedback="Original settings could not be read; changes are disabled."; }
        L.Changed+=LanguageChanged;Closed+=(_,_)=>{closed=true;SecretInput.Clear();L.Changed-=LanguageChanged;};LanguageChanged();
    }
    private void LanguageChanged()
    {
        var selectedRegion=SelectedRegion;
        var selectedSource=SelectedSource;
        choosingSource=true;SourceSelector.ItemsSource=ProviderSources.Modes(definition.Id).Select(id=>new RegionChoice(id,L.T(ProviderSources.ModeLabel(definition.Id,id)))).ToArray();SourceSelector.SelectedValue=selectedSource;choosingSource=false;
        SourceTitle.Text=L.T("Authentication source");System.Windows.Automation.AutomationProperties.SetName(SourceSelector,SourceTitle.Text);
        ProjectTitle.Text=L.T("Google Cloud project ID");System.Windows.Automation.AutomationProperties.SetName(ProjectInput,ProjectTitle.Text);
        OrganizationTitle.Text=L.T("Claude organization UUID");System.Windows.Automation.AutomationProperties.SetName(OrganizationInput,OrganizationTitle.Text);
        AccountIdTitle.Text=L.T("ChatGPT account ID (optional)");System.Windows.Automation.AutomationProperties.SetName(AccountIdInput,AccountIdTitle.Text);
        OrganizationFields.Visibility=definition.Id=="claude"&&selectedSource=="web-cookie"?Visibility.Visible:Visibility.Collapsed;
        AccountIdFields.Visibility=definition.Id=="codex"&&selectedSource=="manual"?Visibility.Visible:Visibility.Collapsed;
        SecretFields.Visibility=definition.IsDemo||selectedSource is "local-oauth" or "saved-oauth" or "browser-session"?Visibility.Collapsed:Visibility.Visible;
        WebsiteFields.Visibility=(definition.Id=="claude"&&selectedSource=="web-cookie")?Visibility.Visible:Visibility.Collapsed;
        if(WebsiteFields.Visibility==Visibility.Visible)
        {
            var browser=BrowserSelector.SelectedValue as string ?? "firefox";
            choosingBrowser=true;BrowserSelector.ItemsSource=WebsiteSession.Browsers(definition.Id).Select(id=>new RegionChoice(id,L.T(id switch {"firefox"=>"Firefox","chrome"=>"Google Chrome","edge"=>"Microsoft Edge",_=>"Default browser"}))).ToArray();BrowserSelector.SelectedValue=browser;choosingBrowser=false;
            BrowserTitle.Text=L.T("Login browser");ProfileTitle.Text=L.T("Firefox profile");
            System.Windows.Automation.AutomationProperties.SetName(BrowserSelector,BrowserTitle.Text);System.Windows.Automation.AutomationProperties.SetName(ProfileSelector,ProfileTitle.Text);
            OpenLogin.Content=L.T("Open login page");ImportCookie.Content=L.T("Import Firefox cookies");CookieHelp.Content=L.T("How to paste Cookie");
            UpdateCookieBrowser();
        }
        SecretTitle.Text=L.T(ProviderSources.IsExtended(definition.Id)?ProviderSources.ModeLabel(definition.Id,selectedSource)+" (leave blank to keep saved credential)":"API key (leave blank to keep saved key)");
        System.Windows.Automation.AutomationProperties.SetName(SecretInput,L.T(ProviderSources.ModeLabel(definition.Id,selectedSource)));
        NativeHint.Text=selectedSource=="saved-oauth"?L.T("Use Manage accounts to save the current CLI login as an encrypted account. Its usage is queried independently; expired credentials need importing again. Switching displayed usage does not change the CLI login."):selectedSource=="local-oauth"?definition.Id=="claude"?L.T(ProviderSources.Description(config with {SourceMode=selectedSource})):L.T("Saving an enabled source connects the current CLI account. Native credentials are read only and never copied or changed. Renew an expired login in the CLI; save Settings again after changing accounts."):L.T("Credentials are encrypted for the current Windows user. Changing the authentication source requires its matching credential.");
        choosingRegion=true;
        RegionSelector.ItemsSource=new[]{TripoRegions.Global,TripoRegions.China}.Select(id=>new RegionChoice(id,L.T(TripoRegions.Label(id)))).ToArray();
        RegionSelector.SelectedValue=selectedRegion;choosingRegion=false;
        Heading.Text=L.T(definition.Name)+" · "+L.T(definition.Channel);Description.Text=L.T(definition.Summary);
        if(ProviderSources.IsExtended(definition.Id))
        {
            EndpointText.Text=L.T(ProviderSources.Description(config with {SourceMode=selectedSource}));
            if(definition.Id is "grsai")EndpointText.Text+="\n"+(selectedRegion=="china"?"https://grsai.dakka.com.cn":"https://grsaiapi.com");
        }
        else if(TripoRegions.Endpoint(definition,config with {TripoRegion=selectedRegion}) is {} endpoint)
            EndpointText.Text=L.F("Read-only GET to {0}:{1}{2}. One query per refresh. No generation calls.\n",endpoint.Host,endpoint.Port,endpoint.Path)+L.T(definition.Id=="openrouter"?"Query account balance with your saved OpenRouter API key. If this account returns 403, check its management-key requirement.":"Enter a key for this provider only; account-wide and key-scoped values remain separate.");
        else if(definition.IsDemo)
        {
            EndpointText.Text=L.T("DEMO DATA only. No credential is needed and no network request is made.");
            var selected=Scenario.SelectedValue as string ?? config.DemoScenario;
            Scenario.ItemsSource=new[]{"Success","Zero","Missing","401","429","Timeout","Slow","Schema error","Offline"}.Select(id=>new DemoChoice(id,L.T(id))).ToArray();Scenario.SelectedValue=selected;
        }
        else EndpointText.Text=L.T(definition.HoldReason ?? "");
        RegionHint.Text=L.T(definition.Id=="tripo"?"Choose the API version and enter the key you intend to use with it. A saved key is only used with the version it was saved for.":"Choose the documented global or China query node. No automatic node fallback.");
        if(definition.Id=="tripo" && config.CredentialRef is not null && !(config with {TripoRegion=selectedRegion}).HasUsableCredential)
            RegionHint.Text+="\n"+L.F("The saved key belongs to {0}. Enter a key for {1}, or disable this source before saving.",L.T(TripoRegions.Label(config.CredentialRegion ?? TripoRegions.Global)),L.T(TripoRegions.Label(selectedRegion)));
        Feedback.Text=L.T(feedback);
    }
    private void Region_Changed(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(!choosingRegion && RegionSelector.SelectedValue is string)LanguageChanged();
    }
    private void Source_Changed(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {if(!choosingSource && SourceSelector.SelectedValue is string)LanguageChanged();}
    private void Browser_Changed(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {if(!choosingBrowser && BrowserSelector.SelectedValue is string)UpdateCookieBrowser();}
    private void UpdateCookieBrowser()
    {
        var firefox=BrowserSelector.SelectedValue as string=="firefox";
        FirefoxFields.Visibility=firefox?Visibility.Visible:Visibility.Collapsed;ImportCookie.Visibility=firefox?Visibility.Visible:Visibility.Collapsed;
        if(firefox)
        {
            if(firefoxProfiles is null)try{firefoxProfiles=WebsiteSession.Profiles();}catch(Exception error){host.RuntimeLog?.RecordException("handled_exception",error);firefoxProfiles=[];}
            ProfileSelector.ItemsSource=firefoxProfiles;if(ProfileSelector.SelectedItem is null&&firefoxProfiles.Length>0)ProfileSelector.SelectedIndex=0;
            ImportCookie.IsEnabled=firefoxProfiles.Length>0&&!importingCookie;
        }
        CookieHint.Text=L.T(firefox?"Sign in using the selected Firefox profile, then import. Only this website's unexpired cookies are read. Click Save to store them encrypted; imported cookies are not yet verified.":"Open the login page in this browser, then paste its Cookie request header below. Automatic import is available for Firefox profiles; Chrome and Edge cookies are not read.");
        if(firefox&&firefoxProfiles?.Length==0)CookieHint.Text+="\n"+L.T("No Firefox profiles found. Start Firefox once, reopen Settings, or paste Cookie manually.");
    }
    private void OpenLogin_Click(object sender,RoutedEventArgs e)
    {
        try{Process.Start(WebsiteSession.LoginCommand(definition.Id,BrowserSelector.SelectedValue as string ?? "firefox",ProfileSelector.SelectedItem as FirefoxProfile));}
        catch(Exception error){host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback("Could not open the selected browser. Choose an installed browser or open the website yourself.");}
    }
    private async void ImportCookie_Click(object sender,RoutedEventArgs e)
    {
        if(importingCookie||BrowserSelector.SelectedValue as string!="firefox"||ProfileSelector.SelectedItem is not FirefoxProfile profile)return;
        importingCookie=true;Save.IsEnabled=false;Remove.IsEnabled=false;ImportCookie.IsEnabled=false;BrowserSelector.IsEnabled=false;ProfileSelector.IsEnabled=false;OpenLogin.IsEnabled=false;
        try
        {
            var cookie=await Task.Run(()=>WebsiteSession.ImportFirefox(definition.Id,profile));
            if(!closed){SecretInput.Password=cookie;SetFeedback("Cookies imported into the protected input. Click Save, then Refresh to verify the login and retrieve usage.");}
        }
        catch(InvalidOperationException){if(!closed)SetFeedback("No website cookies found in this Firefox profile. Sign in first, then import again.");}
        catch(Exception error) {host.RuntimeLog?.RecordException("handled_exception",error);if(!closed)SetFeedback("Could not read Firefox cookies. Close Firefox and try importing again, or paste the Cookie header manually.");}
        finally
        {
            importingCookie=false;if(!closed){Save.IsEnabled=!host.Settings.ReadOnly;Remove.IsEnabled=!host.Settings.ReadOnly;BrowserSelector.IsEnabled=true;ProfileSelector.IsEnabled=true;OpenLogin.IsEnabled=true;UpdateCookieBrowser();}
        }
    }
    private void CookieHelp_Click(object sender,RoutedEventArgs e)=>System.Windows.MessageBox.Show(this,L.T("1. Sign in on the provider website.\n2. Open browser developer tools (F12), select Network, and reload the billing or usage page. Select a request to that website and copy the Cookie value under Request Headers.\n3. Paste the value in the protected Cookie input below, then click Save and Refresh. Never paste Authorization headers or your password."),L.T("How to paste Cookie"),MessageBoxButton.OK,MessageBoxImage.Information);
    private void SetFeedback(string source){feedback=source;Feedback.Text=L.T(source);}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private async void Save_Click(object sender,RoutedEventArgs e)
    {
        if(!int.TryParse(Interval.Text,out var minutes) || minutes is <5 or >120) {SetFeedback("Enter an interval between 5 and 120 minutes.");return;}
        var key=SecretInput.Password.Trim();
        var next=config with {TripoRegion=definition.Id=="tripo"?SelectedRegion:config.TripoRegion,QueryRegion=definition.Id is "grsai"?SelectedRegion:config.QueryRegion,SourceMode=SelectedSource,ProjectId=ProjectInput.Text.Trim(),OrganizationId=OrganizationInput.Text.Trim(),AccountId=AccountIdInput.Text.Trim(),Enabled=Enabled.IsChecked==true};
        try{next.Validate();}catch{SetFeedback("Check the project ID, organization UUID and account ID format.");return;}
        if(Enabled.IsChecked==true && definition.Id=="gemini" && next.ProjectId.Length==0){SetFeedback("Enter the Google Cloud project ID before enabling this source.");return;}
        if(Enabled.IsChecked==true && definition.Id=="claude" && next.SourceMode=="web-cookie" && !Guid.TryParse(next.OrganizationId,out _)){SetFeedback("Enter the organization UUID from your Claude usage page.");return;}
        if(Enabled.IsChecked==true && !definition.IsDemo && next.SourceMode is not ("local-oauth" or "browser-session") && key.Length==0 && !next.HasUsableCredential) {SetFeedback(definition.Id=="tripo"?"Add an API key saved for the selected Tripo version before enabling this source.":"Add the selected source credential before enabling this source.");return;}
        Save.IsEnabled=false; Remove.IsEnabled=false;
        try
        {
                await host.SaveAsync(next with {Label=string.IsNullOrWhiteSpace(AccountLabel.Text)?"Personal":AccountLabel.Text.Trim(),IntervalMinutes=minutes,Enabled=Enabled.IsChecked==true,DemoScenario=Scenario.SelectedValue as string ?? "Success"},key.Length>0&&next.SourceMode is not ("local-oauth" or "saved-oauth")?key:null);
            SecretInput.Clear(); Saved=true; Close();
        }
        catch(QueryException error){host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback(CardPresentation.Failure(error.Kind));Save.IsEnabled=true;Remove.IsEnabled=true;}
        catch(Exception error) {host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback("Could not save local settings or protect the key. The key was not logged."); Save.IsEnabled=true; Remove.IsEnabled=true;}
    }
    private async void Remove_Click(object sender,RoutedEventArgs e)
    {
        if(System.Windows.MessageBox.Show(this,L.T("Remove this app's saved key and cached values? The source will be disabled."),L.T("Remove saved key"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try {await host.RemoveAsync(definition.Id,config.SlotId);Saved=true;Close();} catch(Exception error) {host.RuntimeLog?.RecordException("handled_exception",error);SetFeedback("Could not remove the saved key.");}
    }
}
