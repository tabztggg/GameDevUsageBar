using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

static class QuotaApiChecks
{
    public static IEnumerable<(string Name,Func<Task> Run)> Cases()
    {
        var now=DateTimeOffset.UtcNow;
        var definition=new ProviderDefinition("claude","Claude","Subscription","fixture","#fff",new("api.anthropic.com",443,"/api/oauth/usage"));
        var config=new AccountConfig("claude",Guid.NewGuid(),"private-account-label",true,Guid.NewGuid(),Guid.NewGuid(),AccountId:"private-account-id");
        ProviderState State(decimal? value=80)=>new(config,new(config.Binding(definition),DataOrigin.Live,now,[new("five_hour","5-hour remaining",value,"%",MetricKind.Quota,100,now.AddHours(2))]),now,null,false,now.AddMinutes(10));
        void Check(bool condition){if(!condition)throw new Exception("Quota API check failed.");}
        yield return ("Quota export keeps precision, Claude five-hour semantics and excludes account secrets",()=>{
            var value=UsageExporter.Provider(definition,State(),now);Check(value.Metrics[0].WindowSeconds==18000&&value.Metrics[0].RemainingPercent==80&&value.CanUseForBudget);
            var balance=new Metric("balance","Remaining balance",9.998561156m,"USD");var exported=UsageExporter.Provider(definition,State() with {LastSuccess=new(config.Binding(definition),DataOrigin.Live,now,[balance])},now);
            Check(exported.Metrics[0].Value==9.998561156m);
            var json=JsonSerializer.Serialize(value);Check(!json.Contains(config.Label)&&!json.Contains(config.AccountId)&&!json.Contains(config.CredentialRef!.ToString()!)&&!json.Contains("Credential"));return Task.CompletedTask;
        });
        yield return ("Quota export marks cached, failed, reset, disabled, Demo and unknown values unusable",()=>{
            var state=State();
            foreach(var other in new[]{state with {FromCache=true},state with {Failure=FailureKind.Unauthorized},state with {LastSuccess=state.LastSuccess! with {RetrievedAt=now.AddHours(-1)}},state with {LastSuccess=state.LastSuccess! with {Origin=DataOrigin.Demo}},State(null),state with {LastSuccess=state.LastSuccess! with {Metrics=[state.LastSuccess.Metrics[0] with {ResetAt=now.AddSeconds(-1)}]}}})
                Check(!UsageExporter.Provider(definition,other,now).CanUseForBudget);
            var disabled=UsageExporter.Provider(definition,state with {Config=config with {Enabled=false}},now);Check(disabled.Metrics.Length==0&&disabled.Freshness=="disabled");
            Check(UsageExporter.Provider(definition,State(0),now).Metrics[0].Usable);
            Check(UsageExporter.Provider(definition,state with {LastSuccess=state.LastSuccess! with {Binding="wrong-binding"}},now).Metrics.Length==0);return Task.CompletedTask;
        });
        yield return ("Local API serves snapshots and typed errors without refreshing provider or mutating settings",async()=>{
            int exports=0;UsageExport Export(){exports++;return new(1,"GameDevUsageBar",UsageExporter.AppVersion,now,true,[UsageExporter.Provider(definition,State(),now)]);}
            await using var server=new QuotaApiServer(Export);await server.StartAsync(0);
            using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new(server.Address!),Timeout=TimeSpan.FromSeconds(5)};
            using(var health=await client.GetAsync("/v1/health")){Check(health.StatusCode==HttpStatusCode.OK&&exports==0);}
            var payload=await client.GetStringAsync("/v1/usage");using(var document=JsonDocument.Parse(payload)){Check(document.RootElement.GetProperty("schema_version").GetInt32()==1&&document.RootElement.GetProperty("providers")[0].GetProperty("metrics")[0].GetProperty("window_seconds").GetInt32()==18000);}
            using(var single=await client.GetAsync("/v1/usage/claude")){Check(single.StatusCode==HttpStatusCode.OK);}
            using(var missing=await client.GetAsync("/v1/usage/not-real")){Check(missing.StatusCode==HttpStatusCode.NotFound);}
            using(var post=await client.PostAsync("/v1/usage/claude",null)){Check(post.StatusCode==HttpStatusCode.MethodNotAllowed);}
            using(var query=await client.GetAsync("/v1/usage?refresh=true")){Check(query.StatusCode==HttpStatusCode.BadRequest);}
            var before=exports;await Task.WhenAll(Enumerable.Range(0,8).Select(async _=>{using var response=await client.GetAsync("/v1/usage");Check(response.StatusCode==HttpStatusCode.OK);}));Check(exports>=before+1);
            await server.DisposeAsync();Check(server.Address is null);
        });
        yield return ("Local API rejects browser origins and forged Host and binds only IPv4 loopback",async()=>{
            int exports=0;await using var server=new QuotaApiServer(()=>{exports++;return new(1,"GameDevUsageBar",UsageExporter.AppVersion,now,true,[]);});await server.StartAsync(0);
            var uri=new Uri(server.Address!);Check(uri.Host=="127.0.0.1");using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=uri};
            foreach(var type in new[]{"Origin","Host","Sec-Fetch-Site"})
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,"/v1/usage");request.Headers.TryAddWithoutValidation(type,type=="Origin"?"https://example.invalid":type=="Host"?"example.invalid:"+uri.Port:"cross-site");
                using var response=await client.SendAsync(request);Check(response.StatusCode==HttpStatusCode.Forbidden&&!response.Headers.Contains("Access-Control-Allow-Origin"));
            }
            Check(exports==0);
        });
        yield return ("Quota API port conflicts fail once and never select another port",async()=>{
            var blocker=new TcpListener(IPAddress.Loopback,0);blocker.Start();try {
                var port=((IPEndPoint)blocker.LocalEndpoint).Port;await using var server=new QuotaApiServer(()=>new(1,"GameDevUsageBar",UsageExporter.AppVersion,now,true,[]));
                bool failed=false;try{await server.StartAsync(port);}catch(IOException){failed=true;}Check(failed&&server.Address is null);
            } finally {blocker.Stop();}
        });
    }
}
