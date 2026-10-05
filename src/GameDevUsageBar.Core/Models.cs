using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameDevUsageBar.Core;

public enum MetricKind { Balance, Reserved, Used, Remaining, Limit, Quota, Count, Unlimited }
public enum DataOrigin { Live, Demo }
public enum FailureKind { Unauthorized, Forbidden, RateLimited, Timeout, Network, HttpError, BusinessError, SchemaMismatch, TooLarge, Redirect, Policy, CredentialMissing, CredentialUnreadable, Unsupported, Cancelled, LocalStorage, CredentialExpired, IdentityChanged, ProjectRequired, NoData, IncompleteData, BrowserVerificationRequired, BrowserLoginRequired, BrowserRuntimeMissing, AuthRenewalRequired, AuthRenewalUnknown, AuthRenewalBusy }
public sealed record Metric(string Id, string Label, decimal? Value, string Unit, MetricKind Kind = MetricKind.Balance, decimal? Total = null, DateTimeOffset? ResetAt = null,string Scope="",string DateMeaning="reset",int? WindowSeconds=null)
{
    public string Display => Kind==MetricKind.Unlimited ? "No key limit" : Value is { } number ? $"{number.ToString("0.##", CultureInfo.GetCultureInfo("en-US"))} {Unit}" : "Not reported";
    public string ResetDisplay => ResetAt is { } reset ? (reset <= DateTimeOffset.UtcNow ? "Reset time passed; awaiting refresh" : $"Resets {reset.ToLocalTime():MMM d, HH:mm}") : "";
    public double? Percent => Value is { } value && Total is > 0 && Kind == MetricKind.Quota ? (double)Math.Clamp(value / Total.Value * 100m, 0, 100) : null;
}
public sealed record EndpointRule(string Host, int Port, string Path, string Header = "Authorization", string Prefix = "Bearer ", int MaxBytes = 65536, int TimeoutSeconds = 12)
{
    public string Policy => JsonSerializer.Serialize(new { Scheme = "https", Host, Port, Method = "GET", Path, Header, Prefix, MaxBytes, TimeoutSeconds, Query = "none", Body = "none", Redirects = "disabled" });
    public Uri Uri => new UriBuilder("https", Host, Port, Path).Uri;
}
public sealed record ProviderDefinition(string Id, string Name, string Channel, string Summary, string Accent, EndpointRule? Endpoint, string? HoldReason = null)
{
    public bool IsDemo => Id == "demo";
    public bool CanConfigure => Endpoint != null || IsDemo;
}
public sealed record AccountConfig(string ProviderId, Guid SlotId, string Label, bool Enabled = false, Guid? CredentialRef = null, Guid? CredentialRevision = null, int IntervalMinutes = 10, int Order = 0, string DemoScenario = "Success",string TripoRegion=TripoRegions.Global,string? CredentialRegion=null,string SourceMode="manual",string QueryRegion="global",string ProjectId="",string OrganizationId="",string AccountId="",string? NativeIdentity=null,string? CredentialSource=null,VpsQuerySettings? Vps=null,string? VpsCredentialBinding=null,bool IsActive=true,Guid? NativeAuthRef=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasUsableCredential=>ProviderId=="vps" && Vps?.Auth=="none" ? true : SourceMode=="browser-session" ? ProviderId=="typesafe" && CredentialSource==SourceMode && CredentialRevision is not null : SourceMode=="local-oauth" ? NativeIdentity is not null && CredentialSource==SourceMode : SourceMode=="saved-oauth" ? NativeAuthRef is not null && NativeIdentity is not null && CredentialSource==SourceMode : CredentialRef is not null && (CredentialSource ?? "manual")==SourceMode && (ProviderId!="tripo" || (CredentialRegion ?? TripoRegions.Global)==TripoRegion) && (ProviderId!="vps" || VpsCredentialBinding==Vps?.CredentialBinding());
    public AccountConfig Validate()
    {
        if(SlotId==Guid.Empty || CredentialRef==Guid.Empty || CredentialRevision==Guid.Empty || NativeAuthRef==Guid.Empty || string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length>100 || ProviderId.Any(char.IsControl) || string.IsNullOrWhiteSpace(Label) || Label.Length>200 || Label.Any(char.IsControl)
            || IntervalMinutes is <5 or >120 || !TripoRegions.IsValid(TripoRegion) || CredentialRegion is not (null or TripoRegions.Global or TripoRegions.China)
            || (ProviderId!="tripo" && (TripoRegion!=TripoRegions.Global || CredentialRegion is not null)))throw new InvalidDataException("Invalid account configuration");
        ProviderSources.Validate(this);if(ProviderId=="vps") (Vps??new()).Validate(Enabled);else if(Vps is not null)throw new InvalidDataException("Unexpected VPS configuration");return this;
    }
    public string Binding(ProviderDefinition definition) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ProviderId, definition.Channel, SlotId, CredentialRef, CredentialRevision, IntervalMinutes, DemoScenario, Policy = TripoRegions.Endpoint(definition,this)?.Policy, Schema = 1 })+(ProviderSources.IsExtended(ProviderId)?ProviderSources.Policy(this):"")+(NativeAuthRef is {} native ? "|native:"+native.ToString("N") : "")))).ToLowerInvariant();
}
public sealed record UsageSnapshot(string Binding, DataOrigin Origin, DateTimeOffset RetrievedAt, ImmutableArray<Metric> Metrics,string? Plan=null);
public sealed record AdapterOutcome(ImmutableArray<Metric> Metrics, FailureKind? Failure = null, DateTimeOffset? RetryNotBefore = null,string? Plan=null)
{
    public static AdapterOutcome Fail(FailureKind kind, DateTimeOffset? retry = null) => new([], kind, retry);
}
public sealed record AuthMaintenance(string Owner,bool Enabled,string State,DateTimeOffset? NextAttemptAt);
public sealed record ProviderState(AccountConfig Config, UsageSnapshot? LastSuccess, DateTimeOffset? LastAttempt, FailureKind? Failure, bool IsRefreshing, DateTimeOffset? NextAttempt, bool FromCache = false,AuthMaintenance? AuthMaintenance=null);
public interface IAuthMaintenance
{
    bool BackgroundDue(AccountConfig config);
    Task<AccountConfig> MaintainAsync(AccountConfig config,CancellationToken ct);
    AuthMaintenance? Observe(AccountConfig config);
}
public interface IQueryClient { Task<byte[]> ReadAsync(ProviderDefinition definition, AccountConfig account, CancellationToken ct); }
public interface IProviderAdapter { ProviderDefinition Definition { get; } Task<AdapterOutcome> RefreshAsync(AccountConfig config, IQueryClient queries, CancellationToken ct); }
public interface ISnapshotStore { Task<UsageSnapshot?> LoadAsync(string binding); Task SaveAsync(UsageSnapshot snapshot); Task RemoveAsync(string binding); }
public sealed class QueryException(FailureKind kind, DateTimeOffset? retry = null) : Exception(kind.ToString())
{
    public FailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryNotBefore { get; } = retry;
}
