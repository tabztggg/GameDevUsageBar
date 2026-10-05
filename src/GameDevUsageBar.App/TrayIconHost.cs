using GameDevUsageBar.App.Presentation;
using Forms=System.Windows.Forms;
namespace GameDevUsageBar.App;
public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon artwork;
    private readonly PresentationPreferencesService preferences;
    private readonly System.Windows.Threading.Dispatcher dispatcher;
    private readonly Forms.ToolStripMenuItem widget,top,locked;
    private readonly Forms.ToolStripMenuItem accountMenu;
    private bool disposed;
    public TrayIconHost(SurfaceManager surfaces,ProviderStateHub hub,PresentationPreferencesService preferences,Action exit)
    {
        this.preferences=preferences;dispatcher=surfaces.Overview.Dispatcher;
        using var resource=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/GameDevUsageBar;component/Assets/GameDevUsageBar.ico")).Stream;
        artwork=new System.Drawing.Icon(resource,32,32);
        icon=new Forms.NotifyIcon {Icon=artwork,Text="GameDevUsageBar · AI usage",Visible=true};
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Open &overview",null,(_,_)=>dispatcher.Invoke(surfaces.ShowOverview));
        menu.Items.Add("&Refresh all",null,(_,_)=>dispatcher.Invoke(hub.RefreshAll));
        menu.Items.Add("&Display settings…",null,(_,_)=>dispatcher.Invoke(surfaces.ShowSettings));
        accountMenu=(Forms.ToolStripMenuItem)menu.Items.Add("Accounts");
        menu.Opening+=(_,_)=>{
            accountMenu.DropDownItems.Clear();
            foreach(var model in hub.Models.Where(m=>!m.Definition.IsDemo&&m.Definition.CanConfigure)){
                var providerMenu=new Forms.ToolStripMenuItem(model.Name);
                foreach(var account in hub.GetAccountModels(model.Id)){
                    var fullLabel=account.State.Config.Label;var label=fullLabel;
                    if(label.Length>36){var length=char.IsHighSurrogate(label[35])?35:36;label=label[..length]+"…";}
                    var details=string.Join(" · ",new[]{account.PrimarySummary,account.CompactBadge}.Where(value=>value.Length>0));
                    var choice=new Forms.ToolStripMenuItem(label+" · "+details){Checked=account.SlotId==model.SlotId,ToolTipText=fullLabel+" · "+details};
                    var id=model.Id;var slot=account.SlotId;choice.Click+=(_,_)=>dispatcher.Invoke(()=>surfaces.SelectAccount(id,slot));providerMenu.DropDownItems.Add(choice);
                }
                providerMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
                var manage=new Forms.ToolStripMenuItem(L.T("Manage accounts"));var providerId=model.Id;manage.Click+=(_,_)=>dispatcher.Invoke(()=>surfaces.ShowAccounts(providerId));providerMenu.DropDownItems.Add(manage);
                accountMenu.DropDownItems.Add(providerMenu);
            }
            TrayMenuTheme.Apply(menu);
        };
        widget=(Forms.ToolStripMenuItem)menu.Items.Add("Show desktop &widget",null,(_,_)=>dispatcher.Invoke(surfaces.ToggleWidget));
        top=(Forms.ToolStripMenuItem)menu.Items.Add("Widget always on &top",null,(_,_)=>dispatcher.Invoke(surfaces.ToggleTopmost));
        locked=(Forms.ToolStripMenuItem)menu.Items.Add("&Lock widget position",null,(_,_)=>dispatcher.Invoke(surfaces.ToggleLocked));
        menu.Items.Add("Export diagnostics…",null,(_,_)=>dispatcher.Invoke(()=> {surfaces.ShowOverview();surfaces.Overview.ExportDiagnostics();}));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("E&xit",null,(_,_)=>dispatcher.Invoke(exit));
        TrayMenuTheme.Apply(menu);menu.Opening+=(_,_)=>TrayMenuTheme.Apply(menu);
        icon.ContextMenuStrip=menu;
        icon.MouseDown+=(_,e)=> {if(e.Button==Forms.MouseButtons.Left)dispatcher.Invoke(surfaces.TrayPointerDown);};
        icon.MouseClick+=(_,e)=> {if(e.Button==Forms.MouseButtons.Left)dispatcher.Invoke(surfaces.TrayClick);};
        foreach(var item in menu.Items.OfType<Forms.ToolStripMenuItem>())item.Tag=item.Text;
        preferences.Changed+=Changed;L.Changed+=Changed;Changed();
    }
    private void Changed()
    {
        if(disposed || dispatcher.HasShutdownStarted)return;
        if(!dispatcher.CheckAccess()) {dispatcher.BeginInvoke(Changed);return;}
        var w=preferences.Current.WidgetOrDefault;
        icon.Text=L.T("GameDevUsageBar · AI usage");
        foreach(var item in icon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>())if(item.Tag is string label)item.Text=L.T(label);
        widget.Checked=w.Visible;top.Checked=w.Topmost;locked.Checked=w.Locked;
    }
    public void Dispose() {if(disposed)return;disposed=true;preferences.Changed-=Changed;L.Changed-=Changed;icon.Visible=false;icon.ContextMenuStrip?.Dispose();icon.Dispose();artwork.Dispose();}
}
