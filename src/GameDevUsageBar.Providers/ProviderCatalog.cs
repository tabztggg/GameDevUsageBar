using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Providers;

public static class ProviderCatalog
{
    public static IReadOnlyList<IProviderAdapter> Create() =>
    [
        new ApiAdapter(new("claude","Claude","Subscription","5-hour and weekly quotas","#DFA68B",new("api.anthropic.com",443,"/api/oauth/usage")),UsageParsers.Claude),
        new ApiAdapter(new("codex","Codex","Subscription","Short-period and weekly quotas · reset credits","#74D9BD",new("chatgpt.com",443,"/backend-api/wham/usage")),UsageParsers.Codex),
        new ApiAdapter(new("tripo","Tripo","API credits","Available and frozen API credits · Studio excluded","#9AADFF",new("openapi.tripo3d.ai",443,"/v3/account/balance")),Parsers.Tripo),
        new ApiAdapter(new("grsai","GRSAI","Account credits (API key)","Account balance · queried with your API key","#F1BB66",new("grsaiapi.com",443,"/client/common/getCredits")),UsageParsers.GrsaiAccount),
        new ApiAdapter(new("deepseek","DeepSeek","Official API balance","Granted and topped-up balance by currency","#7BAAFF",new("api.deepseek.com",443,"/user/balance")),Parsers.DeepSeek),
        new ApiAdapter(new("gemini","Gemini AI Studio","Project API requests","Completed requests (24h) · Cloud Monitoring · not a quota percentage","#A89AFA",new("monitoring.googleapis.com",443,"/v3/projects/{project}/timeSeries")),UsageParsers.GeminiProject),
        new ApiAdapter(new("gemini-cli","Gemini CLI","CLI quota","CLI request quota · separate from AI Studio API","#A89AFA",new("cloudcode-pa.googleapis.com",443,"/v1internal:retrieveUserQuota")),UsageParsers.GeminiCli),
        new ApiAdapter(new("elevenlabs","ElevenLabs","Subscription","Native character usage and reset time","#A4D7E4",new("api.elevenlabs.io",443,"/v1/user/subscription","xi-api-key","")),Parsers.ElevenLabs),
        new ApiAdapter(new("openrouter","OpenRouter","Account balance (API key)","Account balance · purchased credits minus usage","#BA9FEE",new("openrouter.ai",443,"/api/v1/credits")),Parsers.OpenRouterAccount),
        new DemoAdapter(),
    ];
}
public sealed class ApiAdapter(ProviderDefinition definition, Func<JsonElement, ImmutableArray<Metric>> parser) : IProviderAdapter
{
    public ProviderDefinition Definition { get; } = definition;
    public async Task<AdapterOutcome> RefreshAsync(AccountConfig config, IQueryClient queries, CancellationToken ct)
    {
        try
        {
            var bytes = await queries.ReadAsync(Definition,config,ct);
            using var document = JsonDocument.Parse(bytes);
            if(document.RootElement.TryGetProperty("error",out _)) return AdapterOutcome.Fail(FailureKind.BusinessError);
            return new(parser(document.RootElement),Plan:Definition.Id=="codex" ? UsageParsers.CodexPlan(document.RootElement) : null);
        }
        catch(QueryException error) { return AdapterOutcome.Fail(error.Kind,error.RetryNotBefore); }
        catch(JsonException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
        catch(InvalidOperationException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
        catch(KeyNotFoundException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
        catch(FormatException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
        catch(OverflowException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
        catch(ArgumentOutOfRangeException) { return AdapterOutcome.Fail(FailureKind.SchemaMismatch); }
    }
}
public sealed class HoldAdapter(ProviderDefinition definition) : IProviderAdapter
{
    public ProviderDefinition Definition { get; } = definition;
    public Task<AdapterOutcome> RefreshAsync(AccountConfig config, IQueryClient queries, CancellationToken ct) => Task.FromResult(AdapterOutcome.Fail(FailureKind.Unsupported));
}
public sealed class DemoAdapter : IProviderAdapter
{
    public ProviderDefinition Definition { get; } = new("demo","Demo sandbox","Demo data","Sample credits · no account or network access","#6BD4A9",null);
    public async Task<AdapterOutcome> RefreshAsync(AccountConfig config, IQueryClient queries, CancellationToken ct)
    {
        switch(config.DemoScenario)
        {
            case "401": return AdapterOutcome.Fail(FailureKind.Unauthorized);
            case "429": return AdapterOutcome.Fail(FailureKind.RateLimited,DateTimeOffset.UtcNow.AddMinutes(1));
            case "Timeout": await Task.Delay(750,ct); return AdapterOutcome.Fail(FailureKind.Timeout);
            case "Slow": await Task.Delay(10000,ct); break;
            case "Schema error": return AdapterOutcome.Fail(FailureKind.SchemaMismatch);
            case "Offline": return AdapterOutcome.Fail(FailureKind.Network);
            case "Missing": return new([new("available","Available",null,"credits"),new("reserved","Frozen",0,"credits",MetricKind.Reserved)]);
        }
        return new([new("available","Available",config.DemoScenario=="Zero" ? 0 : 1250.50m,"credits"),new("reserved","Frozen",200m,"credits",MetricKind.Reserved)]);
    }
}
public static class Parsers
{
    private static decimal Number(JsonElement node)
    {
        if(node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out var number)) return number;
        if(node.ValueKind == JsonValueKind.String && decimal.TryParse(node.GetString(),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out number)) return number;
        throw new FormatException();
    }
    private static decimal Required(JsonElement obj,string key) => Number(obj.GetProperty(key));
    private static decimal? Optional(JsonElement obj,string key) => obj.TryGetProperty(key,out var value) && value.ValueKind!=JsonValueKind.Null ? Number(value) : null;
    public static ImmutableArray<Metric> Tripo(JsonElement root)
    {
        if(root.GetProperty("code").GetInt32()!=0) throw new QueryException(FailureKind.BusinessError);
        var data = root.GetProperty("data");
        return [new("available","Available",Required(data,"balance"),"credits"),new("frozen","Frozen",Required(data,"frozen"),"credits",MetricKind.Reserved)];
    }
    public static ImmutableArray<Metric> DeepSeek(JsonElement root)
    {
        _=root.GetProperty("is_available").GetBoolean();
        var list = ImmutableArray.CreateBuilder<Metric>();
        foreach(var item in root.GetProperty("balance_infos").EnumerateArray())
        {
            var currency = item.GetProperty("currency").GetString();
            if(currency is not ("CNY" or "USD")) throw new FormatException();
            list.Add(new(currency+".total","Available",Required(item,"total_balance"),currency));
            list.Add(new(currency+".grant","Granted",Required(item,"granted_balance"),currency));
            list.Add(new(currency+".topup","Topped up",Required(item,"topped_up_balance"),currency));
        }
        if(list.Count==0) throw new FormatException();
        return list.ToImmutable();
    }
    public static ImmutableArray<Metric> ElevenLabs(JsonElement root)
    {
        var used=Required(root,"character_count"); var limit=Required(root,"character_limit");
        DateTimeOffset? reset=null;
        if(root.TryGetProperty("next_character_count_reset_unix",out var value) && value.ValueKind!=JsonValueKind.Null) reset=DateTimeOffset.FromUnixTimeSeconds(value.GetInt64());
        return [new("remaining","Remaining",limit-used,"characters",MetricKind.Quota,limit,reset),new("used","Used",used,"characters",MetricKind.Used),new("limit","Allowance",limit,"characters",MetricKind.Limit)];
    }
    public static ImmutableArray<Metric> OpenRouterKey(JsonElement root)
    {
        var data=root.GetProperty("data");
        return [new("usage","Key usage",Required(data,"usage"),"USD",MetricKind.Used),new("remaining","Key limit remaining",Optional(data,"limit_remaining"),"USD",MetricKind.Remaining),new("limit","Key limit",Optional(data,"limit"),"USD",MetricKind.Limit)];
    }
    public static ImmutableArray<Metric> OpenRouterAccount(JsonElement root)
    {
        var data=root.GetProperty("data"); var credits=Required(data,"total_credits"); var used=Required(data,"total_usage");
        return [new("remaining","Account balance",credits-used,"USD"),new("credits","Purchased credits",credits,"USD"),new("used","Account usage",used,"USD",MetricKind.Used)];
    }
}
