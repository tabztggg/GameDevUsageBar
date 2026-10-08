using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Data;
using System.Windows.Threading;
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
    private readonly Dictionary<string,FrameworkElement> providerCells=[];
    private readonly DispatcherTimer hoverDelay=new() {Interval=TimeSpan.FromMilliseconds(350)};
    private readonly DispatcherTimer hoverDismiss=new() {Interval=TimeSpan.FromMilliseconds(220)};
    private readonly Border hoverSurface;
    private readonly ScrollViewer hoverScroll;
    private string? pendingHoverId,hoverId;
    private FrameworkElement? pendingHoverTarget;
    private ContextMenu? hoverOwnerMenu;
    public ProviderAccountsView HoverAccounts {get;}=new() {ShowActions=false};
    public Popup ProviderHoverPopup {get;}=new() {Placement=PlacementMode.Custom,AllowsTransparency=true,StaysOpen=true,PopupAnimation=PopupAnimation.None};
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
        hoverScroll=new ScrollViewer {Content=HoverAccounts,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        hoverSurface=new Border {Child=hoverScroll,Padding=new Thickness(12),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8)};
        hoverSurface.SetResourceReference(Border.BackgroundProperty,"PanelBrush");hoverSurface.SetResourceReference(Border.BorderBrushProperty,"EdgeBrush");
        ProviderHoverPopup.Child=hoverSurface;
        ProviderHoverPopup.CustomPopupPlacementCallback=PlaceProviderHover;
        ProviderHoverPopup.Opened+=(_,_)=>RefitProviderHover();
        hoverSurface.MouseEnter+=(_,_)=>hoverDismiss.Stop();
        hoverSurface.MouseLeave+=(_,_)=>ScheduleHoverDismiss();
        hoverSurface.MouseLeftButtonUp+=Hover_Click;
        hoverDelay.Tick+=(_,_)=>{hoverDelay.Stop();if(pendingHoverId is not null && pendingHoverTarget is {IsMouseOver:true})ShowProviderHover(pendingHoverId,pendingHoverTarget);};
        hoverDismiss.Tick+=(_,_)=>{hoverDismiss.Stop();if(!hoverSurface.IsMouseOver && ProviderHoverPopup.PlacementTarget is not {IsMouseOver:true})HideProviderHover();};
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)HideProviderHover();};
        NetworkButton.DataContext=hub.Network;
        NetworkButton.ToolTip=new ToolTip {Content=new NetworkUsageView {DataContext=hub.Network,Width=320},Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Placement=PlacementMode.Bottom,HasDropShadow=false};
        hub.Changed+=Changed;hub.Network.PropertyChanged+=NetworkChanged;
        SourceInitialized+=(_,_)=> {NativeWindows.NeverActivate(this);source=HwndSource.FromHwnd(NativeWindows.Handle(this));source?.AddHook(WindowMessage);Reposition();};
        Closing+=(_,e)=> {if(!AllowClose){e.Cancel=true;Hide();preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Visible=false}});}};
        Closed+=(_,_)=> {HideProviderHover();hub.Changed-=Changed;hub.Network.PropertyChanged-=NetworkChanged;source?.RemoveHook(WindowMessage);};
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
            ((TextBlock)EmptyMessage.Content).Text=L.T("Set up sources");
            // The button's template can retain its previous desired width until the
            // next layout pass. Give the localized empty label a measured box now.
            var emptyText=new System.Windows.Media.FormattedText(L.T("Set up sources"),L.Culture,FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(FontFamily,FontStyle,FontWeight,FontStretch),12,Foreground,scale);
            EmptyMessage.Width=Math.Ceiling(emptyText.WidthIncludingTrailingWhitespace)+12;
            EmptyMessage.Visibility=selected.Length==0 ? Visibility.Visible : Visibility.Collapsed;
            SizeNetwork();
            // Retain the actual provider buttons during background account updates.
            // Replacing the hover anchor would close a readable, scrollable account list.
            var baseWidth=MeasureRow()-Cards.DesiredSize.Width;
            var next=selected.ToList();
            var cells=next.Select(ProviderCell).ToList();
            foreach(var cell in cells)cell.Measure(new Size(double.PositiveInfinity,BarHeight-2));
            var desired=baseWidth+cells.Sum(cell=>Math.Ceiling(cell.DesiredSize.Width));
            // Overflow removes complete cells, never part of a numeric value.
            while(desired>maxWidth && next.Count>0){desired-=Math.Ceiling(cells[^1].DesiredSize.Width);cells.RemoveAt(cells.Count-1);next.RemoveAt(next.Count-1);}
            if(!visibleCards.SequenceEqual(next)){
                if(hoverId is not null && !next.Any(model=>model.Id==hoverId))HideProviderHover();
                Cards.Children.Clear();visibleCards.Clear();
                foreach(var cell in cells)Cards.Children.Add(cell);
                foreach(var model in next)visibleCards.Add(model);
            }
            desired=MeasureRow();
            var width=Math.Min(maxWidth,Math.Max(110,desired));
            Width=width;Height=Math.Min(BarHeight,target.Work.Height/scale);
            if(source is not null){var box=Placement.ResolveBar(p.Placement,monitors,scale,width,BarHeight);NativeWindows.Move(this,box.LeftPx,box.TopPx);NativeWindows.SetTopmost(this,p.Topmost);}
            RefitProviderHover();
        } finally {reflow=applying=false;}
    }
    private FrameworkElement ProviderCell(CardModel model)
    {
        if(providerCells.TryGetValue(model.Id,out var existing))return existing;
        var cell=(FrameworkElement)((DataTemplate)FindResource("ProviderTemplate")).LoadContent();
        var button=(Button)((Border)cell).Child;var content=(StackPanel)button.Content;
        ((Image)content.Children[0]).SetBinding(Image.SourceProperty,new Binding(nameof(CardModel.IconSource)){Source=model});
        var label=(TextBlock)content.Children[1];
        ((Run)label.Inlines.FirstInline!).SetBinding(Run.TextProperty,new Binding(nameof(CardModel.BarNumber)){Source=model,Mode=BindingMode.OneWay});
        ((Run)label.Inlines.LastInline!).SetBinding(Run.TextProperty,new Binding(nameof(CardModel.BarUnit)){Source=model,Mode=BindingMode.OneWay});
        cell.DataContext=model;button.Tag=model.Id;
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new Binding(nameof(CardModel.BarAccessibleText)){Source=model});
        // The hover list is a retained Popup, so the pointer can enter and scroll it.
        button.MouseEnter+=(_,_)=>ScheduleHover(model.Id,button);
        button.MouseLeave+=(_,_)=>{hoverDelay.Stop();ScheduleHoverDismiss();};
        providerCells.Add(model.Id,cell);return cell;
    }
    private void ScheduleHover(string id,FrameworkElement target)
    {
        hoverDismiss.Stop();pendingHoverId=id;pendingHoverTarget=target;
        if(ProviderHoverPopup.IsOpen)ShowProviderHover(id,target);
        else {hoverDelay.Stop();hoverDelay.Start();}
    }
    private void ScheduleHoverDismiss(){hoverDismiss.Stop();hoverDismiss.Start();}
    public void ShowProviderHover(string providerId,FrameworkElement target)
    {
        var ownerMenu=ProviderHoverOwningMenu(target);
        if(!hub.Models.Any(model=>model.Id==providerId) || !ProviderHoverTargetAvailable(target) || ownerMenu is {IsOpen:false})return;
        var providerChanged=hoverId!=providerId;
        hoverDelay.Stop();hoverDismiss.Stop();hoverId=providerId;
        HoverAccounts.SetProvider(hub,providerId);
        if(providerChanged)hoverScroll.ScrollToTop();
        hoverOwnerMenu=ownerMenu;
        ProviderHoverPopup.PlacementTarget=target;
        FitProviderHover(target);
        ProviderHoverPopup.IsOpen=true;
        RefitProviderHover();
    }
    private static bool ProviderHoverTargetAvailable(FrameworkElement target)=>target.IsVisible && PresentationSource.FromVisual(target) is not null;
    private static ContextMenu? ProviderHoverOwningMenu(FrameworkElement target)
    {
        DependencyObject? container=target;
        while(container is MenuItem item){
            container=ItemsControl.ItemsControlFromItemContainer(item)??LogicalTreeHelper.GetParent(item);
            if(container is ContextMenu menu)return menu;
        }
        return null;
    }
    private static MonitorInfo ProviderHoverMonitor(FrameworkElement target)
    {
        // Use a point inside the anchor. Its bottom edge can fall in a taskbar or
        // exactly outside the monitor when the bar sits at the work-area edge.
        var point=target.PointToScreen(new Point(target.ActualWidth/2,target.ActualHeight/2));
        var monitors=NativeWindows.Monitors();
        return monitors.FirstOrDefault(m=>point.X>=m.Bounds.Left&&point.X<m.Bounds.Right&&point.Y>=m.Bounds.Top&&point.Y<m.Bounds.Bottom)
            ??monitors.OrderBy(m=>Math.Pow(Math.Clamp(point.X,m.Bounds.Left,m.Bounds.Right)-point.X,2)
                +Math.Pow(Math.Clamp(point.Y,m.Bounds.Top,m.Bounds.Bottom)-point.Y,2)).First();
    }
    private void FitProviderHover(FrameworkElement target)
    {
        var monitor=ProviderHoverMonitor(target);
        // Overflow-menu anchors can live in a different HWND/DPI context from
        // the widget, so use the actual anchor's visual scale for both axes.
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(target);
        hoverSurface.Width=Math.Max(1,Math.Min(500,monitor.Work.Width/dpi.DpiScaleX-16));
        var chrome=hoverSurface.Padding.Top+hoverSurface.Padding.Bottom+hoverSurface.BorderThickness.Top+hoverSurface.BorderThickness.Bottom;
        hoverScroll.MaxHeight=Math.Max(1,Math.Min(680,monitor.Work.Height/dpi.DpiScaleY-16)-chrome);
    }
    public static CustomPopupPlacement ConstrainProviderHoverPlacement(Size popupSize,Size targetSize,Point targetScreenOrigin,DpiScale dpi,PixelRect work)
    {
        var anchorX=targetScreenOrigin.X+targetSize.Width*dpi.DpiScaleX/2;
        var anchorY=targetScreenOrigin.Y+targetSize.Height*dpi.DpiScaleY;
        var box=Placement.Anchor(anchorX,anchorY,popupSize.Width*dpi.DpiScaleX,popupSize.Height*dpi.DpiScaleY,work);
        return new CustomPopupPlacement(new Point((box.Left-targetScreenOrigin.X)/dpi.DpiScaleX,(box.Top-targetScreenOrigin.Y)/dpi.DpiScaleY),PopupPrimaryAxis.None);
    }
    private CustomPopupPlacement[] PlaceProviderHover(Size popupSize,Size targetSize,Point offset)
    {
        if(ProviderHoverPopup.PlacementTarget is not FrameworkElement target || !ProviderHoverTargetAvailable(target) || hoverOwnerMenu is {IsOpen:false})return [];
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(target);
        return [ConstrainProviderHoverPlacement(popupSize,targetSize,target.PointToScreen(new Point()),dpi,ProviderHoverMonitor(target).Work)];
    }
    private void RefitProviderHover()
    {
        if(!ProviderHoverPopup.IsOpen || ProviderHoverPopup.PlacementTarget is not FrameworkElement target)return;
        if(!ProviderHoverTargetAvailable(target) || hoverOwnerMenu is {IsOpen:false}){HideProviderHover();return;}
        FitProviderHover(target);
        // WPF does not reposition an open Popup when only its owner window
        // moves. A placement-property invalidation reruns our work-area clamp
        // without closing the account list or losing its scroll position. The
        // custom callback deliberately ignores this offset, so no intermediate
        // position is painted.
        var offset=ProviderHoverPopup.HorizontalOffset;
        ProviderHoverPopup.HorizontalOffset=offset+1;
        ProviderHoverPopup.HorizontalOffset=offset;
    }
    public void HideProviderHover(){hoverDelay.Stop();hoverDismiss.Stop();pendingHoverId=hoverId=null;pendingHoverTarget=null;ProviderHoverPopup.IsOpen=false;hoverOwnerMenu=null;}
    private void Hover_Click(object sender,MouseButtonEventArgs e)
    {
        for(DependencyObject? element=e.OriginalSource as DependencyObject;element is not null && element!=hoverSurface;element=element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D?System.Windows.Media.VisualTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element))
            if(element is ScrollBar or Thumb or RepeatButton)return;
        if(hoverId is {} id){HideProviderHover();e.Handled=true;ProviderRequested?.Invoke(id);}
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
    private void Provider_Click(object sender,RoutedEventArgs e){HideProviderHover();if(sender is Button {Tag:string id})ProviderRequested?.Invoke(id);}
    private void Network_Click(object sender,RoutedEventArgs e)=>NetworkRequested?.Invoke();
    private void Settings_Click(object sender,RoutedEventArgs e)=>SettingsRequested?.Invoke();
    private void More_Click(object sender,RoutedEventArgs e)=>CreateMoreMenu().IsOpen=true;
    public ContextMenu CreateMoreMenu()
    {
        var menu=new ContextMenu {PlacementTarget=MoreButton,Placement=PlacementMode.Bottom};
        foreach(var model in selected.Except(visibleCards)){
            var id=model.Id;var item=new MenuItem {Header=model.Name+" · "+model.BarValue};
            item.MouseEnter+=(_,_)=>ScheduleHover(id,item);item.MouseLeave+=(_,_)=>{hoverDelay.Stop();ScheduleHoverDismiss();};
            item.Click+=(_,_)=>{HideProviderHover();ProviderRequested?.Invoke(id);};menu.Items.Add(item);
        }
        menu.AddHandler(ContextMenu.ClosedEvent,new RoutedEventHandler((_,_)=>{
            // A menu owns a temporary HWND. Do not keep a hover anchored to its
            // disconnected item, or let a delayed hover outlive that menu. A
            // separate hover already moved back onto the bar stays untouched.
            if(ReferenceEquals(hoverOwnerMenu,menu) || ProviderHoverPopup.PlacementTarget is MenuItem owner && menu.Items.Contains(owner))HideProviderHover();
            else if(pendingHoverTarget is MenuItem pending && menu.Items.Contains(pending)){
                hoverDelay.Stop();pendingHoverId=null;pendingHoverTarget=null;
            }
        }),true);
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
        HideProviderHover();
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
