using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.App.Interop;

namespace GameDevUsageBar.App;

public partial class TrayPopupWindow : Window
{
    private readonly ProviderStateHub hub;
    private readonly ICollectionView view;
    private string? selectedId;
    private NativeWindows.Point? anchor;
    private double areaHeight=680;
    private bool fitting,refitPending;
    public bool AllowClose { get; set; }
    public event Action? OverviewRequested, SettingsRequested, WidgetRequested, ExitRequested, DismissRequested;
    public event Action<string>? AccountRequested;
    public event Action<string,Guid>? AccountSwitchRequested;
    public event Action<string>? ManageAccountsRequested;
    public TrayPopupWindow(ProviderStateHub hub)
    {
        this.hub=hub; InitializeComponent();
        AddHandler(AccountPickerView.SelectAccountEvent,new EventHandler<AccountSelectionEventArgs>((_,e)=>{e.Handled=true;AccountSwitchRequested?.Invoke(e.ProviderId,e.SlotId);}));
        AddHandler(AccountPickerView.ManageAccountsEvent,new RoutedEventHandler((_,e)=>{if(e.OriginalSource is FrameworkElement {DataContext:CardModel model}){e.Handled=true;ManageAccountsRequested?.Invoke(model.Id);}}));
        view=hub.CreateView(m=>selectedId is null ? m.EligibleForWidget : m.Id==selectedId); Cards.ItemsSource=view;
        hub.Changed+=Changed; Changed();
        ContentRendered+=(_,_)=>QueueRefit();
        ContentScroll.SizeChanged+=(_,_)=> {if(!fitting)QueueRefit();};
        KeyDown+=(_,e)=> {if(e.Key==Key.Escape) {e.Handled=true; DismissRequested?.Invoke();}};
        Deactivated+=(_,_)=>DismissRequested?.Invoke();
        Closing+=(_,e)=> {if(!AllowClose) {e.Cancel=true;Hide();}};
        Closed+=(_,_)=>hub.Changed-=Changed;
    }
    public void SelectProvider(string? id){selectedId=id;Changed();}
    private void Changed()
    {
        view.Refresh();
        EmptyMessage.Visibility=view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        var count=hub.Models.Count(m=>m.Definition.HoldReason!=null);
        PendingText.Text=count>0 ? L.F("{0} sources pending verification",count) : "";
        PendingText.Visibility=count>0 ? Visibility.Visible : Visibility.Collapsed;
        QueueRefit();
    }
    private void QueueRefit()
    {
        if(!IsVisible || refitPending || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)return;
        refitPending=true;
        Dispatcher.BeginInvoke(()=> {
            refitPending=false;
            if(!IsVisible || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)return;
            // A provider/anchor can change again before this callback runs.
            // Re-fit once using the latest anchor instead of a captured old point.
            if(anchor is { } point)PositionAt(point);else FitToWorkArea(Width,areaHeight);
        });
    }
    public void PositionAt(NativeWindows.Point point) {anchor=point;NativeWindows.PlacePopup(this,point);}
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi,newDpi);
        QueueRefit();
    }
    public void FitToWorkArea(double width,double maxHeight)
    {
        if(fitting)return;
        fitting=true;
        try {
        areaHeight=maxHeight;
        SizeToContent=SizeToContent.Manual;Width=width;
        var insideWidth=Math.Max(1,width-34);
        PopupHeader.Measure(new Size(insideWidth,double.PositiveInfinity));
        PopupFooter.Measure(new Size(insideWidth,double.PositiveInfinity));
        ContentScroll.MaxHeight=Math.Max(1,maxHeight-PopupHeader.DesiredSize.Height-PopupFooter.DesiredSize.Height-34);
        PopupRoot.Measure(new Size(width,double.PositiveInfinity));
        Height=Math.Min(maxHeight,Math.Ceiling(PopupRoot.DesiredSize.Height));
        } finally {fitting=false;}
    }
    private void RefreshAll_Click(object sender,RoutedEventArgs e)=>hub.RefreshAll();
    private void Refresh_Requested(object sender,RoutedEventArgs e) {if(sender is CompactCardView {DataContext:CardModel m}) _=hub.RequestManualRefresh(m.Id);}
    private void Setup_Requested(object sender,RoutedEventArgs e) {if(sender is CompactCardView {DataContext:CardModel m}) AccountRequested?.Invoke(m.Id);}
    private void Overview_Click(object sender,RoutedEventArgs e)=>OverviewRequested?.Invoke();
    private void Settings_Click(object sender,RoutedEventArgs e)=>SettingsRequested?.Invoke();
    private void Widget_Click(object sender,RoutedEventArgs e)=>WidgetRequested?.Invoke();
    private void Exit_Click(object sender,RoutedEventArgs e)=>ExitRequested?.Invoke();
}
