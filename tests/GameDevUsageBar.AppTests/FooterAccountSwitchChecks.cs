using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

// Real WPF event routes and SurfaceManager/host/native-vault integration, with
// explicitly injected fixture homes, synthetic credentials and a fixture busy
// guard. No real auth files, process enumeration, CLI or provider requests.
internal static class FooterAccountSwitchChecks
{
    public static async Task Run(string qaRoot)
    {
        var root=Path.Combine(qaRoot,"footer-account-switch-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results=new List<object>();var failed=0;
        async Task Check(string name,Func<Task> body)
        {
            try{await body();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;results.Add(new{name,status="FAIL",error=error.ToString()});Console.WriteLine("FAIL "+name+" "+error.Message);}
        }
        try
        {
            await using(var fixture=await Fixture.Create(Path.Combine(root,"presentation")))
            {
                using var preferences=new PresentationPreferencesService(new PresentationStore(fixture.Host.Root),new(Widget:new(Visible:false,ShowNetwork:false)));
                using var hub=new ProviderStateHub(fixture.Host.Adapters,fixture.Host.Coordinator,preferences,Application.Current.Dispatcher);
                SeedPresentation(hub);
                var panel=new TrayPopupWindow(hub){ShowActivated=false,ShowInTaskbar=false};
                try
                {
                    panel.SelectProvider("codex");panel.FitToWorkArea(440,780);panel.Show();await Idle();
                    await Check("Footer offers localized switch before refresh/settings without overlap",async()=>{
                        foreach(var language in new[]{"zh-CN","en-US"})
                        {
                            Localizer.SetLanguage(language);await Idle();
                            foreach(var width in new[]{380d,440d})
                            {
                                panel.FitToWorkArea(width,780);panel.UpdateLayout();await Idle();
                                var card=Card(panel,"codex");var button=Switch(card);
                                Assert(button.IsVisible&&button.IsEnabled&&button.Content as string==Localizer.T("Switch account"),"localized switch footer is missing");
                                FooterFit(card);
                                Render(panel,Path.Combine(root,$"footer-popup-{language}-{width:0}-150.png"));
                            }
                        }
                    });
                    await Check("Native menu lists saved identities only and current display stays selectable",async()=>{
                        foreach(var language in new[]{"zh-CN","en-US"})
                        {
                            Localizer.SetLanguage(language);await Idle();
                            var card=Card(panel,"codex");var model=(CardModel)card.DataContext;
                            Assert(model.AccountChoices.Count==4&&model.CliAccountChoices.Count==2,"fixture did not separate saved/manual/local CLI identities");
                            var menu=Open(card);await Idle();
                            try
                            {
                                var choices=Options(menu);
                                Assert(choices.Length==2&&choices.All(item=>item.IsEnabled),"native menu exposes unbound logins or disables displayed saved account");
                                Assert(choices.Single(item=>(Guid)item.Tag==fixture.Beta.SlotId).IsChecked&&MenuText(menu).Any(text=>text.Contains(Localizer.T("Displayed account"))),"displayed marker was mistaken for actual CLI login");
                                Assert(fixture.Native.Read("codex").AccountId=="fixture-alpha"&&model.SlotId==fixture.Beta.SlotId,"fixture cannot prove displayed/native identity mismatch");
                                Assert(MenuText(menu).Contains("Beta fixture")&&MenuText(menu).Contains("Alpha fixture")&&!MenuText(menu).Contains("Manual-only fixture"),"menu account identities were mixed");
                                Assert(MenuText(menu).Contains(Localizer.T("Switch CLI login. Close CLI sessions first.")),"native auth-file scope hint missing");
                                Assert(menu.Items.OfType<MenuItem>().Any(item=>item.Header as string==Localizer.T("Manage accounts")),"manage entry missing");
                                Dark(menu);HorizontalFit(menu);Render(menu,Path.Combine(root,$"footer-native-menu-{language}-150.png"));
                            }
                            finally{menu.IsOpen=false;}
                        }
                        Assert(typeof(AccountChoice).GetProperties().Select(p=>p.Name).OrderBy(n=>n).SequenceEqual(new[]{"IsCurrent","Label","SlotId","Status","Summary"}.OrderBy(n=>n)),"menu DTO leaked credential/native binding metadata");
                    });
                    await Check("Codex and Claude footer routes native switch separately from display selection",async()=>{
                        var nativeRequests=new List<(string,Guid)>();var displayRequests=new List<(string,Guid)>();
                        void Native(string id,Guid slot)=>nativeRequests.Add((id,slot));
                        void Display(string id,Guid slot)=>displayRequests.Add((id,slot));
                        panel.CliAccountSwitchRequested+=Native;panel.AccountSwitchRequested+=Display;
                        var before=await File.ReadAllBytesAsync(fixture.Native.PathFor("codex"));
                        try
                        {
                            foreach(var id in new[]{"codex","claude"})
                            {
                                panel.SelectProvider(id);await Idle();var card=Card(panel,id);var menu=Open(card);await Idle();
                                var target=Options(menu).First();var slot=(Guid)target.Tag;Click(target);await Idle();
                                Assert(nativeRequests.Last()==(id,slot)&&displayRequests.Count==0,"footer native event did not preserve provider/slot or invoked display route");
                            }
                            var after=await File.ReadAllBytesAsync(fixture.Native.PathFor("codex"));
                            Assert(nativeRequests.Count==2&&before.SequenceEqual(after),"routing-only fixture unexpectedly switched auth");
                        }
                        finally{panel.CliAccountSwitchRequested-=Native;panel.AccountSwitchRequested-=Display;}
                    });
                    await Check("API footer menu emits display-only selection and keeps single-account manage entry",async()=>{
                        panel.SelectProvider("tripo");await Idle();var card=Card(panel,"tripo");var model=(CardModel)card.DataContext;
                        Assert(!model.SupportsCliAccountSwitch&&model.CanSwitchAccount,"API display switch capability missing");
                        var nativeCount=0;Guid? selected=null;
                        void Native(string _,Guid __)=>nativeCount++;
                        void Display(string id,Guid slot){if(id=="tripo")selected=slot;}
                        panel.CliAccountSwitchRequested+=Native;panel.AccountSwitchRequested+=Display;
                        var before=await File.ReadAllBytesAsync(fixture.Native.PathFor("codex"));
                        try
                        {
                            var menu=Open(card);await Idle();
                            Assert(Options(menu).Length==2&&Options(menu).Single(item=>(Guid)item.Tag==fixture.ApiAlpha.SlotId).IsChecked&&!Options(menu).Single(item=>(Guid)item.Tag==fixture.ApiAlpha.SlotId).IsEnabled,"API current display marker is wrong");
                            Assert(!MenuText(menu).Contains(Localizer.T("Switch CLI login. Close CLI sessions first.")),"API menu claims to switch native auth");
                            Click(Options(menu).Single(item=>(Guid)item.Tag==fixture.ApiBeta.SlotId));await Idle();
                            var after=await File.ReadAllBytesAsync(fixture.Native.PathFor("codex"));
                            Assert(selected==fixture.ApiBeta.SlotId&&nativeCount==0&&before.SequenceEqual(after),"API footer changed native route/auth");
                            await fixture.Host.RemoveAccountAsync("tripo",fixture.ApiBeta.SlotId);await Idle();
                            card=Card(panel,"tripo");
                            menu=Open(card);await Idle();
                            Assert(Options(menu).Length==1&&!Options(menu)[0].IsEnabled&&menu.Items.OfType<MenuItem>().Any(item=>item.Header as string==Localizer.T("Manage accounts")),"single API account lost current/management semantics");menu.IsOpen=false;
                        }
                        finally{panel.CliAccountSwitchRequested-=Native;panel.AccountSwitchRequested-=Display;}
                    });
                    await Check("One saved CLI account remains usable but no saved login and hover cannot switch",async()=>{
                        panel.SelectProvider("claude");await Idle();var card=Card(panel,"claude");var model=(CardModel)card.DataContext;
                        Assert(model.CliAccountChoices.Count==1&&Switch(card).IsEnabled,"one saved CLI account must be restorable even when displayed");
                        var saved=model.CliAccountChoices.ToArray();model.SetAccountChoices(model.AccountChoices,[]);await Idle();
                        Assert(!Switch(card).IsEnabled,"unbound native credentials falsely enable footer switch");
                        Click(Switch(card));Assert(Switch(card).ContextMenu?.IsOpen!=true,"disabled native footer opened a menu");
                        model.SetAccountChoices(model.AccountChoices,saved);
                        var hover=new CompactCardView{DataContext=model,ShowActions=false};
                        var holder=new Window{Content=hover,Width=400,SizeToContent=SizeToContent.Height,ShowActivated=false,ShowInTaskbar=false};
                        try{holder.Show();await Idle();Assert(!Switch(hover).IsVisible&&!Visuals<AccountPickerView>(hover).Single().IsVisible,"hover contains interactive account switching");Click(Switch(hover));Assert(Switch(hover).ContextMenu?.IsOpen!=true,"hover-only control dispatched a menu");}
                        finally{holder.Close();}
                    });
                    await Check("Busy footer rejects rapid reopen and localizes non-replay feedback",async()=>{
                        panel.SelectProvider("codex");await Idle();var card=Card(panel,"codex");var model=(CardModel)card.DataContext;
                        model.SetAccountSwitchFeedback("Switching CLI login…",true);await Idle();
                        Assert(model.AccountSwitchBusy&&!Switch(card).IsEnabled,"busy state leaves switch actionable");
                        for(var i=0;i<3;i++)Click(Switch(card));
                        Assert(Switch(card).ContextMenu?.IsOpen!=true,"rapid busy footer clicks opened another menu");
                        foreach(var language in new[]{"zh-CN","en-US"})
                        {
                            Localizer.SetLanguage(language);await Idle();
                            card=Card(panel,"codex");
                            model.SetAccountSwitchFeedback(NativeLoginSwitcher.Result(NativeAccountStatus.Unknown).MessageKey);await Idle();
                            Assert(Visuals<TextBlock>(card).Any(t=>t.IsVisible&&t.Text==Localizer.T(NativeLoginSwitcher.Result(NativeAccountStatus.Unknown).MessageKey)),"UNKNOWN warning not displayed inline");
                            Assert(language!="zh-CN"||model.AccountSwitchFeedback!=NativeLoginSwitcher.Result(NativeAccountStatus.Unknown).MessageKey,"UNKNOWN feedback lacks Chinese translation");
                            FooterFit(card);Render(panel,Path.Combine(root,$"footer-unknown-feedback-{language}-150.png"));
                        }
                        model.SetAccountSwitchFeedback("");
                    });
                    Assert(fixture.Host.Queries.Events.Count==0&&fixture.Adapters.All(a=>a.Calls==0)&&fixture.Guard.Calls==0,"presentation-only checks contacted providers or scanned native processes");
                }
                finally{panel.AllowClose=true;panel.Close();}
            }
            await Check("SurfaceManager footer switches fixture Codex auth and follows saved slot once",async()=>{
                await using var session=await Session.Create(Path.Combine(root,"surface-success"));var fixture=session.Fixture;
                // Display Beta while the native fixture contains Alpha; clicking
                // the checked option must still apply Beta's actual saved auth.
                var card=await session.Show("codex");var model=(CardModel)card.DataContext;
                var apiSlot=fixture.Host.GetActiveAccount("tripo").SlotId;var menu=Open(card);await Idle();
                var target=Options(menu).Single(item=>(Guid)item.Tag==fixture.Beta.SlotId);
                Assert(target.IsChecked&&target.IsEnabled,"checked saved CLI identity cannot restore native auth");
                var nativeGate=(SemaphoreSlim)typeof(ApplicationHost).GetField("nativeActions",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(fixture.Host)!;
                // Hold this fixture host's ordinary native-operation lock so
                // pending UI state is observable even on very fast disks.
                await nativeGate.WaitAsync();
                try
                {
                    Click(target);
                    Assert(model.AccountSwitchBusy&&!Switch(card).IsEnabled,"SurfaceManager did not immediately set busy feedback");
                    // A queued duplicate routed command must be ignored by the
                    // real SurfaceManager gate, not serialized for another write.
                    card.RaiseEvent(new AccountSelectionEventArgs(CompactCardView.SwitchCliAccountEvent,"codex",fixture.Alpha.SlotId));
                }
                finally{nativeGate.Release();}
                await Until(()=>!model.AccountSwitchBusy&&model.AccountSwitchFeedback.Length>0);
                Assert(fixture.Native.Read("codex").AccountId=="fixture-beta"&&fixture.Guard.Calls==2,"native footer did not publish exactly one verified switch");
                Assert(fixture.Host.GetActiveAccount("codex").SlotId==fixture.Beta.SlotId&&model.SlotId==fixture.Beta.SlotId&&fixture.Host.GetActiveAccount("tripo").SlotId==apiSlot,"successful footer switch selected wrong/sibling slot");
                Assert(model.AccountSwitchFeedback.Contains(Localizer.T(NativeLoginSwitcher.Result(NativeAccountStatus.Switched).MessageKey))&&model.AccountSwitchFeedback.Contains("Beta fixture"),"verified switch feedback omits the target account");
                Assert(fixture.Host.Queries.NativeVault!.Read(fixture.Alpha).AccountId=="fixture-alpha"&&fixture.Host.Queries.NativeVault.Read(fixture.Beta).AccountId=="fixture-beta","switch mixed saved credential identities");
                Assert(!fixture.Host.Queries.NativeVault.HasPendingSwitch("codex")&&Directory.GetFiles(Path.GetDirectoryName(fixture.Native.PathFor("codex"))!,"*.gamedevusagebar.tmp").Length==0,"verified switch retained pending/plaintext staging files");
                var settings=await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root,"settings.json"));Assert(!settings.Contains("nonfunctional-")&&!MenuText(Switch(card).ContextMenu!).Any(t=>t.Contains("nonfunctional-")),"fixture auth leaked into settings or menu");
                NoQueries(fixture);Render(session.Surfaces.Panel,Path.Combine(root,"footer-surface-switched-150.png"));
                // The header picker remains display-only. Its later selection
                // must not make the previous successful auth result ambiguous.
                await fixture.Host.SelectAccountAsync("codex",fixture.Alpha.SlotId);await Until(()=>model.SlotId==fixture.Alpha.SlotId);
                card=Card(session.Surfaces.Panel,"codex");
                Assert(model.AccountSwitchFeedback.Contains("Beta fixture")&&fixture.Native.Read("codex").AccountId=="fixture-beta"&&fixture.Guard.Calls==2,"display-only selection relabelled the completed native switch or changed CLI auth");
                // Now switch the other slot to prove host selection follows the
                // native file instead of merely changing the currently shown row.
                menu=Open(card);await Idle();Click(Options(menu).Single(item=>(Guid)item.Tag==fixture.Alpha.SlotId));
                await Until(()=>!model.AccountSwitchBusy&&model.SlotId==fixture.Alpha.SlotId);
                Assert(fixture.Native.Read("codex").AccountId=="fixture-alpha"&&fixture.Host.GetActiveAccount("codex").SlotId==fixture.Alpha.SlotId&&fixture.Guard.Calls==4,"footer switch did not follow new saved slot");NoQueries(fixture);
            });
            await Check("SurfaceManager BUSY leaves fixture auth/vault/settings unchanged",async()=>{
                await using var session=await Session.Create(Path.Combine(root,"surface-busy"));var fixture=session.Fixture;
                fixture.Guard.Busy=true;var card=await session.Show("codex");var model=(CardModel)card.DataContext;
                var before=Files(fixture);var menu=Open(card);await Idle();Click(Options(menu).Single(item=>(Guid)item.Tag==fixture.Beta.SlotId));
                await Until(()=>!model.AccountSwitchBusy&&model.AccountSwitchFeedback.Length>0);
                Assert(model.AccountSwitchFeedback.Contains(Localizer.T(NativeLoginSwitcher.Result(NativeAccountStatus.Busy).MessageKey))&&model.AccountSwitchFeedback.Contains("Beta fixture")&&fixture.Guard.Calls==1,"BUSY result was not surfaced once for its target");
                Assert(Files(fixture).OrderBy(p=>p.Key).SequenceEqual(before.OrderBy(p=>p.Key)),"BUSY modified fixture auth/vault/settings");NoQueries(fixture);
            });
            await Check("SurfaceManager read-only rejection precedes native guard or auth mutations",async()=>{
                await using var session=await Session.Create(Path.Combine(root,"surface-read-only"));var fixture=session.Fixture;
                var path=Path.Combine(fixture.Host.Root,"settings.json");await File.WriteAllTextAsync(path,"{ unreadable synthetic settings");await fixture.Host.Settings.LoadAsync();
                Assert(fixture.Host.Settings.ReadOnly,"fixture did not enter read-only mode");
                var before=Files(fixture);var card=await session.Show("codex");var model=(CardModel)card.DataContext;
                var menu=Open(card);await Idle();Click(Options(menu).Single(item=>(Guid)item.Tag==fixture.Beta.SlotId));await Until(()=>!model.AccountSwitchBusy&&model.AccountSwitchFeedback.Length>0);
                Assert(model.AccountSwitchFeedback.Contains(Localizer.T("Account settings are read-only. CLI auth files were not changed."))&&model.AccountSwitchFeedback.Contains("Beta fixture")&&fixture.Guard.Calls==0,"read-only check happened after native operation");
                Assert(Files(fixture).OrderBy(p=>p.Key).SequenceEqual(before.OrderBy(p=>p.Key)),"read-only footer changed fixture auth/vault/settings");NoQueries(fixture);
            });
            await Check("Persisted UNKNOWN footer warning blocks subsequent native replays",async()=>{
                await using var session=await Session.Create(Path.Combine(root,"surface-unknown"));var fixture=session.Fixture;
                await fixture.Host.Queries.NativeVault!.MarkSwitchPendingAsync("codex",null);
                var before=Files(fixture);var card=await session.Show("codex");var model=(CardModel)card.DataContext;
                for(var attempt=0;attempt<2;attempt++)
                {
                    var menu=Open(card);await Idle();Click(Options(menu).Single(item=>(Guid)item.Tag==fixture.Beta.SlotId));
                    await Until(()=>!model.AccountSwitchBusy&&model.AccountSwitchFeedback.Contains(Localizer.T(NativeLoginSwitcher.Result(NativeAccountStatus.Unknown).MessageKey)));
                    Assert(fixture.Guard.Calls==0&&Files(fixture).OrderBy(p=>p.Key).SequenceEqual(before.OrderBy(p=>p.Key)),"UNKNOWN footer replayed or changed native state");
                }
                NoQueries(fixture);Render(session.Surfaces.Panel,Path.Combine(root,"footer-surface-unknown-150.png"));
            });
        }
        finally
        {
            Localizer.SetLanguage("en-US");await Idle();
            await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        if(failed>0)throw new InvalidOperationException($"Footer account switch failed: {failed} of {results.Count}; receipt {Path.Combine(root,"results.json")}");
    }
    private static void NoQueries(Fixture fixture)=>Assert(fixture.Host.Queries.Events.Count==0&&fixture.Adapters.All(a=>a.Calls==0),"footer switch queried a provider or started credential renewal");
    private static void SeedPresentation(ProviderStateHub hub)
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var model in hub.Models)
        {
            var snapshot=model.Id switch
            {
                "codex"=>new UsageSnapshot("synthetic-presentation-only",DataOrigin.Live,now,[new("seven_day","Weekly remaining",99,"%",MetricKind.Quota,100,now.AddDays(6),WindowSeconds:604800),new("credits","Credit balance",62500,"credits"),new("reset-credits","Reset credits",1,"tickets",MetricKind.Count)],"pro"),
                "claude"=>new UsageSnapshot("synthetic-presentation-only",DataOrigin.Live,now,[new("five_hour","5-hour remaining",31,"%",MetricKind.Quota,100,now.AddHours(2),WindowSeconds:18000),new("seven_day","Weekly remaining",77,"%",MetricKind.Quota,100,now.AddDays(5),WindowSeconds:604800)]),
                _=>new UsageSnapshot("synthetic-presentation-only",DataOrigin.Live,now,[new("credits","Available",5440,"credits")])
            };
            model.Update(model.State with {LastSuccess=snapshot,FromCache=false});
        }
    }
    private static Dictionary<string,string> Files(Fixture fixture)=>Directory.GetFiles(fixture.Host.Root,"*",SearchOption.AllDirectories)
        .Where(path=>!path.EndsWith("presentation.json",StringComparison.OrdinalIgnoreCase)&&!path.EndsWith("presentation.json.bak",StringComparison.OrdinalIgnoreCase))
        .Concat(Directory.GetFiles(fixture.Home,"*",SearchOption.AllDirectories)).ToDictionary(path=>path,path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private static Button Switch(CompactCardView card)=>(Button)card.FindName("SwitchAccountButton");
    private static CompactCardView Card(TrayPopupWindow panel,string provider)=>Visuals<CompactCardView>(panel).Single(card=>card.DataContext is CardModel model&&model.Id==provider);
    private static ContextMenu Open(CompactCardView card)
    {Assert(Switch(card).IsVisible&&Switch(card).IsEnabled,"footer switch is not usable");Click(Switch(card));return Switch(card).ContextMenu??throw new InvalidOperationException("footer did not create an account menu");}
    private static MenuItem[] Options(ContextMenu menu)=>menu.Items.OfType<MenuItem>().Where(item=>item.Tag is Guid).ToArray();
    private static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem item)=>item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static string[] MenuText(ContextMenu menu)=>Visuals<TextBlock>(menu).Select(text=>text.Text).Concat(menu.Items.OfType<MenuItem>().Select(item=>item.Header as string).OfType<string>()).ToArray();
    private static void FooterFit(CompactCardView card)
    {
        card.UpdateLayout();var button=Switch(card);var buttons=Visuals<Button>(card).Where(b=>b.IsVisible&&ReferenceEquals(VisualTreeHelper.GetParent(b),VisualTreeHelper.GetParent(button))).OrderBy(b=>b.TransformToAncestor(card).Transform(new Point()).X).ToArray();
        Assert(buttons.Length==3&&ReferenceEquals(buttons[0],button),"footer order/count is not switch, refresh, settings");
        var previous=new Rect();foreach(var current in buttons)
        {
            var bounds=current.TransformToAncestor(card).TransformBounds(new Rect(current.RenderSize));
            Assert(bounds.Left>=-1&&bounds.Right<=card.ActualWidth+1&&bounds.Bottom<=card.ActualHeight+1,"footer action clipped");
            Assert(previous.IsEmpty||previous.Right<=bounds.Left+1,"footer actions overlap");previous=bounds;
        }
        HorizontalFit(card);
    }
    private static void HorizontalFit(FrameworkElement root)
    {
        foreach(var text in Visuals<TextBlock>(root).Where(text=>text.IsVisible&&text.ActualWidth>0))
        {
            var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            Assert(bounds.Left>=-1&&bounds.Right<=root.ActualWidth+1,"text escaped footer/menu bounds: "+text.Text);
            if(text.TextWrapping==TextWrapping.NoWrap&&text.TextTrimming==TextTrimming.None)
            {text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Assert(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1,"footer/menu text clipped: "+text.Text);}
        }
    }
    private static void Dark(ContextMenu menu)
    {
        if(SystemParameters.HighContrast)return;
        Assert(menu.Background is SolidColorBrush background&&background.Color.R<80&&background.Color.G<80&&background.Color.B<80,"footer menu reverted to white background");
        Assert(menu.Foreground is SolidColorBrush foreground&&foreground.Color.R>140,"footer menu foreground unreadable");
    }
    private static void Render(FrameworkElement element,string path)
    {
        element.UpdateLayout();const double scale=1.5;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static async Task Until(Func<bool> predicate)
    {var watch=Stopwatch.StartNew();while(!predicate()){if(watch.Elapsed>TimeSpan.FromSeconds(8))throw new TimeoutException("synthetic footer operation did not settle");await Task.Delay(10);await Idle();}await Idle();}
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}

    private sealed class BusyGuard
    {public bool Busy;public int Calls;public bool Check(string _){Calls++;return Busy;}}
    private sealed class FixtureAdapter(string id,string name,string accent):IProviderAdapter
    {
        public ProviderDefinition Definition{get;}=new(id,name,id is "codex" or "claude"?"Subscription":"API credits","Synthetic footer fixture",accent,new("invalid.test",443,"/unused"));
        public int Calls;
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct)
        {Calls++;throw new InvalidOperationException("Footer fixture must not request provider usage");}
    }
    private sealed class Fixture: IAsyncDisposable
    {
        public string Home{get;}public NativeOAuthStore Native{get;}public ApplicationHost Host{get;}public BusyGuard Guard{get;}=new();
        public FixtureAdapter[] Adapters{get;}=[new("codex","Codex","#72D9C6"),new("claude","Claude","#ECAA88"),new("tripo","Tripo","#9EAEFF")];
        public AccountConfig Alpha{get;private set;}=null!;public AccountConfig Beta{get;private set;}=null!;
        public AccountConfig ApiAlpha{get;private set;}=null!;public AccountConfig ApiBeta{get;private set;}=null!;
        private Fixture(string root)
        {
            Home=Path.Combine(root,"fixture-home");Native=new NativeOAuthStore(Home);
            Host=new ApplicationHost(Path.Combine(root,"fixture-app"),Adapters,Native,nativeCliBusyGuard:Guard.Check);
            Assert(Native.PathFor("codex").StartsWith(Path.GetFullPath(Home)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"native fixture escaped its explicit home");
        }
        public static async Task<Fixture> Create(string root)
        {
            var fixture=new Fixture(root);
            try
            {
                var host=fixture.Host;await host.InitializeAsync();
                var alpha=host.GetActiveAccount("codex");await host.SaveAsync(alpha with {Label="Alpha fixture"});await fixture.Write("codex",Codex("fixture-alpha"));
                Assert((await host.CaptureCurrentLoginAsync("codex",alpha.SlotId)).Succeeded,"fixture alpha capture failed");
                var beta=await host.AddAccountAsync("codex","Beta fixture");await fixture.Write("codex",Codex("fixture-beta"));
                Assert((await host.CaptureCurrentLoginAsync("codex",beta.SlotId)).Succeeded,"fixture beta capture failed");
                await host.AddAccountAsync("codex","Manual-only fixture");
                var local=await host.AddAccountAsync("codex","Local-only fixture");await host.SaveAsync(local with {Enabled=true,SourceMode="local-oauth"});
                var claude=host.GetActiveAccount("claude");await host.SaveAsync(claude with {Label="Writer fixture"});await fixture.Write("claude",Claude("fixture-writer"));
                Assert((await host.CaptureCurrentLoginAsync("claude",claude.SlotId)).Succeeded,"fixture Claude capture failed");
                fixture.ApiAlpha=host.GetActiveAccount("tripo");await host.SaveAsync(fixture.ApiAlpha with {Label="Studio fixture",Enabled=true},"nonfunctional-api-studio");fixture.ApiAlpha=host.GetActiveAccount("tripo");
                var apiBeta=await host.AddAccountAsync("tripo","Work fixture");await host.SaveAsync(apiBeta with {Enabled=true},"nonfunctional-api-work");fixture.ApiBeta=host.GetAccounts("tripo").Single(account=>account.SlotId==apiBeta.SlotId);
                await host.SelectAccountAsync("codex",beta.SlotId);
                await fixture.Write("codex",Codex("fixture-alpha"));
                fixture.Alpha=host.GetAccounts("codex").Single(account=>account.SlotId==alpha.SlotId);fixture.Beta=host.GetAccounts("codex").Single(account=>account.SlotId==beta.SlotId);
                return fixture;
            }
            catch{await fixture.DisposeAsync();throw;}
        }
        private async Task Write(string provider,byte[] document)
        {var path=Native.PathFor(provider);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,document);}
        public ValueTask DisposeAsync()=>Host.DisposeAsync();
        private static string Jwt(string account)=>"e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{sub=account,exp=4070908800})).TrimEnd('=').Replace('+','-').Replace('/','_')+".fixture";
        private static byte[] Codex(string account)=>JsonSerializer.SerializeToUtf8Bytes(new{tokens=new{access_token=Jwt(account),id_token=Jwt(account),refresh_token="nonfunctional-refresh-"+account,account_id=account}});
        private static byte[] Claude(string account)=>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken="nonfunctional-access-"+account,refreshToken="nonfunctional-refresh-"+account,expiresAt=4070908800000}});
    }
    private sealed class Session:IAsyncDisposable
    {
        public Fixture Fixture{get;}private readonly PresentationPreferencesService preferences;private readonly ProviderStateHub hub;public SurfaceManager Surfaces{get;}
        private Session(Fixture fixture)
        {
            Fixture=fixture;preferences=new(new PresentationStore(fixture.Host.Root),new(Widget:new(Visible:false,ShowNetwork:false)));
            hub=new(fixture.Host.Adapters,fixture.Host.Coordinator,preferences,Application.Current.Dispatcher);
            SeedPresentation(hub);
            var overview=new MainWindow(fixture.Host,hub,preferences){ShowActivated=false,ShowInTaskbar=false};Surfaces=new(fixture.Host,hub,preferences,overview,()=>{});
        }
        public static async Task<Session> Create(string root)=>new(await FooterAccountSwitchChecks.Fixture.Create(root));
        public async Task<CompactCardView> Show(string provider)
        {Surfaces.Panel.SelectProvider(provider);Surfaces.Panel.FitToWorkArea(440,780);Surfaces.Panel.Show();await Idle();return Card(Surfaces.Panel,provider);}
        public async ValueTask DisposeAsync(){Surfaces.Dispose();hub.Dispose();preferences.Dispose();await Fixture.DisposeAsync();await Idle();}
    }
}
