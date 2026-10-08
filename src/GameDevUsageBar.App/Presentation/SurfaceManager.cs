using System.Windows;
using Microsoft.Win32;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.App.Presentation;

public sealed class SurfaceManager : IDisposable
{
    private readonly ApplicationHost host;
    private readonly ProviderStateHub hub;
    private readonly PresentationPreferencesService preferences;
    private readonly Action exit;
    private readonly Action<string>? exitSource;
    private readonly PopupToggleGuard toggle = new();
    private WidgetPreferences? lastWidget;
    private DisplaySettingsWindow? displaySettings;
    private Window? networkPanel;
    private bool disposed, handingOff;
    private bool switchingCliAccount;
    public MainWindow Overview { get; }
    public TrayPopupWindow Panel { get; }
    public WidgetWindow Widget { get; }
    public SurfaceManager(ApplicationHost host,ProviderStateHub hub,PresentationPreferencesService preferences,MainWindow overview,Action exit,Action<string>? exitSource=null)
    {
        this.host=host;this.hub=hub;this.preferences=preferences;this.exit=exit;this.exitSource=exitSource;Overview=overview;
        Panel=new(hub);Widget=new(hub,preferences);
        Widget.NetworkRequested+=ShowNetwork;
        overview.NetworkRequested+=ShowNetwork;
        Widget.SettingsRequested+=ShowSettings;Widget.ProviderRequested+=ShowProvider;
        Panel.DismissRequested+=DismissFromInput;
        Panel.OverviewRequested+=ShowOverview;Panel.SettingsRequested+=ShowSettings;
        Panel.WidgetRequested+=ShowWidget;Panel.ExitRequested+=PanelExit;Panel.AccountRequested+=ShowAccount;
        Panel.AccountSlotRequested+=ShowAccountSlot;
        Panel.AccountSwitchRequested+=SelectAccount;Panel.ManageAccountsRequested+=ShowAccounts;
        Panel.CliAccountSwitchRequested+=SwitchCliAccount;
        overview.DisplaySettingsRequested+=ShowSettings;
        overview.ExitRequested+=OverviewExit;
        preferences.Changed+=PreferencesChanged;
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        SystemEvents.UserPreferenceChanged+=UserPreferenceChanged;
        SystemEvents.PowerModeChanged+=PowerChanged;
        PreferencesChanged();
    }
    private void PanelExit(){if(exitSource is null)exit();else exitSource("tray_popup_exit");}
    private void OverviewExit(){if(exitSource is null)exit();else exitSource("overview_exit");}
    public void TrayPointerDown()=>toggle.PointerDown(NativeWindows.InputToken,Panel.IsVisible);
    public void TrayClick()
    {
        if(disposed)return;
        if(!toggle.ShouldOpenOnClick(Panel.IsVisible)) {HidePanel();return;}
        NativeWindows.GetCursorPos(out var point);
        Panel.SelectProvider(null);
        Panel.PositionAt(point);Panel.Show();Panel.Activate();
    }
    private void DismissFromInput()
    {
        if(disposed || !Panel.IsVisible)return;
        if(!handingOff)toggle.Deactivated(NativeWindows.InputToken,NativeWindows.PressedOnTray);
        Panel.Hide();
    }
    public void HidePanel() {handingOff=true;try {Widget.HideProviderHover();Panel.Hide();toggle.Reset();}finally {handingOff=false;}}
    public void ShowOverview() {if(disposed)return;HidePanel();Overview.ShowPopup();}
    public void ShowSettings()
    {
        ShowOverview();
        if(displaySettings is {IsVisible:true}) {displaySettings.Activate();return;}
        displaySettings=new(preferences,hub) {Owner=Overview};
        displaySettings.Closed+=(_,_)=>displaySettings=null;displaySettings.Show();
    }
    private void ShowAccount(string id)
        =>ShowAccountSlot(id,host.Coordinator.Get(id).Config.SlotId);
    private void ShowAccountSlot(string id,Guid slot)
    {
        ShowOverview();
        var adapter=host.Adapters.FirstOrDefault(a=>a.Definition.Id==id);
        var account=host.Coordinator.GetAccounts(id).FirstOrDefault(state=>state.Config.SlotId==slot);
        if(adapter is null || account is null)return;
        var dialog=new AccountWindow(host,adapter.Definition,account.Config) {Owner=Overview};
        dialog.ShowDialog();
        if(dialog.Saved && host.Coordinator.GetAccounts(id).FirstOrDefault(state=>state.Config.SlotId==slot)?.Config.Enabled==true) _=hub.RequestManualRefresh(id,slot);
    }
    public void ShowAccounts(string id){ShowOverview();Overview.ShowAccounts(id);}
    public async void SelectAccount(string id,Guid slot)
    {
        try{await host.SelectAccountAsync(id,slot);}catch(Exception error){host.RuntimeLog?.RecordException("handled_exception",error);Overview.ReportLocalError();}
    }
    private async void SwitchCliAccount(string id,Guid slot)
    {
        if(disposed || switchingCliAccount)return;
        var model=hub.Models.FirstOrDefault(m=>m.Id==id);
        if(model is null || !model.SupportsCliAccountSwitch)return;
        var label=model.CliAccountChoices.FirstOrDefault(account=>account.SlotId==slot)?.Label??"";
        var accountModel=hub.GetAccountModels(id).FirstOrDefault(account=>account.SlotId==slot);
        var affected=hub.Models.Concat(hub.Models.SelectMany(provider=>hub.GetAccountModels(provider.Id))).Distinct().ToArray();
        switchingCliAccount=true;
        foreach(var affectedModel in affected)affectedModel.SetAccountSwitchBusy(true);
        model.SetAccountSwitchFeedback("Switching CLI login…",true,label);
        accountModel?.SetAccountSwitchFeedback("Switching CLI login…",true,label);
        try {
            var result=await host.SwitchCliLoginAsync(id,slot);
            if(!disposed){model.SetAccountSwitchFeedback(result.MessageKey,accountLabel:label);accountModel?.SetAccountSwitchFeedback(result.MessageKey,accountLabel:label);}
        } catch(Exception error) {
            host.RuntimeLog?.RecordException("handled_exception",error);
            if(!disposed){model.SetAccountSwitchFeedback("The account operation could not finish. No credentials were logged.",accountLabel:label);accountModel?.SetAccountSwitchFeedback("The account operation could not finish. No credentials were logged.",accountLabel:label);}
        } finally {switchingCliAccount=false;foreach(var affectedModel in affected)affectedModel.SetAccountSwitchBusy(false);}
    }
    public void ShowNetwork()
    {
        if(disposed)return;HidePanel();
        if(networkPanel is null){
            networkPanel=new Window {Content=new NetworkUsageView{DataContext=hub.Network,Width=320},Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
            networkPanel.Deactivated+=(_,_)=>networkPanel?.Hide();
            networkPanel.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)networkPanel.Hide();};
        }
        NativeWindows.GetCursorPos(out var point);
        if(Widget.IsVisible){var bounds=NativeWindows.Bounds(Widget);point=new(){X=(int)bounds.Left,Y=(int)bounds.Bottom};}
        networkPanel.Show();
        var monitors=NativeWindows.Monitors();var target=monitors.FirstOrDefault(m=>point.X>=m.Work.Left&&point.X<m.Work.Right&&point.Y>=m.Work.Top&&point.Y<m.Work.Bottom)??monitors.First();
        NativeWindows.MoveToMonitor(networkPanel,target);networkPanel.UpdateLayout();var scale=NativeWindows.Scale(networkPanel);
        var box=Placement.Anchor(point.X,point.Y,networkPanel.ActualWidth*scale,networkPanel.ActualHeight*scale,target.Work);
        NativeWindows.Move(networkPanel,box.Left,box.Top);networkPanel.Activate();
    }
    public void ShowWidget()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=true}});
    private void ShowProvider(string id)
    {
        if(disposed)return;Widget.HideProviderHover();HidePanel();Panel.SelectProvider(id);var bounds=NativeWindows.Bounds(Widget);
        Panel.PositionAt(new NativeWindows.Point {X=(int)bounds.Left,Y=(int)bounds.Bottom});Panel.Show();Panel.Activate();
    }
    public void ToggleWidget()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=!p.WidgetOrDefault.Visible}});
    public void ToggleCollapsed()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Collapsed=!p.WidgetOrDefault.Collapsed}});
    public void ToggleTopmost()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Topmost=!p.WidgetOrDefault.Topmost}});
    public void ToggleLocked()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Locked=!p.WidgetOrDefault.Locked}});
    private void PreferencesChanged()
    {
        if(disposed || Overview.Dispatcher.HasShutdownStarted)return;
        if(!Overview.Dispatcher.CheckAccess()) {Overview.Dispatcher.BeginInvoke(PreferencesChanged);return;}
        var w=preferences.Current.WidgetOrDefault;var shown=false;
        if(w.Visible && !Widget.IsVisible) {Widget.Show();shown=true;Widget.Reposition();}
        if(!w.Visible && Widget.IsVisible)Widget.Hide();
        if(!shown && Widget.IsVisible && (lastWidget is null || w.Placement!=lastWidget.Placement || w.Collapsed!=lastWidget.Collapsed || w.Topmost!=lastWidget.Topmost))
            Widget.Reposition();
        lastWidget=w;
    }
    private void DisplayChanged(object? sender,EventArgs e)=>EnvironmentChanged();
    private void UserPreferenceChanged(object sender,UserPreferenceChangedEventArgs e)=>EnvironmentChanged();
    private void PowerChanged(object sender,PowerModeChangedEventArgs e) {if(e.Mode==PowerModes.Resume)EnvironmentChanged();}
    private void EnvironmentChanged()
    {
        if(disposed || Overview.Dispatcher.HasShutdownStarted)return;
        Overview.Dispatcher.BeginInvoke(()=> {
            if(disposed)return;
            ThemeService.Apply();HidePanel();if(Widget.IsVisible)Widget.Reposition();
            foreach(var model in hub.Models)model.RefreshTheme();
        });
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        preferences.Changed-=PreferencesChanged;Overview.DisplaySettingsRequested-=ShowSettings;Overview.ExitRequested-=OverviewExit;Panel.ExitRequested-=PanelExit;
        Widget.NetworkRequested-=ShowNetwork;Overview.NetworkRequested-=ShowNetwork;networkPanel?.Close();
        Widget.SettingsRequested-=ShowSettings;Widget.ProviderRequested-=ShowProvider;
        Panel.AccountSwitchRequested-=SelectAccount;Panel.ManageAccountsRequested-=ShowAccounts;
        Panel.CliAccountSwitchRequested-=SwitchCliAccount;
        Panel.AccountSlotRequested-=ShowAccountSlot;
        SystemEvents.DisplaySettingsChanged-=DisplayChanged;SystemEvents.UserPreferenceChanged-=UserPreferenceChanged;SystemEvents.PowerModeChanged-=PowerChanged;
        Panel.AllowClose=Widget.AllowClose=Overview.AllowClose=true;
        displaySettings?.Close();Panel.Close();Widget.Close();Overview.Close();
    }
}
