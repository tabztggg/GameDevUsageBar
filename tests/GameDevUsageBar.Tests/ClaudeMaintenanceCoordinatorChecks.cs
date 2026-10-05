using System.Net;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

static class ClaudeMaintenanceCoordinatorChecks
{
    private static void Check(bool value){if(!value)throw new InvalidOperationException("Claude maintenance coordinator fixture assertion");}
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=new(2030,1,1,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Secrets:ISecretStore{public string Read(AccountConfig config)=>throw new InvalidOperationException("Unexpected credential read");}
    private sealed class Store:ISnapshotStore
    {public Task<UsageSnapshot?> LoadAsync(string binding)=>Task.FromResult<UsageSnapshot?>(null);public Task SaveAsync(UsageSnapshot snapshot)=>Task.CompletedTask;public Task RemoveAsync(string binding)=>Task.CompletedTask;}
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> callback):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>callback(request);}
    private static HttpResponseMessage Json(object value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        foreach(var rateLimited in new[]{false,true})
        yield return (rateLimited?"Claude proactive renewal preserves usage Retry-After without a quota GET":"Claude proactive renewal bypasses the usage interval and updates its current flight",async()=>
        {
            var folder=Path.Combine(root,"claude-coordinator-"+rateLimited);var time=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),time);
            var path=native.PathFor("claude");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path,JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken="nonfunctional-initial-access",refreshToken="nonfunctional-initial-refresh",expiresAt=time.Now.AddMinutes(9).ToUnixTimeMilliseconds(),refreshTokenExpiresAt=time.Now.AddDays(30).ToUnixTimeMilliseconds(),scopes=new[]{"user:profile","user:inference"}}}));
            var config=new AccountConfig("claude",Guid.NewGuid(),"Synthetic",Enabled:true,SourceMode:"local-oauth",CredentialSource:"local-oauth",NativeIdentity:native.Read("claude").Identity);
            var current=config;int usageReads=0,postCalls=0,profileCalls=0;
            var retry=time.Now.AddHours(1);
            using var tokenTransport=new Handler(request=>
            {
                if(request.Method==HttpMethod.Post){postCalls++;return Task.FromResult(Json(new{access_token="nonfunctional-renewed-access",refresh_token="nonfunctional-renewed-refresh",expires_in=28800}));}
                profileCalls++;return Task.FromResult(Json(new{account=new{uuid="11111111-1111-4111-8111-111111111111"},organization=new{uuid="22222222-2222-4222-8222-222222222222"}}));
            });
            var renewal=new NativeClaudeRenewal(folder,native,(old,next)=>{Check(old==current);current=next;return Task.FromResult(next);},tokenTransport,time,()=>false);
            using var usageTransport=new Handler(request=>
            {
                Check(request.RequestUri?.ToString()=="https://api.anthropic.com/api/oauth/usage");usageReads++;
                if(rateLimited&&time.Now<retry){var result=Json(new{error="limit"},HttpStatusCode.TooManyRequests);result.Headers.RetryAfter=new(retry);return Task.FromResult(result);}
                return Task.FromResult(Json(new{five_hour=new{utilization=20,resets_at=time.Now.AddHours(5).ToString("O")},seven_day=new{utilization=30,resets_at=time.Now.AddDays(7).ToString("O")}}));
            });
            using var queries=new ProviderQueryClient(new Secrets(),adapters.Select(a=>a.Definition),usageTransport,native,time,renewal:renewal);
            await using var coordinator=new RefreshCoordinator(queries,new Store(),time);
            var adapter=adapters.Single(a=>a.Definition.Id=="claude");await coordinator.ConfigureAsync(adapter,config);await coordinator.RefreshAsync("claude",false);
            Check(postCalls==0&&profileCalls==1&&usageReads==1);
            var first=coordinator.Get("claude");Check(rateLimited?first.Failure==FailureKind.RateLimited:first.LastSuccess is not null);
            time.Now=time.Now.AddMinutes(4).AddSeconds(1);await coordinator.RefreshAsync("claude",false);
            var renewed=coordinator.Get("claude");Check(postCalls==1&&renewed.Config.NativeIdentity==current.NativeIdentity&&current.NativeIdentity!=config.NativeIdentity);
            Check(renewed.AuthMaintenance?.State=="ready");
            if(rateLimited)
            {
                Check(usageReads==1&&renewed.Failure==FailureKind.RateLimited&&renewed.NextAttempt==retry&&renewed.LastSuccess is null);
                await coordinator.RefreshAsync("claude",true);Check(usageReads==1&&postCalls==1);
                time.Now=retry.AddSeconds(1);await coordinator.RefreshAsync("claude",false);Check(usageReads==2&&coordinator.Get("claude").Failure is null);
            }
            else Check(usageReads==2&&renewed.LastSuccess?.Binding==current.Binding(adapter.Definition)&&renewed.Failure is null);
            var exported=UsageExporter.Provider(adapter.Definition,coordinator.Get("claude"),time.Now);
            var publicText=JsonSerializer.Serialize(exported,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower});
            Check(publicText.Contains("auth_maintenance",StringComparison.Ordinal)&&!publicText.Contains("nonfunctional",StringComparison.Ordinal)&&!publicText.Contains(current.NativeIdentity!,StringComparison.Ordinal)&&!publicText.Contains("11111111-1111",StringComparison.Ordinal));
            var before=postCalls;_=UsageExporter.Provider(adapter.Definition,coordinator.Get("claude"),time.Now);Check(postCalls==before);
        });
        yield return ("Claude auth maintenance is never enabled for saved or manual OAuth sources",async()=>
        {
            var folder=Path.Combine(root,"claude-not-local");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var calls=0;
            using var handler=new Handler(_=>{calls++;throw new InvalidOperationException("Unexpected transport");});
            using var renewal=new NativeClaudeRenewal(folder,native,(_,_)=>throw new InvalidOperationException("Unexpected binding"),handler,busyGuard:()=>false);
            foreach(var mode in new[]{"manual","saved-oauth"})
            {
                var config=new AccountConfig("claude",Guid.NewGuid(),"Synthetic",Enabled:true,SourceMode:mode,NativeIdentity:new string('a',64));
                Check(await renewal.MaintainAsync(config,CancellationToken.None)==config&&!renewal.BackgroundDue(config)&&renewal.Observe(config) is null);
            }
            Check(calls==0);
        });
    }
}
