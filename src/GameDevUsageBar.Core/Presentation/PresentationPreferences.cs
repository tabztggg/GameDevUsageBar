namespace GameDevUsageBar.Core.Presentation;

public sealed record PixelRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double Intersection(PixelRect other) => Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left))
        * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
    public bool IsValid => new[] { Left, Top, Width, Height }.All(double.IsFinite) && Width > 0 && Height > 0;
}
public sealed record SavedPlacement(string MonitorDevice, PixelRect MonitorBounds, double LeftPx, double TopPx,
    double WidthDip = 280, double ExpandedHeightDip = 360);
public sealed record WidgetPreferences(bool Visible = false, bool Topmost = false, bool Collapsed = false,
    bool Locked = false, string[]? CardIds = null, SavedPlacement? Placement = null,bool ShowNetwork=true,string? NetworkAdapterId=null,
    bool SemiTransparent=false,double TransparencyPercent=35);
public sealed record PresentationPreferences(int Schema = 1, string[]? CardOrder = null,
    bool StartInTray = false, WidgetPreferences? Widget = null, string? Language = null, string[]? FeaturedIds = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public WidgetPreferences WidgetOrDefault => Widget ?? new();
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> FeaturedOrDefault => Array.AsReadOnly(NormalizeFeatured(FeaturedIds ?? ["claude", "codex"]));
    private static string[] NormalizeFeatured(IEnumerable<string> ids) => ids.Select(id => id?.Trim())
        .Where(id => !string.IsNullOrWhiteSpace(id) && !ProviderSources.IsRetired(id))
        .Select(id => id!).Distinct(StringComparer.Ordinal).Take(2).ToArray();
    public PresentationPreferences Validate()
    {
        if(Schema != 1) throw new InvalidDataException("Unknown presentation schema");
        if(Language is not (null or "en-US" or "zh-CN"))throw new InvalidDataException("Unknown UI language");
        if(CardOrder?.Any(string.IsNullOrWhiteSpace) == true || WidgetOrDefault.CardIds?.Any(string.IsNullOrWhiteSpace) == true)
            throw new InvalidDataException("Invalid provider IDs");
        var widget = WidgetOrDefault;
        if(!double.IsFinite(widget.TransparencyPercent))throw new InvalidDataException("Invalid widget transparency");
        widget=widget with {TransparencyPercent=Math.Clamp(widget.TransparencyPercent,0,100)};
        if(widget.NetworkAdapterId?.Length>200 || widget.NetworkAdapterId?.Any(char.IsControl)==true)throw new InvalidDataException("Invalid network adapter ID");
        if(widget.Placement is { } p)
        {
            if(p.MonitorBounds is null || !p.MonitorBounds.IsValid || !double.IsFinite(p.LeftPx) || !double.IsFinite(p.TopPx)
                || !double.IsFinite(p.WidthDip) || !double.IsFinite(p.ExpandedHeightDip))
                throw new InvalidDataException("Invalid placement");
            widget = widget with { Placement = p with {
                WidthDip = Math.Clamp(p.WidthDip, 240, 700), ExpandedHeightDip = Math.Clamp(p.ExpandedHeightDip, 160, 1000) } };
        }
        return this with { CardOrder = CardOrder?.Where(id=>!ProviderSources.IsRetired(id)).Distinct().ToArray(),
            FeaturedIds = FeaturedIds is null ? null : NormalizeFeatured(FeaturedIds),
            Widget = widget with { CardIds = widget.CardIds?.Where(id=>!ProviderSources.IsRetired(id)).Distinct().ToArray() } };
    }
}
public sealed record MonitorInfo(string Device, PixelRect Bounds, PixelRect Work, bool Primary);
public sealed record ResolvedPlacement(MonitorInfo Monitor, double LeftPx, double TopPx,
    double WidthDip, double HeightDip, bool Recovered);

public static class Placement
{
    public static ResolvedPlacement ResolveBar(SavedPlacement? saved, IReadOnlyList<MonitorInfo> monitors, double scale, double widthDip, double heightDip=36)
    {
        if(!double.IsFinite(scale)||scale<=0||!double.IsFinite(widthDip)||widthDip<=0||!double.IsFinite(heightDip)||heightDip<=0)
            throw new ArgumentException("Invalid bar size or DPI scale");
        var m=SelectMonitor(saved,monitors);var w=Math.Min(widthDip,m.Work.Width/scale);var h=Math.Min(heightDip,m.Work.Height/scale);
        var x=saved?.LeftPx ?? m.Work.Right-w*scale-24*scale;var y=saved?.TopPx ?? m.Work.Top+24*scale;
        var cx=Math.Clamp(x,m.Work.Left,Math.Max(m.Work.Left,m.Work.Right-w*scale));
        var cy=Math.Clamp(y,m.Work.Top,Math.Max(m.Work.Top,m.Work.Bottom-h*scale));
        return new(m,cx,cy,w,h,saved is not null && (cx!=x||cy!=y||m.Device!=saved.MonitorDevice||m.Bounds!=saved.MonitorBounds));
    }
    public static MonitorInfo SelectMonitor(SavedPlacement? saved, IReadOnlyList<MonitorInfo> monitors)
    {
        if(monitors.Count == 0) throw new ArgumentException("A monitor is required");
        var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
        if(saved is null) return primary;
        var match = monitors.FirstOrDefault(m => m.Device == saved.MonitorDevice && m.Bounds == saved.MonitorBounds)
            ?? monitors.FirstOrDefault(m => m.Bounds == saved.MonitorBounds);
        if(match != null) return match;
        var rect = new PixelRect(saved.LeftPx, saved.TopPx, saved.WidthDip, saved.ExpandedHeightDip);
        var intersect = monitors.OrderByDescending(m => m.Bounds.Intersection(rect)).First();
        if(intersect.Bounds.Intersection(rect) > 0) return intersect;
        return monitors.OrderBy(m => Math.Pow(Math.Clamp(saved.LeftPx, m.Bounds.Left, m.Bounds.Right) - saved.LeftPx, 2)
            + Math.Pow(Math.Clamp(saved.TopPx, m.Bounds.Top, m.Bounds.Bottom) - saved.TopPx, 2)).First();
    }
    public static ResolvedPlacement Resolve(SavedPlacement? saved, IReadOnlyList<MonitorInfo> monitors, double scale, bool collapsed)
    {
        if(!double.IsFinite(scale) || scale <= 0) throw new ArgumentException("Invalid DPI scale");
        var m = SelectMonitor(saved, monitors);
        var w = Math.Min(Math.Clamp(saved?.WidthDip ?? 280, 240, 700), m.Work.Width / scale);
        var h = Math.Min(collapsed ? 76 : Math.Clamp(saved?.ExpandedHeightDip ?? 360, 160, 1000), m.Work.Height / scale);
        var x = saved?.LeftPx ?? m.Work.Right - w * scale - 24 * scale;
        var y = saved?.TopPx ?? m.Work.Top + 24 * scale;
        var cx = Math.Clamp(x, m.Work.Left, Math.Max(m.Work.Left, m.Work.Right - w * scale));
        var cy = Math.Clamp(y, m.Work.Top, Math.Max(m.Work.Top, m.Work.Bottom - h * scale));
        return new(m, cx, cy, w, h, saved is not null && (cx != x || cy != y || m.Device != saved.MonitorDevice || m.Bounds != saved.MonitorBounds));
    }
    public static PixelRect Anchor(double x, double y, double widthPx, double heightPx, PixelRect work)
    {
        var w = Math.Min(widthPx, work.Width); var h = Math.Min(heightPx, work.Height);
        var left = x > work.Left + work.Width / 2 ? x - w : x;
        var top = y > work.Top + work.Height / 2 ? y - h - 8 : y + 8;
        return new(Math.Clamp(left, work.Left, work.Right - w), Math.Clamp(top, work.Top, work.Bottom - h), w, h);
    }
}
