using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

internal static class TransparencyChecks
{
    private static void Check(bool condition,string reason){if(!condition)throw new Exception(reason);}
    public static async Task Run(string root)
    {
        var dir=Path.Combine(root,"transparency");Directory.CreateDirectory(dir);
        var legacy=Path.Combine(dir,"presentation.json");File.WriteAllText(legacy,"{\"schema\":1,\"widget\":{\"visible\":true}}");
        var store=new PresentationStore(dir);var initial=(await store.LoadAsync())!;
        Check(!initial.WidgetOrDefault.SemiTransparent&&initial.WidgetOrDefault.TransparencyPercent==35,"legacy defaults changed");
        Check(new PresentationPreferences(Widget:new(TransparencyPercent:200)).Validate().WidgetOrDefault.TransparencyPercent==100,"range not clamped");
        bool rejected=false;try{new PresentationPreferences(Widget:new(TransparencyPercent:double.NaN)).Validate();}catch(InvalidDataException){rejected=true;}Check(rejected,"nonfinite transparency accepted");
        await using var host=new ApplicationHost(dir,ProviderCatalog.Create().Where(a=>a.Definition.Id=="demo").ToArray());await host.InitializeAsync();
        await host.SaveAsync(host.Accounts.Single());
        var accounts=File.ReadAllBytes(Path.Combine(dir,"settings.json"));
        using var preferences=new PresentationPreferencesService(store,initial);
        using var hub=new ProviderStateHub(host.Adapters,host.Coordinator,preferences,Application.Current.Dispatcher);
        var widget=new WidgetWindow(hub,preferences);var settings=new DisplaySettingsWindow(preferences,hub);ContextMenu? menu=null;
        try {
            widget.Show();settings.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var settingsControl=((StackPanel)settings.FindName("TransparencySlot")).Children.OfType<WidgetTransparencyControl>().Single();
            foreach(var language in new[]{"zh-CN","en-US"}) {
                Localizer.SetLanguage(language);menu=widget.CreateMoreMenu();menu.IsOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var control=menu.Items.OfType<MenuItem>().Select(i=>i.Header).OfType<WidgetTransparencyControl>().Single();
                var toggle=(CheckBox)control.FindName("EnabledToggle");var slider=(Slider)control.FindName("AmountSlider");
                Check((string)toggle.Content==Localizer.T("Semi-transparent"),"menu untranslated");
                toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));slider.Value=60;
                var background=(Border)widget.FindName("BarBackground");
                Check(widget.AllowsTransparency&&widget.Opacity==1&&Math.Abs(background.Opacity-.4)<.001,"native background opacity missing or text faded");
                Check(((Slider)settingsControl.FindName("AmountSlider")).Value==60,"settings slider out of sync");
                widget.UpdateLayout();var bitmap=new RenderTargetBitmap((int)background.ActualWidth,(int)background.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(background);
                var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect((int)background.ActualWidth/2,18,1,1),pixel,4,0);Check(pixel[3]>=100&&pixel[3]<=104,"background alpha not composed");
                control.UpdateLayout();var preview=new RenderTargetBitmap((int)Math.Ceiling(control.ActualWidth),(int)Math.Ceiling(control.ActualHeight),96,96,PixelFormats.Pbgra32);preview.Render(control);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(preview));using(var file=File.Create(Path.Combine(root,"transparency-"+language+".png")))encoder.Save(file);
                toggle.IsChecked=false;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Check(background.Opacity==1&&preferences.Current.WidgetOrDefault.TransparencyPercent==60,"disabled opacity or saved amount wrong");
                toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                slider.Value=0;Check(background.Opacity==1,"zero transparency wrong");slider.Value=100;Check(background.Opacity==0&&widget.Opacity==1,"full transparency wrong");slider.Value=60;
                await preferences.FlushAsync();var loaded=(await new PresentationStore(dir).LoadAsync())!;Check(loaded.WidgetOrDefault.SemiTransparent&&loaded.WidgetOrDefault.TransparencyPercent==60,"restart preference lost");
                Check(menu.IsOpen,"slider/toggle closed menu");menu.IsOpen=false;menu=null;
            }
            Check(accounts.SequenceEqual(File.ReadAllBytes(Path.Combine(dir,"settings.json")))&&host.Queries.Events.Count==0,"transparency touched accounts or sent a query");
        } finally {if(menu is not null)menu.IsOpen=false;settings.Close();widget.AllowClose=true;widget.Close();Localizer.SetLanguage("en-US");}
        Console.WriteLine("PASS transparency: bilingual menu/settings, live background alpha, readable text, 0/100 boundaries, legacy defaults, persistence and no account writes/queries");
    }
}
