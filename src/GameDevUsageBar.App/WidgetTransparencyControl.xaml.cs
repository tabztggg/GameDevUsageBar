using System.Windows;
using System.Windows.Controls;
using GameDevUsageBar.App.Presentation;

namespace GameDevUsageBar.App;

public partial class WidgetTransparencyControl : UserControl
{
    private readonly PresentationPreferencesService preferences;
    private bool refreshing=true;
    public WidgetTransparencyControl(PresentationPreferencesService preferences)
    {
        this.preferences=preferences;InitializeComponent();Refresh();
        Loaded+=(_,_)=>{preferences.Changed+=Refresh;L.Changed+=Refresh;Refresh();};
        Unloaded+=(_,_)=>{preferences.Changed-=Refresh;L.Changed-=Refresh;};
    }
    private void Refresh()
    {
        if(!Dispatcher.CheckAccess()){if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(Refresh);return;}
        refreshing=true;
        try {
            var p=preferences.Current.WidgetOrDefault;
            EnabledToggle.Content=L.T("Semi-transparent");EnabledToggle.IsChecked=p.SemiTransparent;
            AmountLabel.Text=L.F("Transparency: {0}%",p.TransparencyPercent.ToString("0",L.Culture));
            AmountSlider.Value=p.TransparencyPercent;AmountSlider.IsEnabled=p.SemiTransparent;
            HintLabel.Text=L.T("Only the bar background fades. Text and icons stay clear.");
            System.Windows.Automation.AutomationProperties.SetName(AmountSlider,L.T("Background transparency"));
        } finally {refreshing=false;}
    }
    private void Toggle_Click(object sender,RoutedEventArgs e)
    {
        if(!refreshing)preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {SemiTransparent=EnabledToggle.IsChecked==true}});
    }
    private void Amount_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(!refreshing)preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {TransparencyPercent=Math.Round(e.NewValue)}});
    }
}
