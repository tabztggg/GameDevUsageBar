using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Providers;

public static class UsageParsers
{
    private static decimal Num(JsonElement v)=>v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var n)?n:v.ValueKind==JsonValueKind.String&&decimal.TryParse(v.GetString(),NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out n)?n:throw new FormatException();
    private static decimal? Optional(JsonElement o,string key)=>o.TryGetProperty(key,out var v)&&v.ValueKind!=JsonValueKind.Null?Num(v):null;
    private static DateTimeOffset? Date(JsonElement o,string key)
    {if(!o.TryGetProperty(key,out var v)||v.ValueKind==JsonValueKind.Null)return null;return v.ValueKind==JsonValueKind.Number?DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()):DateTimeOffset.Parse(v.GetString()!,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal);}
    private static string Text(JsonElement o,string key)=>o.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    private static decimal Percent(decimal n){if(n<0||n>100)throw new FormatException();return n;}
    private static string Clean(string s)=>s.Length<=120&&!s.Any(char.IsControl)?s:throw new FormatException();
    public static string? CodexPlan(JsonElement root)
    {
        var node=root.TryGetProperty("usage",out var usage)&&usage.ValueKind==JsonValueKind.Object?usage:root;
        var plan=Text(node,"plan_type");if(plan.Length==0)plan=Text(root,"plan_type");
        return plan.Length is >0 and <=80&&!plan.Any(char.IsControl)?plan:null;
    }
    public static ImmutableArray<Metric> Codex(JsonElement root)
    {
        var usage=root.GetProperty("usage");var list=ImmutableArray.CreateBuilder<Metric>();
        void Window(JsonElement obj,string key,string scope="")
        {
            if(!obj.TryGetProperty(key,out var w)||w.ValueKind!=JsonValueKind.Object)return;
            var used=Optional(w,"used_percent");if(used is null)return;
            var duration=Optional(w,"limit_window_seconds");var label=duration>=2592000?"Monthly remaining":duration>=604800?"Weekly remaining":duration==18000?"5-hour remaining":duration<=18000?"Session remaining":"Reported window remaining";
            list.Add(new(scope+key,label,100-Percent(used.Value),"%",MetricKind.Quota,100,Date(w,"reset_at"),scope,WindowSeconds:duration is >0 && duration<=int.MaxValue && duration==decimal.Truncate(duration.Value)?(int)duration.Value:null));
        }
        if(usage.TryGetProperty("rate_limit",out var rate)&&rate.ValueKind==JsonValueKind.Object){Window(rate,"primary_window");Window(rate,"secondary_window");Window(rate,"code_review_window","Code review");}
        if(usage.TryGetProperty("code_review_rate_limit",out var review)&&review.ValueKind==JsonValueKind.Object){Window(review,"primary_window","Code review");Window(review,"secondary_window","Code review");}
        if(usage.TryGetProperty("additional_rate_limits",out var additional)&&additional.ValueKind==JsonValueKind.Array)
            foreach(var entry in additional.EnumerateArray().Take(24))if(entry.TryGetProperty("rate_limit",out var r)&&r.ValueKind==JsonValueKind.Object){var scope=Clean(Text(entry,"limit_name"));Window(r,"primary_window",scope);Window(r,"secondary_window",scope);}
        if(list.Count==0)throw new QueryException(FailureKind.NoData);
        if(usage.TryGetProperty("credits",out var credits)&&credits.ValueKind==JsonValueKind.Object)list.Add(new("credits","Credit balance",Optional(credits,"balance"),"credits"));
        AddResetTickets(root,list);
        return list.ToImmutable();
    }
    private static void AddResetTickets(JsonElement root,ImmutableArray<Metric>.Builder list)
    {
        if(!root.TryGetProperty("reset_credits",out var inventory)||inventory.ValueKind!=JsonValueKind.Object)return;
        var available=Optional(inventory,"available_count");
        if(available is {} count && (count<0||count!=decimal.Truncate(count)))throw new FormatException();
        if(available is not null)list.Add(new("reset-credits","Reset credits",available,"tickets",MetricKind.Count));
        if(root.TryGetProperty("reset_credits",out inventory)&&inventory.ValueKind==JsonValueKind.Object&&inventory.TryGetProperty("credits",out var tickets)&&tickets.ValueKind==JsonValueKind.Array)
        {
            // Per-ticket dates only for available tickets. Do not infer an expiry for
            // the aggregate count when dates are absent or differ between tickets.
            DateTimeOffset? Expiry(JsonElement ticket){try{return Date(ticket,"expires_at");}catch(Exception e)when(e is FormatException or ArgumentException or InvalidOperationException){return null;}}
            var expiries=tickets.EnumerateArray().Take(128).Where(t=>t.ValueKind==JsonValueKind.Object&&Text(t,"status")=="available"&&(!t.TryGetProperty("is_supported_by_plan",out var supported)||supported.ValueKind!=JsonValueKind.False)).Select(Expiry).ToArray();
            foreach(var group in expiries.GroupBy(d=>d).OrderBy(g=>g.Key))
                list.Add(new("reset-ticket-expiry-"+list.Count,"Reset tickets expiring",group.Count(),"tickets",MetricKind.Count,ResetAt:group.Key,DateMeaning:"expiry"));
        }
        if(available is not null&&!list.Any(m=>m.DateMeaning=="expiry"))list.Add(new("reset-ticket-expiry-unknown","Reset tickets expiring",available,"tickets",MetricKind.Count,DateMeaning:"expiry"));
    }
    public static ImmutableArray<Metric> Claude(JsonElement root)
    {
        var list=ImmutableArray.CreateBuilder<Metric>();
        foreach(var pair in new[]{("five_hour","5-hour remaining"),("seven_day","Weekly remaining"),("seven_day_opus","Opus weekly remaining"),("seven_day_sonnet","Sonnet weekly remaining"),("seven_day_oauth_apps","OAuth apps weekly remaining")})
        {
            if(!root.TryGetProperty(pair.Item1,out var w)||w.ValueKind!=JsonValueKind.Object)continue;
            var used=Optional(w,"utilization");if(used is null)continue;
            list.Add(new(pair.Item1,pair.Item2,100-Percent(used.Value),"%",MetricKind.Quota,100,Date(w,"resets_at")));
        }
        if(list.Count==0)throw new QueryException(FailureKind.NoData);
        if(root.TryGetProperty("extra_usage",out var extra)&&extra.ValueKind==JsonValueKind.Object)
        {
            var enabled=extra.TryGetProperty("is_enabled",out var flag)&&flag.ValueKind==JsonValueKind.True;
            if(enabled){list.Add(new("extra-used","Extra usage spent",Optional(extra,"used_credits")/100m,"USD",MetricKind.Used));list.Add(new("extra-limit","Extra usage limit",Optional(extra,"monthly_limit")/100m,"USD",MetricKind.Limit));}
        }
        AddResetTickets(root,list);
        return list.ToImmutable();
    }
    public static ImmutableArray<Metric> GeminiCli(JsonElement root)
    {
        var buckets=root.GetProperty("buckets");var list=ImmutableArray.CreateBuilder<Metric>();int index=0;
        if(buckets.ValueKind!=JsonValueKind.Array)throw new FormatException();
        foreach(var bucket in buckets.EnumerateArray().Take(64))
        {
            var fraction=Optional(bucket,"remainingFraction");if(fraction is null)continue;
            var model=Clean(Text(bucket,"modelId"));var tokens=Clean(Text(bucket,"tokenType"));
            list.Add(new("bucket-"+index++,"Model quota remaining",Percent(fraction.Value*100),"%",MetricKind.Quota,100,Date(bucket,"resetTime"),string.Join(" · ",new[]{model,tokens}.Where(s=>s.Length>0))));
        }
        if(list.Count==0)throw new QueryException(FailureKind.NoData);
        return list.OrderBy(m=>m.Value).ToImmutableArray();
    }
    public static ImmutableArray<Metric> Grsai(JsonElement root)
    {
        if(root.GetProperty("code").GetInt32()!=0)throw new QueryException(FailureKind.BusinessError);
        var data=root.GetProperty("data");
        if(data.TryGetProperty("type",out var type))
        {
            var kind=type.GetInt32();
            if(kind==0)return [new("key-limit","Key credit limit",null,"credits",MetricKind.Unlimited)];
            if(kind!=1)throw new FormatException();
        }
        return GrsaiAccount(root);
    }
    public static ImmutableArray<Metric> GrsaiAccount(JsonElement root)
    {
        if(root.GetProperty("code").GetInt32()!=0)throw new QueryException(FailureKind.BusinessError);
        return [new("credits","Available",Num(root.GetProperty("data").GetProperty("credits")),"credits")];
    }
    public static ImmutableArray<Metric> TypeSafe(JsonElement root)
    {
        if(root.TryGetProperty("browserSnapshot",out var browser)&&browser.ValueKind==JsonValueKind.True)
        {
            var visibleBalance=Num(root.GetProperty("balance"));if(visibleBalance<0)throw new FormatException();
            // The visible page exposes a balance, not a verified billing-cycle spend.
            return [new("balance","Account balance",visibleBalance,"USD",Scope:"Website billing")];
        }
        var balance=Num(root.GetProperty("balance"));var spent=Num(root.GetProperty("spent"));if(balance<0||spent<0)throw new FormatException();
        var list=ImmutableArray.CreateBuilder<Metric>();list.Add(new("balance","Account balance",balance,"USD"));list.Add(new("spent","Billing-cycle spend",spent,"USD",MetricKind.Used,Scope:Clean(Text(root,"cycleLabel"))));
        if(root.TryGetProperty("credits",out var credits)&&credits.ValueKind==JsonValueKind.Array)
        {
            var index=0;
            foreach(var credit in credits.EnumerateArray().Take(64))
            {
                var remaining=Optional(credit,"remaining");var amount=Optional(credit,"amount");var expiry=Date(credit,"expiresAt");
                if(remaining is null||remaining<=0||amount is null||amount<0||expiry is null||expiry<=DateTimeOffset.UtcNow)continue;
                list.Add(new("credit-"+index++,"Unexpired credit",remaining,"USD",MetricKind.Remaining,amount,expiry,DateMeaning:"expiry"));
            }
        }
        return list.ToImmutable();
    }
    public static ImmutableArray<Metric> GeminiProject(JsonElement root)
    {
        if(root.TryGetProperty("nextPageToken",out var next)&&next.ValueKind==JsonValueKind.String&&next.GetString()?.Length>0)throw new QueryException(FailureKind.IncompleteData);
        if(!root.TryGetProperty("timeSeries",out var series)||series.ValueKind!=JsonValueKind.Array||series.GetArrayLength()==0)throw new QueryException(FailureKind.NoData);
        decimal total=0;bool found=false;
        foreach(var row in series.EnumerateArray())
        {
            if(!row.TryGetProperty("points",out var points)||points.ValueKind!=JsonValueKind.Array)throw new FormatException();
            foreach(var point in points.EnumerateArray()){var value=point.GetProperty("value");if(!value.TryGetProperty("int64Value",out var n))throw new FormatException();var count=Num(n);if(count<0)throw new FormatException();total=checked(total+count);found=true;}
        }
        if(!found)throw new QueryException(FailureKind.NoData);
        return [new("requests-24h","Completed requests (24h)",total,"requests",MetricKind.Count)];
    }
}
