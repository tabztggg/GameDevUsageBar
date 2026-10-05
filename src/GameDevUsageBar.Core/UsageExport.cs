using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.Core;

// Explicit DTOs: never serialize AccountConfig, bindings, credentials, or provider responses.
public sealed record UsageExport(int SchemaVersion,string App,string AppVersion,DateTimeOffset GeneratedAt,bool SharedAccountState,ProviderUsage[] Providers);
public sealed record ProviderUsage(string Id,string Name,string Channel,string AuthSource,string? Region,bool Enabled,string Status,string Freshness,bool CanUseForBudget,string? Origin,DateTimeOffset? RetrievedAt,long? AgeSeconds,int RefreshIntervalSeconds,DateTimeOffset? LastAttemptAt,DateTimeOffset? NextAttemptAt,string? Error,UsageMetric[] Metrics,AuthMaintenance? AuthMaintenance=null);
public sealed record UsageMetric(string Id,string Label,string LabelZhCn,string Kind,decimal? Value,string Unit,decimal? Total,double? RemainingPercent,int? WindowSeconds,DateTimeOffset? ResetsAt,long? ResetInSeconds,string DateMeaning,string Scope,bool Usable);
public sealed record AccountUsageExport(int SchemaVersion,string App,string AppVersion,DateTimeOffset GeneratedAt,AccountUsage[] Accounts);
public sealed record AccountUsage(string ProviderId,Guid SlotId,string Label,bool Selected,ProviderUsage Usage);
public static class UsageExporter
{
    public static string AppVersion=>typeof(Metric).Assembly.GetName().Version!.ToString(3);
    public static UsageExport Export(IEnumerable<IProviderAdapter> adapters,RefreshCoordinator coordinator,DateTimeOffset now)=>new(1,"GameDevUsageBar",AppVersion,now,true,adapters.Where(a=>!ProviderSources.IsRetired(a.Definition.Id)).Select(a=>Provider(a.Definition,coordinator.Get(a.Definition.Id),now)).ToArray());
    public static AccountUsageExport Accounts(IEnumerable<IProviderAdapter> adapters,RefreshCoordinator coordinator,DateTimeOffset now)=>new(1,"GameDevUsageBar",AppVersion,now,adapters.Where(a=>!ProviderSources.IsRetired(a.Definition.Id)).SelectMany(a=>coordinator.GetAccounts(a.Definition.Id).Select(state=>new AccountUsage(a.Definition.Id,state.Config.SlotId,state.Config.Label,state.Config.IsActive,Provider(a.Definition,state,now)))).ToArray());
    public static ProviderUsage Provider(ProviderDefinition definition,ProviderState state,DateTimeOffset now)
    {
        var config=state.Config;var snapshot=config.Enabled&&definition.HoldReason is null&&state.LastSuccess?.Binding==config.Binding(definition)?state.LastSuccess:null;
        long? age=snapshot is null?null:(long)Math.Max(0,(now-snapshot.RetrievedAt).TotalSeconds);
        var card=CardPresentation.Classify(definition,state,now);
        var failure=state.Failure is not null&&card.Kind!=CardStateKind.LocalSaveFailed;
        var freshness=!config.Enabled?"disabled":snapshot is null?"no_data":snapshot.Origin==DataOrigin.Demo?"demo":state.FromCache?"cached":failure?"stale":snapshot.RetrievedAt>now.AddMinutes(1)?"invalid_time":age>config.IntervalMinutes*60+60?"expired":"fresh";
        var sourceUsable=freshness=="fresh";
        var metrics=snapshot?.Metrics.Select(m=>new UsageMetric(m.Id,m.Label,Localizer.Catalogue.GetValueOrDefault(m.Label,m.Label),m.Kind.ToString(),m.Value,m.Unit,m.Total,m.Percent,
            m.WindowSeconds??(definition.Id=="claude"?(m.Id=="five_hour"?18000:m.Id.StartsWith("seven_day",StringComparison.Ordinal)?604800:null):null),
            m.ResetAt,m.ResetAt is {} reset?(long)Math.Max(0,(reset-now).TotalSeconds):null,m.DateMeaning,m.Scope,sourceUsable&&m.Value is not null&&(m.ResetAt is null||m.ResetAt>now))).ToArray()??[];
        return new(definition.Id,definition.Name,definition.Channel,config.SourceMode,definition.Id=="tripo"?config.TripoRegion:definition.Id=="grsai"?config.QueryRegion:null,config.Enabled,card.Kind.ToString(),freshness,metrics.Any(m=>m.Usable),snapshot?.Origin.ToString().ToLowerInvariant(),snapshot?.RetrievedAt,age,config.IntervalMinutes*60,state.LastAttempt,state.NextAttempt,state.Failure?.ToString(),metrics,state.AuthMaintenance);
    }
}
