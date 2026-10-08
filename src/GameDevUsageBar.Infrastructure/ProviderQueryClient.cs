using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

// Query workflows are enumerated here, not supplied by providers or settings.
// POST is allowed only for the enumerated usage and billing read operations. No fallback,
// redirects, scripts, arbitrary URL, or model call is supported. Credential
// maintenance is a separate, single-owner operation used by the coordinator.
public sealed class ProviderQueryClient : IQueryClient,IAuthMaintenance,IDisposable
{
    private readonly GuardedQueryClient legacy;
    private readonly ISecretStore secrets;
    private readonly NativeOAuthStore native;
    private readonly Dictionary<string,ProviderDefinition> catalog;
    private readonly HttpClient client;
    private readonly HttpClient claudeLocal;
    private readonly NativeClaudeRenewal? renewal;
    private readonly TimeProvider clock;
    private readonly Action<Exception>? onError;
    private readonly List<SafeEvent> events=[];
    public IReadOnlyList<SafeEvent> Events {get{lock(events)return legacy.Events.Concat(events).OrderBy(e=>e.At).TakeLast(200).ToArray();}}
    public NativeOAuthStore Native=>native;
    public NativeAuthVault? NativeVault {get;}
    public ProviderQueryClient(ISecretStore secrets,IEnumerable<ProviderDefinition> definitions,HttpMessageHandler? handler=null,NativeOAuthStore? native=null,TimeProvider? time=null,NativeAuthVault? nativeVault=null,NativeClaudeRenewal? renewal=null,Action<Exception>? onError=null)
    {
        this.secrets=secrets;catalog=definitions.ToDictionary(d=>d.Id);clock=time ?? TimeProvider.System;this.native=native ?? new();NativeVault=nativeVault;this.renewal=renewal;this.onError=onError;
        legacy=new(secrets,catalog.Values,handler is null?null:new SharedHandler(handler),clock,onError);
        client=new(handler ?? new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate}){Timeout=Timeout.InfiniteTimeSpan};
        claudeLocal=new(handler is null?NativeClaudeRenewal.CreateSystemProxyHandler():new SharedHandler(handler)){Timeout=Timeout.InfiniteTimeSpan};
    }
    public bool BackgroundDue(AccountConfig config)=>renewal?.BackgroundDue(config)==true;
    public AuthMaintenance? Observe(AccountConfig config)=>renewal?.Observe(config);
    public Task<AccountConfig> MaintainAsync(AccountConfig config,CancellationToken ct)=>renewal?.MaintainAsync(config,ct)??Task.FromResult(config);
    public Task SourceSavedAsync(AccountConfig approved)=>renewal?.SourceSavedAsync(approved)??Task.CompletedTask;
    public Task<IDisposable> PauseClaudeRenewalAsync(CancellationToken ct=default)=>renewal?.PauseAsync(ct)??Task.FromResult<IDisposable>(new NoRenewalLease());
    private sealed class NoRenewalLease:IDisposable{public void Dispose(){}}
    private sealed class SharedHandler(HttpMessageHandler inner):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>new HttpMessageInvoker(inner,false).SendAsync(request,ct);}
    public async Task<byte[]> ReadAsync(ProviderDefinition definition,AccountConfig account,CancellationToken ct)
    {
        if(ProviderSources.IsRetired(definition.Id))throw new QueryException(FailureKind.Policy);
        if(!ProviderSources.IsExtended(definition.Id))return await legacy.ReadAsync(definition,account,ct);
        try{account.Validate();}catch{throw new QueryException(FailureKind.Policy);}
        if(!catalog.TryGetValue(definition.Id,out var approved)||approved!=definition||account.ProviderId!=definition.Id||!account.Enabled)throw new QueryException(FailureKind.Policy);
        if(account.ProviderId=="gemini" && account.ProjectId.Length==0)throw new QueryException(FailureKind.ProjectRequired);
        if(account.ProviderId=="claude" && account.SourceMode=="web-cookie" && !Guid.TryParse(account.OrganizationId,out _))throw new QueryException(FailureKind.ProjectRequired);
        if(!account.HasUsableCredential)throw new QueryException(FailureKind.CredentialMissing);
        string secret,accountId=account.AccountId;
        if(account.SourceMode=="local-oauth")
        {
            var credential=native.ForAccount(account).Read(account.ProviderId);
            if(credential.Identity!=account.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            secret=credential.Token;accountId=credential.AccountId;
        }
        else if(account.SourceMode=="saved-oauth")
        {
            var credential=(NativeVault??throw new QueryException(FailureKind.CredentialMissing)).Read(account);
            secret=credential.Token;accountId=credential.AccountId;
        }
        else secret=secrets.Read(account);
        if(string.IsNullOrWhiteSpace(secret)||secret.Length>8192||secret.Any(char.IsControl))throw new QueryException(FailureKind.CredentialUnreadable);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            switch(account.ProviderId)
            {
                case "codex":
                {
                    var headers=Bearer(secret);if(accountId.Length>0)headers["ChatGPT-Account-Id"]=accountId;
                    var usage=await Send("codex",new("https://chatgpt.com/backend-api/wham/usage"),HttpMethod.Get,headers,null,"application/json",65536,deadline.Token);
                    using var doc=JsonDocument.Parse(usage);JsonElement? reset=null;string? resetError=null;
                    try {var bytes=await Send("codex",new("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"),HttpMethod.Get,headers,null,"application/json",65536,deadline.Token);using var inventory=JsonDocument.Parse(bytes);reset=inventory.RootElement.Clone();}
                    catch(QueryException e){ErrorObserver.Report(onError,e);resetError=e.Kind.ToString();}catch(JsonException error){ErrorObserver.Report(onError,error);resetError="SchemaMismatch";}
                    catch(HttpRequestException error){ErrorObserver.Report(onError,error);resetError="Network";}catch(IOException error){ErrorObserver.Report(onError,error);resetError="Network";}
                    catch(OperationCanceledException error)when(!ct.IsCancellationRequested){ErrorObserver.Report(onError,error);resetError="Timeout";}
                    return JsonSerializer.SerializeToUtf8Bytes(new{usage=doc.RootElement,reset_credits=reset,reset_credits_status=resetError});
                }
                case "claude":
                    if(account.SourceMode=="web-cookie")return await Send("claude",new("https://claude.ai/api/organizations/"+Guid.Parse(account.OrganizationId).ToString()+"/usage"),HttpMethod.Get,new(){["Cookie"]=Cookie(secret),["Origin"]="https://claude.ai",["Referer"]="https://claude.ai/settings/usage"},null,"application/json",65536,deadline.Token);
                    var claudeHeaders=Bearer(secret);claudeHeaders["anthropic-beta"]="oauth-2025-04-20";
                    return await Send("claude",new("https://api.anthropic.com/api/oauth/usage"),HttpMethod.Get,claudeHeaders,null,"application/json",65536,deadline.Token,account.SourceMode=="local-oauth");
                case "gemini-cli":
                    return await Send("gemini-cli",new("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota"),HttpMethod.Post,Bearer(secret),"{}","application/json",65536,deadline.Token);
                case "grsai":
                    // Documented account balance via API key. URI contains a credential:
                    // never persist/log it; redirects remain disabled and events omit URLs.
                    return await Send("grsai",new("https://"+(account.QueryRegion=="china"?"grsai.dakka.com.cn":"grsaiapi.com")+"/client/common/getCredits?apikey="+Uri.EscapeDataString(secret)),HttpMethod.Get,[],null,"application/json",65536,deadline.Token);
                case "gemini":
                {
                    var now=clock.GetUtcNow();
                    var filter="metric.type=\"serviceruntime.googleapis.com/api/request_count\" AND resource.type=\"consumed_api\" AND resource.labels.service=\"generativelanguage.googleapis.com\"";
                    var query="filter="+Uri.EscapeDataString(filter)+"&interval.startTime="+Uri.EscapeDataString(now.AddHours(-24).ToString("o"))+"&interval.endTime="+Uri.EscapeDataString(now.ToString("o"))+"&view=FULL&aggregation.alignmentPeriod=60s&aggregation.perSeriesAligner=ALIGN_SUM&aggregation.crossSeriesReducer=REDUCE_SUM&pageSize=2000";
                    return await Send("gemini",new("https://monitoring.googleapis.com/v3/projects/"+account.ProjectId+"/timeSeries?"+query),HttpMethod.Get,Bearer(secret),null,"application/json",262144,deadline.Token);
                }
                default:throw new QueryException(FailureKind.Policy);
            }
        }
        catch(OperationCanceledException error)when(!ct.IsCancellationRequested){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.Timeout);}
        catch(HttpRequestException error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.Network);}
        catch(IOException error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.Network);}
        catch(JsonException error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.SchemaMismatch);}
    }
    private static Dictionary<string,string> Bearer(string token)=>new(){["Authorization"]="Bearer "+token};
    public static string Cookie(string value)
    {
        value=value.Trim();if(value.StartsWith("Cookie:",StringComparison.OrdinalIgnoreCase))value=value[7..].Trim();
        if(value.Length==0||value.Length>8192||value.Any(char.IsControl)||!value.Contains('='))throw new QueryException(FailureKind.CredentialUnreadable);
        return value;
    }
    public static bool Allowed(string id,Uri uri,HttpMethod method)
    {
        if(uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length>0||uri.Fragment.Length>0||uri.IsLoopback||uri.AbsolutePath.Contains('%')||uri.AbsolutePath.Contains(".."))return false;
        var get=method==HttpMethod.Get;var post=method==HttpMethod.Post;
        if(id is not ("gemini" or "grsai")&&uri.Query.Length>0)return false;
        return id switch {
            "codex"=>get&&uri.Host=="chatgpt.com"&&uri.AbsolutePath is "/backend-api/wham/usage" or "/backend-api/wham/rate-limit-reset-credits",
            "claude"=>get&&((uri.Host=="api.anthropic.com"&&uri.AbsolutePath=="/api/oauth/usage")||(uri.Host=="claude.ai"&&Regex.IsMatch(uri.AbsolutePath,"^/api/organizations/[a-f0-9-]{36}/usage$"))),
            "gemini-cli"=>post&&uri.Host=="cloudcode-pa.googleapis.com"&&uri.AbsolutePath=="/v1internal:retrieveUserQuota",
            "grsai"=>get&&uri.Host is "grsaiapi.com" or "grsai.dakka.com.cn"&&uri.AbsolutePath=="/client/common/getCredits"&&GrsaiKeyQuery(uri.Query),
            "gemini"=>get&&uri.Host=="monitoring.googleapis.com"&&Regex.IsMatch(uri.AbsolutePath,"^/v3/projects/[a-z][a-z0-9-]{4,61}[a-z0-9]/timeSeries$")&&uri.Query.StartsWith("?filter=",StringComparison.Ordinal),_=>false
        };
    }
    private static bool GrsaiKeyQuery(string query)
    {
        if(!query.StartsWith("?apikey=",StringComparison.Ordinal)||query.Length>24584)return false;
        var encoded=query[8..];
        if(!Regex.IsMatch(encoded,"^(?:[A-Za-z0-9_.~-]|%[a-fA-F0-9]{2})+$",RegexOptions.None,TimeSpan.FromSeconds(1)))return false;
        var value=Uri.UnescapeDataString(encoded);
        return value.Length is >0 and <=8192&&!value.Any(char.IsControl)&&!value.Contains('%');
    }
    private async Task<byte[]> Send(string id,Uri uri,HttpMethod method,Dictionary<string,string> headers,string? body,string accept,int max,CancellationToken ct,bool protectedClaudeLocal=false)
    {
        if(!Allowed(id,uri,method))throw new QueryException(FailureKind.Policy);
        using var request=new HttpRequestMessage(method,uri);request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));request.Headers.UserAgent.ParseAdd("GameDevUsageBar/0.6");
        foreach(var pair in headers){if(pair.Value.Any(char.IsControl)||!request.Headers.TryAddWithoutValidation(pair.Key,pair.Value))throw new QueryException(FailureKind.Policy);}
        if(body is not null)request.Content=new StringContent(body,Encoding.UTF8,"application/json");
        using var response=await (protectedClaudeLocal?claudeLocal:client).SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);var status=(int)response.StatusCode;
        lock(events){events.Add(new(clock.GetUtcNow(),id,"query",status,0));if(events.Count>200)events.RemoveAt(0);}
        if(status is 401 or 403)throw new QueryException(status==401?FailureKind.Unauthorized:FailureKind.Forbidden);
        if(status==429||(status==503&&response.Headers.RetryAfter is not null))throw new QueryException(FailureKind.RateLimited,GuardedQueryClient.RetryAfter(response,clock.GetUtcNow()));
        if(status is >=300 and <400)throw new QueryException(FailureKind.Redirect);
        if(!response.IsSuccessStatusCode)throw new QueryException(FailureKind.HttpError);
        var media=response.Content.Headers.ContentType?.MediaType;
        if(accept=="application/json"&&!(media=="application/json"||(media?.EndsWith("+json",StringComparison.OrdinalIgnoreCase)??false)))throw new QueryException(FailureKind.SchemaMismatch);
        if(accept!="application/json"&&!(media==accept||(accept=="application/javascript"&&media=="text/javascript")))throw new QueryException(FailureKind.SchemaMismatch);
        if(response.Content.Headers.ContentLength>max)throw new QueryException(FailureKind.TooLarge);
        using var buffer=new MemoryStream();await using var stream=await response.Content.ReadAsStreamAsync(ct);var chunk=new byte[4096];int read;
        while((read=await stream.ReadAsync(chunk,ct))>0){if(buffer.Length+read>max)throw new QueryException(FailureKind.TooLarge);await buffer.WriteAsync(chunk.AsMemory(0,read),ct);}
        return buffer.ToArray();
    }
    public void Dispose(){legacy.Dispose();client.Dispose();claudeLocal.Dispose();renewal?.Dispose();}
}
