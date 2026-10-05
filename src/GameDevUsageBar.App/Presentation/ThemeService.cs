using System.Windows;
using System.Windows.Media;

namespace GameDevUsageBar.App.Presentation;

public static class ThemeService
{
    public static void Apply()
    {
        var resources = System.Windows.Application.Current.Resources;
        var hc = SystemParameters.HighContrast;
        Brush Brush(string hex, Brush contrast) => hc ? contrast : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        resources["BackgroundBrush"] = Brush("#10151E", SystemColors.WindowBrush);
        resources["StripBackgroundBrush"] = Brush("#12161C", SystemColors.WindowBrush);
        resources["StripEdgeBrush"] = Brush("#262D38", SystemColors.WindowTextBrush);
        resources["StripHoverBrush"] = Brush("#222830", SystemColors.ControlBrush);
        resources["StripTextBrush"] = Brush("#E4E7EB", SystemColors.WindowTextBrush);
        resources["StripMutedBrush"] = Brush("#9CA6B4", SystemColors.WindowTextBrush);
        resources["PanelBrush"] = Brush("#1A2230", SystemColors.WindowBrush);
        resources["TextBrush"] = Brush("#E7EDF7", SystemColors.WindowTextBrush);
        resources["MutedBrush"] = Brush("#9BA9BF", SystemColors.WindowTextBrush);
        resources["EdgeBrush"] = Brush("#2A364A", SystemColors.WindowTextBrush);
        resources["BannerBrush"] = Brush("#172135", SystemColors.WindowBrush);
        resources["AccentBrush"] = Brush("#9AADFF", SystemColors.HighlightBrush);
        resources["AlertBrush"] = Brush("#F1BB66", SystemColors.WindowTextBrush);
        resources["ButtonBrush"] = Brush("#253248", SystemColors.ControlBrush);
        resources["HoverBrush"] = Brush("#35476A", SystemColors.HighlightBrush);
        resources["InputBrush"] = Brush("#101723", SystemColors.WindowBrush);
        resources["ActionBrush"] = Brush("#394F89", SystemColors.ControlBrush);
        resources["ComboTextBrush"] = Brush("#E7EDF7", SystemColors.WindowTextBrush);
        resources["ComboBackgroundBrush"] = Brush("#101723", SystemColors.WindowBrush);
        resources["MenuSelectedTextBrush"] = Brush("#E7EDF7", SystemColors.HighlightTextBrush);
        resources["MenuDisabledTextBrush"] = Brush("#9BA9BF", SystemColors.GrayTextBrush);
    }
}
