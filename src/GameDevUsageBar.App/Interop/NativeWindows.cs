using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using GameDevUsageBar.Core.Presentation;
using Forms = System.Windows.Forms;

namespace GameDevUsageBar.App.Interop;

public static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    public static IntPtr Handle(Window window) => new WindowInteropHelper(window).EnsureHandle();
    public static uint InputToken
    {
        get { var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() }; return GetLastInputInfo(ref input) ? input.Time : 0; }
    }
    public static bool PressedOnTray
    {
        get {
            if((GetAsyncKeyState(1) & 0x8000) == 0 || !GetCursorPos(out var point)) return false;
            var root = GetAncestor(WindowFromPoint(point), 2);
            var name = new StringBuilder(256); GetClassName(root, name, name.Capacity);
            return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow";
        }
    }
    public static double Scale(Window window) => Math.Max(96, GetDpiForWindow(Handle(window))) / 96d;
    public static PixelRect Bounds(Window window)
    {
        if(!GetWindowRect(Handle(window), out var r)) throw new InvalidOperationException("Window bounds unavailable");
        return new(r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top);
    }
    public static IReadOnlyList<MonitorInfo> Monitors() => Forms.Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName,
        new(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height),
        new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height), s.Primary)).ToArray();
    public static void Move(Window window, double left, double top) =>
        SetWindowPos(Handle(window), IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), 0, 0, 0x0010 | 0x0004 | 0x0001);
    public static void SetTopmost(Window window, bool topmost) =>
        SetWindowPos(Handle(window), new IntPtr(topmost ? -1 : -2), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
    public static void NeverActivate(Window window)
    {
        var hwnd = Handle(window);
        SetStyle(hwnd, -20, new IntPtr((GetStyle(hwnd, -20).ToInt64() | 0x08000000L | 0x00000080L) & ~0x00040000L));
    }
    public static ResolvedPlacement PlaceWidget(Window window, SavedPlacement? saved, bool collapsed)
    {
        var monitors = Monitors(); var target = Placement.SelectMonitor(saved, monitors);
        // Ask the window's actual per-monitor DPI. No GetDpiForMonitor call from a PM-aware thread.
        if(Forms.Screen.FromHandle(Handle(window)).DeviceName != target.Device)
            Move(window, target.Work.Left + 24, target.Work.Top + 24);
        var resolved = Placement.Resolve(saved, monitors, Scale(window), collapsed);
        window.Width = resolved.WidthDip; window.Height = resolved.HeightDip;
        Move(window, resolved.LeftPx, resolved.TopPx);
        return resolved;
    }
    public static void MoveToMonitor(Window window,MonitorInfo target)
    {
        if(Forms.Screen.FromHandle(Handle(window)).DeviceName!=target.Device)Move(window,target.Work.Left+24,target.Work.Top+24);
    }
    public static void PlacePopup(Window window,Point anchor)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y));
        Move(window, screen.WorkingArea.Left + 8, screen.WorkingArea.Top + 8);
        var scale = Scale(window);
        var width = Math.Min(400, screen.WorkingArea.Width / scale);
        var maxHeight = Math.Min(680, screen.WorkingArea.Height / scale);
        if(window is TrayPopupWindow panel)panel.FitToWorkArea(width,maxHeight);
        else {window.Width=width;window.Height=maxHeight;}
        var work = new PixelRect(screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height);
        var box = Placement.Anchor(anchor.X, anchor.Y, window.Width * scale, window.Height * scale, work);
        Move(window, box.Left, box.Top);
    }
    public static SavedPlacement SavePlacement(Window window, double expandedHeight)
    {
        var bounds = Bounds(window); var monitor = Monitors().OrderByDescending(m => m.Bounds.Intersection(bounds)).First();
        return new(monitor.Device, monitor.Bounds, bounds.Left, bounds.Top, window.Width, expandedHeight);
    }
}
