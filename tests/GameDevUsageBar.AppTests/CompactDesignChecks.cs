using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

internal static class CompactDesignChecks
{
    public static async Task Run(string root)
    {
        var now=DateTimeOffset.UtcNow;
        var clock=new ManualClock(now);
        var definition=new ProviderDefinition("claude","Claude","Subscription","5-hour and weekly quotas","#D97757",new("example.invalid",443,"/usage"));
        var config=new AccountConfig("claude",Guid.NewGuid(),"Sample account",true,SourceMode:"local-oauth");
        var snapshot=new UsageSnapshot(config.Binding(definition),DataOrigin.Live,now,
            [new("five_hour","5-hour remaining",0,"%",MetricKind.Quota,100,now.AddHours(1).AddMinutes(23)),new("seven_day","Weekly remaining",79,"%",MetricKind.Quota,100,now.AddDays(1).AddHours(2).AddMinutes(3))]);
        var state=new ProviderState(config,snapshot,now,null,false,null);
        var model=new CardModel(definition,state,clock);
        var tip=UsageToolTip.Create(model);
        var card=(CompactCardView)tip.Content;
        var window=new Window{Content=card,Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
        try {
            window.Show();
            foreach(var language in new[]{"zh-CN","en-US"}) {
                Localizer.SetLanguage(language);model.RefreshLanguage();window.UpdateLayout();
                var text=Visuals<TextBlock>(card).Where(t=>t.IsVisible).ToArray();
                Assert(text.Any(t=>t.Text==Localizer.T("5-hour remaining")) && text.Any(t=>t.Text==Localizer.T("Weekly remaining")),"metric labels missing");
                Assert(text.Any(t=>t.Text.Contains("79")) && text.Any(t=>t.Text.Contains("0%")),"remaining values missing");
                Assert(!Visuals<Button>(card).Any(b=>b.IsVisible),"hover exposes interactive actions");
                Assert(card.ShowReset && model.Metrics.All(m=>text.Any(t=>t.Text==m.ResetDisplay)&&text.Any(t=>t.Text==m.CountdownDisplay)),"hover omitted reset or countdown details");
                Assert(model.Metrics.First().CountdownDisplay==Localizer.F("Resets in {0}h {1}m",1,23)&&model.Metrics.Last().CountdownDisplay==Localizer.F("Resets in {0}d {1}h {2}m",1,2,3),"countdown units or values wrong");
                var zero=text.Single(t=>t.Text=="0%");var weekly=text.Single(t=>t.Text=="79%");
                Assert(zero.Foreground.ToString()!=weekly.Foreground.ToString() || SystemParameters.HighContrast,"zero quota lost emphasis");
                Assert(zero.TransformToAncestor(card).Transform(new Point()).X>150,"values are not right aligned");
                Assert(window.ActualHeight<340,"hover contains too much empty space");
                AssertNoClipping(card);
                Render(window,Path.Combine(root,"tooltip-"+language+"-150.png"));
                var clicked=new CompactCardView{DataContext=model,Width=320,ShowActions=true,ShowReset=true};
                var clickedWindow=new Window{Content=clicked,Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
                try{
                    clickedWindow.Show();clickedWindow.UpdateLayout();
                    var clickedText=Visuals<TextBlock>(clicked).Where(t=>t.IsVisible).Select(t=>t.Text).ToArray();
                    foreach(var metric in model.Metrics)foreach(var detail in new[]{metric.Label,metric.Display,metric.ResetDisplay,metric.CountdownDisplay})Assert(clickedText.Contains(detail)&&text.Any(t=>t.Text==detail),"hover and click information differ");
                    AssertNoClipping(clicked);Render(clickedWindow,Path.Combine(root,"clicked-card-"+language+"-150.png"));
                }finally{clickedWindow.Close();}
            }
            var five=model.Metrics.First();int changes=0;five.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(MetricView.CountdownDisplay))changes++;};five.Tick();
            clock.Advance(TimeSpan.FromMinutes(1));five.Tick();window.UpdateLayout();
            Assert(five.CountdownDisplay==Localizer.F("Resets in {0}h {1}m",1,22)&&changes==2&&Visuals<TextBlock>(card).Any(t=>t.Text==five.CountdownDisplay),"live minute change did not update binding");
            clock.Advance(TimeSpan.FromMinutes(82));five.Tick();window.UpdateLayout();Assert(five.CountdownDisplay==Localizer.T("Reset time passed; awaiting refresh"),"elapsed reset falsely guarantees fresh quota");
            var unknown=new MetricView(new("five_hour","5-hour remaining",100,"%",MetricKind.Quota,100),clock,"claude");
            Assert(unknown.HasReset&&unknown.ResetDisplay==Localizer.T("Reset time not reported")&&unknown.CountdownDisplay=="","missing time became a guessed countdown");
            var finalMinute=new MetricView(new("five_hour","5-hour remaining",0,"%",MetricKind.Quota,100,clock.GetUtcNow().AddSeconds(1)),clock,"claude");
            Assert(finalMinute.CountdownDisplay==Localizer.F("Resets in {0}h {1}m",0,1),"remaining seconds rounded to a false zero");
            foreach(var language in new[]{"zh-CN","en-US"}){
                Localizer.SetLanguage(language);
                foreach(var minutes in new[]{59,60,61,300}){
                    var boundary=new MetricView(new("five_hour","5-hour remaining",0,"%",MetricKind.Quota,100,clock.GetUtcNow().AddMinutes(minutes)),clock,"claude");
                    Assert(boundary.CountdownDisplay==Localizer.F("Resets in {0}h {1}m",minutes/60,minutes%60),"hour boundary or localization is wrong");
                }
            }
            clock.Set(now);
            model.Update(state with {Failure=FailureKind.Unauthorized,FromCache=true});window.UpdateLayout();
            Assert(model.CompactBadge==Localizer.T("STALE") && model.CompactNotice.Contains("401"),"failed query falsely marked updated");
            Render(window,Path.Combine(root,"tooltip-stale-150.png"));
            model.Update(state with {LastSuccess=null,Failure=FailureKind.NoData});window.UpdateLayout();
            Assert(!model.Metrics.Any() && model.CompactNotice==Localizer.T(CardPresentation.Failure(FailureKind.NoData)),"unknown became a zero");
            Render(window,Path.Combine(root,"tooltip-unknown-150.png"));
            model.Update(state with {LastSuccess=snapshot with {RetrievedAt=now.AddHours(-1)}});
            Assert(model.CompactBadge==Localizer.T("STALE"),"old successful query falsely marked updated");
            Console.WriteLine("PASS compact hover bilingual, zero, failed, unknown and aged states");
        } finally {window.Close();}

        var adapters=Enumerable.Range(0,12).Select(i=>new DesignAdapter(i,now)).ToArray();
        await using var host=new ApplicationHost(Path.Combine(root,"compact-layout"),adapters);
        await host.InitializeAsync();
        foreach(var account in host.Accounts.ToArray()){await host.SaveAsync(account with {Enabled=true},"nonfunctional-design-fixture");await host.Coordinator.RefreshAsync(account.ProviderId);}
        var before=File.ReadAllBytes(Path.Combine(host.Root,"settings.json"));
        using var preferences=new PresentationPreferencesService(new PresentationStore(host.Root),new());
        using var hub=new ProviderStateHub(adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        var panel=new TrayPopupWindow(hub);
        try {
            foreach(var language in new[]{"zh-CN","en-US"}) {
                Localizer.SetLanguage(language);panel.SelectProvider("claude");panel.FitToWorkArea(400,680);
                panel.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);panel.UpdateLayout();
                Assert(panel.Height<430,"single-source popup kept fixed blank space");
                Assert(Visuals<Button>(panel).Any(b=>b.Content as string==Localizer.T("Refresh")),"refresh missing");
                Assert(Visuals<Button>(panel).Any(b=>b.Content as string==Localizer.T("Settings")),"settings missing");
                Assert(Visuals<TextBlock>(panel).Any(t=>t.IsVisible && (t.Text.StartsWith("Resets") || t.Text.StartsWith("重置时间"))),"click panel lost reset times");
                Render(panel,Path.Combine(root,"popup-"+language+"-150.png"));
                AssertNoClipping(panel);panel.Hide();
            }
            panel.SelectProvider(null);panel.FitToWorkArea(400,430);panel.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);panel.UpdateLayout();
            var scroll=(ScrollViewer)panel.FindName("ContentScroll");
            Assert(panel.Height<=430 && scroll.ScrollableHeight>0,"many sources not bounded or scrollable");
            Assert(Visuals<Button>(panel).Where(b=>b.IsVisible && b.Content as string==Localizer.T("Exit")).Any(),"footer lost to scrolling");
            var footer=(FrameworkElement)panel.FindName("PopupFooter");
            Assert(footer.TransformToAncestor(panel).TransformBounds(new Rect(footer.RenderSize)).Bottom<=panel.ActualHeight+1,"footer clipped below the popup");
            scroll.ScrollToBottom();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);panel.UpdateLayout();Render(panel,Path.Combine(root,"popup-many-bottom-150.png"));
            panel.SelectProvider("missing");panel.FitToWorkArea(400,680);panel.UpdateLayout();
            Assert(panel.Height<250 && ((TextBlock)panel.FindName("EmptyMessage")).IsVisible,"empty state kept fixed blank space");
            Assert(before.SequenceEqual(File.ReadAllBytes(Path.Combine(host.Root,"settings.json"))) && adapters.All(a=>a.Calls==1),"presentation rewrote accounts or queried");
            Console.WriteLine("PASS compact popup bilingual, reset detail, dynamic height, overflow and empty state without queries");
        } finally {panel.AllowClose=true;panel.Close();Localizer.SetLanguage("en-US");}
    }
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private sealed class ManualClock(DateTimeOffset now):TimeProvider
    {
        public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(TimeSpan span)=>now+=span;
        public void Set(DateTimeOffset value)=>now=value;
    }
    private static void AssertNoClipping(FrameworkElement root)
    {
        foreach(var text in Visuals<TextBlock>(root).Where(t=>t.IsVisible && t.ActualWidth>0)) {
            var bounds=text.TransformToAncestor(root).TransformBounds(new Rect(text.RenderSize));
            Assert(bounds.Left>=-1 && bounds.Right<=root.ActualWidth+1,"text clipped horizontally: "+text.Text);
            if(text.TextWrapping==TextWrapping.NoWrap){text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Assert(text.DesiredSize.Width-text.Margin.Left-text.Margin.Right<=text.ActualWidth+1,"unwrapped label clipped: "+text.Text+" desired="+text.DesiredSize.Width+" actual="+text.ActualWidth);}
        }
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var item in Visuals<T>(child))yield return item;}
    }
    private static void Render(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*1.5),(int)Math.Ceiling(window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private sealed class DesignAdapter(int index,DateTimeOffset now):IProviderAdapter
    {
        public ProviderDefinition Definition{get;}=new(index==0?"claude":"design"+index,index==0?"Claude":"Test source "+index,"Subscription","5-hour and weekly quotas","#D97757",new("example.invalid",443,"/usage"));
        public int Calls;
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient client,System.Threading.CancellationToken ct){Calls++;return Task.FromResult(new AdapterOutcome([new("five_hour","5-hour remaining",0,"%",MetricKind.Quota,100,now.AddHours(1)),new("seven_day","Weekly remaining",79,"%",MetricKind.Quota,100,now.AddDays(1))]));}
    }
}
