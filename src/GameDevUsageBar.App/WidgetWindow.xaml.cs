using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.ComponentModel;
using System.Windows.Interop;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.App;

public partial class WidgetWindow : Window
{
    public const double BarHeight=36;
    private readonly ProviderStateHub hub;
    private readonly PresentationPreferencesService preferences;
    private readonly ObservableCollection<CardModel> visibleCards=[];
    private CardModel[] selected=[];
    private HwndSource? source;
    private NativeWindows.Point startPointer;
    private double startLeft,startTop;
    private bool gesture,applying,reflow;
    public bool AllowClose {get;set;}
    public event Action? SettingsRequested;
    public event Action? NetworkRequested;
    public event Action<string>? ProviderRequested;
    public WidgetWindow(ProviderStateHub hub,PresentationPreferencesService preferences)
    {
        this.hub=hub;this.preferences=preferences;InitializeComponent();
        NetworkButton.DataContext=hub.Network;
        NetworkButton.ToolTip=new ToolTip {Content=new NetworkUsageView {DataContext=hub.Network,Width=320},Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Placement=PlacementMode.Bottom,HasDropShadow=false};
        hub.Changed+=Changed;hub.Network.PropertyChanged+=NetworkChanged;
        SourceInitialized+=(_,_)=> {NativeWindows.NeverActivate(this);source=HwndSource.FromHwnd(NativeWindows.Handle(this));source?.AddHook(WindowMessage);Reposition();};
        Closing+=(_,e)=> {if(!AllowClose){e.Cancel=true;Hide();preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=false}});}};
        Closed+=(_,_)=> {hub.Changed-=Changed;hub.Network.PropertyChanged-=NetworkChanged;source?.RemoveHook(WindowMessage);};
        Changed();
    }
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x0021){handled=true;return new IntPtr(3);} // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        if(message==0x02E0 && !reflow && !Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(Reposition);
        return IntPtr.Zero;
    }
    private void Changed()
    {
        var p=preferences.Current.WidgetOrDefault;
        if(p.CardIds is { } ids)selected=hub.Models.Where(m=>ids.Contains(m.Id)).ToArray();
        else {
            var enabled=hub.Models.Where(m=>m.EligibleForWidget).ToArray();
            selected=enabled.Length>0 ? enabled.Take(5).ToArray() : hub.Models.Where(m=>!m.Definition.IsDemo).Take(5).ToArray();
        }
        NetworkCell.Visibility=p.ShowNetwork?Visibility.Visible:Visibility.Collapsed;
        DragHandle.Cursor=p.Locked ? Cursors.Arrow : Cursors.SizeAll;
        TopButton.ToolTip=L.T(p.Topmost ? "Always on top: on" : "Always on top: off");
        TopButton.Opacity=p.Topmost ? 1 : .65;
        BarBackground.Opacity=p.SemiTransparent ? 1-p.TransparencyPercent/100 : 1;
        Reposition();
    }
    public void Reposition()
    {
        if(gesture || reflow)return;
        reflow=applying=true;
        try {
            var p=preferences.Current.WidgetOrDefault;
            var monitors=NativeWindows.Monitors();var target=Placement.SelectMonitor(p.Placement,monitors);
            if(source is not null)NativeWindows.MoveToMonitor(this,target);
            var scale=source is null ? 1 : NativeWindows.Scale(this);
            var maxWidth=Math.Min(1100,target.Work.Width/scale);
            visibleCards.Clear();Cards.Children.Clear();
            foreach(var model in selected){
                var cell=(FrameworkElement)((DataTemplate)FindResource("ProviderTemplate")).LoadContent();
                // Set the measured content before layout: inherited DataContext bindings
                // can otherwise settle after the window has already chosen its width.
                var button=(Button)((Border)cell).Child;
                var content=(StackPanel)button.Content;
                ((Image)content.Children[0]).Source=model.IconSource;
                var label=(TextBlock)content.Children[1];((Run)label.Inlines.FirstInline!).Text=model.BarNumber;((Run)label.Inlines.LastInline!).Text=model.BarUnit;
                cell.DataContext=model;button.Tag=model.Id;button.ToolTip=UsageToolTip.Create(model);
                System.Windows.Automation.AutomationProperties.SetName(button,model.BarAccessibleText);
                Cards.Children.Add(cell);visibleCards.Add(model);
            }
            ((TextBlock)EmptyMessage.Content).Text=L.T("Set up sources");
            // The button's template can retain its previous desired width until the
            // next layout pass. Give the localized empty label a measured box now.
            var emptyText=new System.Windows.Media.FormattedText(L.T("Set up sources"),L.Culture,FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(FontFamily,FontStyle,FontWeight,FontStretch),12,Foreground,scale);
            EmptyMessage.Width=Math.Ceiling(emptyText.WidthIncludingTrailingWhitespace)+12;
            EmptyMessage.Visibility=selected.Length==0 ? Visibility.Visible : Visibility.Collapsed;
            SizeNetwork();
            var desired=MeasureRow();
            // Overflow removes complete cells, never part of a numeric value.
            while(desired>maxWidth && visibleCards.Count>0){Cards.Children.RemoveAt(Cards.Children.Count-1);visibleCards.RemoveAt(visibleCards.Count-1);desired=MeasureRow();}
            var width=Math.Min(maxWidth,Math.Max(110,desired));
            Width=width;Height=Math.Min(BarHeight,target.Work.Height/scale);
            if(source is not null){var box=Placement.ResolveBar(p.Placement,monitors,scale,width,BarHeight);NativeWindows.Move(this,box.LeftPx,box.TopPx);NativeWindows.SetTopmost(this,p.Topmost);}
        } finally {reflow=applying=false;}
    }
    private void NetworkChanged(object? sender,PropertyChangedEventArgs e){if(!reflow&&SizeNetwork())Reposition();}
    private bool SizeNetwork(){
        // Reserve equal-width digits so ordinary samples never move the cells.
        var text=new string(hub.Network.BarValue.Select(c=>char.IsDigit(c)?'8':c).ToArray());
        var formatted=new System.Windows.Media.FormattedText(text,L.Culture,FlowDirection.LeftToRight,new System.Windows.Media.Typeface("Segoe UI"),13,Foreground,source is null?1:NativeWindows.Scale(this));
        var width=Math.Max(148,Math.Ceiling(formatted.WidthIncludingTrailingWhitespace)+12);
        if(Math.Abs(NetworkButton.Width-width)<1)return false;NetworkButton.Width=width;return true;
    }
    private double MeasureRow(){Cards.InvalidateMeasure();ValuesRow.InvalidateMeasure();BarRow.InvalidateMeasure();BarRow.Measure(new Size(double.PositiveInfinity,BarHeight-2));return Math.Ceiling(BarRow.DesiredSize.Width)+12;}
    private void Provider_Click(object sender,RoutedEventArgs e){if(sender is Button {Tag:string id})ProviderRequested?.Invoke(id);}
    private void Network_Click(object sender,RoutedEventArgs e)=>NetworkRequested?.Invoke();
    private void Settings_Click(object sender,RoutedEventArgs e)=>SettingsRequested?.Invoke();
    private void More_Click(object sender,RoutedEventArgs e)=>CreateMoreMenu().IsOpen=true;
    public ContextMenu CreateMoreMenu()
    {
        var menu=new ContextMenu {PlacementTarget=MoreButton,Placement=PlacementMode.Bottom};
        foreach(var model in selected.Except(visibleCards)){
            var id=model.Id;var item=new MenuItem {Header=model.Name+" · "+model.BarValue,ToolTip=UsageToolTip.Create(model)};item.Click+=(_,_)=>ProviderRequested?.Invoke(id);menu.Items.Add(item);
        }
        if(menu.Items.Count>0)menu.Items.Add(new Separator());
        Add("Display settings…",()=>SettingsRequested?.Invoke());
        Add("Always on top",()=>Top_Click(this,new RoutedEventArgs()),preferences.Current.WidgetOrDefault.Topmost);
        menu.Items.Add(new MenuItem {Header=new WidgetTransparencyControl(preferences),StaysOpenOnClick=true});
        Add("Lock position",()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Locked=!p.WidgetOrDefault.Locked}}),preferences.Current.WidgetOrDefault.Locked);
        Add("Hide widget",()=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=false}}));
        return menu;
        void Add(string label,Action action,bool? isChecked=null){var item=new MenuItem {Header=L.T(label),IsCheckable=isChecked.HasValue,IsChecked=isChecked==true};item.Click+=(_,_)=>action();menu.Items.Add(item);}
    }
    private void Drag_Down(object sender,MouseButtonEventArgs e)
    {
        if(preferences.Current.WidgetOrDefault.Locked || applying)return;
        NativeWindows.GetCursorPos(out startPointer);var bounds=NativeWindows.Bounds(this);startLeft=bounds.Left;startTop=bounds.Top;
        gesture=true;((UIElement)sender).CaptureMouse();e.Handled=true;
    }
    private void Gesture_Move(object sender,MouseEventArgs e)
    {
        if(!gesture)return;if(e.LeftButton!=MouseButtonState.Pressed){FinishGesture();return;}
        NativeWindows.GetCursorPos(out var point);NativeWindows.Move(this,startLeft+point.X-startPointer.X,startTop+point.Y-startPointer.Y);
    }
    private void Gesture_Up(object sender,MouseButtonEventArgs e){FinishGesture();e.Handled=true;}
    private void Gesture_Lost(object sender,MouseEventArgs e)=>FinishGesture();
    private void FinishGesture()
    {
        if(!gesture)return;gesture=false;Mouse.Capture(null);
        var p=preferences.Current.WidgetOrDefault;var saved=NativeWindows.SavePlacement(this,p.Placement?.ExpandedHeightDip ?? 360);
        var box=Placement.ResolveBar(saved,NativeWindows.Monitors(),NativeWindows.Scale(this),Width,BarHeight);
        saved=saved with {LeftPx=box.LeftPx,TopPx=box.TopPx};
        preferences.Update(v=>v with {Widget=v.WidgetOrDefault with {Placement=saved}});Reposition();
    }
    private void Top_Click(object sender,RoutedEventArgs e)=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Topmost=!p.WidgetOrDefault.Topmost}});
}
