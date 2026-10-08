using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.App;

public sealed class MetricView(Metric metric, TimeProvider time,string providerId="",IReadOnlyList<Metric>? ticketExpiries=null) : INotifyPropertyChanged
{
    private string? previousReset;
    private string? previousCountdown;
    private bool CodexWindow=>providerId=="codex" && metric.DateMeaning=="reset" && metric.Kind==MetricKind.Quota;
    private bool ClaudeWindow=>providerId=="claude" && metric.DateMeaning=="reset" && (metric.Id=="five_hour" || metric.Id.StartsWith("seven_day",StringComparison.Ordinal));
    private bool ShortWindow=>metric.Id=="five_hour" || metric.WindowSeconds is >0 and <=18060
        || metric.Label.Contains("5-hour",StringComparison.OrdinalIgnoreCase) || metric.Label.Contains("5 hour",StringComparison.OrdinalIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id=>metric.Id;
    public MetricKind Kind=>metric.Kind;
    public string Unit=>metric.Unit;
    public string Label => L.T(metric.Label)+(metric.Scope.Length>0?" · "+L.T(metric.Scope):"");
    private static string DisplayUnit(string unit,decimal? value)=>unit=="tickets" && value==1 && L.Language=="en-US" ? "ticket" : L.T(unit);
    public string Display => metric.Kind==MetricKind.Unlimited ? L.T("No key limit") : L.T(CardPresentation.Value(metric.Value, DisplayUnit(metric.Unit,metric.Value))).Replace(" %", "%");
    public string OverviewDisplay
    {
        get
        {
            if(metric.Kind==MetricKind.Unlimited)return L.T("No key limit");
            if(metric.Value is not {} value)return L.T("Not reported");
            var tiny=value!=0 && Math.Abs(value)<0.01m;
            var format=tiny?"#,##0.############################":metric.Unit is "USD" or "CNY"?"#,##0.00":"#,##0.##";
            var amount=value.ToString(format,L.Culture);
            if(metric.Unit is "USD" or "CNY")return (value<0?"-":"")+(metric.Unit=="USD"?"$":"¥")+Math.Abs(value).ToString(format,L.Culture);
            return amount+(metric.Unit=="%"?"%":" "+DisplayUnit(metric.Unit,value));
        }
    }
    private static string Expiry(Metric entry)=>entry.ResetAt is {} date
        ? L.F("Expires {0}",date.ToLocalTime().ToString(L.Language=="zh-CN"?"yyyy年M月d日 HH:mm":"MMM d, yyyy HH:mm",L.Culture)) : L.T("Expiry not reported");
    private string TicketExpiryDisplay=>ticketExpiries!.Count==0 ? L.T("Expiry not reported")
        : string.Join(Environment.NewLine,ticketExpiries.Select(entry=>Expiry(entry)
            +(ticketExpiries.Count==1&&entry.Value==metric.Value ? "" : " · "+CardPresentation.Value(entry.Value,DisplayUnit("tickets",entry.Value)))));
    public string ResetDisplay => ticketExpiries is not null ? TicketExpiryDisplay : metric.DateMeaning=="expiry" && metric.ResetAt is null ? L.T("Expiry not reported") : metric.ResetAt is { } reset
        ? metric.DateMeaning=="expiry" ? L.F("Expires {0}",reset.ToLocalTime().ToString(L.Language=="zh-CN"?"yyyy年M月d日 HH:mm":"MMM d, yyyy HH:mm",L.Culture)) : reset <= time.GetUtcNow() && !ClaudeWindow && !CodexWindow ? L.T("Reset time passed; awaiting refresh") : L.F("Resets {0}",L.Date(reset)) : (ClaudeWindow || CodexWindow) ? L.T("Reset time not reported") : "";
    public string CountdownDisplay
    {
        get
        {
            if(!(ClaudeWindow || CodexWindow) || metric.ResetAt is not {} reset)return "";
            var remaining=reset-time.GetUtcNow();
            if(remaining<=TimeSpan.Zero)return L.T("Reset time passed; awaiting refresh");
            // Round up: an active final minute must not appear as already reset.
            var minutes=(long)Math.Ceiling(remaining.TotalMinutes);
            return ShortWindow ? L.F("Resets in {0}h {1}m",minutes/60,minutes%60)
                : L.F("Resets in {0}d {1}h {2}m",minutes/1440,minutes%1440/60,minutes%60);
        }
    }
    public bool HasReset => ticketExpiries is not null || metric.DateMeaning=="expiry" || metric.ResetAt is not null || ClaudeWindow || CodexWindow;
    public bool IsExhausted => metric.Value == 0 && metric.Kind is MetricKind.Balance or MetricKind.Remaining or MetricKind.Quota;
    public double? Percent => metric.Percent;
    public bool HasPercent=>Percent is not null;
    public double ProgressPercent=>Percent??0;
    public bool IsPrimaryCandidate=>metric.DateMeaning!="expiry" && metric.Kind is MetricKind.Quota or MetricKind.Remaining or MetricKind.Balance;
    public bool IsAuxiliaryBalance=>metric.Kind==MetricKind.Balance && (
        metric.Id.Split(['.','-','_'],StringSplitOptions.RemoveEmptyEntries).Any(part=>part.Equals("grant",StringComparison.OrdinalIgnoreCase)
            || part.Equals("granted",StringComparison.OrdinalIgnoreCase) || part.Equals("topup",StringComparison.OrdinalIgnoreCase) || part.Equals("purchased",StringComparison.OrdinalIgnoreCase))
        || metric.Label.StartsWith("Purchased",StringComparison.OrdinalIgnoreCase) || metric.Label.StartsWith("Granted",StringComparison.OrdinalIgnoreCase)
        || metric.Label.StartsWith("Topped up",StringComparison.OrdinalIgnoreCase));
    public bool IsFallbackCandidate=>metric.DateMeaning!="expiry" && metric.Unit!="tickets" && metric.Id!="reset-credits"
        && !metric.Id.StartsWith("reset-ticket",StringComparison.Ordinal) && metric.Kind is MetricKind.Count or MetricKind.Used or MetricKind.Limit or MetricKind.Unlimited;
    public void Tick()
    {
        var value=ResetDisplay;if(value!=previousReset){previousReset=value;PropertyChanged?.Invoke(this,new(nameof(ResetDisplay)));}
        var countdown=CountdownDisplay;if(countdown!=previousCountdown){previousCountdown=countdown;PropertyChanged?.Invoke(this,new(nameof(CountdownDisplay)));}
    }
}
// The picker carries display facts and the stable local slot key only. Native
// identity, credential references and binding hashes never enter menu options.
public sealed record AccountChoice(Guid SlotId, string Label, string Summary, string Status, bool IsCurrent);

public sealed class CardModel(ProviderDefinition definition, ProviderState state, TimeProvider? clock = null) : INotifyPropertyChanged
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private MetricView[]? metricViews;
    private IReadOnlyList<AccountChoice>? accountChoices;
    private IReadOnlyList<AccountChoice> cliAccountChoices=[];
    private string accountSwitchFeedback="";
    private string accountSwitchLabel="";
    private bool accountSwitchBusy;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ProviderState State { get; private set; } = state;
    public ProviderDefinition Definition => definition;
    private CardStatus Presentation => CardPresentation.Classify(definition, State, time.GetUtcNow());
    public string Id => definition.Id;
    public string Name => L.T(definition.Name);
    public Guid SlotId => State.Config.SlotId;
    public string AccountLabel => State.Config.Label;
    public string AccountSource => $"{L.T(definition.Channel)}"+(Id=="tripo" ? " · "+L.T(TripoRegions.Label(State.Config.TripoRegion)) : Id=="grsai" ? " · "+L.T(TripoRegions.Label(State.Config.QueryRegion)) : "")+(ProviderSources.SupportsLocal(Id)?" · "+L.T(ProviderSources.ModeLabel(Id,State.Config.SourceMode)):"")+(Id=="gemini"&&State.Config.ProjectId.Length>0?" · "+State.Config.ProjectId:"");
    // This is the bar's display selection, not a claim about a running CLI's identity.
    public bool IsDisplayedAccount => AccountChoices.Any(account=>account.SlotId==SlotId && account.IsCurrent);
    public bool CanSelectDisplayedAccount => !IsDisplayedAccount && definition.CanConfigure && !definition.IsDemo;
    public bool CanSwitchThisCliAccount => SupportsCliAccountSwitch && CanSwitchAccount && CliAccountChoices.Any(account=>account.SlotId==SlotId);
    public IReadOnlyList<AccountChoice> AccountChoices => accountChoices ?? Array.AsReadOnly(new[] { new AccountChoice(SlotId,State.Config.Label,PrimarySummary,CompactBadge,true) });
    public bool SupportsCliAccountSwitch=>Id is "codex" or "claude";
    public IReadOnlyList<AccountChoice> CliAccountChoices=>cliAccountChoices;
    public bool CanSwitchAccount=>!accountSwitchBusy && definition.CanConfigure && !definition.IsDemo && (!SupportsCliAccountSwitch || cliAccountChoices.Count>0);
    public string AccountSwitchHint=>L.T(SupportsCliAccountSwitch ? "Choose a saved CLI account. Only its auth file is replaced; existing sessions stay unchanged." : "Choose the displayed account");
    public string AccountSwitchFeedback=>accountSwitchFeedback.Length==0?"":(accountSwitchLabel.Length>0?accountSwitchLabel+" · ":"")+L.T(accountSwitchFeedback);
    public bool AccountSwitchBusy=>accountSwitchBusy;
    public void SetAccountSwitchBusy(bool busy)
    {
        accountSwitchBusy=busy;
        PropertyChanged?.Invoke(this,new(nameof(AccountSwitchBusy)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchAccount)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchThisCliAccount)));
    }
    public void SetAccountSwitchFeedback(string message,bool busy=false,string accountLabel="")
    {
        accountSwitchFeedback=message;accountSwitchBusy=busy;accountSwitchLabel=accountLabel;
        PropertyChanged?.Invoke(this,new(nameof(AccountSwitchFeedback)));
        PropertyChanged?.Invoke(this,new(nameof(AccountSwitchBusy)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchAccount)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchThisCliAccount)));
    }
    public void SetAccountChoices(IReadOnlyList<AccountChoice> value,IReadOnlyList<AccountChoice>? savedCli=null)
    {
        savedCli??=[];
        if(accountChoices is not null && accountChoices.SequenceEqual(value) && cliAccountChoices.SequenceEqual(savedCli))return;
        accountChoices=Array.AsReadOnly(value.ToArray());
        cliAccountChoices=Array.AsReadOnly(savedCli.ToArray());
        PropertyChanged?.Invoke(this,new(nameof(AccountChoices)));
        PropertyChanged?.Invoke(this,new(nameof(CliAccountChoices)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchAccount)));
        PropertyChanged?.Invoke(this,new(nameof(IsDisplayedAccount)));
        PropertyChanged?.Invoke(this,new(nameof(CanSelectDisplayedAccount)));
        PropertyChanged?.Invoke(this,new(nameof(CanSwitchThisCliAccount)));
    }
    public string Channel => $"{L.T(definition.Channel)}"+(Id=="tripo" ? " · "+L.T(TripoRegions.Label(State.Config.TripoRegion)) : Id=="grsai" ? " · "+L.T(TripoRegions.Label(State.Config.QueryRegion)) : "")+(ProviderSources.SupportsLocal(Id)?" · "+L.T(ProviderSources.ModeLabel(Id,State.Config.SourceMode)):"")+(Id=="gemini"&&State.Config.ProjectId.Length>0?" · "+State.Config.ProjectId:"")+$" · {State.Config.Label}";
    public string Accent => SystemParameters.HighContrast ? SystemColors.WindowTextColor.ToString() : definition.Accent;
    public string Summary => L.T(State.LastSuccess?.Metrics.Any(m=>m.Kind==MetricKind.Unlimited)==true ? "Key has no credit limit; account balance requires an account query token" : definition.Summary);
    public bool EligibleForWidget => definition.CanConfigure && State.Config.Enabled;
    public string DemoBanner => definition.IsDemo ? L.T("DEMO DATA · NOT REAL USAGE") : "";
    public IEnumerable<MetricView> Metrics => metricViews ??= CreateMetricViews();
    // Pick featured values by the reported metric semantics, not a provider or
    // subscription tier. Secondary values remain available in full detail.
    public IReadOnlyList<MetricView> PrimaryMetrics
    {
        get
        {
            var reported=Metrics.ToArray();
            var hasCurrentBalance=reported.Any(m=>m.IsPrimaryCandidate && m.Kind==MetricKind.Balance && !m.IsAuxiliaryBalance);
            var candidates=reported.Where(m=>m.IsPrimaryCandidate && (!hasCurrentBalance || !m.IsAuxiliaryBalance))
                .OrderBy(m=>m.Kind switch { MetricKind.Quota=>0,MetricKind.Remaining=>1,MetricKind.Balance=>2,_=>3 }).Take(2).ToArray();
            if(candidates.Length>0)return candidates;
            // A usage-only source still has a meaningful featured value. Ticket
            // inventory and expiry facts remain secondary, including unknowns.
            return reported.Where(m=>m.IsFallbackCandidate)
                .OrderBy(m=>m.Kind switch { MetricKind.Count=>0,MetricKind.Used=>1,MetricKind.Limit=>2,MetricKind.Unlimited=>3,_=>4 }).Take(2).ToArray();
        }
    }
    public int PrimaryMetricsCount=>PrimaryMetrics.Count;
    public IReadOnlyList<MetricView> SecondaryMetrics
    {
        get { var primary=PrimaryMetrics;return Metrics.Where(m=>!primary.Contains(m)).ToArray(); }
    }
    public bool HasSecondaryMetrics=>SecondaryMetrics.Count>0;
    public IReadOnlyList<MetricView> CompactFeaturedMetrics
    {
        get {var quotas=Metrics.Where(metric=>metric.Kind==MetricKind.Quota).ToArray();return quotas.Length>0?quotas:PrimaryMetrics;}
    }
    public IReadOnlyList<MetricView> CompactOtherMetrics=>Metrics.Where(metric=>!CompactFeaturedMetrics.Contains(metric)).ToArray();
    public string PrimarySummary=>string.Join(" · ",PrimaryMetrics.Select(m=>m.Label+" "+m.OverviewDisplay));
    public string PlanLabel=>State.LastSuccess?.Plan is {Length:>0} plan ? plan.ToLowerInvariant() switch {"pro"=>"Pro","plus"=>"Plus",_=>plan} : "";
    public string DetailsLabel=>L.T("Details");
    private MetricView[] CreateMetricViews()
    {
        if(definition.HoldReason is not null || !State.Config.Enabled || State.LastSuccess is not {} snapshot)return [];
        var ticketTotal=snapshot.Metrics.FirstOrDefault(m=>m.Id=="reset-credits" && m.Kind==MetricKind.Count && m.Unit=="tickets");
        if(ticketTotal is null)return snapshot.Metrics.Select(m=>new MetricView(m,time,Id)).ToArray();
        // Keep transport/API inventory intact. Present the total and its expiry
        // details in one shared row on overview, hover and click surfaces.
        var expiries=snapshot.Metrics.Where(m=>m.Id.StartsWith("reset-ticket-expiry-",StringComparison.Ordinal) && m.Kind==MetricKind.Count && m.Unit=="tickets").ToArray();
        return snapshot.Metrics.Where(m=>!expiries.Contains(m))
            .Select(m=>new MetricView(m,time,Id,m==ticketTotal?expiries:null)).ToArray();
    }
    public string SetupLabel => L.T(definition.CanConfigure ? (State.Config.HasUsableCredential || definition.IsDemo ? "Settings" : "Set up") : "Why pending?");
    public bool CanRefresh => EligibleForWidget && !State.IsRefreshing && !(State.Failure == FailureKind.RateLimited && State.NextAttempt > time.GetUtcNow());
    public Brush StatusBrush => SystemParameters.HighContrast ? SystemColors.WindowTextBrush
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString(State.Failure is not null ? "#F1BB66" : State.LastSuccess is not null ? "#6BD4A9" : "#9BA9BF"));
    public string Badge => L.T(Presentation.Badge);
    public string Status => L.T(Presentation.Text);
    public string TimeText => string.Join(" · ",new[]{
        State.LastSuccess is {} s ? L.F("Retrieved {0}",L.Date(s.RetrievedAt,true)) : L.T("No successful query"),
        State.LastAttempt is {} a ? L.F("Attempt {0}",a.ToLocalTime().ToString("HH:mm:ss",L.Culture)) : "",
        State.Failure==FailureKind.RateLimited && State.NextAttempt is {} n ? L.F("Next attempt after {0}",L.Date(n,true)) : ""
    }.Where(s=>s.Length>0));
    private bool CompactAged => Presentation.Kind == CardStateKind.Live && State.LastSuccess is { } success
        && (success.RetrievedAt > time.GetUtcNow().AddMinutes(1) || time.GetUtcNow() - success.RetrievedAt > TimeSpan.FromSeconds(State.Config.IntervalMinutes * 60 + 60)
            || success.Metrics.Any(m => m.ResetAt <= time.GetUtcNow()));
    public string CompactBadge => CompactAged ? L.T("STALE") : Presentation.Kind == CardStateKind.Live ? L.T("Updated") : Badge;
    public bool HasCurrentUsage=>Presentation.Kind==CardStateKind.Live&&!CompactAged;
    public Brush CompactStatusBrush => SystemParameters.HighContrast ? SystemColors.WindowTextBrush
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString(Presentation.Kind == CardStateKind.Live && !CompactAged ? "#6BD4A9"
            : Presentation.Kind is CardStateKind.Pending or CardStateKind.NotConfigured or CardStateKind.Disabled or CardStateKind.Waiting or CardStateKind.Refreshing ? "#9BA9BF" : "#F1BB66"));
    public string CompactChannel => Channel.Replace(L.T("Use local CLI login (read only)"), L.T("Local CLI")).Replace(L.T("Current CLI login (automatic renewal)"),L.T("Local CLI"));
    public string CompactNotice => CompactAged ? L.T("Previous values; refresh to verify current usage")
        : Presentation.Kind == CardStateKind.Live ? "" : Status;
    public string CompactExplanation => State.LastSuccess?.Metrics.Any(m => m.Kind == MetricKind.Unlimited) == true ? Summary : "";
    public string CompactTime => State.LastSuccess is { } success ? L.F("Updated {0}", L.Date(success.RetrievedAt, true))
        : State.LastAttempt is { } attempt ? L.F("Attempt {0}", L.Date(attempt, true)) : L.T("No successful query");
    public string AccessibleText => string.Join(". ", new[] { Name, Channel, DemoBanner, Badge, Status,
        string.Join(". ", Metrics.Select(m => string.Join(" ",new[]{m.Label,m.Display,m.ResetDisplay,m.CountdownDisplay}.Where(s=>s.Length>0)))), TimeText }.Where(s => s.Length > 0));
    public string ShortValue => Metrics.FirstOrDefault() is { } metric ? $"{Name} [{Badge}]: {metric.Label} {metric.Display}" : $"{Name}: {Badge}";
    public string BarValue=>L.Compact(BarPresentation.Value(definition,State,time.GetUtcNow()));
    public string BarUnit=>new[]{" cr"," 积分"," characters"," tokens"," GB"," EUR"," JPY"}.FirstOrDefault(unit=>BarValue.EndsWith(unit,StringComparison.Ordinal))??"";
    public string BarNumber=>BarUnit.Length>0?BarValue[..^BarUnit.Length]:BarValue;
    public ImageSource IconSource=>ProviderArtwork.For(Id,Accent);
    public string BarAccessibleText=>AccessibleText+L.T(". Compact display: ")+BarValue+(Metrics.Any(m=>m.Percent is not null) ? L.T(". Percentage is remaining quota.") : "");
    public void RefreshLanguage(){metricViews=null;RefreshTheme();}
    public void Update(ProviderState value) { State = value; metricViews=null; RefreshTheme(); }
    public void RefreshTheme()=>PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(null));
    public void Tick()
    {
        foreach(var metric in Metrics)metric.Tick();
        PropertyChanged?.Invoke(this,new(nameof(CanRefresh)));
        PropertyChanged?.Invoke(this,new(nameof(CompactBadge)));
        PropertyChanged?.Invoke(this,new(nameof(CompactStatusBrush)));
        PropertyChanged?.Invoke(this,new(nameof(CompactNotice)));
    }
}
