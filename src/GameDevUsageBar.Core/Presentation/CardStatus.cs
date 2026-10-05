using System.Globalization;

namespace GameDevUsageBar.Core.Presentation;

public enum CardStateKind { Pending, NotConfigured, Disabled, Waiting, Refreshing, Live, Demo, Cached, Stale, RateLimited, Unavailable, LocalSaveFailed }
public sealed record CardStatus(CardStateKind Kind, string Badge, string Text, string TimeText);
public static class CardPresentation
{
    public static string Value(decimal? value, string unit)
    {
        if(value is null) return "Not reported";
        // Preserve sign and native precision for tiny balances; never round them into zero.
        var format = value != 0 && Math.Abs(value.Value) < 0.01m ? "0.############################" : "0.##";
        return value.Value.ToString(format, CultureInfo.InvariantCulture) + " " + unit;
    }
    public static string Failure(FailureKind kind) => kind switch {
        FailureKind.Unauthorized => "Key rejected (401)", FailureKind.Forbidden => "Permission denied (403)",
        FailureKind.BrowserVerificationRequired => "Website security verification required; connect the built-in browser in Settings",
        FailureKind.BrowserLoginRequired => "Sign in using the built-in browser in Settings, then refresh",
        FailureKind.BrowserRuntimeMissing => "Microsoft Edge WebView2 Runtime is required for browser billing",
        FailureKind.RateLimited => "Rate limited; waiting for the provider", FailureKind.Timeout => "Query timed out",
        FailureKind.Network => "Offline or network unavailable", FailureKind.SchemaMismatch => "Unexpected response format",
        FailureKind.BusinessError => "Provider returned an error", FailureKind.Redirect => "Redirect blocked",
        FailureKind.TooLarge => "Response exceeds size limit", FailureKind.CredentialUnreadable => "Saved key cannot be read; re-enter it",
        FailureKind.CredentialMissing => "Configure credentials to start", FailureKind.LocalStorage => "Local files could not be saved",
        FailureKind.CredentialExpired=>"CLI login expired; renew it in the CLI, then refresh",
        FailureKind.AuthRenewalRequired=>"CLI renewal grant is unavailable; sign in again",
        FailureKind.AuthRenewalUnknown=>"CLI renewal is unconfirmed; an uncertain request will not be repeated",
        FailureKind.AuthRenewalBusy=>"CLI login is busy; automatic renewal is scheduled",
        FailureKind.IdentityChanged=>"Local CLI account changed; save Settings to reconnect",
        FailureKind.ProjectRequired=>"Complete the project or organization setting",
        FailureKind.NoData=>"Provider reported no usage data; this is not zero",
        FailureKind.IncompleteData=>"Response is incomplete; no partial total is shown",
        FailureKind.Policy=>"Source configuration rejected",FailureKind.Unsupported=>"This account or source is not supported",_ => kind.ToString()
    };
    public static CardStatus Classify(ProviderDefinition definition, ProviderState state, DateTimeOffset now)
    {
        var time = string.Join(" · ", new[] {
            state.LastSuccess is { } s ? "Retrieved " + s.RetrievedAt.ToLocalTime().ToString("MMM d, HH:mm:ss", CultureInfo.InvariantCulture) : "No successful query",
            state.LastAttempt is { } a ? "Attempt " + a.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "",
            state.Failure == FailureKind.RateLimited && state.NextAttempt is { } n ? "Next attempt after " + n.ToLocalTime().ToString("MMM d, HH:mm:ss", CultureInfo.InvariantCulture) : ""
        }.Where(s => s.Length > 0));
        CardStatus Result(CardStateKind kind, string badge, string text) => new(kind, badge, text, time);
        if(definition.HoldReason is { } reason) return Result(CardStateKind.Pending, "PENDING", reason);
        if(!state.Config.Enabled) return state.Config.HasUsableCredential || definition.IsDemo
            ? Result(CardStateKind.Disabled, "DISABLED", "Disabled · enable in Settings")
            : Result(CardStateKind.NotConfigured, "NOT SET UP", "Configure credentials to start");
        if(state.IsRefreshing) return Result(CardStateKind.Refreshing, "REFRESHING", "Refreshing…");
        if(state.Failure == FailureKind.LocalStorage && !state.FromCache && state.LastSuccess is { } current
            && state.LastAttempt is { } attempt && current.RetrievedAt >= attempt)
            return Result(CardStateKind.LocalSaveFailed, definition.IsDemo ? "DEMO · SAVE FAILED" : "LIVE · SAVE FAILED", "Values are current; local cache could not be saved");
        if(state.Failure == FailureKind.RateLimited) return Result(CardStateKind.RateLimited, "RATE LIMITED", "Waiting for the provider; manual refresh respects Retry-After");
        if(state.Failure is not null&&state.AuthMaintenance is {State:"scheduled"})return Result(CardStateKind.Unavailable,"WAITING","Automatic CLI credential maintenance is scheduled");
        if(state.Failure is { } failure) return state.LastSuccess is not null
            ? Result(CardStateKind.Stale, "STALE", Failure(failure))
            : Result(CardStateKind.Unavailable, "UNAVAILABLE", Failure(failure));
        if(state.LastSuccess is null) return Result(CardStateKind.Waiting, "WAITING", "Waiting for first refresh");
        if(state.FromCache) return Result(CardStateKind.Cached, definition.IsDemo ? "DEMO · CACHED" : "CACHED", "From previous session · awaiting a fresh query");
        if(state.LastSuccess.Origin == DataOrigin.Demo) return Result(CardStateKind.Demo, "DEMO", "DEMO DATA · NOT REAL USAGE");
        return Result(CardStateKind.Live, "LIVE", "Live data · read-only query");
    }
}
