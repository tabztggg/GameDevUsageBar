using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Providers;
using GameDevUsageBar.Infrastructure;

internal static class AdditionalSurfaceChecks
{
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void Check(bool value,string reason){if(!value)throw new Exception(reason);}
    private static string[] Text(FrameworkElement root)=>Visuals<TextBlock>(root).Where(t=>t.IsVisible).Select(t=>t.Text).ToArray();
    private static void Render(Window window,string path)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth*1.5),(int)Math.Ceiling(window.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
        foreach(var text in Visuals<TextBlock>(window).Where(t=>t.IsVisible)){
            var rect=text.TransformToAncestor(window).TransformBounds(new Rect(0,0,text.ActualWidth,text.ActualHeight));
            Check(rect.Bottom<=window.ActualHeight+1&&rect.Right<=window.ActualWidth+1,"new card content extends outside window");
        }
    }
    public static async Task Run(string root)
    {
        var now=DateTimeOffset.UtcNow;
        foreach(var language in new[]{"zh-CN","en-US"}){
            Localizer.SetLanguage(language);
            var definition=ProviderCatalog.Create().Single(a=>a.Definition.Id=="codex").Definition;
            var config=new AccountConfig("codex",Guid.NewGuid(),"Fixture",true);
            var snapshot=new UsageSnapshot(config.Binding(definition),DataOrigin.Live,now,[new("primary_window","Weekly remaining",29,"%",MetricKind.Quota,100,now.AddDays(5).AddHours(10).AddMinutes(3),WindowSeconds:604800),new("reset-credits","Reset credits",1,"tickets",MetricKind.Count),new("reset-ticket-expiry-3","Reset tickets expiring",1,"tickets",MetricKind.Count,ResetAt:now.AddDays(25),DateMeaning:"expiry")]);
            var model=new CardModel(definition,new(config,snapshot,now,null,false,null));
            var hover=(CompactCardView)UsageToolTip.Create(model).Content;var click=new CompactCardView{DataContext=model,Width=320,ShowActions=true};
            var hovered=new Window{Content=hover,Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
            var clicked=new Window{Content=click,Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
            try{
                hovered.Show();clicked.Show();hovered.UpdateLayout();clicked.UpdateLayout();
                foreach(var metric in model.Metrics)foreach(var value in new[]{metric.Label,metric.Display,metric.ResetDisplay,metric.CountdownDisplay}.Where(s=>s.Length>0))Check(Text(hover).Contains(value)&&Text(click).Contains(value),"Codex hover/click metric parity failed");
                Check(model.Metrics.First().CountdownDisplay.Contains("5"),"Codex weekly countdown absent");
                Check(model.Metrics.Skip(1).All(m=>m.Display==Localizer.F("{0} {1}",1,Localizer.T("tickets"))),"reset coupon uses credits instead of ticket counts");
                Check(model.Metrics.Last().ResetDisplay.Contains(now.AddDays(25).Year.ToString()),"ticket expiry omits year");
                Check(model.Metrics.Count()==2&&snapshot.Metrics.Length==3,"presentation merge changed API inventory or kept a duplicate row");
                Check(Text(hover).Count(t=>t==Localizer.F("{0} {1}",1,Localizer.T("tickets")))==1&&Text(click).Count(t=>t==Localizer.F("{0} {1}",1,Localizer.T("tickets")))==1,"one ticket is displayed twice");
                Check(!Text(hover).Contains(Localizer.T("Reset tickets expiring")),"separate ticket expiry row remains visible");
                Render(hovered,Path.Combine(root,"codex-hover-"+language+"-150.png"));Render(clicked,Path.Combine(root,"codex-click-"+language+"-150.png"));
                var several=snapshot with {Metrics=[snapshot.Metrics[0],snapshot.Metrics[1] with {Value=3},snapshot.Metrics[2] with {Value=1},snapshot.Metrics[2] with {Id="reset-ticket-expiry-4",Value=2,ResetAt=now.AddDays(30)}]};
                model.Update(new(config,several,now,null,false,null));hovered.UpdateLayout();clicked.UpdateLayout();
                Check(model.Metrics.Count()==2&&model.Metrics.Last().ResetDisplay.Split(Environment.NewLine).Length==2,"different expiry dates were lost or split into separate rows");
                foreach(var detail in model.Metrics.Last().ResetDisplay.Split(Environment.NewLine))Check(Text(hover).Any(t=>t.Contains(detail))&&Text(click).Any(t=>t.Contains(detail)),"multiple ticket expiry details missing on a surface");
                model.Update(new(config,snapshot with {Metrics=[snapshot.Metrics[0],snapshot.Metrics[1]]},now,null,false,null));hovered.UpdateLayout();
                Check(model.Metrics.Count()==2&&model.Metrics.Last().ResetDisplay==Localizer.T("Expiry not reported"),"missing ticket expiry was guessed");
            }finally{hovered.Close();clicked.Close();}
            var network=new NetworkUsageModel(new("live","fixture","Ethernet 2",12.34m,1.25m,now));
            var networkView=new NetworkUsageView{DataContext=network,Width=320};var networkWindow=new Window{Content=networkView,Width=320,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,ShowInTaskbar=false};
            try{networkWindow.Show();networkWindow.UpdateLayout();Check(Text(networkView).Contains(network.Download)&&Text(networkView).Contains(network.Upload)&&network.BarValue.Contains("MB/s"),"network speeds or units absent");Render(networkWindow,Path.Combine(root,"network-"+language+"-150.png"));}finally{networkWindow.Close();}
        }
        Console.WriteLine("PASS Codex countdown/ticket expiry bilingual hover-click parity and network card rendering");
        var adapter=ProviderCatalog.Create().Single(a=>a.Definition.Id=="demo");await using var host=new ApplicationHost(Path.Combine(root,"network-form"),[adapter]);await host.InitializeAsync();
        Check(host.Adapters.All(a=>a.Definition.Id is not ("vps" or "typesafe")),"retired VPS remains registered");
        using var preferences=new PresentationPreferencesService(new PresentationStore(host.Root),new(Widget:new(CardIds:["demo"],ShowNetwork:true)));
        using var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        foreach(var language in new[]{"zh-CN","en-US"}){
            Localizer.SetLanguage(language);
            var display=new DisplaySettingsWindow(preferences,hub);display.Show();display.UpdateLayout();
            try{
                var selector=(ComboBox)display.FindName("NetworkSelector");Check(selector.Items.Count>0,"network adapter selection missing");
                var check=(CheckBox)display.FindName("ShowNetwork");check.IsChecked=false;check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Check(!preferences.Current.WidgetOrDefault.ShowNetwork,"network display toggle not persisted");
                check.IsChecked=true;check.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            }finally{display.Close();}
            var widget=new WidgetWindow(hub,preferences);widget.Show();widget.UpdateLayout();
            try{
                Check(widget.ActualHeight==WidgetWindow.BarHeight&&((Border)widget.FindName("NetworkCell")).IsVisible,"network changed one-row height or failed to show");
                var button=(Button)widget.FindName("NetworkButton");Check(button.ToolTip is ToolTip {Content:NetworkUsageView},"network hover card absent");
                Render(widget,Path.Combine(root,"network-bar-"+language+"-150.png"));
            }finally{widget.AllowClose=true;widget.Close();}
        }
        Console.WriteLine("PASS retired VPS absent; network settings and one-row bar integration bilingual");
    }
}
