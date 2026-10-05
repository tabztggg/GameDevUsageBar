using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace GameDevUsageBar.App;

public static class ProviderArtwork
{
    private static readonly Dictionary<string,ImageSource> images=new();
    public static ImageSource For(string id,string accent)
    {
        var name=id switch {"gemini-cli"=>"gemini",_=>id};
        var key=name+accent;if(images.TryGetValue(key,out var image))return image;
        var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent));brush.Freeze();var group=new DrawingGroup();
        if(name is "codex" or "claude" or "gemini" or "deepseek" or "openrouter" or "elevenlabs") {
            using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/GameDevUsageBar;component/Assets/ProviderIcons/"+name+".svg")).Stream;
            var root=XDocument.Load(stream).Root!;
            var box=root.Attribute("viewBox")!.Value.Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray();
            var bounds=new Rect(box[0],box[1],box[2],box[3]);group.ClipGeometry=new RectangleGeometry(bounds);
            group.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(bounds)));
            foreach(var path in root.Descendants().Where(e=>e.Name.LocalName=="path")) {
                var geometry=Geometry.Parse((path.Attribute("fill-rule")?.Value=="evenodd" ? "F0 " : "F1 ")+path.Attribute("d")!.Value);
                var pen=path.Attribute("stroke-width") is { } stroke ? new Pen(brush,double.Parse(stroke.Value,CultureInfo.InvariantCulture)) : null;
                group.Children.Add(new GeometryDrawing(brush,pen,geometry));
            }
        } else {
            var letter=name switch {"tripo"=>"T","grsai"=>"G","typesafe"=>"T","demo"=>"◇",_=>name[..1].ToUpperInvariant()};
            var text=new FormattedText(letter,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),14,brush,1);
            group.Children.Add(new GeometryDrawing(brush,null,text.BuildGeometry(new Point())));
        }
        group.Freeze();var drawing=new DrawingImage(group);drawing.Freeze();images[key]=drawing;return drawing;
    }
}
