using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

// Native WPF integration with synthetic snapshots. No real accounts, cookies,
// provider requests, or production desktop input are used by this test.
internal static class OverviewDesignChecks
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var fixtureRoot=Path.Combine(root,"overview-design");
        var ids=new[]{"claude","codex","tripo","grsai","elevenlabs","openrouter","deepseek"};
        var now=DateTimeOffset.UtcNow;
        await using var host=new ApplicationHost(fixtureRoot,ProviderCatalog.Create());
        await host.InitializeAsync();
        var before=FilesExceptPresentation(fixtureRoot);
        using var preferences=new PresentationPreferencesService(new PresentationStore(fixtureRoot),
            new(CardOrder:ids,Widget:new(CardIds:ids,TransparencyPercent:47),FeaturedIds:["claude","codex"]));
        using var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        foreach(var model in hub.Models){
            var config=model.State.Config with {Enabled=ids.Contains(model.Id),Label="Sample account"};
            if(!config.Enabled){model.Update(new(config,null,null,null,false,null));continue;}
            var metrics=FixtureMetrics(model.Id,now);
            model.Update(new(config,new(config.Binding(model.Definition),DataOrigin.Live,now,[..metrics],Plan:model.Id=="codex"?"plus":null),now,null,false,null));
        }
        var overview=new MainWindow(host,hub,preferences){ShowActivated=false,ShowInTaskbar=false};
        try{
            overview.Show();await Idle();
            var featured=(ItemsControl)overview.FindName("FeaturedCards");
            var regular=(ItemsControl)overview.FindName("Cards");
            AssertModels(featured,["claude","codex"],"default featured slots");
            AssertUnion(featured,regular,ids);
            Assert(Visuals<OverviewCardView>(featured).Count()==2,"featured views not generated");
            Assert(Visuals<Button>(overview).Any(b=>b.IsVisible&&b.Content as string==Localizer.T("Display settings")),"display/settings entry missing");
            var connections=(ItemsControl)overview.FindName("OtherConnections");
            Assert(connections.Items.Cast<CardModel>().Any(m=>m.Id=="gemini")&&connections.Items.Cast<CardModel>().All(m=>!m.State.Config.Enabled),"disabled/unconfigured services lost their setup entry");
            var connectionSection=(Expander)overview.FindName("ConnectionsSection");connectionSection.IsExpanded=true;await Idle();
            foreach(var row in Visuals<OverviewRowView>(connections))Assert(((Button)row.FindName("SetupButton")).IsVisible&&((Button)row.FindName("SetupButton")).IsEnabled,"disabled service cannot be configured");
            connectionSection.IsExpanded=false;
            var detailRow=Visuals<OverviewRowView>(regular).Single(v=>((CardModel)v.DataContext).Id=="openrouter");
            Visuals<Button>(detailRow).Single(b=>b.Content as string==Localizer.T("Details")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
            var detailed=(CompactCardView)detailRow.FindName("ExpandedDetails");
            foreach(var metric in ((CardModel)detailRow.DataContext).Metrics)Assert(Text(detailed).Contains(metric.Display)&&Text(detailed).Contains(metric.Label),"list expansion omitted a reported account metric");
            Assert(detailed.IsVisible&&!Visuals<Button>(detailed).Any(b=>b.IsVisible),"expanded row omitted details or duplicated actions");
            detailed.Visibility=Visibility.Collapsed;

            foreach(var language in new[]{"zh-CN","en-US"}){
                Localizer.SetLanguage(language);await Idle();
                foreach(var width in new[]{1280d,780d}){
                    overview.Width=width;overview.Height=900;overview.UpdateLayout();await Idle();
                    AssertUnion(featured,regular,ids);
                    foreach(var card in Visuals<OverviewCardView>(featured)){
                        var model=(CardModel)card.DataContext;
                        Assert(model.PrimaryMetrics.Count is >0 and <=2,"featured card does not retain one/two primary metrics");
                        Assert(model.PrimaryMetrics.All(m=>model.Metrics.Contains(m)),"featured metric diverged from shared model");
                        var visible=Text(card);
                        foreach(var metric in model.PrimaryMetrics){
                            Assert(visible.Contains(metric.OverviewDisplay)&&visible.Contains(metric.Label),"primary quota label/value missing: "+model.Id);
                            if(metric.CountdownDisplay.Length>0)Assert(visible.Contains(metric.CountdownDisplay),"primary countdown hidden: "+model.Id);
                        }
                        Assert(!((Expander)card.FindName("SecondaryDetailsExpander")).IsExpanded,"secondary details are expanded by default");
                        Assert(((Button)card.FindName("SetupButton")).IsVisible,"featured configure access missing");
                        Assert(Visuals<Button>(card).Count(b=>b.IsVisible&&System.Windows.Automation.AutomationProperties.GetName(b)==Localizer.T("Refresh"))==1,"featured duplicate refresh actions");
                        if(model.PlanLabel.Length>0)Assert(visible.Contains(model.PlanLabel),"reported account plan hidden in the featured view");
                        AssertHorizontalFit(card);
                    }
                    AssertHorizontalFit(overview);
                    foreach(var scale in new[]{1d,1.5,2d})Render(overview,Path.Combine(root,$"overview-{language}-{(int)width}-{(int)(scale*100)}.png"),scale);
                    if(width==1280){
                        var scroll=Visuals<ScrollViewer>(overview).Single(s=>s.Content is FrameworkElement {Name:"DashboardHost"});
                        var serviceViews=Visuals<FrameworkElement>(overview).Where(v=>(v is OverviewCardView || v is OverviewRowView)&&v.IsVisible&&v.DataContext is CardModel m&&ids.Contains(m.Id)).ToArray();
                        Assert(serviceViews.Length==ids.Length,"wide overview omitted one of seven service views");
                        foreach(var service in serviceViews){var bounds=service.TransformToAncestor(scroll).TransformBounds(new Rect(service.RenderSize));Assert(bounds.Top>=-1&&bounds.Bottom<=scroll.ViewportHeight+1,"wide overview needs scrolling to show service: "+((CardModel)service.DataContext).Id+" bottom="+bounds.Bottom+" viewport="+scroll.ViewportHeight);}
                    }
                }
            }

            // API services can occupy either large slot. The small list retains
            // the other enabled services once each, including Claude and Codex.
            preferences.Update(p=>p with {FeaturedIds=["tripo","grsai"]});await Idle();
            AssertModels(featured,["tripo","grsai"],"arbitrary API featured slots");AssertUnion(featured,regular,ids);
            overview.Width=1280;overview.UpdateLayout();Render(overview,Path.Combine(root,"overview-api-featured-en-150.png"),1.5);
            preferences.Update(p=>p with {FeaturedIds=["grsai","grsai","tripo"]});await Idle();
            AssertModels(featured,["grsai","tripo"],"duplicate featured entry prevention");AssertUnion(featured,regular,ids);
            preferences.Update(p=>p with {FeaturedIds=[]});await Idle();
            Assert(featured.Items.Count==0&&regular.Items.Count==ids.Length,"explicit empty slots restore defaults or hide a service");
            await preferences.FlushAsync();
            var reloaded=await new PresentationStore(fixtureRoot).LoadAsync();
            Assert(reloaded is not null&&reloaded.FeaturedIds is {Length:0}&&reloaded.FeaturedOrDefault.Count==0,"empty featured selection did not persist");
            preferences.Update(p=>p with {FeaturedIds=["tripo","grsai"]});await preferences.FlushAsync();
            reloaded=await new PresentationStore(fixtureRoot).LoadAsync();
            Assert(reloaded!.FeaturedOrDefault.SequenceEqual(new[]{"tripo","grsai"}),"API featured choices did not persist");

            // Moving a regular row changes the visible list even when featured
            // services lie between its entries in the saved global order.
            try{
                var row=Visuals<OverviewRowView>(regular).Single(v=>((CardModel)v.DataContext).Id=="elevenlabs");
                Visuals<Button>(row).Single(b=>b.Content as string=="↑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
                AssertModels(regular,["claude","elevenlabs","codex","openrouter","deepseek"],"regular reorder across featured services");
                AssertModels(featured,["tripo","grsai"],"regular reorder changed featured slots");

                const string future="future-service";
                preferences.Update(p=>p with {CardOrder=[future,"claude","tripo","grsai"]});await Idle();
                var previous=regular.Items.Cast<CardModel>().Select(m=>m.Id).ToArray();
                var omitted=previous.Last(id=>!preferences.Current.CardOrder!.Contains(id));
                var index=Array.IndexOf(previous,omitted);Assert(index>0,"partial-order fixture has no preceding regular row");
                var expected=previous.ToArray();(expected[index],expected[index-1])=(expected[index-1],expected[index]);
                row=Visuals<OverviewRowView>(regular).Single(v=>((CardModel)v.DataContext).Id==omitted);
                Visuals<Button>(row).Single(b=>b.Content as string=="↑").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
                AssertModels(regular,expected,"omitted service reorder with partial saved order");
                AssertModels(featured,["tripo","grsai"],"partial-order reorder changed featured slots");
                var reordered=preferences.Current.CardOrder??[];
                Assert(reordered.Count(id=>id==future)==1&&reordered[0]==future&&hub.Models.All(m=>reordered.Contains(m.Id)),"reorder dropped an unknown/future ID or an omitted known service");
            }finally{preferences.Update(p=>p with {CardOrder=ids});await Idle();}
            Assert(preferences.Current.CardOrder!.SequenceEqual(ids),"reorder fixture did not restore original service order");

            var display=new DisplaySettingsWindow(preferences,hub){Owner=overview,ShowActivated=false,ShowInTaskbar=false};
            try{
                display.Show();await Idle();
                var first=(ComboBox)display.FindName("FeaturedFirstSelector");
                var second=(ComboBox)display.FindName("FeaturedSecondSelector");
                Assert((string?)first.SelectedValue=="tripo"&&(string?)second.SelectedValue=="grsai","settings does not reflect saved featured choices");
                Assert(!ChoiceIds(first).Contains("grsai")&&!ChoiceIds(second).Contains("tripo"),"settings allows occupied service in other slot");
                first.SelectedValue="openrouter";await Idle();AssertModels(featured,["openrouter","grsai"],"settings-selected API module");
                second.SelectedValue="";await Idle();AssertModels(featured,["openrouter"],"one empty module selection");
                first.SelectedValue="";await Idle();Assert(featured.Items.Count==0,"settings cannot leave both modules empty");
                ((Button)display.FindName("FeaturedResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
                AssertModels(featured,["claude","codex"],"restore featured defaults");
                Assert(preferences.Current.FeaturedIds is null&&preferences.Current.WidgetOrDefault.TransparencyPercent==47&&preferences.Current.CardOrder!.SequenceEqual(ids),"featured reset changed unrelated display preferences");
            }finally{display.Close();}

            // Both providers' optional inventory is displayed from the snapshot,
            // not synthesized from the provider's name or a plan assumption.
            foreach(var id in new[]{"claude","codex"}){
                var model=hub.Models.Single(m=>m.Id==id);
                var card=Visuals<OverviewCardView>(featured).Single(v=>((CardModel)v.DataContext).Id==id);
                var expander=(Expander)card.FindName("SecondaryDetailsExpander");expander.IsExpanded=true;await Idle();
                Assert(model.Metrics.Count(m=>m.Id=="reset-credits")==1&&!model.Metrics.Any(m=>m.Id.StartsWith("reset-ticket-expiry-")),"coupon inventory rendered as duplicate rows: "+id);
                var ticket=model.Metrics.Single(m=>m.Id=="reset-credits");
                Assert(Text(card).Contains(ticket.OverviewDisplay)&&Text(card).Contains(ticket.ResetDisplay),"coupon count or expiry hidden after expansion: "+id);
                Assert(ticket.ResetDisplay.Length>0&&!ticket.ResetDisplay.Contains(Localizer.T("Expiry not reported")),"reported coupon expiry discarded: "+id);
                var primary=model.PrimaryMetrics;
                Assert(primary.Count==2&&primary.All(m=>m.CountdownDisplay.Length>0),"returned two reset periods were replaced by a fixed provider schema: "+id);
                Assert(primary[0].CountdownDisplay.Contains(Localizer.F("Resets in {0}h {1}m",2,9))||primary[0].CountdownDisplay.Contains(Localizer.F("Resets in {0}h {1}m",2,8)),"5-hour period uses incorrect countdown units: "+id);
                var noTickets=model.State.LastSuccess! with {Metrics=[..model.State.LastSuccess!.Metrics.Where(m=>!m.Id.StartsWith("reset-",StringComparison.Ordinal))]};
                model.Update(model.State with {LastSuccess=noTickets});await Idle();
                Assert(!model.Metrics.Any(m=>m.Kind==MetricKind.Count)&&!model.SecondaryMetrics.Any(m=>m.Unit=="tickets"),"absent coupon inventory inferred as zero: "+id);
                if(id=="codex")Assert(model.PlanLabel.Length>0,"reported Codex plan not shown");
                model.Update(model.State with {LastSuccess=noTickets with {Plan=null}});await Idle();
                Assert(model.PlanLabel.Length==0,"missing plan inferred from quota period");
                expander.IsExpanded=false;
            }
            await VerifyStatePresentation(root,hub.Models.Single(m=>m.Id=="tripo"),now);
            await preferences.FlushAsync();
            AssertFilesUnchanged(before,FilesExceptPresentation(fixtureRoot));
            Assert(host.Queries.Events.Count==0,"overview rendering/settings made upstream requests");
            Console.WriteLine("PASS overview redesign: custom/duplicate/empty/default featured slots, independent persisted settings, dynamic quota and optional coupon/plan fields, bilingual 780/1280 rendering at 100/150/200%, cached/error/disabled/unknown distinction, no account writes or provider requests");
        }finally{overview.AllowClose=true;overview.Close();Localizer.SetLanguage("en-US");}
    }

    private static Metric[] FixtureMetrics(string id,DateTimeOffset now)=>id switch{
        "claude"=>[new("five_hour","5-hour remaining",31,"%",MetricKind.Quota,100,now.AddHours(2).AddMinutes(9),WindowSeconds:18000),new("seven_day","Weekly remaining",73,"%",MetricKind.Quota,100,now.AddDays(2).AddHours(6).AddMinutes(59),WindowSeconds:604800),new("reset-credits","Reset tickets",2,"tickets",MetricKind.Count),new("reset-ticket-expiry-0","Reset ticket expiry",2,"tickets",MetricKind.Count,ResetAt:now.AddDays(20),DateMeaning:"expiry")],
        "codex"=>[new("primary_window","5-hour remaining",23,"%",MetricKind.Quota,100,now.AddHours(2).AddMinutes(9),WindowSeconds:18000),new("secondary_window","Weekly remaining",64,"%",MetricKind.Quota,100,now.AddDays(4).AddHours(1).AddMinutes(15),WindowSeconds:604800),new("credits","Credit balance",62500,"credits"),new("reset-credits","Reset tickets",1,"tickets",MetricKind.Count),new("reset-ticket-expiry-0","Reset ticket expiry",1,"tickets",MetricKind.Count,ResetAt:now.AddDays(25),DateMeaning:"expiry")],
        "tripo"=>[new("credits","Available",12345,"credits"),new("frozen","Frozen",0,"credits",MetricKind.Reserved)],
        "grsai"=>[new("credits","Available",456789,"credits")],
        "elevenlabs"=>[new("remaining","Remaining",16000,"characters",MetricKind.Remaining,24000,now.AddDays(8)),new("used","Used",8000,"characters",MetricKind.Used),new("limit","Limit",24000,"characters",MetricKind.Limit)],
        "openrouter"=>[new("balance","Account balance",25.50m,"USD"),new("credits","Purchased credits",30,"USD"),new("usage","Account usage",4.50m,"USD",MetricKind.Used)],
        _=>[new("balance","Account balance",88.25m,"CNY")]
    };
    private static async Task VerifyStatePresentation(string root,CardModel model,DateTimeOffset now)
    {
        var original=model.State;
        var view=new OverviewCardView{DataContext=model};
        var window=new Window{Content=view,Width=420,SizeToContent=SizeToContent.Height,ShowActivated=false,ShowInTaskbar=false};
        try{
            window.Show();await Idle();
            var live=model.CompactBadge;
            model.Update(original with {FromCache=true});await Idle();
            Assert(model.CompactBadge!=live&&model.CompactNotice.Length>0,"cached values claimed current data");
            Render(window,Path.Combine(root,"overview-cached-150.png"),1.5);
            model.Update(original with {FromCache=true,Failure=FailureKind.Forbidden});await Idle();
            Assert(model.CompactNotice.Contains("403")&&((Button)view.FindName("SetupButton")).IsEnabled,"permission failure lacks explanation or configure access");
            Render(window,Path.Combine(root,"overview-forbidden-150.png"),1.5);
            model.Update(original with {LastSuccess=null,Failure=FailureKind.NoData});await Idle();
            Assert(model.PrimaryMetrics.Count==0&&!model.Metrics.Any()&&model.CompactNotice.Length>0,"no data inferred zero quota");
            Assert(!Text(view).Any(t=>t=="0%"||t=="0 credits"||t=="0 积分"),"unknown view renders zero");
            Render(window,Path.Combine(root,"overview-unknown-150.png"),1.5);
            model.Update(original with {Config=original.Config with {Enabled=false}});await Idle();
            Assert(!model.Metrics.Any()&&!model.CanRefresh&&model.CompactBadge!=live,"disabled connection claims live quota");
            Render(window,Path.Combine(root,"overview-disabled-150.png"),1.5);
            var missing=original.LastSuccess! with {Metrics=[new("credits","Available",null,"credits")]};
            model.Update(original with {LastSuccess=missing});await Idle();
            Assert(model.PrimaryMetrics.Count==1&&model.PrimaryMetrics.Single().Display.Contains(Localizer.T("Not reported")),"missing value replaced with zero");
            Render(window,Path.Combine(root,"overview-unreported-value-150.png"),1.5);
        }finally{model.Update(original);window.Close();}
    }
    private static string[] ChoiceIds(ComboBox selector)=>selector.Items.Cast<object>().Select(item=>item.GetType().GetProperty("Id")?.GetValue(item) as string).Where(id=>id is not null).Cast<string>().ToArray();
    private static void AssertModels(ItemsControl items,string[] expected,string scenario)=>Assert(items.Items.Cast<CardModel>().Select(m=>m.Id).SequenceEqual(expected),scenario+" mismatched");
    private static void AssertUnion(ItemsControl featured,ItemsControl regular,string[] expected)
    {
        var ids=featured.Items.Cast<CardModel>().Concat(regular.Items.Cast<CardModel>()).Select(m=>m.Id).ToArray();
        Assert(ids.Length==expected.Length&&ids.Distinct().Count()==ids.Length&&expected.All(ids.Contains),"featured/list partition duplicated or lost an enabled service");
    }
    // AtomicFile maintains this exact display-preference backup on each save.
    // Account, credential, snapshot, and all other backup files remain guarded.
    private static Dictionary<string,byte[]> FilesExceptPresentation(string path)=>Directory.Exists(path)?Directory.GetFiles(path,"*",SearchOption.AllDirectories).Where(p=>Path.GetRelativePath(path,p) is not ("presentation.json" or "presentation.json.bak")).ToDictionary(p=>Path.GetRelativePath(path,p),File.ReadAllBytes):[];
    private static void AssertFilesUnchanged(Dictionary<string,byte[]> before,Dictionary<string,byte[]> after)
    {
        static string Hash(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var removed=before.Keys.Except(after.Keys).OrderBy(k=>k).Select(k=>"removed "+k+" sha256="+Hash(before[k]));
        var added=after.Keys.Except(before.Keys).OrderBy(k=>k).Select(k=>"new "+k+" sha256="+Hash(after[k]));
        var changed=before.Keys.Intersect(after.Keys).Where(k=>!before[k].SequenceEqual(after[k])).OrderBy(k=>k).Select(k=>"changed "+k+" sha256="+Hash(before[k])+" -> "+Hash(after[k]));
        var differences=removed.Concat(added).Concat(changed).ToArray();
        Assert(differences.Length==0,"presentation modified account, snapshot, credential, or other application data: "+string.Join("; ",differences));
    }
    private static string Content(TextBlock text)=>text.Inlines.Count>0?string.Concat(text.Inlines.OfType<Run>().Select(run=>run.Text)):text.Text;
    private static string[] Text(DependencyObject root)=>Visuals<TextBlock>(root).Where(t=>t.IsVisible).Select(Content).ToArray();
    private static void AssertHorizontalFit(FrameworkElement root)
    {
        foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible&&t.ActualWidth>0)){
            var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            Assert(bounds.Left>=-1&&bounds.Right<=root.ActualWidth+1,"overview text outside container: "+Content(text));
            if(text.TextWrapping==TextWrapping.NoWrap&&text.TextTrimming==TextTrimming.None){text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Assert(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1,"overview label cropped: "+Content(text));}
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static async Task Idle(){await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    private static void Render(Window window,string path,double scale)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*scale),(int)Math.Ceiling(window.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static void Assert(bool condition,string reason){if(!condition)throw new Exception(reason);}
}
