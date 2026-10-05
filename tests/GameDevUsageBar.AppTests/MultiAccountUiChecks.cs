using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using Forms=System.Windows.Forms;

// Every account and response here is synthetic. The adapter never calls its
// query client, and no native credential capture/switch or CLI is exercised.
internal static class MultiAccountUiChecks
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var fixtureRoot=Path.Combine(root,"multi-account-ui-"+Guid.NewGuid().ToString("N"));
        var codex=new FixtureAdapter("codex","Codex","#72D9C6");
        var tripo=new FixtureAdapter("tripo","Tripo","#9EAEFF");
        var claude=new FixtureAdapter("claude","Claude","#ECAA88");
        var adapters=new[]{codex,tripo,claude};
        await using var host=new ApplicationHost(fixtureRoot,adapters);
        await host.InitializeAsync();
        var codexFirst=await Configure(host,codex,host.GetActiveAccount("codex"),"Personal fixture",91,77);
        var codexSecond=await Configure(host,codex,await host.AddAccountAsync("codex","Work fixture"),"Work fixture",17,43);
        var tripoFirst=await Configure(host,tripo,host.GetActiveAccount("tripo"),"Studio fixture",5440);
        var longLabel="Long account label "+new string('W',140)+" 中文标签测试";
        var tripoSecond=await Configure(host,tripo,await host.AddAccountAsync("tripo",longLabel),longLabel,222);
        tripo.Outcomes[tripoSecond.SlotId]=AdapterOutcome.Fail(FailureKind.Forbidden);
        await host.Coordinator.RefreshAsync("tripo",tripoSecond.SlotId);
        await Configure(host,claude,host.GetActiveAccount("claude"),"Writer fixture",55,72);
        using var preferences=new PresentationPreferencesService(new PresentationStore(fixtureRoot),new(CardOrder:["codex","tripo","claude"],FeaturedIds:["codex","tripo"]));
        using var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        var overview=new MainWindow(host,hub,preferences){ShowActivated=false,ShowInTaskbar=false};
        try
        {
            overview.Show();await Idle();
            Check(hub.Models.Count==3&&hub.GetAccountModels("codex").Count==2&&hub.GetAccountModels("tripo").Count==2,"one provider model or independent slot models lost");
            var codexModel=hub.Models.Single(m=>m.Id=="codex");
            var tripoModel=hub.Models.Single(m=>m.Id=="tripo");
            Check(codexModel.AccountChoices.Single(a=>a.SlotId==codexFirst.SlotId).Summary.Contains("91%")&&codexModel.AccountChoices.Single(a=>a.SlotId==codexSecond.SlotId).Summary.Contains("17%"),"slot quota summaries leaked across identities");
            Check(tripoModel.AccountChoices.Single(a=>a.SlotId==tripoSecond.SlotId).Summary.Contains("222")&&tripoModel.AccountChoices.Single(a=>a.SlotId==tripoSecond.SlotId).Status!=tripoModel.AccountChoices.Single(a=>a.SlotId==tripoFirst.SlotId).Status,"failed sibling lost retained values or independent status");
            Check(typeof(AccountChoice).GetProperties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"IsCurrent","Label","SlotId","Status","Summary"}.OrderBy(n=>n)),"picker option exposes credentials or native binding metadata");
            var calls=adapters.Sum(a=>a.Calls.Values.Sum());
            var featured=(ItemsControl)overview.FindName("FeaturedCards");
            var codexPicker=Visuals<AccountPickerView>(featured).Single(p=>((CardModel)p.DataContext).Id=="codex");
            var menu=Open(codexPicker);await Idle();
            var accountItems=menu.Items.OfType<MenuItem>().Where(i=>i.Tag is Guid).ToArray();
            Check(accountItems.Length==2&&accountItems.Single(i=>i.Tag is Guid slot&&slot==codexFirst.SlotId).IsChecked,"current account marker missing");
            Guid? eventSlot=null;string? eventProvider=null;
            overview.AddHandler(AccountPickerView.SelectAccountEvent,new EventHandler<AccountSelectionEventArgs>((_,e)=>{eventSlot=e.SlotId;eventProvider=e.ProviderId;}),true);
            accountItems.Single(i=>i.Tag is Guid slot&&slot==codexSecond.SlotId).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
            await Until(()=>host.GetActiveAccount("codex").SlotId==codexSecond.SlotId&&codexModel.SlotId==codexSecond.SlotId);
            Check(eventProvider=="codex"&&eventSlot==codexSecond.SlotId,"picker routed selection did not preserve provider and local slot");
            Check(codexModel.PrimarySummary.Contains("17%")&&!codexModel.PrimarySummary.Contains("91%")&&tripoModel.SlotId==tripoFirst.SlotId,"overview quick switch retained wrong account or changed sibling provider");
            Check(adapters.Sum(a=>a.Calls.Values.Sum())==calls&&host.Queries.Events.Count==0,"display selection performed a provider query");

            foreach(var language in new[]{"zh-CN","en-US"})
            {
                Localizer.SetLanguage(language);await Idle();
                foreach(var width in new[]{780d,1280d})
                {
                    overview.Width=width;overview.Height=900;overview.UpdateLayout();await Idle();
                    Check(codexModel.AccountChoices.Single(a=>a.IsCurrent).SlotId==codexSecond.SlotId,"language/layout changed displayed account");
                    HorizontalFit(overview);
                    Render(overview,Path.Combine(root,$"multi-account-overview-{language}-{width:0}-150.png"));
                }
                var tripoPicker=Visuals<AccountPickerView>(featured).Single(p=>((CardModel)p.DataContext).Id=="tripo");
                var accountMenu=Open(tripoPicker);await Idle();
                Check(accountMenu.Items.OfType<MenuItem>().Where(i=>i.Tag is Guid).Count()==2&&MenuText(accountMenu).Contains(longLabel),"long account label lost in picker");
                Check(MenuText(accountMenu).Any(text=>text.Contains(Localizer.T("Current account")))&&accountMenu.Items.OfType<MenuItem>().Any(item=>item.Header as string==Localizer.T("Manage accounts")),"picker current/manage labels not localized");
                HorizontalFit(accountMenu);Render(accountMenu,Path.Combine(root,$"multi-account-picker-{language}-150.png"));accountMenu.IsOpen=false;
            }
            string? manageProvider=null;
            codexPicker.AddHandler(AccountPickerView.ManageAccountsEvent,new RoutedEventHandler((_,e)=>{manageProvider=(e.OriginalSource as FrameworkElement)?.DataContext is CardModel m?m.Id:null;e.Handled=true;}));
            menu=Open(codexPicker);await Idle();menu.Items.OfType<MenuItem>().Single(i=>i.Tag is null).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
            Check(manageProvider=="codex","manage event did not bubble from its owning card");
            var claudePicker=Visuals<AccountPickerView>(overview).Single(p=>p.IsVisible&&((CardModel)p.DataContext).Id=="claude");
            menu=Open(claudePicker);await Idle();
            Check(menu.Items.OfType<MenuItem>().Count(i=>i.Tag is Guid)==1&&menu.Items.OfType<MenuItem>().Any(i=>i.Header as string==Localizer.T("Manage accounts")),"single-account provider lost its management entry");menu.IsOpen=false;
            var popup=new TrayPopupWindow(hub){ShowActivated=false,ShowInTaskbar=false};
            try
            {
                popup.SelectProvider("codex");popup.Show();await Idle();
                var popupPicker=Visuals<AccountPickerView>(popup).Single();Guid? selected=null;
                popup.AccountSwitchRequested+=(_,slot)=>selected=slot;
                menu=Open(popupPicker);await Idle();menu.Items.OfType<MenuItem>().Single(i=>i.Tag is Guid slot&&slot==codexFirst.SlotId).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu.IsOpen=false;
                Check(selected==codexFirst.SlotId,"tray popup did not forward rapid selection");
                var hover=new CompactCardView{DataContext=codexModel,ShowActions=false};var holder=new Window{Content=hover,Width=440,SizeToContent=SizeToContent.Height,ShowActivated=false,ShowInTaskbar=false};
                try{holder.Show();await Idle();Check(!Visuals<AccountPickerView>(hover).Single().IsVisible,"hover-only card contains functional account selection");}finally{holder.Close();}
            }
            finally{popup.AllowClose=true;popup.Close();}

            await VerifyManager(host,hub,root,tripoFirst,tripoSecond);
            var nativeManager=new AccountsWindow(host,hub,"codex"){Owner=overview,ShowActivated=false,ShowInTaskbar=false};
            try
            {
                nativeManager.Show();await Idle();
                Check(((FrameworkElement)nativeManager.FindName("NativeActions")).IsVisible&&!((Button)nativeManager.FindName("SwitchButton")).IsEnabled,"manual fixture falsely exposes saved CLI switch capability");
                Check(Visuals<TextBlock>(nativeManager).Any(t=>t.Text==Localizer.T("Each account keeps its own credentials, usage and retry state. Choosing a displayed account does not change the CLI login.")),"account selection explanation is missing");
                Check(Visuals<TextBlock>(nativeManager).Any(t=>t.Text==Localizer.T("Sign in to each account in the CLI yourself, then save its current login here. Saved logins are encrypted for this Windows user.")),"capture explanation is missing");
                Check(Visuals<TextBlock>(nativeManager).Any(t=>t.Text.Contains(Localizer.T("CLI switching changes only its auth file. Close CLI sessions first. Existing desktop sessions are not restarted or changed."))),"native login operation boundary not explained");
                Render(nativeManager,Path.Combine(root,"multi-account-native-manager-150.png"));
            }
            finally{nativeManager.Close();}
            await VerifyTrayAccounts(host,hub,preferences,overview,root,codexFirst,tripoSecond,longLabel);
            Check(host.Queries.Events.Count==0,"UI fixture contacted a provider");
            Console.WriteLine("PASS multi-account UI: independent slot values/status, stable provider cards, real picker routing and overview selection, localized 780/1280 layouts, long-label menu, tray forwarding, native WinForms tray dynamic menus/click/current/status/manage, read-only hover, manager add/select/refresh, CLI action boundary; all data synthetic");
        }
        finally{overview.AllowClose=true;overview.Close();Localizer.SetLanguage("en-US");}
    }
    private static async Task VerifyTrayAccounts(ApplicationHost host,ProviderStateHub hub,PresentationPreferencesService preferences,MainWindow overview,string root,AccountConfig target,AccountConfig longAccount,string longLabel)
    {
        var queries=host.Queries.Events.Count;
        var calls=host.Adapters.Cast<FixtureAdapter>().Sum(a=>a.Calls.Values.Sum());
        var snapshots=host.Adapters.SelectMany(a=>host.Coordinator.GetAccounts(a.Definition.Id)).ToDictionary(s=>(s.Config.ProviderId,s.Config.SlotId),s=>s.LastSuccess);
        using var surfaces=new SurfaceManager(host,hub,preferences,overview,()=>{});
        using var tray=new TrayIconHost(surfaces,hub,preferences,()=>{});
        var notification=(Forms.NotifyIcon)typeof(TrayIconHost).GetField("icon",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
        var context=notification.ContextMenuStrip!;
        Forms.ToolStripMenuItem Accounts()=>context.Items.OfType<Forms.ToolStripMenuItem>().Single(i=>i.Tag as string=="Accounts");
        Forms.ToolStripMenuItem Provider(string id)=>Accounts().DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(i=>i.Text==hub.Models.Single(m=>m.Id==id).Name);
        try
        {
            foreach(var language in new[]{"zh-CN","en-US"})
            {
                Localizer.SetLanguage(language);await Idle();
                context.Show(40,40);await Idle();
                Check(Accounts().DropDownItems.OfType<Forms.ToolStripMenuItem>().Count()==3,"actual tray Opening did not construct provider groups");
                foreach(var id in new[]{"codex","tripo","claude"})
                {
                    var items=Provider(id).DropDownItems.OfType<Forms.ToolStripMenuItem>().Where(i=>i.Text!=Localizer.T("Manage accounts")).ToArray();
                    var models=hub.GetAccountModels(id);
                    Check(items.Length==models.Count&&items.Count(i=>i.Checked)==1,"tray dropped an account or current marker: "+id);
                    for(var index=0;index<models.Count;index++)
                    {
                        Check(items[index].Checked==(models[index].SlotId==host.GetActiveAccount(id).SlotId),"tray current marker belongs to wrong slot");
                        Check((items[index].Text??"").Contains(models[index].CompactBadge),"tray omits live/error/unconfigured state: "+id);
                    }
                    Check(Provider(id).DropDownItems.OfType<Forms.ToolStripMenuItem>().Any(i=>i.Text==Localizer.T("Manage accounts")),"tray provider lacks management entry");
                }
                Accounts().ShowDropDown();Provider("tripo").ShowDropDown();await Idle();
                var tripoMenu=Provider("tripo").DropDown;
                var longItem=Provider("tripo").DropDownItems.OfType<Forms.ToolStripMenuItem>().First(i=>(i.Text??"").StartsWith("Long account label ",StringComparison.Ordinal));
                Check(!(longItem.Text??"").Contains(longLabel)&&(longItem.Text??"").Length<120&&(longItem.ToolTipText??"").Contains(longLabel),"tray long label is not compact or full tooltip was lost");
                Check(tripoMenu.Width<=Math.Min(1000,Forms.Screen.PrimaryScreen!.WorkingArea.Width-80),"tray account submenu is excessively wide");
                RenderTray(tripoMenu,Path.Combine(root,$"multi-account-native-tray-{language}.png"));
                tripoMenu.Close();Accounts().DropDown.Close();context.Close();
            }
            context.Show(40,40);await Idle();
            var targetItem=Provider("codex").DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(i=>(i.Text??"").StartsWith(target.Label+" · ",StringComparison.Ordinal));
            targetItem.PerformClick();context.Close();
            await Until(()=>host.GetActiveAccount("codex").SlotId==target.SlotId&&hub.Models.Single(m=>m.Id=="codex").SlotId==target.SlotId);
            Check(host.GetActiveAccount("tripo").SlotId==longAccount.SlotId,"tray selection changed an unrelated provider");
            context.Show(40,40);await Idle();
            Check(Provider("codex").DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(i=>(i.Text??"").StartsWith(target.Label+" · ",StringComparison.Ordinal)).Checked,"tray reopening did not refresh active account marker");
            Provider("codex").DropDownItems.OfType<Forms.ToolStripMenuItem>().Single(i=>i.Text==Localizer.T("Manage accounts")).PerformClick();context.Close();await Idle();
            var manager=Application.Current.Windows.OfType<AccountsWindow>().Single();
            try
            {
                Check(((TextBlock)manager.FindName("Heading")).Text.StartsWith(hub.Models.Single(m=>m.Id=="codex").Name,StringComparison.Ordinal)&&((ListBox)manager.FindName("AccountList")).Items.Count==2,"tray Manage accounts opened wrong provider");
            }
            finally{manager.Close();}
            Check(host.Queries.Events.Count==queries&&host.Adapters.Cast<FixtureAdapter>().Sum(a=>a.Calls.Values.Sum())==calls,"opening or selecting tray accounts performed a provider query");
            foreach(var state in host.Adapters.SelectMany(a=>host.Coordinator.GetAccounts(a.Definition.Id)))Check(state.LastSuccess==snapshots[(state.Config.ProviderId,state.Config.SlotId)],"tray selection changed an independent quota snapshot");
        }
        finally{context.Close();}
    }
    private static void RenderTray(Forms.ToolStrip menu,string path)
    {
        using var image=new System.Drawing.Bitmap(menu.Width,menu.Height);menu.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,image.Width,image.Height));
        var background=image.GetPixel(image.Width/2,2);Check(background.R<80||SystemParameters.HighContrast,"native tray account submenu reverted to a white theme");image.Save(path);
    }
    private static async Task VerifyManager(ApplicationHost host,ProviderStateHub hub,string root,AccountConfig first,AccountConfig second)
    {
        var manager=new AccountsWindow(host,hub,"tripo"){ShowActivated=false,ShowInTaskbar=false};
        try
        {
            manager.Show();await Idle();var list=(ListBox)manager.FindName("AccountList");
            Check(list.Items.Count==2&&!((FrameworkElement)manager.FindName("NativeActions")).IsVisible,"API manager lost accounts or shows CLI controls");
            list.SelectedItem=hub.GetAccountModels("tripo").Single(m=>m.SlotId==second.SlotId);
            ((Button)manager.FindName("SelectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>host.GetActiveAccount("tripo").SlotId==second.SlotId&&((Button)manager.FindName("AddButton")).IsEnabled);
            Check(list.SelectedItem is CardModel selected&&selected.SlotId==second.SlotId,"manager display selection did not retain selected row");
            var adapter=(FixtureAdapter)host.Adapters.Single(a=>a.Definition.Id=="tripo");
            var firstCalls=adapter.Calls[first.SlotId];var secondCalls=adapter.Calls[second.SlotId];
            ((Button)manager.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>adapter.Calls[second.SlotId]==secondCalls+1&&((Button)manager.FindName("AddButton")).IsEnabled);
            Check(adapter.Calls[first.SlotId]==firstCalls&&host.GetActiveAccount("tripo").SlotId==second.SlotId,"account manager refresh changed or queried the sibling slot");
            foreach(var language in new[]{"zh-CN","en-US"}){Localizer.SetLanguage(language);await Idle();HorizontalFit(manager);Render(manager,Path.Combine(root,$"multi-account-manager-{language}-150.png"));}
            ((TextBox)manager.FindName("NewLabel")).Text="Added from actual button";
            ((Button)manager.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>host.GetAccounts("tripo").Count==3&&((Button)manager.FindName("AddButton")).IsEnabled);
            await Idle();var added=host.GetAccounts("tripo").Single(c=>c.Label=="Added from actual button");
            Check(list.SelectedItem is CardModel account&&account.SlotId==added.SlotId,"new account button lost its pending selection before hub dispatch");
            Check(host.GetActiveAccount("tripo").SlotId==second.SlotId&&!added.Enabled&&added.CredentialRef is null,"adding an account changed active login or inherited another slot's credentials");
            Check(((Button)manager.FindName("EditButton")).IsEnabled&&!((Button)manager.FindName("RefreshButton")).IsEnabled,"unconfigured account action states incorrect");
        }
        finally{manager.Close();}
    }
    private static async Task<AccountConfig> Configure(ApplicationHost host,FixtureAdapter adapter,AccountConfig config,string label,decimal value,decimal? weekly=null)
    {
        config=config with {Label=label,Enabled=true,CredentialRef=Guid.NewGuid(),CredentialRevision=Guid.NewGuid(),CredentialSource="manual"};
        var now=DateTimeOffset.UtcNow;
        adapter.Outcomes[config.SlotId]=weekly is {} week?new([new("five_hour","5-hour remaining",value,"%",MetricKind.Quota,100,now.AddHours(2),WindowSeconds:18000),new("seven_day","Weekly remaining",week,"%",MetricKind.Quota,100,now.AddDays(3),WindowSeconds:604800)],Plan:value>50?"plus":"pro"):new([new("credits","Available",value,"credits")]);
        await host.SaveAsync(config);await host.Coordinator.RefreshAsync(config.ProviderId,config.SlotId);return config;
    }
    private static ContextMenu Open(AccountPickerView picker)
    {
        var button=(ToggleButton)picker.FindName("PickerButton");button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));return button.ContextMenu??throw new Exception("picker did not create a context menu");
    }
    private static string[] MenuText(DependencyObject source)=>Visuals<TextBlock>(source).Select(t=>t.Text).ToArray();
    private static void HorizontalFit(FrameworkElement root)
    {
        foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible&&t.ActualWidth>0))
        {
            var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            Check(bounds.Left>=-1&&bounds.Right<=root.ActualWidth+1,"account text escaped layout: "+text.Text);
            if(text.TextWrapping==TextWrapping.NoWrap&&text.TextTrimming==TextTrimming.None)
            {text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Check(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1,"account label is clipped: "+text.Text);}
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var timer=Stopwatch.StartNew();while(!predicate()){if(timer.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("fixture UI operation did not settle");await Task.Delay(20);await Idle();}await Idle();
    }
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,index)))yield return child;}
    private static void Render(FrameworkElement element,string path)
    {
        element.UpdateLayout();const double scale=1.5;var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class FixtureAdapter(string id,string name,string accent):IProviderAdapter
    {
        public ProviderDefinition Definition{get;}=new(id,name,id is "codex" or "claude"?"Subscription":"API credits","Synthetic account UI fixture",accent,new("invalid.test",443,"/unused"));
        public ConcurrentDictionary<Guid,AdapterOutcome> Outcomes{get;}=new();public ConcurrentDictionary<Guid,int> Calls{get;}=new();
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct)
        {Calls.AddOrUpdate(config.SlotId,1,(_,count)=>count+1);return Task.FromResult(Outcomes.GetValueOrDefault(config.SlotId,AdapterOutcome.Fail(FailureKind.NoData)));}
    }
}
