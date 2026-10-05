using System.Drawing;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Forms = System.Windows.Forms;

namespace GameDevUsageBar.App.Presentation;

// WinForms tray menus do not inherit WPF styles. Resolve the same palette at paint time.
public static class TrayMenuTheme
{
    private static System.Drawing.Color Palette(string name)
    {
        var color=((SolidColorBrush)System.Windows.Application.Current.Resources[name]).Color;
        return System.Drawing.Color.FromArgb(color.A,color.R,color.G,color.B);
    }
    public static void Apply(Forms.ContextMenuStrip menu)
    {
        menu.Renderer=new Renderer();menu.BackColor=Palette("PanelBrush");menu.ForeColor=Palette("TextBrush");
        menu.Padding=new Forms.Padding(4);menu.ShowImageMargin=true;
        foreach(Forms.ToolStripItem item in menu.Items)item.ForeColor=Palette("TextBrush");
        menu.Invalidate();
    }
    private sealed class Renderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {using var brush=new SolidBrush(Palette("PanelBrush"));e.Graphics.FillRectangle(brush,e.AffectedBounds);}
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {using var pen=new Pen(Palette("EdgeBrush"));e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);}
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {using var brush=new SolidBrush(Palette("PanelBrush"));e.Graphics.FillRectangle(brush,e.AffectedBounds);}
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {using var brush=new SolidBrush(Palette(e.Item.Selected && e.Item.Enabled ? "HoverBrush" : "PanelBrush"));e.Graphics.FillRectangle(brush,new Rectangle(Point.Empty,e.Item.Size));}
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {e.TextColor=Palette(!e.Item.Enabled?"MenuDisabledTextBrush":e.Item.Selected?"MenuSelectedTextBrush":"TextBrush");base.OnRenderItemText(e);}
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {using var pen=new Pen(Palette("EdgeBrush"));e.Graphics.DrawLine(pen,28,e.Item.Height/2,e.Item.Width-8,e.Item.Height/2);}
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var pen=new Pen(Palette(e.Item.Selected?"MenuSelectedTextBrush":"TextBrush"),2);
            var r=e.ImageRectangle;var x=r.Left+r.Width/2;var y=r.Top+r.Height/2;
            e.Graphics.DrawLines(pen,[new Point(x-5,y),new Point(x-1,y+4),new Point(x+6,y-4)]);
        }
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {e.ArrowColor=Palette(e.Item?.Enabled==true?"TextBrush":"MenuDisabledTextBrush");base.OnRenderArrow(e);}
    }
}
