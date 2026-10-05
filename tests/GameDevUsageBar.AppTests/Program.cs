using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

internal static class Program
{
    private static int failed;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr handle,int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle,uint msg,IntPtr wParam,IntPtr lParam);
    [STAThread]
    private static int Main(string[] args)
    {
        var app=new System.Windows.Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri("pack://application:,,,/GameDevUsageBar;component/Themes/Resources.xaml")});ThemeService.Apply();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        app.Dispatcher.BeginInvoke(async () =>
        {
            if (!args.Any(a => a is "--multi-account" or "--overview-design" or "--compact-design"))
            {
                await RunAsync(app, args.Contains("--interactive"));
                return;
            }
            try
            {
                var qa = Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT")
                    ?? Path.Combine(Path.GetTempPath(), "WorkBuddy-Tasks", "work", "gamedevusagebar-multiaccount-20261004", "workspace");
                Directory.CreateDirectory(qa);
                if (args.Contains("--multi-account"))
                {
                    await MultiAccountUiChecks.Run(qa);
                    await NativeHostChecks.Run(qa);
                }
                else if (args.Contains("--overview-design")) await OverviewDesignChecks.Run(qa);
                else
                {
                    await CompactDesignChecks.Run(qa);
                    await AdditionalSurfaceChecks.Run(qa);
                    await StripDesignChecks.Run(qa);
                    await TransparencyChecks.Run(qa);
                }
            }
            catch (Exception e) { failed++; Console.WriteLine(e); }
            finally { await ShutdownAsync(app); }
        });
        return app.Run();
    }
    private static async Task ShutdownAsync(System.Windows.Application app)
    {
        // Closed windows and popups enqueue resource invalidation. Drain that work while
        // Application.Resources is still alive; Application.Shutdown owns dispatcher exit.
        await Dispatcher.Yield(DispatcherPriority.SystemIdle);
        app.Shutdown(failed == 0 ? 0 : 1);
    }
    private static async Task RunAsync(System.Windows.Application app,bool interactive)
    {
        var root=Path.Combine(Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT") ?? Path.Combine(Path.GetTempPath(),"WorkBuddy-Tasks","work","gamedevusagebar-provider-repair-20261004","workspace"),"ui-tests-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        var checks=new List<object>();
        ApplicationHost? host=null;ProviderStateHub? hub=null;SurfaceManager? surfaces=null;PresentationPreferencesService? preferences=null;TrayIconHost? tray=null;Window? sentinel=null;
        async Task Check(string name,Func<Task> check)
        {
            try {await check();checks.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
            catch(Exception e){failed++;checks.Add(new{name,status="FAIL",error=e.ToString()});Console.WriteLine("FAIL "+name+" "+e.Message);}
        }
        void Assert(bool value,string detail) {if(!value)throw new Exception(detail);}
        try {
            var limited=new FixtureAdapter("limited","Rate-limit fixture",FailureKind.RateLimited);
            var unauthorized=new FixtureAdapter("unauthorized","Authentication fixture",FailureKind.Unauthorized);
            var demo=new FixtureAdapter("demo","Demo sandbox",null);
            host=new(root,new[]{demo,limited,unauthorized});
            await host.InitializeAsync();
            foreach(var c in host.Accounts.ToArray())await host.SaveAsync(c with {Enabled=true});
            foreach(var a in host.Adapters)await host.Coordinator.RefreshAsync(a.Definition.Id);
            var snapshot=host.Adapters.ToDictionary(a=>a.Definition.Id,a=>host.Coordinator.Get(a.Definition.Id));
            var initialCalls=limited.Calls+unauthorized.Calls+demo.Calls;
            var settings=File.ReadAllBytes(Path.Combine(root,"settings.json"));
            preferences=new(new PresentationStore(root),new(CardOrder:["demo","limited","unauthorized"],Widget:new(CardIds:["demo"])));
            hub=new(host.Adapters,host.Coordinator,preferences,app.Dispatcher);
            var overview=new MainWindow(host,hub,preferences);app.MainWindow=overview;
            var close=new TaskCompletionSource();
            surfaces=new(host,hub,preferences,overview,()=>close.TrySetResult());
            tray=new(surfaces,hub,preferences,()=>close.TrySetResult());
            surfaces.ShowOverview();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Check("One model per adapter and independent view filters",()=> {
                Assert(hub.Models.Count==3,"wrong model count");
                var empty=hub.CreateView(_=>false);var full=hub.CreateView(_=>true);
                Assert(empty.IsEmpty && !full.IsEmpty,"shared default view filter leaked");return Task.CompletedTask;
            });
            await Check("Showing overview does not rewrite accounts or query enabled Demo",()=> {
                Assert(settings.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"settings.json"))),"startup rewrote account settings");
                Assert(initialCalls==limited.Calls+unauthorized.Calls+demo.Calls,"window initialization caused a refresh");return Task.CompletedTask;
            });
            await Check("Real overview reorder keeps backoff and account files unchanged",async ()=> {
                overview.UpdateLayout();
                var items=(ItemsControl)overview.FindName("Cards");
                var presenter=(ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0);
                presenter.ApplyTemplate();
                var buttons=FindVisual<Button>(presenter).Where(b=>b.Tag as string=="demo").ToArray();
                var down=buttons.Single(b=>b.Content as string=="↓");
                down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=true,Topmost=true,Collapsed=true,Locked=true}});
                await preferences.FlushAsync();
                Assert(hub.Models[1].Id=="demo","actual reorder handler did not move the card");
                foreach(var a in host.Adapters) {
                    Assert(snapshot[a.Definition.Id]==host.Coordinator.Get(a.Definition.Id),"presentation changed coordinator state");
                    await host.Coordinator.RefreshAsync(a.Definition.Id,false);
                }
                Assert(settings.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"settings.json"))),"layout rewrote settings");
                Assert(initialCalls==limited.Calls+unauthorized.Calls+demo.Calls,"presentation caused extra calls");
            });
            await Check("Widget show and state updates do not activate it",async ()=> {
                sentinel=new Window {Title="GameDevUsageBar QA focus sentinel",Width=180,Height=100};
                sentinel.Show();sentinel.Activate();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);var foreground=GetForegroundWindow();
                Assert(foreground==NativeWindows.Handle(sentinel),"focus sentinel did not establish the test baseline");
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=false}});
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=true,Topmost=false,Collapsed=false,Locked=false}});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(GetForegroundWindow()==foreground,"widget show stole focus");
                var flags=GetStyle(NativeWindows.Handle(surfaces.Widget),-20).ToInt64();
                Assert((flags&0x08000000L)!=0 && (flags&0x80L)!=0,"no-activate/tool-window flags absent");
                Assert(SendMessage(NativeWindows.Handle(surfaces.Widget),0x21,IntPtr.Zero,IntPtr.Zero)==new IntPtr(3),"mouse activation not rejected");
                await host.Coordinator.RefreshAsync("demo");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(GetForegroundWindow()==foreground,"state update stole focus");
            });
            await Check("Legacy collapse preference cannot restore a tall widget; topmost matches native state",()=> {
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Collapsed=true,Topmost=true}});
                Assert(Math.Abs(surfaces.Widget.Height-36)<1,"single-row height wrong");
                Assert((GetStyle(NativeWindows.Handle(surfaces.Widget),-20).ToInt64()&8)!=0,"topmost flag absent");
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Collapsed=false,Topmost=false}});
                Assert(Math.Abs(surfaces.Widget.Height-36)<1,"legacy expand changed single-row height");
                Assert((GetStyle(NativeWindows.Handle(surfaces.Widget),-20).ToInt64()&8)==0,"topmost flag stuck");return Task.CompletedTask;
            });
            await Check("Single-row geometry fits visible text and icons with no progress bars",async ()=> {
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=["demo","limited","unauthorized"]}});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);surfaces.Widget.UpdateLayout();
                Assert(!FindVisual<ProgressBar>(surfaces.Widget).Any(),"widget has a progress bar");
                var labels=FindVisual<TextBlock>(surfaces.Widget).Where(t=>{var text=string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(run=>run.Text));return text.Contains("cr")||text=="401"||text=="429";}).ToArray();
                Assert(labels.Length>=3,"metric cells missing");
                RenderWidget(surfaces.Widget,Path.Combine(root,"geometry-before-assert.png"),1.5);
                foreach(var label in labels){var bounds=label.TransformToAncestor(surfaces.Widget).TransformBounds(new Rect(label.RenderSize));Assert(bounds.Top>=0 && bounds.Bottom<=36 && bounds.Left>=0 && bounds.Right<=surfaces.Widget.ActualWidth,$"metric clipped: {label.Text}, bounds {bounds}, window {surfaces.Widget.ActualWidth}x{surfaces.Widget.ActualHeight}, desired {((FrameworkElement)surfaces.Widget.FindName("BarRow")).DesiredSize}");Assert(label.ActualHeight>=label.DesiredSize.Height,"text height clipped");}
                foreach(var icon in FindVisual<System.Windows.Controls.Image>(surfaces.Widget)){Assert(icon.Width==12 && icon.Height==12 && icon.ActualWidth>0 && icon.ActualWidth<=15 && icon.ActualHeight>0 && icon.ActualHeight<=15,"provider icon exceeds the small uniform-fit box: "+icon.RenderSize);}
                foreach(var button in FindVisual<Button>(surfaces.Widget)){var bounds=button.TransformToAncestor(surfaces.Widget).TransformBounds(new Rect(button.RenderSize));Assert(bounds.Left>=0 && bounds.Right<=surfaces.Widget.ActualWidth && bounds.Top>=0 && bounds.Bottom<=36,"widget control clipped");}
                var y=labels[0].TransformToAncestor(surfaces.Widget).Transform(new Point()).Y;
                Assert(labels.All(t=>Math.Abs(t.TransformToAncestor(surfaces.Widget).Transform(new Point()).Y-y)<1),"metric baselines not aligned");
                foreach(var scale in new[]{1d,1.5,2d})RenderWidget(surfaces.Widget,Path.Combine(root,"fixture-bar-"+(int)(scale*100)+".png"),scale);
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=[]}});surfaces.Widget.UpdateLayout();
                Assert(((Button)surfaces.Widget.FindName("EmptyMessage")).IsVisible && surfaces.Widget.Height==36,"empty state does not fit single row");
                RenderWidget(surfaces.Widget,Path.Combine(root,"fixture-empty-150.png"),1.5);
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=["demo"]}});
            });
            await Check("Many selected providers overflow as complete cells without queries",async ()=> {
                var adapters=Enumerable.Range(0,20).Select(i=>new FixtureAdapter("source"+i,"Source "+i,null)).ToArray();
                await using var overflowHost=new ApplicationHost(Path.Combine(root,"overflow"),adapters);
                await overflowHost.InitializeAsync();
                using var overflowPreferences=new PresentationPreferencesService(new PresentationStore(overflowHost.Root),new(Widget:new(CardIds:adapters.Select(a=>a.Definition.Id).ToArray())));
                using var overflowHub=new ProviderStateHub(adapters,overflowHost.Coordinator,overflowPreferences,app.Dispatcher);
                var widget=new WidgetWindow(overflowHub,overflowPreferences);
                try {
                    widget.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);widget.UpdateLayout();
                    var cells=((StackPanel)widget.FindName("Cards")).Children.Count;
                    Assert(cells>0 && cells<20 && widget.Width<=1100 && widget.Height==36,"whole-cell overflow missing");
                    foreach(var button in FindVisual<Button>(widget)){var bounds=button.TransformToAncestor(widget).TransformBounds(new Rect(button.RenderSize));Assert(bounds.Right<=widget.ActualWidth && bounds.Bottom<=36,"overflow clipped a cell");}
                    Assert(adapters.All(a=>a.Calls==0),"overflow triggered a provider request");
                    RenderWidget(widget,Path.Combine(root,"fixture-overflow-150.png"),1.5);
                } finally {widget.AllowClose=true;widget.Close();}
            });
            await Check("Provider icon resources render as frozen local vectors",()=> {
                foreach(var id in new[]{"claude","codex","gemini","gemini-cli","deepseek","openrouter","elevenlabs","tripo","grsai","typesafe"}){var icon=ProviderArtwork.For(id,"#ffffff");Assert(icon.IsFrozen && icon.Width>0 && icon.Height>0,"invalid provider icon: "+id);}
                return Task.CompletedTask;
            });
            await Check("Popup open-close and settings handoff with a visible owner",()=> {
                surfaces.TrayPointerDown();surfaces.TrayClick();Assert(surfaces.Panel.IsVisible,"popup did not show");
                surfaces.TrayPointerDown();surfaces.TrayClick();Assert(!surfaces.Panel.IsVisible,"popup did not close");
                surfaces.ShowSettings();
                var settingsWindow=app.Windows.OfType<DisplaySettingsWindow>().Single();
                Assert(settingsWindow.Owner==overview && overview.IsVisible && !surfaces.Panel.IsVisible,"wrong dialog handoff");
                settingsWindow.Close();
                surfaces.Panel.SelectProvider("demo");Assert(((ItemsControl)surfaces.Panel.FindName("Cards")).Items.Count==1,"selected-provider detail missing");
                surfaces.Panel.SelectProvider(null);return Task.CompletedTask;
            });
            await Check("Recovered placement does not replace preferred monitor coordinates",async ()=> {
                var saved=new SavedPlacement("missing-monitor",new(-10000,-10000,1920,1080),-9000,-9000);
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Placement=saved}});
                await preferences.FlushAsync();var bytes=File.ReadAllBytes(Path.Combine(root,"presentation.json"));
                surfaces.Widget.Reposition();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(preferences.Current.WidgetOrDefault.Placement==saved,"preferred coordinates were replaced");
                Assert(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"presentation.json"))),"recovery saved layout");
                var rect=NativeWindows.Bounds(surfaces.Widget);
                Assert(NativeWindows.Monitors().Any(m=>rect.Left>=m.Work.Left-1 && rect.Top>=m.Work.Top-1 && rect.Right<=m.Work.Right+1 && rect.Bottom<=m.Work.Bottom+1),"widget off-screen");
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Placement=null}});
            });
            await Check("Background state changes marshal to the dispatcher; unknown IDs ignored",async ()=> {
                var c=host.Coordinator.Get("demo").Config with {Label="Thread-pool fixture"};
                await Task.Run(()=>host.Coordinator.ConfigureAsync(demo,c));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(hub.Models.Single(m=>m.Id=="demo").State.Config.Label==c.Label,"dispatcher projection missing");
                await host.Coordinator.ConfigureAsync(new FixtureAdapter("unknown","Unknown fixture",null),new("unknown",Guid.NewGuid(),"Unknown"));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert(hub.Models.Count==3,"unknown ID entered UI");
                await host.Coordinator.RefreshAsync("demo");
            });
            await Check("Disposed hub no longer receives coordinator changes",async ()=> {
                using var other=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,app.Dispatcher);
                var model=other.Models.Single(m=>m.Id=="demo");var before=model.State;other.Dispose();
                await host.Coordinator.ConfigureAsync(demo,host.Coordinator.Get("demo").Config with {Label="After dispose"});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert(model.State==before,"disposed hub updated");
                await host.Coordinator.RefreshAsync("demo");
            });
            await Check("Language selectors update every surface live without queries or account writes",async ()=> {
                var queryCount=limited.Calls+unauthorized.Calls+demo.Calls;
                var before=host.Adapters.ToDictionary(a=>a.Definition.Id,a=>host.Coordinator.Get(a.Definition.Id));
                var display=new DisplaySettingsWindow(preferences,hub){Owner=overview};display.Show();
                var accountWindow=new AccountWindow(host,demo.Definition,host.Coordinator.Get("demo").Config){Owner=overview};
                accountWindow.Show();
                ((ComboBox)accountWindow.FindName("Scenario")).SelectedValue="Zero";
                var accountLabel=((TextBox)accountWindow.FindName("AccountLabel")).Text;
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=["demo","limited","unauthorized"]}});
                try {
                    ((ComboBox)overview.FindName("LanguageSelector")).SelectedValue="zh-CN";
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);surfaces.Widget.UpdateLayout();
                    Assert(Localizer.Language=="zh-CN" && preferences.Current.Language=="zh-CN","overview selector did not persist Chinese");
                    Assert(FindVisual<Button>(overview).Any(b=>b.Content as string=="显示设置"),"overview static label did not switch");
                    Assert(((TextBlock)overview.FindName("OverviewStatus")).Text.Contains("已启用"),"overview summary not localized");
                    Assert(display.Title.Contains("显示设置") && ((ComboBox)display.FindName("LanguageSelector")).SelectedValue as string=="zh-CN","settings window did not synchronize");
                    Assert(accountWindow.Title.Contains("服务设置") && ((ComboBox)accountWindow.FindName("Scenario")).SelectedValue as string=="Zero","account label or demo identity changed");
                    Assert(((TextBox)accountWindow.FindName("AccountLabel")).Text==accountLabel,"custom account label changed");
                    Assert(hub.Models.Single(m=>m.Id=="demo").BarValue.Contains("演示") && hub.Models.Single(m=>m.Id=="demo").Metrics.First().Label=="可用","metric units or Demo provenance not localized");
                    Assert(hub.Models.Single(m=>m.Id=="unauthorized").Status=="密钥被拒绝（401）","failure text not localized");
                    Assert(FindVisual<Button>(surfaces.Panel).Any(b=>b.Content as string=="总览"),"popup did not switch");
                    var notification=(System.Windows.Forms.NotifyIcon)typeof(TrayIconHost).GetField("icon",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(tray)!;
                    Assert(notification.ContextMenuStrip!.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i=>i.Text=="打开总览(&O)"),"tray did not switch");
                    foreach(var button in FindVisual<Button>(surfaces.Widget)){var box=button.TransformToAncestor(surfaces.Widget).TransformBounds(new Rect(button.RenderSize));Assert(box.Right<=surfaces.Widget.ActualWidth && box.Bottom<=36,"Chinese widget clipped");}
                    RenderWidget(surfaces.Widget,Path.Combine(root,"fixture-bar-zh-150.png"),1.5);
                    RenderWindow(overview,Path.Combine(root,"fixture-overview-zh-150.png"),1.5);
                    RenderWindow(display,Path.Combine(root,"fixture-settings-zh-150.png"),1.5);
                    ((ComboBox)display.FindName("LanguageSelector")).SelectedValue="en-US";
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(FindVisual<Button>(overview).Any(b=>b.Content as string=="Display settings") && accountWindow.Title.EndsWith("Service settings"),"English restoration failed");
                    Assert(notification.ContextMenuStrip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Any(i=>i.Text=="Open &overview"),"English tray restoration failed");
                    preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=[]}});
                    foreach(var language in new[]{"zh-CN","en-US"}){preferences.Update(p=>p with {Language=language});await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);surfaces.Widget.UpdateLayout();RenderWidget(surfaces.Widget,Path.Combine(root,"fixture-empty-"+language+"-150.png"),1.5);Assert(surfaces.Widget.Height==36 && ((Button)surfaces.Widget.FindName("EmptyMessage")).IsVisible,"localized empty state missing");foreach(var button in FindVisual<Button>(surfaces.Widget)){var box=button.TransformToAncestor(surfaces.Widget).TransformBounds(new Rect(button.RenderSize));Assert(box.Right<=surfaces.Widget.ActualWidth,$"localized empty state clipped: {language}, {button.Name}, {box}, window={surfaces.Widget.ActualWidth}, row={((FrameworkElement)surfaces.Widget.FindName("BarRow")).DesiredSize}");}}
                    Assert(queryCount==limited.Calls+unauthorized.Calls+demo.Calls && settings.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"settings.json"))),"language caused a query or account write");
                    Assert(before.All(item=>item.Value==host.Coordinator.Get(item.Key)),"language changed coordinator/backoff state");
                } finally {accountWindow.Close();display.Close();Localizer.SetLanguage("en-US");}
            });
            await Check("GRSAI unrestricted-key cards and compact values are readable in both languages",()=> {
                var now=DateTimeOffset.UtcNow;var config=new AccountConfig("grsai",Guid.NewGuid(),"Fixture",true,Guid.NewGuid());
                var definition=new ProviderDefinition("grsai","GRSAI","API key credits","Key-scoped credits · separate from account balance","#F1BB66",new("grsaiapi.com",443,"/client/openapi/getAPIKeyCredits"));
                var state=new ProviderState(config,new(config.Binding(definition),DataOrigin.Live,now,[new("key-limit","Key credit limit",null,"credits",MetricKind.Unlimited)]),now,null,false,null);
                foreach(var language in new[]{"en-US","zh-CN"}) {
                    Localizer.SetLanguage(language);var model=new CardModel(definition,state);
                    var expected=language=="zh-CN"?"密钥不限额":"No key limit";
                    Assert(model.Metrics.Single().Display==expected && model.BarValue==expected,"unrestricted key became a zero balance");
                    Assert(model.Summary==Localizer.T("Key has no credit limit; account balance requires an account query token"),"account balance explanation missing");
                    var card=new CompactCardView{DataContext=model};var window=new Window{Content=card,Width=420,SizeToContent=SizeToContent.Height,ShowActivated=false,ShowInTaskbar=false};
                    try {window.Show();window.UpdateLayout();Assert(FindVisual<TextBlock>(window).Any(t=>t.Text==expected),"unrestricted label missing from rendered WPF card");RenderWindow(window,Path.Combine(root,"fixture-grsai-unrestricted-"+language+"-150.png"),1.5);}
                    finally {window.Close();}
                }
                Localizer.SetLanguage("en-US");return Task.CompletedTask;
            });
            await Check("Persisted language reloads and disposed views release translation subscriptions",async ()=> {
                preferences.Update(p=>p with {Language="zh-CN"});await preferences.FlushAsync();
                var loaded=await new PresentationStore(root).LoadAsync();Assert(loaded?.Language=="zh-CN","language not persisted");
                using var other=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,app.Dispatcher);
                var notifications=0;other.Changed+=()=>notifications++;other.Dispose();
                preferences.Update(p=>p with {Language="en-US"});
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Assert(notifications==0,"disposed hub retained language subscription");
            });
            await Check("Tripo selector previews both endpoints and saves the selected version with its key",async ()=> {
                await using var regionalHost=new ApplicationHost(Path.Combine(root,"regional-settings"));
                await regionalHost.InitializeAsync();
                var adapter=regionalHost.Adapters.Single(a=>a.Definition.Id=="tripo");
                var initial=regionalHost.Coordinator.Get("tripo").Config;
                await regionalHost.SaveAsync(initial,"nonfunctional-global-region-fixture");
                initial=regionalHost.Coordinator.Get("tripo").Config;
                var bytes=File.ReadAllBytes(Path.Combine(regionalHost.Root,"settings.json"));
                var dialog=new AccountWindow(regionalHost,adapter.Definition,initial){Owner=overview};dialog.Show();
                try {
                    var selector=(ComboBox)dialog.FindName("RegionSelector");
                    Assert(selector.IsVisible && selector.SelectedValue as string=="global","legacy selector default incorrect");
                    Assert(((TextBlock)dialog.FindName("EndpointText")).Text.Contains("openapi.tripo3d.ai"),"global preview wrong");
                    selector.SelectedValue="china";
                    Localizer.SetLanguage("zh-CN");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(selector.SelectedValue as string=="china" && ((TextBlock)dialog.FindName("EndpointText")).Text.Contains("openapi.tripo3d.com"),"China preview or language switch lost selection");
                    Assert(((TextBlock)dialog.FindName("RegionHint")).Text.Contains("全球版"),"saved-key version explanation missing");
                    Assert(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(regionalHost.Root,"settings.json"))) && regionalHost.Queries.Events.Count==0,"preview persisted or queried");
                    ((CheckBox)dialog.FindName("Enabled")).IsChecked=true;
                    ((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(!dialog.Saved && ((TextBlock)dialog.FindName("Feedback")).Text.Contains("所选 Tripo"),"cross-region key reuse not rejected");
                    RenderWindow(dialog,Path.Combine(root,"fixture-tripo-China-zh-150.png"),1.5);
                    Localizer.SetLanguage("en-US");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    RenderWindow(dialog,Path.Combine(root,"fixture-tripo-China-en-150.png"),1.5);
                    ((PasswordBox)dialog.FindName("SecretInput")).Password="nonfunctional-china-region-fixture";
                    ((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline=DateTime.UtcNow.AddSeconds(5);while(!dialog.Saved && DateTime.UtcNow<deadline)await Task.Delay(20);
                    Assert(dialog.Saved,"selected region save failed");
                    var saved=regionalHost.Coordinator.Get("tripo").Config;
                    Assert(saved.TripoRegion=="china" && saved.CredentialRegion=="china" && saved.HasUsableCredential && saved.CredentialRef!=initial.CredentialRef,"regional key binding incorrect");
                    Assert(regionalHost.Queries.Events.Count==0,"saving performed a query inside the settings window");
                    var model=new CardModel(adapter.Definition,regionalHost.Coordinator.Get("tripo"));
                    Assert(model.Channel.Contains("China"),"source identity does not show version");
                    await using var reloaded=new ApplicationHost(regionalHost.Root);await reloaded.InitializeAsync();
                    Assert(reloaded.Coordinator.Get("tripo").Config==saved,"region did not survive host reload");
                } finally {Localizer.SetLanguage("en-US");if(dialog.IsVisible)dialog.Close();}
            });
            await Check("Other services do not expose a Tripo version selector",()=> {
                var dialog=new AccountWindow(host,demo.Definition,host.Coordinator.Get("demo").Config){Owner=overview};dialog.Show();
                try {Assert(!((StackPanel)dialog.FindName("RegionFields")).IsVisible,"region controls leaked to other providers");}
                finally {dialog.Close();}
                return Task.CompletedTask;
            });
            await Check("New provider source controls preserve selections across languages without queries",async()=> {
                await using var sourceHost=new ApplicationHost(Path.Combine(root,"source-previews"));await sourceHost.InitializeAsync();
                foreach(var id in new[]{"claude","codex","gemini-cli","gemini","grsai"}) {
                    var dialog=new AccountWindow(sourceHost,sourceHost.Adapters.Single(a=>a.Definition.Id==id).Definition,sourceHost.Coordinator.Get(id).Config){Owner=overview};dialog.Show();
                    try {
                        var selector=(ComboBox)dialog.FindName("SourceSelector");Assert(selector.IsVisible,"source selector missing: "+id);
                        Assert(((StackPanel)dialog.FindName("ProjectFields")).IsVisible==(id=="gemini"),"project fields wrong: "+id);
                        Assert(((StackPanel)dialog.FindName("RegionFields")).IsVisible==(id is "grsai"),"region fields wrong: "+id);
                        if(id=="claude") {selector.SelectedValue="web-cookie";Assert(((StackPanel)dialog.FindName("OrganizationFields")).IsVisible,"organization missing");}
                        if(id is "codex" or "gemini-cli") {selector.SelectedValue="local-oauth";Assert(!((StackPanel)dialog.FindName("SecretFields")).IsVisible,"local mode requests a copied key");}
                        var selected=selector.SelectedValue;Localizer.SetLanguage("zh-CN");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert(Equals(selector.SelectedValue,selected)&&((TextBlock)dialog.FindName("SourceTitle")).Text=="认证来源","language lost source selection");
                        RenderWindow(dialog,Path.Combine(root,"fixture-source-"+id+"-zh-150.png"),1.5);
                    } finally {dialog.Close();Localizer.SetLanguage("en-US");}
                }
                Assert(sourceHost.Queries.Events.Count==0&&!File.Exists(Path.Combine(sourceHost.Root,"settings.json")),"preview queried or saved account settings");
            });
            await Check("Changing Claude OAuth to Cookie requires a matching credential and persists schema 3",async()=> {
                await using var sourceHost=new ApplicationHost(Path.Combine(root,"cookie-settings"));await sourceHost.InitializeAsync();
                var initial=sourceHost.Coordinator.Get("claude").Config;await sourceHost.SaveAsync(initial,"nonfunctional-oauth-fixture");initial=sourceHost.Coordinator.Get("claude").Config;
                var dialog=new AccountWindow(sourceHost,sourceHost.Adapters.Single(a=>a.Definition.Id=="claude").Definition,initial){Owner=overview};dialog.Show();
                try {
                    ((ComboBox)dialog.FindName("SourceSelector")).SelectedValue="web-cookie";((TextBox)dialog.FindName("OrganizationInput")).Text=Guid.NewGuid().ToString();((CheckBox)dialog.FindName("Enabled")).IsChecked=true;
                    ((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(!dialog.Saved&&((TextBlock)dialog.FindName("Feedback")).Text.Contains("selected source credential"),"old OAuth reused as Cookie");
                    ((PasswordBox)dialog.FindName("SecretInput")).Password="session=nonfunctional-cookie-fixture";((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline=DateTime.UtcNow.AddSeconds(5);while(!dialog.Saved&&DateTime.UtcNow<deadline)await Task.Delay(20);Assert(dialog.Saved,"Cookie save failed");
                    var saved=sourceHost.Coordinator.Get("claude").Config;Assert(saved.SourceMode=="web-cookie"&&saved.CredentialSource=="web-cookie"&&saved.HasUsableCredential&&saved.CredentialRef!=initial.CredentialRef,"source binding wrong");
                    await using var reloaded=new ApplicationHost(sourceHost.Root);await reloaded.InitializeAsync();Assert(reloaded.Coordinator.Get("claude").Config==saved,"source binding did not reload");
                    Assert(sourceHost.Queries.Events.Count==0,"settings performed a usage query");
                } finally {if(dialog.IsVisible)dialog.Close();}
            });
            await Check("Local CLI settings connect without copying credentials; removing connection preserves native file",async()=> {
                var nativeHome=Path.Combine(root,"native-ui-home");Directory.CreateDirectory(Path.Combine(nativeHome,".claude"));var file=Path.Combine(nativeHome,".claude",".credentials.json");
                await File.WriteAllTextAsync(file,"""{"claudeAiOauth":{"accessToken":"nonfunctional-native-fixture","refreshToken":"nonfunctional-refresh-fixture","expiresAt":4070908800000}}""");
                var bytes=await File.ReadAllBytesAsync(file);var native=new NativeOAuthStore(nativeHome);
                await using var sourceHost=new ApplicationHost(Path.Combine(root,"native-settings"),native:native);await sourceHost.InitializeAsync();
                var dialog=new AccountWindow(sourceHost,sourceHost.Adapters.Single(a=>a.Definition.Id=="claude").Definition,sourceHost.Coordinator.Get("claude").Config){Owner=overview};dialog.Show();
                try {
                    ((ComboBox)dialog.FindName("SourceSelector")).SelectedValue="local-oauth";((CheckBox)dialog.FindName("Enabled")).IsChecked=true;((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var deadline=DateTime.UtcNow.AddSeconds(5);while(!dialog.Saved&&DateTime.UtcNow<deadline)await Task.Delay(20);Assert(dialog.Saved,"local connection save failed");
                    var saved=sourceHost.Coordinator.Get("claude").Config;Assert(saved.NativeIdentity==native.Read("claude").Identity&&saved.CredentialRef is null&&saved.HasUsableCredential,"native credential copied or unbound");
                    await sourceHost.RemoveAsync("claude");var removed=sourceHost.Coordinator.Get("claude").Config;Assert(!removed.Enabled&&removed.NativeIdentity is null,"connection removal failed");
                    var after=await File.ReadAllBytesAsync(file);Assert(bytes.SequenceEqual(after)&&sourceHost.Queries.Events.Count==0,"native settings changed credentials or queried");
                } finally {if(dialog.IsVisible)dialog.Close();}
            });
            await Check("AI Studio project validation rejects incomplete source setup before saving or querying",async()=> {
                await using var sourceHost=new ApplicationHost(Path.Combine(root,"project-settings"));await sourceHost.InitializeAsync();
                var dialog=new AccountWindow(sourceHost,sourceHost.Adapters.Single(a=>a.Definition.Id=="gemini").Definition,sourceHost.Coordinator.Get("gemini").Config){Owner=overview};dialog.Show();
                try {
                    ((CheckBox)dialog.FindName("Enabled")).IsChecked=true;((PasswordBox)dialog.FindName("SecretInput")).Password="nonfunctional-monitoring-fixture";
                    ((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(!dialog.Saved&&((TextBlock)dialog.FindName("Feedback")).Text.Contains("project ID"),"missing project accepted");
                    ((TextBox)dialog.FindName("ProjectInput")).Text="../invalid";((Button)dialog.FindName("Save")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(!dialog.Saved,"invalid project accepted");
                    Assert(sourceHost.Queries.Events.Count==0&&!File.Exists(Path.Combine(sourceHost.Root,"settings.json")),"invalid source queried or persisted");
                } finally {dialog.Close();}
            });
            await Check("All application menu families render readable theme colors without queries",()=>MenuThemeChecks.Run(host,hub,preferences,surfaces,tray,root));
            await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{failed,checks,monitors=NativeWindows.Monitors(),scope="Actual WPF/Win32 integration with isolated fixtures; not a game, physical mouse, hardware DPI-change or real-provider acceptance test"},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("RESULT UI "+(checks.Count-failed)+" passed, "+failed+" failed; "+root);
            sentinel?.Close();sentinel=null;
            if(interactive && failed==0) {
                surfaces.ShowOverview();preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=true,Topmost=false,Collapsed=false,Locked=false,Placement=null,CardIds=["demo"]}});
                await preferences.FlushAsync();
                Console.WriteLine("Interactive Demo QA preview. Exit from tray/overview to finish.");
                await close.Task;
            }
            await Check("Surface teardown is idempotent",()=> {surfaces.Dispose();surfaces.Dispose();return Task.CompletedTask;});
            await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{failed,checks},new JsonSerializerOptions{WriteIndented=true}));
        } catch(Exception e) {failed++;Console.WriteLine(e);await File.WriteAllTextAsync(Path.Combine(root,"fatal.txt"),e.ToString());}
        finally {
            sentinel?.Close();tray?.Dispose();hub?.Dispose();surfaces?.Dispose();
            if(preferences is not null){await preferences.FlushAsync();preferences.Dispose();}
            if(host is not null)await host.DisposeAsync();
            await ShutdownAsync(app);
        }
    }
    private static IEnumerable<T> FindVisual<T>(DependencyObject obj) where T:DependencyObject
    {
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj);i++) {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(obj,i);
            if(child is T found)yield return found;
            foreach(var item in FindVisual<T>(child))yield return item;
        }
    }
    private static void RenderWidget(WidgetWindow widget,string file,double scale)
        =>RenderWindow(widget,file,scale);
    private static void RenderWindow(Window widget,string file,double scale)
    {
        widget.UpdateLayout();var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(widget.ActualWidth*scale),(int)Math.Ceiling(widget.ActualHeight*scale),96*scale,96*scale,System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(widget);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var stream=File.Create(file);png.Save(stream);
    }
    private sealed class FixtureAdapter(string id,string name,FailureKind? failure):IProviderAdapter
    {
        public ProviderDefinition Definition {get;}=new(id,name,"Fixture","Isolated QA fixture · not real usage","#6BD4A9",id=="demo"?null:new("example.invalid",443,"/balance"));
        public int Calls;
        public Task<AdapterOutcome> RefreshAsync(AccountConfig c,IQueryClient q,CancellationToken ct) {
            Calls++;return Task.FromResult(failure is {} f ? AdapterOutcome.Fail(f,DateTimeOffset.UtcNow.AddDays(3))
                : new AdapterOutcome([new("available","Available",1250.50m,"credits"),new("frozen","Frozen",40m,"credits",MetricKind.Reserved)]));
        }
    }
}
