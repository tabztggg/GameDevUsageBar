using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
using GameDevUsageBar.Providers;
using Forms=System.Windows.Forms;

internal static class MenuThemeChecks
{
    public static async Task Run(ApplicationHost host,ProviderStateHub hub,PresentationPreferencesService preferences,SurfaceManager surfaces,TrayIconHost tray,string root)
    {
        var settings=File.ReadAllBytes(Path.Combine(host.Root,"settings.json"));var events=host.Queries.Events.Count;
        var languageBefore=Localizer.Language;
        try {
            foreach(var language in new[]{"zh-CN","en-US"}) {
                Localizer.SetLanguage(language);
                var menu=surfaces.Widget.CreateMoreMenu();
                var checkedItem=menu.Items.OfType<MenuItem>().First(item=>item.IsCheckable);checkedItem.IsChecked=true;
                var disabled=new MenuItem{Header=language=="zh-CN"?"禁用项测试":"Disabled item",IsEnabled=false};menu.Items.Add(disabled);
                var nested=new MenuItem{Header=language=="zh-CN"?"子菜单测试":"Submenu"};nested.Items.Add(new MenuItem{Header=Localizer.T("Settings")});menu.Items.Add(nested);
                try {
                    menu.IsOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);menu.UpdateLayout();
                    AssertContrast(menu.Foreground,menu.Background,"widget menu");
                    Assert(((SolidColorBrush)menu.Background).Color.R<80 || SystemParameters.HighContrast,"widget menu reverted to white");
                    Assert(checkedItem.Template.FindName("CheckMark",checkedItem) is TextBlock{Visibility:Visibility.Visible},"check mark missing");
                    AssertContrast(disabled.Foreground,menu.Background,"disabled item");
                    Render(menu,Path.Combine(root,"widget-menu-"+language+".png"));
                    nested.IsSubmenuOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var popup=(Popup)nested.Template.FindName("PART_Popup",nested);var submenu=(Border)popup.Child;
                    AssertContrast(nested.Foreground,submenu.Background,"submenu");
                    Render(submenu,Path.Combine(root,"submenu-"+language+".png"));nested.IsSubmenuOpen=false;
                } finally {menu.IsOpen=false;}
                var notification=(Forms.NotifyIcon)typeof(TrayIconHost).GetField("icon",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(tray)!;
                var trayMenu=notification.ContextMenuStrip!;
                trayMenu.Show(40,40);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                try {
                    using var bitmap=new System.Drawing.Bitmap(trayMenu.Width,trayMenu.Height);trayMenu.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(root,"tray-menu-"+language+".png"));
                    var color=bitmap.GetPixel(bitmap.Width/2,2);
                    Assert(color.R<80 || SystemParameters.HighContrast,"tray menu background still white");
                    AssertContrast(new SolidColorBrush(Color.FromRgb(trayMenu.ForeColor.R,trayMenu.ForeColor.G,trayMenu.ForeColor.B)),new SolidColorBrush(Color.FromRgb(color.R,color.G,color.B)),"tray menu");
                } finally {trayMenu.Close();}
                await CheckCombo((ComboBox)surfaces.Overview.FindName("LanguageSelector"),root,"overview-language-"+language);
                var display=new DisplaySettingsWindow(preferences,hub){Owner=surfaces.Overview};
                try {display.Show();await CheckCombo((ComboBox)display.FindName("LanguageSelector"),root,"settings-language-"+language);}
                finally {display.Close();}
                foreach(var id in new[]{"claude","tripo","grsai","demo"}) {
                    var definition=ProviderCatalog.Create().Single(a=>a.Definition.Id==id).Definition;
                    var account=new AccountWindow(host,definition,new AccountConfig(id,Guid.NewGuid(),"Menu fixture"),[new("Fixture profile",root),new("Second profile",root)] ){Owner=surfaces.Overview};
                    try {
                        account.Show();await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        foreach(var name in new[]{"SourceSelector","RegionSelector","BrowserSelector","ProfileSelector","Scenario"}) {
                            var combo=(ComboBox)account.FindName(name);
                            if(combo.Visibility==Visibility.Visible && combo.IsVisible && combo.Items.Count>0)await CheckCombo(combo,root,id+"-"+name+"-"+language);
                        }
                    } finally {account.Close();}
                }
            }
            // Verify the custom templates resolve changed system palette resources,
            // without changing the user's actual high-contrast setting.
            var resources=Application.Current.Resources;
            resources["PanelBrush"]=SystemColors.WindowBrush;resources["TextBrush"]=SystemColors.WindowTextBrush;
            resources["HoverBrush"]=SystemColors.HighlightBrush;resources["MenuSelectedTextBrush"]=SystemColors.HighlightTextBrush;
            resources["ComboTextBrush"]=SystemColors.WindowTextBrush;resources["ComboBackgroundBrush"]=SystemColors.WindowBrush;
            var themed=surfaces.Widget.CreateMoreMenu();
            try {themed.IsOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);AssertContrast(themed.Foreground,themed.Background,"system-color menu");Render(themed,Path.Combine(root,"widget-menu-system-colors.png"));}
            finally {themed.IsOpen=false;ThemeService.Apply();}
            Assert(settings.SequenceEqual(File.ReadAllBytes(Path.Combine(host.Root,"settings.json"))) && events==host.Queries.Events.Count,"opening menus caused account writes or upstream queries");
            Console.WriteLine("PASS widget and nested menus, tray menu, all seven selector families, bilingual and system palette rendering");
        } finally {ThemeService.Apply();Localizer.SetLanguage(languageBefore);}
    }
    private static async Task CheckCombo(ComboBox combo,string root,string name)
    {
        combo.BringIntoView();combo.UpdateLayout();var selected=combo.SelectedValue;
        AssertContrast(combo.Foreground,combo.Background,name+" selection");
        try {
            combo.IsDropDownOpen=true;await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);combo.UpdateLayout();
            var popup=(Popup)combo.Template.FindName("PART_Popup",combo);var border=(Border)popup.Child;
            AssertContrast(combo.Foreground,border.Background,name+" dropdown");
            Assert(((SolidColorBrush)border.Background).Color.R<80 || SystemParameters.HighContrast,"dropdown reverted to white: "+name);
            foreach(var item in Visuals<ComboBoxItem>(border)) {
                var surface=(Border)item.Template.FindName("ChoiceSurface",item);
                var background=((SolidColorBrush)surface.Background).Color.A==0?border.Background:surface.Background;
                AssertContrast(item.Foreground,background,name+" dropdown item");
                foreach(var label in Visuals<TextBlock>(item))if(!string.IsNullOrEmpty(label.Text))AssertContrast(label.Foreground,background,name+" item label");
            }
            Render(border,Path.Combine(root,"dropdown-"+name+".png"));
        } finally {combo.IsDropDownOpen=false;}
        Assert(Equals(selected,combo.SelectedValue),"opening menu changed selection: "+name);
    }
    private static void Assert(bool value,string detail){if(!value)throw new Exception(detail);}
    private static void AssertContrast(Brush foreground,Brush background,string detail)
    {
        var f=((SolidColorBrush)foreground).Color;var b=((SolidColorBrush)background).Color;
        double Channel(byte value){var c=value/255d;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
        double L(Color c)=>.2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);
        var a=L(f);var d=L(b);var ratio=(Math.Max(a,d)+.05)/(Math.Min(a,d)+.05);
        Assert(ratio>=4.5,detail+" contrast too low: "+ratio);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T match)yield return match;foreach(var item in Visuals<T>(child))yield return item;}}
    private static void Render(FrameworkElement element,string file)
    {
        element.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth*1.5),(int)Math.Ceiling(element.ActualHeight*1.5),144,144,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(file);encoder.Save(stream);
    }
}
