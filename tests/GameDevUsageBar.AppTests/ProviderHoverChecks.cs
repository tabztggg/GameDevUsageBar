using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

// Synthetic UI fixtures only. Every native path is injected beneath this test
// root; providers return in-memory outcomes and never use the query client.
internal static class ProviderHoverChecks
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    public static async Task Run(string qaRoot)
    {
        var root=Path.Combine(qaRoot,"provider-hover-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var results=new List<object>();var failed=0;
        async Task Check(string name,Func<Task> body)
        {
            try{await body();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;results.Add(new{name,status="FAIL",error=error.ToString()});Console.WriteLine("FAIL "+name+" "+error.Message);}
        }
        var adapters=new[]{new FixtureAdapter("codex","Codex","#72D9C6"),new FixtureAdapter("claude","Claude","#ECAA88"),new FixtureAdapter("tripo","Tripo","#9EAEFF")};
        await using var host=new ApplicationHost(Path.Combine(root,"fixture-app"),adapters,new NativeOAuthStore(Path.Combine(root,"fixture-home")),nativeCliBusyGuard:_=>throw new InvalidOperationException("Hover must not scan native CLI processes"));
        await host.InitializeAsync();
        var now=DateTimeOffset.UtcNow;
        await Configure(host,adapters[0],host.GetActiveAccount("codex"),"Developer fixture",now,0);
        await Configure(host,adapters[1],host.GetActiveAccount("claude"),"Writer fixture",now,1);
        var longName="Second account 中文标签 "+new string('W',110);
        await Configure(host,adapters[1],await host.AddAccountAsync("claude",longName),longName,now,2);
        await Configure(host,adapters[2],host.GetActiveAccount("tripo"),"Studio fixture",now,3);
        for(var i=4;i<7;i++)await Configure(host,adapters[2],await host.AddAccountAsync("tripo","Studio "+i),"Studio "+i,now,i);
        var failedSlot=host.GetAccounts("tripo")[2];adapters[2].Outcomes[failedSlot.SlotId]=AdapterOutcome.Fail(FailureKind.Forbidden);
        await host.Coordinator.RefreshAsync("tripo",failedSlot.SlotId,true);
        var unconfigured=await host.AddAccountAsync("tripo","Unconfigured fixture");
        using var preferences=new PresentationPreferencesService(new PresentationStore(host.Root),new(Widget:new(Visible:false,ShowNetwork:false)));
        using var hub=new ProviderStateHub(adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        var selected=host.Adapters.ToDictionary(a=>a.Definition.Id,a=>host.GetActiveAccount(a.Definition.Id).SlotId);
        var states=host.Adapters.SelectMany(a=>host.Coordinator.GetAccounts(a.Definition.Id)).ToDictionary(s=>(s.Config.ProviderId,s.Config.SlotId),s=>s);
        var before=Files(host.Root);var calls=adapters.Sum(a=>a.Calls.Values.Sum());
        try
        {
            await Check("Hover scopes all saved accounts to its provider with dynamic 1, 2 and 5 counts",async()=>{
                foreach(var id in new[]{"codex","claude","tripo"})
                {
                    var tip=UsageToolTip.Create(hub,id);
                    var tipScroll=(ScrollViewer)((Border)tip.Content).Child;
                    Assert(tipScroll.Content is ProviderAccountsView,"Provider tooltip still contains one selected-account card");
                    var view=(ProviderAccountsView)tipScroll.Content;
                    tipScroll.Content=null;
                    var holder=Holder(view,440,660);
                    try
                    {
                        holder.Show();await Idle();
                        var expected=hub.GetAccountModels(id);
                        Assert(view.ProviderId==id&&view.AccountModels.Select(m=>m.SlotId).SequenceEqual(expected.Select(m=>m.SlotId)),"Provider hover dropped or reordered saved slots: "+id);
                        Assert(Visuals<CompactAccountView>(view).Count()==expected.Count,"Provider hover did not realize every account: "+id);
                        Assert(view.AccountModels.All(m=>m.Id==id)&&!Visuals<CompactAccountView>(view).Any(c=>c.DataContext is CardModel m&&m.Id!=id),"Another provider leaked into hover: "+id);
                        Assert(!Visuals<Button>(view).Any(button=>button.IsVisible),"Read-only hover exposes account actions");
                        Assert(view.AccountModels.Count==(id=="codex"?1:id=="claude"?2:5),"Fixture count is capped or hard-coded");
                        if(id=="tripo")
                        {
                            var failedModel=view.AccountModels.Single(m=>m.SlotId==failedSlot.SlotId);
                            Assert(failedModel.State.Failure==FailureKind.Forbidden&&failedModel.Metrics.Any(),"Failed sibling lost its retained snapshot");
                            Assert(view.AccountModels.Any(m=>m.SlotId==unconfigured.SlotId&&!m.Metrics.Any()),"Unconfigured sibling was hidden or given fake zero values");
                        }
                    }
                    finally{holder.Close();}
                }
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Hover and click show each account's complete quota, reset, balance and ticket expiry facts",async()=>{
                foreach(var language in new[]{"zh-CN","en-US"})
                {
                    Localizer.SetLanguage(language);await Idle();
                    foreach(var id in new[]{"codex","claude"})
                    {
                        var view=new ProviderAccountsView{ShowActions=false};view.SetProvider(hub,id);
                        var holder=Holder(view,440,660);var panel=new TrayPopupWindow(hub){ShowActivated=false,ShowInTaskbar=false};
                        try
                        {
                            holder.Show();panel.SelectProvider(id);panel.FitToWorkArea(440,660);panel.Show();await Idle();
                            var clicked=(ProviderAccountsView)panel.FindName("ProviderAccounts");
                            Assert(clicked.IsVisible&&clicked.AccountModels.Count==view.AccountModels.Count,"Clicked provider surface omitted accounts");
                            var hoverCards=Visuals<CompactAccountView>(view).ToArray();var clickCards=Visuals<CompactAccountView>(clicked).ToArray();
                            foreach(var model in view.AccountModels)
                            {
                                var hoverCard=hoverCards.Single(c=>c.DataContext is CardModel m&&m.SlotId==model.SlotId);
                                var clickCard=clickCards.Single(c=>c.DataContext is CardModel m&&m.SlotId==model.SlotId);
                                var hoverText=Text(hoverCard);var clickText=Text(clickCard);
                                foreach(var fact in model.Metrics.SelectMany(m=>new[]{m.Label,m.OverviewDisplay,m.ResetDisplay,m.CountdownDisplay}).Append(model.State.Config.Label).Append(model.PlanLabel).Where(s=>s.Length>0))
                                {
                                    Assert(hoverText.Any(t=>t.Contains(fact,StringComparison.Ordinal)),"Hover omitted slot fact: "+model.State.Config.Label+" / "+fact);
                                    Assert(clickText.Any(t=>t.Contains(fact,StringComparison.Ordinal)),"Click omitted slot fact: "+model.State.Config.Label+" / "+fact);
                                }
                                var tickets=model.Metrics.Single(m=>m.Id=="reset-credits");
                                Assert(hoverText.Count(t=>t.Contains(tickets.OverviewDisplay,StringComparison.Ordinal))==1&&clickText.Count(t=>t.Contains(tickets.OverviewDisplay,StringComparison.Ordinal))==1,"Ticket count is duplicated for one slot");
                                Assert(tickets.ResetDisplay.Contains(now.AddDays(25).Year.ToString()),"Ticket expiry year missing");
                            }
                            AssertNoHorizontalClipping(view);AssertNoHorizontalClipping(clicked);
                            Render(holder,Path.Combine(root,$"provider-hover-{id}-{language}-150.png"),1.5);
                            Render(panel,Path.Combine(root,$"provider-click-{id}-{language}-150.png"),1.5);
                            if(id=="claude")
                            {
                                var named=view.AccountModels.Last();var original=named.State;
                                try{named.Update(original with {Config=original.Config with {Label="Backup fixture"}});await Idle();holder.UpdateLayout();Assert(holder.ActualHeight<=500,"Normal two-account hover retains excessive blank space");Render(holder,Path.Combine(root,$"provider-normal-two-{language}-150.png"),1.5);}
                                finally{named.Update(original);await Idle();}
                            }
                        }
                        finally{holder.Close();panel.AllowClose=true;panel.Close();}
                    }
                }
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Per-account click routes provider and slot IDs without a native or provider side effect",async()=>{
                var panel=new TrayPopupWindow(hub){ShowActivated=false,ShowInTaskbar=false};
                try
                {
                    panel.SelectProvider("claude");panel.FitToWorkArea(440,660);panel.Show();await Idle();
                    var native=new List<(string,Guid)>();var display=new List<(string,Guid)>();
                    panel.CliAccountSwitchRequested+=(id,slot)=>native.Add((id,slot));panel.AccountSwitchRequested+=(id,slot)=>display.Add((id,slot));
                    var view=(ProviderAccountsView)panel.FindName("ProviderAccounts");
                    foreach(var card in Visuals<CompactAccountView>(view))
                    {
                        var model=(CardModel)card.DataContext;
                        // Synthetic routed events verify the real parent route;
                        // enabled-state rendering is tested separately below.
                        card.RaiseEvent(new AccountSelectionEventArgs(CompactCardView.SwitchCliAccountEvent,model.Id,model.SlotId));
                        card.RaiseEvent(new AccountSelectionEventArgs(AccountPickerView.SelectAccountEvent,model.Id,model.SlotId));
                    }
                    var expected=view.AccountModels.Select(m=>(m.Id,m.SlotId)).ToArray();
                    Assert(native.SequenceEqual(expected)&&display.SequenceEqual(expected),"Per-account event route changed provider or slot identity");
                    Assert(Visuals<CompactAccountView>(view).All(card=>Visuals<Button>(card).Any(button=>button.IsVisible)),"Clicked account rows have no interactive entry");
                    native.Clear();display.Clear();
                    foreach(var card in Visuals<CompactAccountView>(view))
                    {
                        var model=(CardModel)card.DataContext;
                        var select=(Button)card.FindName("SelectAccountButton");
                        if(select.IsEnabled){select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(display.Last()==(model.Id,model.SlotId),"Actual display button targeted another account");}
                        // Grant a synthetic UI allowlist only. No vault read or
                        // actual native login switch is invoked in this panel.
                        var saved=model.CliAccountChoices.ToArray();model.SetAccountChoices(model.AccountChoices,model.AccountChoices);
                        var button=(Button)card.FindName("SwitchAccountButton");
                        Assert(button.IsVisible&&button.IsEnabled,"Saved CLI identity has no usable per-account switch");
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(native.Last()==(model.Id,model.SlotId),"Actual CLI switch button targeted another account");
                        model.SetAccountChoices(model.AccountChoices,saved);
                    }
                    Guid? refreshed=null,setup=null;
                    view.AddHandler(CompactAccountView.RefreshAccountRequestedEvent,new EventHandler<AccountSelectionEventArgs>((_,e)=>{refreshed=e.SlotId;e.Handled=true;}));
                    view.AddHandler(CompactAccountView.SetupAccountRequestedEvent,new EventHandler<AccountSelectionEventArgs>((_,e)=>{setup=e.SlotId;e.Handled=true;}));
                    var second=Visuals<CompactAccountView>(view).Last();var secondSlot=((CardModel)second.DataContext).SlotId;
                    ((Button)second.FindName("RefreshButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((Button)second.FindName("SetupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(refreshed==secondSlot&&setup==secondSlot,"Actual per-account refresh/setup lost its slot key");
                }
                finally{panel.AllowClose=true;panel.Close();}
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Actual floating-bar hover stays scoped, bounded and nonactivating across updates",async()=>{
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=["codex","claude","tripo"],ShowNetwork=false}});
                var widget=new WidgetWindow(hub,preferences){ShowActivated=false};
                try
                {
                    widget.Show();await Idle();widget.UpdateLayout();
                    var foreground=GetForegroundWindow();
                    foreach(var id in new[]{"codex","claude","tripo"})
                    {
                        var owner=Visuals<Button>(widget).Single(button=>button.DataContext is CardModel model&&model.Id==id);
                        widget.ShowProviderHover(id,owner);await Idle();
                        Assert(widget.ProviderHoverPopup.IsOpen&&widget.HoverAccounts.ProviderId==id,"Floating-bar hover is not the requested provider");
                        Assert(widget.HoverAccounts.AccountModels.Select(m=>m.SlotId).SequenceEqual(hub.GetAccountModels(id).Select(m=>m.SlotId)),"Actual hover host omits saved sibling accounts");
                        Assert(!Visuals<Button>(widget.HoverAccounts).Any(button=>button.IsVisible),"Actual hover host exposes login/query actions");
                        AssertHoverFrame(widget,id,Path.Combine(root,$"actual-bar-hover-{id}-geometry.json"));
                        Assert(GetForegroundWindow()==foreground,"Hover stole foreground activation");
                        // Presentation/language updates should retain the open
                        // pointer surface instead of destroying its owner cell.
                        Localizer.SetLanguage("zh-CN");await Idle();
                        Assert(widget.ProviderHoverPopup.IsOpen&&widget.HoverAccounts.ProviderId==id,"Open hover closes or changes provider on an unrelated presentation update");
                        Render(widget.HoverAccounts,Path.Combine(root,$"actual-bar-hover-{id}-150.png"),1.5);
                        if(id=="tripo")
                        {
                            string? requested=null;void OnRequest(string value)=>requested=value;widget.ProviderRequested+=OnRequest;
                            try
                            {
                                var surface=(Border)widget.ProviderHoverPopup.Child;
                                var scroll=Visuals<ScrollViewer>(surface).Single();scroll.ScrollToBottom();await Idle();
                                var previous=scroll.VerticalOffset;
                                Assert(previous>0,"Actual hover did not exercise a scrollable multi-account list");
                                Localizer.SetLanguage("en-US");await Idle();
                                Assert(scroll.VerticalOffset>=previous-1,"Same-provider update reset a readable scroll position");
                                var thumb=Visuals<Thumb>(surface).FirstOrDefault(element=>element.IsVisible);
                                Assert(thumb is not null,"Multi-account hover has no accessible scrollbar thumb");
                                surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent,Source=thumb});
                                Assert(requested is null&&widget.ProviderHoverPopup.IsOpen,"Scrollbar interaction opened account actions");
                                var run=Visuals<TextBlock>(widget.HoverAccounts).SelectMany(text=>text.Inlines.OfType<Run>()).First();
                                surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent,Source=run});
                                Assert(requested=="tripo"&&!widget.ProviderHoverPopup.IsOpen,"Inline Run hover click failed to open the correct provider");
                            }
                            finally{widget.ProviderRequested-=OnRequest;}
                        }
                        widget.HideProviderHover();await Idle();Assert(!widget.ProviderHoverPopup.IsOpen,"Explicit dismissal did not close actual hover");
                    }
                }
                finally{widget.AllowClose=true;widget.Close();}
                // The widget preferences changed intentionally above; account
                // files, independent quota states and query counts still must not.
                var accountFiles=Files(host.Root).Where(p=>!Path.GetFileName(p.Key).StartsWith("presentation.json",StringComparison.Ordinal)).OrderBy(p=>p.Key);
                Assert(accountFiles.SequenceEqual(before.Where(p=>!Path.GetFileName(p.Key).StartsWith("presentation.json",StringComparison.Ordinal)).OrderBy(p=>p.Key)),"Actual hover rewrote account data");
                Assert(adapters.Sum(a=>a.Calls.Values.Sum())==calls&&host.Queries.Events.Count==0,"Actual hover requested provider usage");
                foreach(var id in selected.Keys)Assert(host.GetActiveAccount(id).SlotId==selected[id],"Actual hover changed account selection");
            });
            await Check("Closing a menu hover owner safely dismisses its popup and disconnected targets are refused",async()=>{
                var widget=new WidgetWindow(hub,preferences){ShowActivated=false};ContextMenu? menu=null;
                try
                {
                    widget.Show();await Idle();widget.UpdateLayout();
                    var disconnected=new Button{Content="Disconnected fixture"};
                    Assert(PresentationSource.FromVisual(disconnected) is null,"Disconnected target fixture unexpectedly has a native presentation source");
                    var foreground=GetForegroundWindow();widget.ShowProviderHover("tripo",disconnected);await Idle();
                    Assert(!widget.ProviderHoverPopup.IsOpen&&GetForegroundWindow()==foreground,"Disconnected target opened or activated a hover popup");
                    menu=widget.CreateMoreMenu();var target=new MenuItem{Header="Synthetic provider hover owner"};menu.Items.Insert(0,target);
                    var watch=Stopwatch.StartNew();var lifecycle=new List<object>();
                    void Observe(string stage)
                    {
                        lifecycle.Add(new{stage,elapsed_ms=watch.ElapsedMilliseconds,menu_open=menu.IsOpen,menu_visible=menu.IsVisible,
                            owner_visible=target.IsVisible,owner_has_presentation_source=PresentationSource.FromVisual(target) is not null,
                            popup_open=widget.ProviderHoverPopup.IsOpen,provider=widget.HoverAccounts.ProviderId,
                            popup_owner_is_menu_item=ReferenceEquals(widget.ProviderHoverPopup.PlacementTarget,target),menu_contains_owner=menu.Items.Contains(target)});
                        File.WriteAllText(Path.Combine(root,"menu-owner-lifecycle.json"),JsonSerializer.Serialize(lifecycle,new JsonSerializerOptions{WriteIndented=true}));
                    }
                    menu.AddHandler(ContextMenu.OpenedEvent,new RoutedEventHandler((_,_)=>Observe("menu Opened event")),true);
                    menu.AddHandler(ContextMenu.ClosedEvent,new RoutedEventHandler((_,_)=>Observe("menu Closed event")),true);
                    menu.IsOpen=true;await Idle();
                    Assert(target.IsVisible&&PresentationSource.FromVisual(target) is not null,"Menu owner fixture was not connected and visible");
                    foreground=GetForegroundWindow();widget.ShowProviderHover("tripo",target);await Idle();
                    Observe("provider hover opened");
                    Assert(widget.ProviderHoverPopup.IsOpen&&widget.HoverAccounts.ProviderId=="tripo","Connected menu owner did not open its provider hover");
                    Assert(GetForegroundWindow()==foreground,"Hover stole activation from its open menu owner");
                    AssertHoverFrame(widget,"tripo / synthetic menu owner",Path.Combine(root,"menu-owner-hover-geometry.json"));
                    menu.IsOpen=false;Observe("menu IsOpen set false");await Idle();widget.Reposition();Localizer.SetLanguage("en-US");await Idle();Observe("after reposition and language update");
                    // IsVisible and its native source can survive IsOpen=false
                    // while WPF closes the menu. Refit must refuse that closing
                    // owner immediately, independently of the later Closed event.
                    Assert(!widget.ProviderHoverPopup.IsOpen,"A closing menu retained its provider popup after refit: "+JsonSerializer.Serialize(lifecycle));
                    foreground=GetForegroundWindow();widget.ShowProviderHover("tripo",disconnected);widget.Reposition();await Idle();
                    Assert(!widget.ProviderHoverPopup.IsOpen&&GetForegroundWindow()==foreground,"A later disconnected target resurrected or activated the menu hover");
                    // Closing an unrelated menu must not dismiss an existing
                    // hover whose retained anchor belongs to the floating bar.
                    menu.IsOpen=true;await Idle();var barOwner=Visuals<Button>(widget).Single(button=>button.DataContext is CardModel model&&model.Id=="codex");
                    widget.ShowProviderHover("codex",barOwner);await Idle();menu.IsOpen=false;await Idle();Observe("unrelated menu close after idle");
                    Assert(widget.ProviderHoverPopup.IsOpen&&widget.HoverAccounts.ProviderId=="codex","Unrelated menu closure dismissed a valid floating-bar anchor");
                    widget.HideProviderHover();
                }
                finally{if(menu is not null)menu.IsOpen=false;widget.AllowClose=true;widget.Close();await Idle();}
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Actual native hover fits every monitor work area at all four widget corners",async()=>{
                var original=preferences.Current.WidgetOrDefault;
                var widget=new WidgetWindow(hub,preferences){ShowActivated=false};
                try
                {
                    widget.Show();await Idle();
                    var foreground=GetForegroundWindow();
                    foreach(var monitor in NativeWindows.Monitors())
                    foreach(var corner in new[]{"top-left","top-right","bottom-left","bottom-right"})
                    {
                        var x=corner.EndsWith("right",StringComparison.Ordinal)?monitor.Work.Right-1:monitor.Work.Left+1;
                        var y=corner.StartsWith("bottom",StringComparison.Ordinal)?monitor.Work.Bottom-1:monitor.Work.Top+1;
                        preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Placement=new(monitor.Device,monitor.Bounds,x,y),CardIds=["codex","claude","tripo"],ShowNetwork=false}});
                        await Idle();widget.Reposition();await Idle();widget.UpdateLayout();
                        var owner=Visuals<Button>(widget).Single(button=>button.DataContext is CardModel model&&model.Id=="tripo");
                        widget.ShowProviderHover("tripo",owner);await Idle();
                        var caseName=Array.IndexOf(NativeWindows.Monitors().ToArray(),monitor)+"-"+corner;
                        AssertHoverFrame(widget,"tripo / "+monitor.Device+" / "+corner,Path.Combine(root,"hover-corner-"+caseName+".json"));
                        Assert(widget.HoverAccounts.AccountModels.Select(m=>m.SlotId).SequenceEqual(hub.GetAccountModels("tripo").Select(m=>m.SlotId)),"Corner placement dropped provider accounts");
                        AssertNoHorizontalClipping(widget.HoverAccounts);
                        Assert(GetForegroundWindow()==foreground,"Corner placement stole foreground activation");
                        var scroll=Visuals<ScrollViewer>(widget.ProviderHoverPopup.Child).Single();
                        Assert(scroll.ScrollableHeight>0,"Corner fixture lost its scrollable account inventory");
                        scroll.ScrollToBottom();await Idle();
                        var last=Visuals<CompactAccountView>(widget.HoverAccounts).Last();
                        var viewport=last.TransformToAncestor((FrameworkElement)widget.ProviderHoverPopup.Child).TransformBounds(new Rect(last.RenderSize));
                        Assert(viewport.Bottom<=((FrameworkElement)widget.ProviderHoverPopup.Child).ActualHeight+1,"Corner popup cannot reach its final saved account");
                        widget.HideProviderHover();await Idle();
                    }
                }
                finally{widget.AllowClose=true;widget.Close();preferences.Update(p=>p with {Widget=original});await Idle();}
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Provider placement callback respects small work areas, negative origins and 125 or 150 percent DPI",()=>{
                foreach(var scale in new[]{1d,1.25,1.5})
                foreach(var work in new[]{new PixelRect(0,0,1024,728),new PixelRect(-1280,24,1280,696),new PixelRect(50,-400,640,360)})
                {
                    var size=new Size(Math.Min(500,(work.Width-16)/scale),Math.Min(620,(work.Height-16)/scale));
                    var target=new Size(26,26);
                    foreach(var origin in new[]{new Point(work.Left+1,work.Top+1),new Point(work.Right-26*scale-1,work.Top+1),new Point(work.Left+1,work.Bottom-26*scale-1),new Point(work.Right-26*scale-1,work.Bottom-26*scale-1)})
                    {
                        var placed=WidgetWindow.ConstrainProviderHoverPlacement(size,target,origin,new DpiScale(scale,scale),work);
                        var frame=new PixelRect(origin.X+placed.Point.X*scale,origin.Y+placed.Point.Y*scale,size.Width*scale,size.Height*scale);
                        Assert(frame.IsValid&&frame.Left>=work.Left-1&&frame.Top>=work.Top-1&&frame.Right<=work.Right+1&&frame.Bottom<=work.Bottom+1,
                            "Small-work-area callback placed physical popup outside work area: "+JsonSerializer.Serialize(new{scale,origin,size,frame,work}));
                    }
                }
                return Task.CompletedTask;
            });
            await Check("Changing clicked provider starts at its first account while updates retain scroll position",async()=>{
                var panel=new TrayPopupWindow(hub){ShowActivated=false,ShowInTaskbar=false};
                try
                {
                    panel.SelectProvider("tripo");panel.FitToWorkArea(440,330);panel.Show();await Idle();
                    var scroll=(ScrollViewer)panel.FindName("ContentScroll");scroll.ScrollToBottom();await Idle();
                    Assert(scroll.VerticalOffset>0,"Clicked scroll fixture did not overflow");
                    var previous=scroll.VerticalOffset;Localizer.SetLanguage("zh-CN");await Idle();
                    Assert(scroll.VerticalOffset>=previous-1,"Same-provider language update reset clicked account list");
                    panel.SelectProvider("claude");await Idle();
                    Assert(scroll.ScrollableHeight>0&&scroll.VerticalOffset<1,"Changing clicked provider did not start at its first saved account");
                }
                finally{panel.AllowClose=true;panel.Close();}
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("Normal two-account Codex popup stays compact with all quota and ticket facts",async()=>{
                var adapter=new FixtureAdapter("codex","Codex","#72D9C6");
                await using var example=new ApplicationHost(Path.Combine(root,"normal-two-codex"),[adapter],new NativeOAuthStore(Path.Combine(root,"normal-two-home")),nativeCliBusyGuard:_=>throw new InvalidOperationException("Compact fixture must not inspect native processes"));
                await example.InitializeAsync();await Configure(example,adapter,example.GetActiveAccount("codex"),"Developer fixture",now,0);
                await Configure(example,adapter,await example.AddAccountAsync("codex","Backup fixture"),"Backup fixture",now,1);
                using var examplePreferences=new PresentationPreferencesService(new PresentationStore(example.Root),new(Widget:new(Visible:false,ShowNetwork:false)));
                using var exampleHub=new ProviderStateHub(example.Adapters,example.Coordinator,examplePreferences,Application.Current.Dispatcher);
                var exampleFiles=Files(example.Root);var exampleCalls=adapter.Calls.Values.Sum();
                foreach(var language in new[]{"zh-CN","en-US"})
                {
                    Localizer.SetLanguage(language);await Idle();
                    var view=new ProviderAccountsView{ShowActions=false};view.SetProvider(exampleHub,"codex");var holder=Holder(view,500,640);
                    var panel=new TrayPopupWindow(exampleHub){ShowActivated=false,ShowInTaskbar=false};
                    try
                    {
                        holder.Show();panel.SelectProvider("codex");panel.FitToWorkArea(500,640);panel.Show();await Idle();
                        Assert(view.AccountModels.Count==2&&Visuals<CompactAccountView>(view).Count()==2,"Representative Codex fixture dropped an account");
                        var clicked=(ProviderAccountsView)panel.FindName("ProviderAccounts");
                        foreach(var model in view.AccountModels)
                        {
                            var hoverCard=Visuals<CompactAccountView>(view).Single(c=>((CardModel)c.DataContext).SlotId==model.SlotId);
                            var clickCard=Visuals<CompactAccountView>(clicked).Single(c=>((CardModel)c.DataContext).SlotId==model.SlotId);
                            Assert(model.Metrics.Count()==4,"Representative fixture no longer contains both quotas, credits and merged ticket expiry");
                            foreach(var fact in model.Metrics.SelectMany(m=>new[]{m.Label,m.OverviewDisplay,m.ResetDisplay,m.CountdownDisplay}).Where(s=>s.Length>0))
                                Assert(Text(hoverCard).Any(t=>t.Contains(fact,StringComparison.Ordinal))&&Text(clickCard).Any(t=>t.Contains(fact,StringComparison.Ordinal)),"Compact Codex fixture omitted a complete slot fact: "+fact);
                        }
                        foreach(var scale in new[]{1.25,1.5})
                        {Render(holder,Path.Combine(root,$"normal-two-codex-hover-{language}-{scale*100:0}.png"),scale);Render(panel,Path.Combine(root,$"normal-two-codex-click-{language}-{scale*100:0}.png"),scale);}
                        await File.WriteAllTextAsync(Path.Combine(root,$"normal-two-codex-geometry-{language}.json"),JsonSerializer.Serialize(new{account_count=2,hover_width_dip=holder.ActualWidth,hover_height_dip=holder.ActualHeight,click_width_dip=panel.ActualWidth,click_height_dip=panel.ActualHeight}));
                        Assert(holder.ActualHeight<=400,"Normal two-account Codex hover exceeds compact 400 DIP target: "+holder.ActualHeight);
                        AssertNoHorizontalClipping(view);AssertNoHorizontalClipping(clicked);
                    }
                    finally{holder.Close();panel.AllowClose=true;panel.Close();}
                }
                Assert(Files(example.Root).OrderBy(p=>p.Key).SequenceEqual(exampleFiles.OrderBy(p=>p.Key))&&adapter.Calls.Values.Sum()==exampleCalls&&example.Queries.Events.Count==0,"Representative hover/click modified account data or queried a provider");
            });
            await Check("Bilingual long labels and five accounts fit 125 and 150 percent work areas with scroll and readable dark text",async()=>{
                foreach(var language in new[]{"zh-CN","en-US"})
                foreach(var scale in new[]{1.25,1.5})
                {
                    Localizer.SetLanguage(language);await Idle();
                    var view=new ProviderAccountsView{ShowActions=false};view.SetProvider(hub,"tripo");
                    var availableHeight=660/scale;var width=640/scale;
                    var holder=Holder(view,width,availableHeight);
                    try
                    {
                        holder.Show();await Idle();view.UpdateLayout();
                        Assert(holder.ActualHeight<=availableHeight+1,"Scaled provider hover extends outside its work area");
                        var scroll=Visuals<ScrollViewer>(holder).Single();
                        Assert(scroll.ScrollableHeight>0,"All accounts are present but cannot scroll in a small work area");
                        AssertNoHorizontalClipping(view);AssertDark(view);
                        Render(holder,Path.Combine(root,$"provider-many-{language}-{scale*100:0}-top.png"),scale);
                        scroll.ScrollToBottom();await Idle();
                        var last=Visuals<CompactAccountView>(view).Last();
                        var lastBounds=last.TransformToAncestor(holder).TransformBounds(new Rect(last.RenderSize));
                        Assert(lastBounds.Bottom<=holder.ActualHeight+1,"Scrolling cannot reach the final unconfigured account");
                        Render(holder,Path.Combine(root,$"provider-many-{language}-{scale*100:0}-bottom.png"),scale);
                    }
                    finally{holder.Close();}
                    view=new ProviderAccountsView{ShowActions=false};view.SetProvider(hub,"claude");holder=Holder(view,width,availableHeight);
                    try{holder.Show();await Idle();AssertNoHorizontalClipping(view);AssertDark(view);Render(holder,Path.Combine(root,$"provider-longname-{language}-{scale*100:0}.png"),scale);}
                    finally{holder.Close();}
                }
                Pure(host,adapters,before,calls,selected,states);
            });
            await Check("An already-open provider surface receives added and removed slots without a count cap",async()=>{
                var view=new ProviderAccountsView{ShowActions=false};view.SetProvider(hub,"tripo");var holder=Holder(view,440,500);
                try
                {
                    holder.Show();await Idle();var added=await host.AddAccountAsync("tripo","Sixth fixture");await Idle();
                    Assert(view.AccountModels.Count==6&&Visuals<CompactAccountView>(view).Count()==6&&view.AccountModels.Any(m=>m.SlotId==added.SlotId),"Open hover dropped dynamically added sixth slot");
                    await host.RemoveAccountAsync("tripo",added.SlotId);await Idle();
                    Assert(view.AccountModels.Count==5&&!view.AccountModels.Any(m=>m.SlotId==added.SlotId),"Removed slot remains in open hover");
                    Assert(adapters.Sum(a=>a.Calls.Values.Sum())==calls&&host.Queries.Events.Count==0,"Account list update queried providers");
                }
                finally{holder.Close();}
            });
        }
        finally
        {
            Localizer.SetLanguage("en-US");await Idle();
            await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));
        }
        if(failed>0)throw new InvalidOperationException($"Provider hover failed: {failed} of {results.Count}; receipt {Path.Combine(root,"results.json")}");
    }
    private static Window Holder(ProviderAccountsView view,double width,double maxHeight)
    {
        view.Width=double.NaN;
        var scroll=new ScrollViewer{Content=view,MaxHeight=Math.Max(1,maxHeight-30),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var border=new Border{Child=scroll,Padding=new Thickness(12),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)};border.SetResourceReference(Border.BackgroundProperty,"PanelBrush");border.SetResourceReference(Border.BorderBrushProperty,"EdgeBrush");
        return new Window{Content=border,Width=width,MaxHeight=maxHeight,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
    }
    private static void AssertHoverFrame(WidgetWindow widget,string provider,string receiptPath)
    {
        var source=PresentationSource.FromVisual(widget.HoverAccounts) as System.Windows.Interop.HwndSource;
        Assert(source is not null&&source.Handle!=NativeWindows.Handle(widget),"Hover has no distinct native popup HWND: "+provider);
        Assert(GetWindowRect(source!.Handle,out var native),"Floating-bar hover native bounds unavailable: "+provider);
        var frame=new PixelRect(native.Left,native.Top,native.Right-native.Left,native.Bottom-native.Top);
        var child=(FrameworkElement)widget.ProviderHoverPopup.Child;
        var dpi=VisualTreeHelper.GetDpi(child);var transform=source.CompositionTarget?.TransformToDevice??Matrix.Identity;
        var monitors=NativeWindows.Monitors();
        var facts=new{provider,native_popup_rect=frame,widget_rect=NativeWindows.Bounds(widget),
            popup_native_dpi=NativeWindows.GetDpiForWindow(source.Handle),widget_native_dpi=NativeWindows.GetDpiForWindow(NativeWindows.Handle(widget)),
            child_width_dip=child.ActualWidth,child_height_dip=child.ActualHeight,
            visual_dpi_x=dpi.DpiScaleX,visual_dpi_y=dpi.DpiScaleY,transform_to_device_x=transform.M11,transform_to_device_y=transform.M22,
            placement=widget.ProviderHoverPopup.Placement.ToString(),
            monitors=monitors.Select(m=>new{m.Device,m.Bounds,m.Work,m.Primary}).ToArray()};
        var diagnostic=JsonSerializer.Serialize(facts,new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(receiptPath,diagnostic);
        // GetWindowRect and WinForms WorkingArea are physical pixels in this
        // PMv2 fixture. Compare those directly, without mixing WPF DIP sizes.
        Assert(frame.IsValid&&monitors.Any(m=>frame.Left>=m.Work.Left-1&&frame.Top>=m.Work.Top-1&&frame.Right<=m.Work.Right+1&&frame.Bottom<=m.Work.Bottom+1),
            "Actual floating-bar hover is outside a monitor work area: "+diagnostic);
        Assert(child.ActualWidth*dpi.DpiScaleX<=frame.Width+2&&child.ActualHeight*dpi.DpiScaleY<=frame.Height+2,
            "Native popup clips its measured content instead of constraining the scrolling viewport: "+diagnostic);
    }
    private static async Task Configure(ApplicationHost host,FixtureAdapter adapter,AccountConfig config,string label,DateTimeOffset now,int index)
    {
        config=config with {Label=label,Enabled=true,CredentialRef=Guid.NewGuid(),CredentialRevision=Guid.NewGuid(),CredentialSource="manual"};
        adapter.Outcomes[config.SlotId]=new([
            new("five_hour","5-hour remaining",80-index,"%",MetricKind.Quota,100,now.AddHours(2).AddMinutes(index),WindowSeconds:18000),
            new("seven_day","Weekly remaining",31+index,"%",MetricKind.Quota,100,now.AddDays(6).AddHours(index).AddMinutes(12),WindowSeconds:604800),
            new("credits","Credit balance",62500-index*100,"credits"),
            new("reset-credits","Reset credits",1,"tickets",MetricKind.Count),
            new("reset-ticket-expiry-0","Reset tickets expiring",1,"tickets",MetricKind.Count,ResetAt:now.AddDays(25).AddHours(index),DateMeaning:"expiry")],Plan:index%2==0?"pro":"plus");
        await host.SaveAsync(config);await host.Coordinator.RefreshAsync(config.ProviderId,config.SlotId,true);
    }
    private static void Pure(ApplicationHost host,FixtureAdapter[] adapters,Dictionary<string,string> files,int calls,Dictionary<string,Guid> selected,Dictionary<(string,Guid),ProviderState> states)
    {
        Assert(host.Queries.Events.Count==0&&adapters.Sum(a=>a.Calls.Values.Sum())==calls,"Presentation queried a provider");
        Assert(Files(host.Root).OrderBy(p=>p.Key).SequenceEqual(files.OrderBy(p=>p.Key)),"Presentation modified account, credential, cache or settings files");
        foreach(var id in selected.Keys)Assert(host.GetActiveAccount(id).SlotId==selected[id],"Hover changed displayed/native account selection");
        foreach(var state in host.Adapters.SelectMany(a=>host.Coordinator.GetAccounts(a.Definition.Id)))Assert(state==states[(state.Config.ProviderId,state.Config.SlotId)],"Presentation changed independent quota or retry state");
    }
    private static Dictionary<string,string> Files(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .Where(p=>!Path.GetFileName(p).StartsWith("presentation.json",StringComparison.Ordinal))
        .ToDictionary(p=>p,p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private static string[] Text(DependencyObject root)=>Visuals<TextBlock>(root).Where(t=>t.IsVisible).Select(t=>
    {
        // Bound Run values render correctly but do not consistently coerce the
        // TextBlock.Text aggregate. Read the actual displayed inline content.
        var inline=string.Concat(t.Inlines.Select(InlineText));return inline.Length>0?inline:t.Text;
    }).ToArray();
    private static string InlineText(Inline inline)=>inline switch {Run run=>run.Text,Span span=>string.Concat(span.Inlines.Select(InlineText)),LineBreak=>Environment.NewLine,_=>""};
    private static void AssertNoHorizontalClipping(FrameworkElement root)
    {
        foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible&&t.ActualWidth>0))
        {
            var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            Assert(bounds.Left>=-1&&bounds.Right<=root.ActualWidth+1,"Text escaped provider surface: "+text.Text);
            if(text.TextWrapping==TextWrapping.NoWrap&&text.TextTrimming==TextTrimming.None)
            {text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Assert(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1,"Unwrapped provider text clipped: "+text.Text);}
        }
    }
    private static void AssertDark(FrameworkElement root)
    {
        if(SystemParameters.HighContrast)return;
        foreach(var border in Visuals<Border>(root).Where(b=>b.IsVisible&&b.Background is SolidColorBrush brush&&brush.Color.A>0))
        {var color=((SolidColorBrush)border.Background).Color;Assert(color.R<100&&color.G<100&&color.B<120,"Provider surface contains a bright default background");}
        foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible&&t.ActualWidth>0&&t.Text.Length>0&&t.Foreground is SolidColorBrush))
        {
            var background=((SolidColorBrush)Application.Current.FindResource("PanelBrush")).Color;
            for(DependencyObject? parent=text;parent!=null;parent=VisualTreeHelper.GetParent(parent))
                if(parent is Border {Background:SolidColorBrush brush}&&brush.Color.A==255){background=brush.Color;break;}
            var foreground=((SolidColorBrush)text.Foreground).Color;var light=Luminance(foreground);var dark=Luminance(background);var ratio=(Math.Max(light,dark)+0.05)/(Math.Min(light,dark)+0.05);
            Assert(ratio>=(text.FontSize>=18?3:4.5),"Provider text contrast too low: "+text.Text+" / "+ratio.ToString("0.00"));
        }
    }
    private static double Luminance(Color color)
    {double Linear(byte channel){var value=channel/255d;return value<=0.04045?value/12.92:Math.Pow((value+0.055)/1.055,2.4);}return 0.2126*Linear(color.R)+0.7152*Linear(color.G)+0.0722*Linear(color.B);}
    private static async Task Idle()=>await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T item)yield return item;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void Render(FrameworkElement element,string path,double scale)
    {element.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);}
    private static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class FixtureAdapter(string id,string name,string accent):IProviderAdapter
    {
        public ProviderDefinition Definition{get;}=new(id,name,id=="tripo"?"API credits":"Subscription","Synthetic provider hover fixture",accent,new("invalid.test",443,"/unused"));
        public ConcurrentDictionary<Guid,AdapterOutcome> Outcomes{get;}=new();public ConcurrentDictionary<Guid,int> Calls{get;}=new();
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct)
        {Calls.AddOrUpdate(config.SlotId,1,(_,count)=>count+1);return Task.FromResult(Outcomes[config.SlotId]);}
    }
}
