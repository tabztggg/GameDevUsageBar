using System.Windows;
using System.Windows.Controls;
using GameDevUsageBar.App.Presentation;

namespace GameDevUsageBar.App;

public partial class DisplaySettingsWindow : Window
{
    private readonly PresentationPreferencesService preferences;
    private readonly ProviderStateHub hub;
    private readonly Dictionary<string,CheckBox> choices = new();
    private bool choosingLanguage;
    private bool choosingNetwork;
    private bool refreshing=true;
    private sealed record NetworkChoice(string Id,string Label);
    private sealed record FeaturedChoice(string Id,string Label);
    public DisplaySettingsWindow(PresentationPreferencesService preferences,ProviderStateHub hub)
    {
        this.preferences=preferences; this.hub=hub; InitializeComponent();
        TransparencySlot.Children.Add(new WidgetTransparencyControl(preferences));
        LanguageSelector.ItemsSource=LanguageChoice.All;
        Height=Math.Min(Height,SystemParameters.WorkArea.Height-30); MinHeight=Math.Min(MinHeight,Height);
        foreach(var model in hub.Models) {
            var check=new CheckBox {Content=model.Name+" · "+L.T(model.Definition.Channel),Tag=model.Id};
            check.Click+=Selection_Click; choices.Add(model.Id,check); CardChoices.Children.Add(check);
        }
        preferences.Changed+=Changed; Changed();
        L.Changed+=Changed;
        Closed+=(_,_)=>{preferences.Changed-=Changed;L.Changed-=Changed;};
    }
    private void Changed()
    {
        if(!Dispatcher.CheckAccess()) {if(!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Changed);return;}
        refreshing=true;
        try {
        var p=preferences.Current; var w=p.WidgetOrDefault;
        FeaturedTitle.Text=L.T("Featured modules");
        FeaturedHint.Text=L.T("Choose up to two services for the large overview modules. Any service can be featured; choose (None) to leave fewer modules.");
        FeaturedFirstLabel.Text=L.T("Module 1");FeaturedSecondLabel.Text=L.T("Module 2");
        FeaturedResetButton.Content=L.T("Restore featured defaults");
        var featured=p.FeaturedOrDefault;
        var first=featured.ElementAtOrDefault(0)??"";var second=featured.ElementAtOrDefault(1)??"";
        FeaturedFirstSelector.ItemsSource=FeaturedChoices(first,second);FeaturedFirstSelector.SelectedValue=first;
        FeaturedSecondSelector.ItemsSource=FeaturedChoices(second,first);FeaturedSecondSelector.SelectedValue=second;
        System.Windows.Automation.AutomationProperties.SetName(FeaturedFirstSelector,FeaturedFirstLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(FeaturedSecondSelector,FeaturedSecondLabel.Text);
        OrderTitle.Text=L.T("Service order");
        OrderHint.Text=L.T("Move services up or down to set their order in the overview and floating bar. Featured services keep their selected module positions.");
        RefreshOrderChoices();
        choosingLanguage=true;LanguageSelector.SelectedValue=L.Language;choosingLanguage=false;
        ShowNetwork.Content=L.T("Show network speed in the floating bar");ShowNetwork.IsChecked=w.ShowNetwork;
        NetworkTitle.Text=L.T("Network adapter");NetworkHint.Text=L.T("Select the adapter shown in Task Manager. Auto prefers a connected Ethernet adapter, then Wi-Fi; common virtual adapters are excluded.");
        choosingNetwork=true;
        var networkChoices=hub.Network.Choices().Select(n=>new NetworkChoice(n.Id,n.Name+" · "+n.Kind+" · "+L.T(n.Connected?"Connected":"Disconnected"))).Prepend(new("",L.T("Auto (Ethernet first)"))).ToList();
        if(w.NetworkAdapterId is {Length:>0} saved&&!networkChoices.Any(c=>c.Id==saved))networkChoices.Add(new(saved,L.T("Saved adapter (unavailable)")));
        NetworkSelector.ItemsSource=networkChoices;NetworkSelector.SelectedValue=w.NetworkAdapterId??"";choosingNetwork=false;
        System.Windows.Automation.AutomationProperties.SetName(NetworkSelector,NetworkTitle.Text);
        StartInTray.IsChecked=p.StartInTray; WidgetVisible.IsChecked=w.Visible; WidgetTopmost.IsChecked=w.Topmost;
        WidgetLocked.IsChecked=w.Locked; AutomaticCards.IsChecked=w.CardIds is null;
        foreach(var (id,check) in choices) {var model=hub.Models.Single(m=>m.Id==id);check.Content=model.Name+" · "+L.T(model.Definition.Channel);check.IsChecked=w.CardIds?.Contains(id)==true;check.IsEnabled=w.CardIds is not null;}
        Notice.Text=L.T(preferences.Notice);
        } finally {refreshing=false;}
    }
    private List<FeaturedChoice> FeaturedChoices(string selected,string other)
    {
        var result=hub.Models.Where(m=>m.Id!="demo"&&m.Id!=other).Select(m=>new FeaturedChoice(m.Id,m.Name)).ToList();
        if(selected.Length>0&&selected!="demo"&&!result.Any(c=>c.Id==selected))
            result.Add(new(selected,L.F("Saved service ({0})",selected)));
        result.Insert(0,new("",L.T("(None)")));
        return result;
    }
    private void Featured_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(refreshing)return;
        var first=FeaturedFirstSelector.SelectedValue as string??"";
        var second=FeaturedSecondSelector.SelectedValue as string??"";
        preferences.Update(p=>p with {FeaturedIds=new[]{first,second}.Where(id=>id.Length>0).Distinct(StringComparer.Ordinal).Take(2).ToArray()});
    }
    private void FeaturedReset_Click(object sender,RoutedEventArgs e)
    {
        if(!refreshing)preferences.Update(p=>p with {FeaturedIds=null});
    }
    private void RefreshOrderChoices()
    {
        OrderChoices.Children.Clear();
        var models=hub.Models.Where(m=>m.Id!="demo").ToList();
        for(var index=0;index<models.Count;index++)
        {
            var model=models[index];var row=new Grid {Margin=new Thickness(0,2,0,2)};
            row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var label=new TextBlock {Text=model.Name,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
            row.Children.Add(label);
            var up=new Button {Content="↑",Tag=model.Id,Width=32,Padding=new Thickness(0,5,0,5),Margin=new Thickness(2),IsEnabled=index>0,ToolTip=L.T("Move up")};
            var down=new Button {Content="↓",Tag=model.Id,Width=32,Padding=new Thickness(0,5,0,5),Margin=new Thickness(2),IsEnabled=index<models.Count-1,ToolTip=L.T("Move down")};
            System.Windows.Automation.AutomationProperties.SetName(up,model.Name+" · "+L.T("Move up"));
            System.Windows.Automation.AutomationProperties.SetName(down,model.Name+" · "+L.T("Move down"));
            up.Click+=OrderUp_Click;down.Click+=OrderDown_Click;
            Grid.SetColumn(up,1);Grid.SetColumn(down,2);row.Children.Add(up);row.Children.Add(down);OrderChoices.Children.Add(row);
        }
    }
    private void OrderUp_Click(object sender,RoutedEventArgs e)=>MoveService(sender,-1);
    private void OrderDown_Click(object sender,RoutedEventArgs e)=>MoveService(sender,1);
    private void MoveService(object sender,int delta)
    {
        if(refreshing||sender is not Button {Tag:string id})return;
        var visible=hub.Models.Where(m=>m.Id!="demo").Select(m=>m.Id).ToList();
        var index=visible.IndexOf(id);var other=index+delta;
        if(index<0||other<0||other>=visible.Count)return;
        // Keep unknown future IDs and the demo entry in place; only swap the two known services.
        var order=(preferences.Current.CardOrder??[]).Concat(hub.Models.Select(m=>m.Id)).Distinct(StringComparer.Ordinal).ToList();
        var firstIndex=order.IndexOf(id);var secondIndex=order.IndexOf(visible[other]);
        (order[firstIndex],order[secondIndex])=(order[secondIndex],order[firstIndex]);
        preferences.Update(p=>p with {CardOrder=order.ToArray()});
    }
    private void Toggle_Click(object sender,RoutedEventArgs e)
    {
        if(refreshing)return;
        preferences.Update(p=>p with {
        StartInTray=StartInTray.IsChecked==true,
        Widget=p.WidgetOrDefault with {Visible=WidgetVisible.IsChecked==true,Topmost=WidgetTopmost.IsChecked==true,Locked=WidgetLocked.IsChecked==true,ShowNetwork=ShowNetwork.IsChecked==true}
        });
    }
    private void Network_Changed(object sender,SelectionChangedEventArgs e){if(!refreshing&&!choosingNetwork&&NetworkSelector.SelectedValue is string id)preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {NetworkAdapterId=id.Length==0?null:id}});}
    private void Language_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!refreshing&&!choosingLanguage && LanguageSelector.SelectedValue is string language)preferences.Update(p=>p with {Language=language});
    }
    private void Selection_Click(object sender,RoutedEventArgs e)
    {
        if(refreshing)return;
        // Preserve unknown future IDs; only known checkbox choices are replaced.
        var unknown=(preferences.Current.WidgetOrDefault.CardIds ?? []).Where(id=>!choices.ContainsKey(id));
        var selected=AutomaticCards.IsChecked==true ? null : choices.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).Concat(unknown).ToArray();
        preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {CardIds=selected}});
    }
    private void Position_Click(object sender,RoutedEventArgs e)=>preferences.Update(p=>p with {Widget=p.WidgetOrDefault with {Placement=null}});
    private async void Reset_Click(object sender,RoutedEventArgs e)
    {
        if(System.Windows.MessageBox.Show(this,L.T("Reset layout preferences? The existing layout file will be kept as a backup. Account settings and keys stay unchanged."),L.T("Reset layout"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        try {await preferences.ResetAsync();} catch {Notice.Text=L.T("Could not reset local layout preferences.");}
    }
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
