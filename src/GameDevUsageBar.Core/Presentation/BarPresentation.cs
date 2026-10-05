using System.Globalization;

namespace GameDevUsageBar.Core.Presentation;

public static class BarPresentation
{
    public static string Value(ProviderDefinition definition, ProviderState state, DateTimeOffset now)
    {
        var status=CardPresentation.Classify(definition,state,now);
        var value=status.Kind switch {
            CardStateKind.Pending=>"Pending",CardStateKind.NotConfigured=>"Set up",
            CardStateKind.Disabled=>"Off",CardStateKind.Waiting=>"—",_=>Amount(definition.Id,state.LastSuccess?.Metrics ?? [])
        };
        if(state.Config.Enabled && definition.HoldReason is null && state.Failure is { } failure)
            value=(failure switch {FailureKind.Unauthorized=>"401",FailureKind.Forbidden=>"403",FailureKind.RateLimited=>"429",FailureKind.CredentialMissing=>"Set up",_=>"!"})
                +(state.LastSuccess is not null ? " "+Amount(definition.Id,state.LastSuccess.Metrics) : "");
        else if(state.FromCache)value="~"+value;
        else if(state.IsRefreshing && state.LastSuccess is null)value="…";
        return definition.IsDemo ? "DEMO "+value : value;
    }
    private static string Amount(string providerId,IEnumerable<Metric> metrics)
    {
        var all=metrics.ToArray();
        if(providerId=="claude" && all.Any(m=>m.Id is "five_hour" or "seven_day"))
        {
            string Window(string id)=>all.FirstOrDefault(m=>m.Id==id)?.Percent is {} remaining
                ? CardPresentation.Value((decimal)remaining,"%").Replace(" %","%") : "—";
            return Window("five_hour")+" / "+Window("seven_day");
        }
        var metric=all.FirstOrDefault(m=>m.Percent is not null) ?? all.FirstOrDefault(m=>m.Kind is MetricKind.Balance or MetricKind.Remaining)
            ?? all.FirstOrDefault(m=>m.Kind!=MetricKind.Reserved);
        if(metric?.Kind==MetricKind.Unlimited)return "No key limit";
        if(metric is null || metric.Value is null)return "—";
        if(metric.Percent is { } percent)return CardPresentation.Value((decimal)percent,"%").Replace(" %","%");
        var number=metric.Value.Value;
        if(metric.Unit is "USD" or "CNY"){
            var symbol=metric.Unit=="USD"?"$":"¥";
            var format=number!=0&&Math.Abs(number)<.01m?"#,##0.############################":"#,##0.00";
            return (number<0?"-":"")+symbol+Math.Abs(number).ToString(format,CultureInfo.InvariantCulture);
        }
        if(metric.Unit=="credits"){
            var format=number!=0&&Math.Abs(number)<.01m?"#,##0.############################":"#,##0.##";
            return number.ToString(format,CultureInfo.InvariantCulture)+" cr";
        }
        return CardPresentation.Value(metric.Value,metric.Unit).TrimEnd();
    }
}
