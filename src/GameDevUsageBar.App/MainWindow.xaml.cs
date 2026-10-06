using GameDevUsageBar.App.Presentation;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.App;
public partial class MainWindow : Window
{
    private readonly ApplicationHost host;
    private readonly ProviderStateHub hub;
    private readonly PresentationPreferencesService preferences;
    private readonly System.ComponentModel.ICollectionView view;
    private readonly System.ComponentModel.ICollectionView connections;
    private readonly ObservableCollection<CardModel> featured=[];
    private HashSet<string> featuredIds=[];
    private IEnumerable<CardModel> models => hub.Models;
    private string localError="";
    public bool AllowClose { get; set; }
    public event Action? DisplaySettingsRequested;
    public event Action? ExitRequested;
    public event Action? NetworkRequested;
    private bool initializing=true;
    private bool choosingLanguage;
    private AccountsWindow? accountsWindow;

    public MainWindow(ApplicationHost host,ProviderStateHub hub,PresentationPreferencesService preferences)
    {
        this.host=host; this.hub=hub; this.preferences=preferences; InitializeComponent();
        AddHandler(AccountPickerView.SelectAccountEvent,new EventHandler<AccountSelectionEventArgs>(Account_Selected));
        AddHandler(AccountPickerView.ManageAccountsEvent,new RoutedEventHandler(Account_Manage));
        LanguageSelector.ItemsSource=LanguageChoice.All;
        L.Changed+=LanguageChanged;LanguageChanged();
        Width=Math.Min(Width,SystemParameters.WorkArea.Width-30);
        Height=Math.Min(Height,SystemParameters.WorkArea.Height-30);
        MinWidth=Math.Min(MinWidth,Width); MinHeight=Math.Min(MinHeight,Height);
        view=hub.CreateView(model=>model.State.Config.Enabled&&!featuredIds.Contains(model.Id));
        connections=hub.CreateView(model=>!model.State.Config.Enabled&&model.Id!="demo"&&!featuredIds.Contains(model.Id));
        Cards.ItemsSource=view;OtherConnections.ItemsSource=connections;FeaturedCards.ItemsSource=featured;NetworkOverview.DataContext=hub.Network;
        DemoToggle.IsChecked=host.Accounts.FirstOrDefault(a=>a.ProviderId=="demo")?.Enabled==true;
        DemoToggle.IsEnabled=models.Any(m=>m.Id=="demo");
        initializing=false;
        hub.Changed+=HubChanged;
        Closing+=(_,e)=> { if(!AllowClose) {e.Cancel=true;Hide();} };
        Closed+=(_,_)=>{hub.Changed-=HubChanged;L.Changed-=LanguageChanged;};
        UpdateOverview();
    }
    public void ShowPopup() { Show(); WindowState=WindowState.Normal; Activate(); }
    public void ShowAccounts(string provider)
    {
        if(accountsWindow is {IsVisible:true}){accountsWindow.Close();}
        accountsWindow=new AccountsWindow(host,hub,provider){Owner=this};
        accountsWindow.Closed+=(_,_)=>accountsWindow=null;accountsWindow.Show();
    }
    private async void Account_Selected(object? sender,AccountSelectionEventArgs e)
    {
        e.Handled=true;
        try{await host.SelectAccountAsync(e.ProviderId,e.SlotId);}catch(Exception error){host.RuntimeLog?.RecordException("handled_exception",error);ReportLocalError();}
    }
    private void Account_Manage(object sender,RoutedEventArgs e)
    {
        if(e.OriginalSource is FrameworkElement {DataContext:CardModel model}){e.Handled=true;ShowAccounts(model.Id);}
    }
    public void TogglePopup() { if(IsVisible) Hide(); else ShowPopup(); }
    private void Reorder() =>UpdateOverview();
    private void HubChanged()=>UpdateOverview();
    private void Network_Click(object sender,RoutedEventArgs e)=>NetworkRequested?.Invoke();
    private void Display_Click(object sender,RoutedEventArgs e)=>DisplaySettingsRequested?.Invoke();
    private void Widget_Click(object sender,RoutedEventArgs e)=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=true}});
    private void UpdateOverview()
    {
        var ids=preferences.Current.FeaturedOrDefault;
        var selected=ids.Select(id=>models.FirstOrDefault(m=>m.Id==id)).OfType<CardModel>().ToArray();
        featuredIds=selected.Select(m=>m.Id).ToHashSet();
        if(!featured.SequenceEqual(selected)){featured.Clear();foreach(var model in selected)featured.Add(model);}
        view.Refresh();connections.Refresh();
        FeaturedTitle.Visibility=featured.Count>0?Visibility.Visible:Visibility.Collapsed;
        OtherTitle.Visibility=view.IsEmpty?Visibility.Collapsed:Visibility.Visible;
        EmptyOverview.Visibility=view.IsEmpty&&featured.Count==0?Visibility.Visible:Visibility.Collapsed;
        ConnectionsSection.Visibility=connections.IsEmpty?Visibility.Collapsed:Visibility.Visible;
        var live=models.Count(m=>m.State.Config.Enabled&&m.Id!="demo"&&m.HasCurrentUsage);
        var configured=models.Count(m=>m.State.Config.Enabled && m.Id!="demo");
        var loggingNotice=host.RuntimeLog is {SessionLoggingAvailable:false}
            ? "Runtime logs could not be saved. Export diagnostics to see the logging failure; usage queries remain available." : "";
        LayoutNotice.Text=string.Join(" · ",new[]{host.Settings.ReadOnly ? "Account settings could not be read; the original file is preserved." : "",preferences.Notice,localError,loggingNotice}.Where(n=>n.Length>0).Select(L.T));
        OverviewStatus.Text=L.F("{0} enabled · {1} current",configured,live);
        ResizeLayout();
    }
    public void ReportLocalError() {localError="A local error occurred. No diagnostics were uploaded. You can export diagnostics from the overview.";UpdateOverview();}
    public void ReportQuotaApiError(){localError="Local quota API could not start on port 17864. Check diagnostics and port availability; the tray and widget remain available.";UpdateOverview();}
    private void LanguageChanged()
    {
        choosingLanguage=true;LanguageSelector.SelectedValue=L.Language;choosingLanguage=false;
        // Constructor calls this before the model view has been initialized.
        if(!initializing)UpdateOverview();
    }
    private void Language_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!choosingLanguage && LanguageSelector.SelectedValue is string language)
            preferences.Update(p=>p with {Language=language});
    }
    private void Hide_Click(object sender,RoutedEventArgs e)=>Hide();
    private void More_Click(object sender,RoutedEventArgs e){OverviewMore.ContextMenu.PlacementTarget=OverviewMore;OverviewMore.ContextMenu.IsOpen=true;}
    private void Dashboard_SizeChanged(object sender,SizeChangedEventArgs e){if(!initializing)ResizeLayout();}
    private void ResizeLayout()
    {
        var narrow=ActualWidth<1100;
        Grid.SetRow(HeaderActions,narrow?1:0);Grid.SetColumn(HeaderActions,narrow?0:1);Grid.SetColumnSpan(HeaderActions,narrow?2:1);
        HeaderActions.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Right;HeaderActions.Margin=narrow?new Thickness(0,12,0,0):new Thickness(0);
        var available=DashboardHost.ActualWidth;
        if(available<=0)return;
        FeaturedCards.Tag=featured.Count>1&&available>=940?2:1;
    }
    private void Card_Refresh(object sender,RoutedEventArgs e){if(sender is FrameworkElement{DataContext:CardModel model})_=host.Coordinator.RefreshAsync(model.Id);}
    private void Card_Setup(object sender,RoutedEventArgs e)=>Setup_Click(sender,e);
    private async void Card_Up(object sender,RoutedEventArgs e){if(sender is FrameworkElement{DataContext:CardModel model})await MoveAsync(model.Id,-1);}
    private async void Card_Down(object sender,RoutedEventArgs e){if(sender is FrameworkElement{DataContext:CardModel model})await MoveAsync(model.Id,1);}
    private void Exit_Click(object sender,RoutedEventArgs e)=>ExitRequested?.Invoke();
    private void RefreshAll_Click(object sender,RoutedEventArgs e) { hub.RefreshAll(); }
    private void Refresh_Click(object sender,RoutedEventArgs e) { if(sender is Button {Tag:string id}) _=host.Coordinator.RefreshAsync(id); }
    private async void Setup_Click(object sender,RoutedEventArgs e)
    {
        var id=sender is Button {Tag:string buttonId}?buttonId:sender is FrameworkElement {DataContext:CardModel model}?model.Id:null;
        if(id is null)return;
        var adapter=host.Adapters.Single(a=>a.Definition.Id==id);
        var dialog=new AccountWindow(host,adapter.Definition,host.Coordinator.Get(id).Config) {Owner=this};
        dialog.ShowDialog();
        if(dialog.Saved) { Reorder(); if(host.Coordinator.Get(id).Config.Enabled) await host.Coordinator.RefreshAsync(id); }
    }
    private async void Demo_Changed(object sender,RoutedEventArgs e)
    {
        if(initializing || !IsInitialized || !models.Any()) return;
        try { var config=host.Coordinator.Get("demo").Config; await host.SaveAsync(config with {Enabled=DemoToggle.IsChecked==true}); Reorder(); if(DemoToggle.IsChecked==true) await host.Coordinator.RefreshAsync("demo"); }
        catch(Exception error) { host.RuntimeLog?.RecordException("handled_exception",error);System.Windows.MessageBox.Show(this,L.T("Could not save demo settings."),"GameDevUsageBar"); }
    }
    private Task MoveAsync(string id,int delta)
    {
        var order=(preferences.Current.CardOrder ?? []).Concat(models.Select(m=>m.Id)).Distinct(StringComparer.Ordinal).ToList();
        var model=models.FirstOrDefault(m=>m.Id==id);
        if(model is null||featuredIds.Contains(id))return Task.CompletedTask;
        var visible=models.Where(m=>!featuredIds.Contains(m.Id)&&m.State.Config.Enabled==model.State.Config.Enabled)
            .Where(m=>model.State.Config.Enabled||m.Id!="demo").Select(m=>m.Id).ToHashSet(StringComparer.Ordinal);
        var index=order.IndexOf(id);
        var other=index+Math.Sign(delta);
        while(other>=0&&other<order.Count&&!visible.Contains(order[other]))other+=Math.Sign(delta);
        if(delta!=0&&index>=0&&other>=0&&other<order.Count){(order[index],order[other])=(order[other],order[index]);preferences.Update(p=>p with {CardOrder=order.ToArray()});}
        return Task.CompletedTask;
    }
    private async void Up_Click(object sender,RoutedEventArgs e) { if(sender is Button {Tag:string id}) await MoveAsync(id,-1); }
    private async void Down_Click(object sender,RoutedEventArgs e) { if(sender is Button {Tag:string id}) await MoveAsync(id,1); }
    private void Export_Click(object sender,RoutedEventArgs e)=>ExportDiagnostics();
    public void ExportDiagnostics()
    {
        var preview=new Window {Title="GameDevUsageBar · Diagnostic preview",Width=Math.Min(760,SystemParameters.WorkArea.Width-30),Height=Math.Min(600,SystemParameters.WorkArea.Height-30),Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        LanguageChoice.Bind(preview,Window.TitleProperty,"GameDevUsageBar · Diagnostic preview");
        var dock=new DockPanel {Margin=new Thickness(20)};
        var hasRuntimeLog=host.RuntimeLog is not null;
        var notice=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        LanguageChoice.Bind(notice,TextBlock.TextProperty,hasRuntimeLog
            ? host.RuntimeLog!.SessionLoggingAvailable
                ? "Preview the current diagnostic summary below. Saving creates a ZIP with a filtered JSON summary and bounded runtime logs. Logs are kept in %LOCALAPPDATA%\\GameDevBar\\logs. No account files, cached balances, credentials, or native login files are included. Nothing is uploaded."
                : "Runtime logs are unavailable for this session. The ZIP includes safe in-memory diagnostics and may include earlier session logs. No account files or credentials are included. Nothing is uploaded."
            : "Preview the current diagnostic summary below. Runtime logs are unavailable in this session; saving creates a JSON file only. Nothing is uploaded.");
        DockPanel.SetDock(notice,Dock.Top);dock.Children.Add(notice);
        var save=new Button {HorizontalAlignment=HorizontalAlignment.Right}; DockPanel.SetDock(save,Dock.Bottom);
        LanguageChoice.Bind(save,ContentControl.ContentProperty,hasRuntimeLog?"Save diagnostic ZIP…":"Save diagnostic JSON…");
        var text=host.Diagnostics();
        save.Click+=async (_,_)=>
        {
            var dialog=new Microsoft.Win32.SaveFileDialog {
                FileName=$"GameDevUsageBar-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}"+(hasRuntimeLog?".zip":".json"),
                Filter=hasRuntimeLog?L.T("Diagnostic ZIP archive|*.zip"):L.T("Diagnostic JSON file|*.json"),
                DefaultExt=hasRuntimeLog?".zip":".json",AddExtension=true,OverwritePrompt=!hasRuntimeLog
            };
            if(hasRuntimeLog)dialog.FileOk+=(_,args)=>
            {
                if(!System.IO.File.Exists(dialog.FileName))return;
                args.Cancel=true;
                System.Windows.MessageBox.Show(preview,L.T("Choose a new filename. Diagnostic archives do not overwrite existing files."),"GameDevUsageBar");
            };
            if(dialog.ShowDialog(preview)!=true)return;
            save.IsEnabled=false;
            try
            {
                if(host.RuntimeLog is { } runtimeLog)
                {
                    if(!await Task.Run(()=>runtimeLog.ExportBundle(dialog.FileName,text)))
                    {
                        System.Windows.MessageBox.Show(preview,L.T("Could not save diagnostics. Check the selected folder and available disk space."),"GameDevUsageBar");
                        return;
                    }
                }
                else await System.IO.File.WriteAllTextAsync(dialog.FileName,text);
                preview.Close();
            }
            catch(Exception error)
            {
                host.RuntimeLog?.RecordException("export_failed",error);
                System.Windows.MessageBox.Show(preview,L.T("Could not save diagnostics. Check the selected folder and available disk space."),"GameDevUsageBar");
            }
            finally {save.IsEnabled=true;}
        };
        dock.Children.Add(save); dock.Children.Add(new TextBox {Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new System.Windows.Media.FontFamily("Consolas")});
        preview.Content=dock; preview.ShowDialog();
    }
}
