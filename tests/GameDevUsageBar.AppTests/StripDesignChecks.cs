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
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

internal static class StripDesignChecks
{
    private static string Content(TextBlock text)=>text.Inlines.Count>0?string.Concat(text.Inlines.OfType<Run>().Select(run=>run.Text)):text.Text;
    private static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
    private static IEnumerable<T> Visuals<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)yield return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Visuals<T>(VisualTreeHelper.GetChild(root,i)))yield return child;}
    public static async Task Run(string root)
    {
        var ids=new[]{"claude","codex","tripo","grsai","elevenlabs","openrouter","deepseek"};
        var adapters=ProviderCatalog.Create().Where(a=>ids.Contains(a.Definition.Id)).ToArray();
        await using var host=new ApplicationHost(Path.Combine(root,"strip-design"),adapters);await host.InitializeAsync();
        using var preferences=new PresentationPreferencesService(new PresentationStore(host.Root),new(CardOrder:ids,Widget:new(CardIds:ids,ShowNetwork:true)));
        using var hub=new ProviderStateHub(adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        var now=DateTimeOffset.UtcNow;
        foreach(var model in hub.Models){
            var config=model.State.Config with {Enabled=true,Label="Sample account"};
            Metric[] metrics=model.Id switch {
                "claude"=>[new("five_hour","5-hour remaining",84,"%",MetricKind.Quota,100,now.AddHours(2)),new("seven_day","Weekly remaining",77,"%",MetricKind.Quota,100,now.AddDays(4))],
                "codex"=>[new("primary_window","Weekly remaining",27,"%",MetricKind.Quota,100)],
                "tripo"=>[new("credits","Available",12345,"credits")],
                "grsai"=>[new("credits","Available",456789,"credits")],
                "elevenlabs"=>[new("remaining","Available",99.32m,"%",MetricKind.Quota,100)],
                "openrouter"=>[new("balance","Account balance",25.50m,"USD")],
                _=>[new("balance","Account balance",88.25m,"CNY")]
            };
            model.Update(new(config,new(config.Binding(model.Definition),DataOrigin.Live,now,[..metrics]),now,null,false,null));
        }
        foreach(var language in new[]{"zh-CN","en-US"}){
            Localizer.SetLanguage(language);var widget=new WidgetWindow(hub,preferences);
            try{
                ((Button)widget.FindName("NetworkButton")).DataContext=new NetworkUsageModel(new("live","fixture","Ethernet",.10m,.03m,now));
                widget.Show();widget.UpdateLayout();
                Check(widget.ActualHeight==36&&((StackPanel)widget.FindName("Cards")).Children.Count==7,"redesigned strip lost a source or changed height");
                Check(widget.ActualWidth<900,"strip has excess width");
                Check(Visuals<System.Windows.Controls.Image>(widget).All(i=>i.Width==12&&i.Height==12),"provider icons no longer small");
                Check(!Visuals<ProgressBar>(widget).Any(),"progress bar added");
                var labels=Visuals<TextBlock>(widget).Where(t=>t.IsVisible).ToArray();
                foreach(var value in new[]{"84% / 77%","27%","12,345","456,789","$25.50","¥88.25"})Check(labels.Any(t=>Content(t).Contains(value)),"formatted metric absent: "+value);
                foreach(var label in labels){
                    var bounds=label.TransformToAncestor(widget).TransformBounds(new Rect(label.RenderSize));
                    Check(bounds.Right<=widget.ActualWidth+1&&bounds.Bottom<=36,"strip text clipped");
                    label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Check(label.DesiredSize.Width-label.Margin.Left-label.Margin.Right<=label.ActualWidth+1,"strip label has hidden text: "+Content(label));
                }
                var credit=labels.Single(t=>Content(t).Contains("456,789"));Check(credit.Inlines.Count==2&&((Run)credit.Inlines.LastInline!).Foreground.ToString()!=credit.Foreground.ToString(),"credit unit lacks quieter styling");
                var cells=((StackPanel)widget.FindName("Cards")).Children.Cast<Border>().ToArray();Check(cells.All(c=>c.BorderThickness==new Thickness(0)),"per-provider divider remains");
                foreach(var scale in new[]{1d,1.5,2d}){
                    var bitmap=new RenderTargetBitmap((int)Math.Ceiling(widget.ActualWidth*scale),(int)(36*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(widget);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(root,"strip-"+language+"-"+(int)(scale*100)+".png"));encoder.Save(file);
                }
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {ShowNetwork=false}});widget.UpdateLayout();Check(((Border)widget.FindName("NetworkCell")).Visibility==Visibility.Collapsed,"network toggle failed");
                preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {ShowNetwork=true}});
                Check(host.Queries.Events.Count==0,"redesign caused upstream query");
            }finally{widget.AllowClose=true;widget.Close();}
        }
        Localizer.SetLanguage("en-US");Console.WriteLine("PASS approved strip design: seven providers, bilingual, 100/150/200% rendering, tiny icons, currency/credit formats, muted units, no clipping or queries");
    }
}
