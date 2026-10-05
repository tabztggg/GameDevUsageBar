using System.Net;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

internal static class NetworkVpsChecks
{
    private static void Check(bool value){if(!value)throw new Exception("Network/VPS check failed");}
    private sealed class Secret : ISecretStore{public int Reads;public string Read(AccountConfig c){Reads++;return "fixture-key";}}
    private sealed class RetiredAdapter(ProviderDefinition definition):IProviderAdapter{public ProviderDefinition Definition=>definition;public Task<AdapterOutcome> RefreshAsync(AccountConfig c,IQueryClient q,CancellationToken ct)=>throw new Exception("Retired adapter refreshed");}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> run):HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(run(request));}}
    public static IEnumerable<(string Name,Func<Task> Run)> Cases()
    {
        yield return ("Network byte counters use real elapsed seconds and decimal MB/s; reset, switching and resume never create spikes",()=>{
            var now=DateTimeOffset.UtcNow;var calc=new NetworkRateCalculator();
            Check(calc.Update(new("a","Ethernet",0,0),0,1000,now).Status=="warming_up");
            var speed=calc.Update(new("a","Ethernet",4_000_000,1_000_000),2000,1000,now.AddSeconds(2));Check(speed.DownloadMbPerSecond==2&&speed.UploadMbPerSecond==.5m);
            Check(calc.Update(new("b","Wi-Fi",long.MaxValue/2,1),3000,1000,now).Status=="warming_up");
            Check(calc.Update(new("b","Wi-Fi",1,0),4000,1000,now).Status=="warming_up");
            Check(calc.Update(new("b","Wi-Fi",1,0),25000,1000,now).DownloadMbPerSecond is null);
            Check(calc.Update(null,26000,1000,now).Status=="unavailable");return Task.CompletedTask;
        });
        foreach(var retiredId in new[]{"vps","typesafe"})
        yield return (retiredId+" retirement removes from legacy settings, layout and exports and cannot read credentials or send requests",async()=>{
            Check(ProviderCatalog.Create().All(a=>a.Definition.Id!=retiredId));
            var main=new AccountConfig("codex",Guid.NewGuid(),"Fixture");var old=new AccountConfig(retiredId,Guid.NewGuid(),"Old",true,Guid.NewGuid(),SourceMode:retiredId=="typesafe"?"browser-session":"manual",Vps:retiredId=="vps"?new("https://api.example.invalid/traffic"):null);
            var root=Path.Combine(Path.GetTempPath(),"WorkBuddy-Tasks","work","gamedevusagebar-remove-vps-20261004","workspace","migration-"+Guid.NewGuid());Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),JsonSerializer.Serialize(new{Schema=3,Accounts=new[]{main,old}}));
            var store=new SettingsStore(root);Check((await store.LoadAsync()).SequenceEqual(new[]{main})&&!store.ReadOnly);await store.SaveAsync(new[]{main,old});Check((await store.LoadAsync()).SequenceEqual(new[]{main}));
            var layout=new GameDevUsageBar.Core.Presentation.PresentationPreferences(CardOrder:[retiredId,"codex"],Widget:new(CardIds:[retiredId,"codex"],ShowNetwork:true,NetworkAdapterId:"fixture")).Validate();Check(layout.CardOrder!.SequenceEqual(new[]{"codex"})&&layout.WidgetOrDefault.CardIds!.SequenceEqual(new[]{"codex"})&&layout.WidgetOrDefault.ShowNetwork&&layout.WidgetOrDefault.NetworkAdapterId=="fixture");
            var definition=new ProviderDefinition(retiredId,"Retired","retired","retired","#ffffff",new("api.example.invalid",443,"/traffic"));
            var secrets=new Secret();using var handler=new Handler(_=>throw new Exception("Retired VPS network request"));using var queries=new ProviderQueryClient(secrets,[definition],handler);
            try{await queries.ReadAsync(definition,old,CancellationToken.None);throw new Exception("Retired VPS queried");}catch(QueryException error){Check(error.Kind==FailureKind.Policy);}
            Check(handler.Calls==0&&secrets.Reads==0);
            var coordinator=new RefreshCoordinator(queries,new DiskSnapshotStore(root));var exported=UsageExporter.Export([new RetiredAdapter(definition)],coordinator,DateTimeOffset.UtcNow);Check(exported.Providers.Length==0);
            await using var server=new QuotaApiServer(()=>exported);await server.StartAsync(0);using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){BaseAddress=new(server.Address!)};using var response=await http.GetAsync("/v1/usage/"+retiredId);Check(response.StatusCode==HttpStatusCode.NotFound);
        });
        yield return ("Codex ticket expiry groups use ticket counts and omit redeemed tickets; export preserves real quota windows",()=>{
            using var doc=JsonDocument.Parse("""{"usage":{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800,"reset_at":1900000000}}},"reset_credits":{"available_count":2,"credits":[{"status":"available","expires_at":"2030-02-01T00:00:00Z"},{"status":"available","expires_at":"2030-03-01T00:00:00Z"},{"status":"redeemed","expires_at":"2030-02-01T00:00:00Z"}]}}""");
            var metrics=UsageParsers.Codex(doc.RootElement);Check(metrics.Single(m=>m.Id=="reset-credits").Value==2&&metrics.Single(m=>m.Id=="reset-credits").Unit=="tickets");
            Check(metrics.Count(m=>m.DateMeaning=="expiry")==2&&metrics.Where(m=>m.DateMeaning=="expiry").All(m=>m.Value==1&&m.ResetAt is not null));
            var adapter=ProviderCatalog.Create().Single(a=>a.Definition.Id=="codex");var config=new AccountConfig("codex",Guid.NewGuid(),"Fixture",true);var now=DateTimeOffset.UtcNow;
            var exported=UsageExporter.Provider(adapter.Definition,new(config,new(config.Binding(adapter.Definition),DataOrigin.Live,now,metrics),now,null,false,null),now);Check(exported.Metrics[0].WindowSeconds==604800);
            using var missing=JsonDocument.Parse("""{"usage":{"rate_limit":{"primary_window":{"used_percent":10}}},"reset_credits":null}""");var absent=UsageParsers.Codex(missing.RootElement);Check(absent.Length==1&&absent.Single().Value==90&&absent.Single().WindowSeconds is null&&absent.All(m=>!m.Id.StartsWith("reset-",StringComparison.Ordinal)));return Task.CompletedTask;
        });
        yield return ("Network API returns the existing sample without sampling or quota refresh and retains local-client guards",async()=>{
            int reads=0,exports=0;var sample=new NetworkSpeedSnapshot("live","fixture","Ethernet",1.25m,.125m,DateTimeOffset.UtcNow);
            await using var server=new QuotaApiServer(()=>{exports++;return new(1,"GameDevUsageBar","test",DateTimeOffset.UtcNow,true,[]);},()=>{reads++;return sample;});await server.StartAsync(0);
            using var http=new HttpClient(new SocketsHttpHandler{UseProxy=false}){BaseAddress=new(server.Address!)};
            using var doc=JsonDocument.Parse(await http.GetStringAsync("/v1/network"));Check(doc.RootElement.GetProperty("download_mb_per_second").GetDecimal()==1.25m&&reads==1&&exports==0);
            using var request=new HttpRequestMessage(HttpMethod.Get,"/v1/network");request.Headers.TryAddWithoutValidation("Origin","https://example.invalid");using var blocked=await http.SendAsync(request);Check(blocked.StatusCode==HttpStatusCode.Forbidden&&reads==1);
        });
    }
}
