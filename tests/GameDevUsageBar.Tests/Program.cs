using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

const string canary="nonfunctional-test-canary-not-a-real-key-82416";
if(args.FirstOrDefault()=="dpapi-child")
{
    var stored=JsonSerializer.Deserialize<AccountConfig>(File.ReadAllText(Path.Combine(args[1],"test-config.json")))!;
    if(new DpapiSecretStore(args[1]).Read(stored)!=canary) return 2;
    Console.WriteLine("fresh-process DPAPI OK"); return 0;
}
var root=Path.Combine(Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT") ?? Path.Combine(Path.GetTempPath(),"WorkBuddy-Tasks","work","gamedevusagebar-provider-repair-20261004","workspace"),"tests-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
var results=new List<object>(); int passed=0,failed=0;
void Assert(bool condition,string detail="Assertion failed") {if(!condition)throw new Exception(detail);}
async Task Test(string name,Func<Task> test)
{
    try {await test();passed++;results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
    catch(Exception error){failed++;results.Add(new{name,status="FAIL",error=error.GetType().Name});Console.WriteLine("FAIL "+name+" "+error.GetType().Name);}
}
ImmutableArray<Metric> Parse(Func<JsonElement,ImmutableArray<Metric>> parser,string body) {using var document=JsonDocument.Parse(body);return parser(document.RootElement);}
var adapters=ProviderCatalog.Create(); var tripo=adapters.Single(a=>a.Definition.Id=="tripo");
var account=new AccountConfig("tripo",Guid.NewGuid(),"Test",true,Guid.NewGuid(),Guid.NewGuid());
await Test("Tripo precise decimal and zero",()=> {var values=Parse(Parsers.Tripo,"{\"code\":0,\"data\":{\"balance\":41465.01,\"frozen\":0}}");Assert(values[0].Value==41465.01m && values[1].Value==0);Assert(values[0].Percent==null);return Task.CompletedTask;});
await Test("Tripo missing-required and business error never become zero",()=>
{
    bool missing=false,business=false;
    try{Parse(Parsers.Tripo,"{\"code\":0,\"data\":{\"balance\":0}}");}catch(KeyNotFoundException){missing=true;}
    try{Parse(Parsers.Tripo,"{\"code\":42,\"data\":{\"balance\":0,\"frozen\":0}}");}catch(QueryException e){business=e.Kind==FailureKind.BusinessError;}
    Assert(missing && business);return Task.CompletedTask;
});
await Test("DeepSeek keeps currency and balance scopes separate",()=> {var values=Parse(Parsers.DeepSeek,"{\"is_available\":true,\"balance_infos\":[{\"currency\":\"CNY\",\"total_balance\":\"110.00\",\"granted_balance\":\"10\",\"topped_up_balance\":\"100\"},{\"currency\":\"USD\",\"total_balance\":\"2\",\"granted_balance\":\"0\",\"topped_up_balance\":\"2\"}]}");Assert(values.Length==6 && values[0].Unit=="CNY" && values[3].Unit=="USD");return Task.CompletedTask;});
await Test("OpenRouter unlimited key does not become account balance",()=> {var values=Parse(Parsers.OpenRouterKey,"{\"data\":{\"usage\":3.55,\"limit\":null,\"limit_remaining\":null}}");Assert(values[0].Value==3.55m && values[1].Value==null && values[2].Value==null);return Task.CompletedTask;});
await Test("OpenRouter API-key balance uses account contract",()=> {var values=Parse(Parsers.OpenRouterAccount,"{\"data\":{\"total_credits\":20,\"total_usage\":2.50}}");Assert(values[0].Value==17.50m);return Task.CompletedTask;});
await Test("ElevenLabs keeps character units and absolute reset",()=> {var values=Parse(Parsers.ElevenLabs,"{\"character_count\":100,\"character_limit\":1000,\"next_character_count_reset_unix\":1738356858}");Assert(values[0].Value==900 && values[0].Percent==90 && values[0].Unit=="characters" && values[0].ResetAt==DateTimeOffset.FromUnixTimeSeconds(1738356858));return Task.CompletedTask;});
await Test("Codex periods follow reported window duration and cannot infer a subscription plan",()=> {
    foreach(var period in new (int? Seconds,string Label)[]{(18000,"5-hour remaining"),(604800,"Weekly remaining"),(2592000,"Monthly remaining"),(null,"Reported window remaining")}){
        var window=period.Seconds is {} seconds?new {used_percent=23,limit_window_seconds=(int?)seconds}:new {used_percent=23,limit_window_seconds=(int?)null};
        using var document=JsonDocument.Parse(JsonSerializer.Serialize(new {usage=new {rate_limit=new {primary_window=window}}}));
        var metric=UsageParsers.Codex(document.RootElement).Single();
        Assert(metric.Label==period.Label&&metric.Value==77&&metric.Total==100&&metric.WindowSeconds==period.Seconds&&UsageParsers.CodexPlan(document.RootElement) is null);
    }
    return Task.CompletedTask;
});
await Test("Codex plan labels use actual reported strings and reject absent or unsafe metadata",()=> {
    foreach(var sample in new (string Body,string? Plan)[]{
        ("""{"usage":{"plan_type":"plus"}}""","plus"),
        ("""{"plan_type":"pro","usage":{}}""","pro"),
        ("""{"plan_type":"plus","usage":{"plan_type":"pro"}}""","pro"),
        ("""{"usage":{"plan_type":"future-tier"}}""","future-tier"),
        ("""{"usage":{"plan_type":null}}""",null),
        ("""{"usage":{"plan_type":42}}""",null),
        ("""{"usage":{"plan_type":""}}""",null),
        ("""{"usage":{"plan_type":"plus\n"}}""",null),
        (JsonSerializer.Serialize(new {usage=new {plan_type=new string('x',81)}}),null)
    }){using var document=JsonDocument.Parse(sample.Body);Assert(UsageParsers.CodexPlan(document.RootElement)==sample.Plan);}
    return Task.CompletedTask;
});
await Test("Claude optional reset inventory retains only reported ticket counts and per-ticket expiry facts",()=> {
    const string quota="\"five_hour\":{\"utilization\":37,\"resets_at\":\"2030-01-01T00:00:00Z\"}";
    foreach(var suffix in new[]{"",",\"reset_credits\":null",",\"reset_credits\":{}"}){
        var absent=Parse(UsageParsers.Claude,"{"+quota+suffix+"}");
        Assert(absent.Length==1&&absent[0].Value==63&&absent[0].ResetAt==DateTimeOffset.Parse("2030-01-01T00:00:00Z")&&!absent.Any(m=>m.Kind==MetricKind.Count));
    }
    var reported=Parse(UsageParsers.Claude,"{"+quota+",\"reset_credits\":{\"available_count\":2,\"credits\":[{\"status\":\"available\",\"expires_at\":\"2030-02-01T00:00:00Z\"},{\"status\":\"available\",\"expires_at\":\"not-a-date\"},{\"status\":\"redeemed\",\"expires_at\":\"2030-03-01T00:00:00Z\"},{\"status\":\"available\",\"is_supported_by_plan\":false,\"expires_at\":\"2030-04-01T00:00:00Z\"}]}}");
    var count=reported.Single(m=>m.Id=="reset-credits");var dates=reported.Where(m=>m.DateMeaning=="expiry").ToArray();
    Assert(count.Kind==MetricKind.Count&&count.Unit=="tickets"&&count.Value==2&&count.ResetAt is null&&reported[0].Value==63);
    Assert(dates.Length==2&&dates.All(m=>m.Unit=="tickets"&&m.Value==1)&&dates.Count(m=>m.ResetAt is null)==1&&dates.Single(m=>m.ResetAt is not null).ResetAt==DateTimeOffset.Parse("2030-02-01T00:00:00Z"));
    var zero=Parse(UsageParsers.Claude,"{"+quota+",\"reset_credits\":{\"available_count\":0}}");Assert(zero.Single(m=>m.Id=="reset-credits").Value==0);
    var unknownCount=Parse(UsageParsers.Claude,"{"+quota+",\"reset_credits\":{\"available_count\":null,\"credits\":[{\"status\":\"available\",\"expires_at\":\"2030-02-01T00:00:00Z\"}]}}");
    Assert(unknownCount.All(m=>m.Id!="reset-credits")&&unknownCount.Single(m=>m.DateMeaning=="expiry").Value==1);
    return Task.CompletedTask;
});
await Test("Reported Codex plan and quota fields survive adapter, coordinator and cache without extra requests",async ()=> {
    var adapter=adapters.Single(a=>a.Definition.Id=="codex");
    var config=new AccountConfig("codex",Guid.NewGuid(),"Fixture",true,Guid.NewGuid(),Guid.NewGuid());
    var handler=new FakeHandler(request=>new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath.EndsWith("reset-credits",StringComparison.Ordinal)?"{}":"""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":23,"limit_window_seconds":18000,"reset_at":1900000000},"secondary_window":{"used_percent":36,"limit_window_seconds":604800}}}""",Encoding.UTF8,"application/json")});
    var secrets=new FakeSecrets();using var queries=new ProviderQueryClient(secrets,adapters.Select(a=>a.Definition),handler);
    var cache=new DiskSnapshotStore(Path.Combine(root,"reported-plan"));await using var coordinator=new RefreshCoordinator(queries,cache);
    await coordinator.ConfigureAsync(adapter,config);await coordinator.RefreshAsync("codex");
    var success=coordinator.Get("codex").LastSuccess;Assert(success?.Plan=="plus"&&handler.Calls==2&&secrets.Reads==1);
    var current=success!;Assert(current.Metrics.Length==2&&current.Metrics[0].Value==77&&current.Metrics[0].WindowSeconds==18000&&current.Metrics[0].ResetAt==DateTimeOffset.FromUnixTimeSeconds(1900000000)&&current.Metrics[1].Value==64&&current.Metrics[1].WindowSeconds==604800);
    var loaded=await cache.LoadAsync(config.Binding(adapter.Definition));Assert(loaded is not null&&loaded.Plan=="plus"&&loaded.Metrics.SequenceEqual(current.Metrics)&&handler.Calls==2);
});
await Test("All unsafe destinations rejected before secrets are read",async ()=>
{
    var secrets=new FakeSecrets();using var client=new GuardedQueryClient(secrets,adapters.Select(a=>a.Definition),new FakeHandler(_=>new(HttpStatusCode.OK)));
    foreach(var uri in new[]{"http://openapi.tripo3d.ai/v3/account/balance","https://openapi.tripo3d.ai:8443/v3/account/balance","https://u@openapi.tripo3d.ai/v3/account/balance","https://openapi.tripo3d.ai/v3/task","https://openapi.tripo3d.ai/v3/account/balance?key=x","https://openapi.tripo3d.ai/v3/account/balance#x","https://example.invalid/v3/account/balance"})
    {
        bool rejected=false;try{GuardedQueryClient.Validate(new(uri),tripo.Definition.Endpoint!,HttpMethod.Get);}catch(QueryException e){rejected=e.Kind==FailureKind.Policy;}Assert(rejected);
    }
    var forged=tripo.Definition with {Endpoint=tripo.Definition.Endpoint! with {Host="example.invalid"}};
    bool failedBeforeRead=false;try{await client.ReadAsync(forged,account,CancellationToken.None);}catch(QueryException e){failedBeforeRead=e.Kind==FailureKind.Policy;}
    Assert(failedBeforeRead && secrets.Reads==0);
});
foreach(var code in new[]{301,302,307,308,401,403,429,500})
await Test("HTTP "+code+" is a typed failure without following redirects",async ()=>
{
    var handler=new FakeHandler(_=>new((HttpStatusCode)code));using var client=new GuardedQueryClient(new FakeSecrets(),adapters.Select(a=>a.Definition),handler);
    var outcome=await tripo.RefreshAsync(account,client,CancellationToken.None);
    Assert(outcome.Failure==(code<400?FailureKind.Redirect:code==401?FailureKind.Unauthorized:code==403?FailureKind.Forbidden:code==429?FailureKind.RateLimited:FailureKind.HttpError) && handler.Calls==1);
});
await Test("Oversized response and HTML 200 rejected",async ()=>
{
    foreach(var content in new[]{new StringContent(new string('x',70000),Encoding.UTF8,"application/json"),new StringContent("<html>error</html>",Encoding.UTF8,"text/html")})
    {using var client=new GuardedQueryClient(new FakeSecrets(),adapters.Select(a=>a.Definition),new FakeHandler(_=>new(HttpStatusCode.OK){Content=content}));var outcome=await tripo.RefreshAsync(account,client,CancellationToken.None);Assert(outcome.Failure is FailureKind.TooLarge or FailureKind.SchemaMismatch);}
});
await Test("Retry-After is never shortened even beyond one day",()=>
{
    var now=DateTimeOffset.UtcNow;using var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests);response.Headers.RetryAfter=new(TimeSpan.FromDays(3));Assert(GuardedQueryClient.RetryAfter(response,now)==now.AddDays(3));response.Headers.RetryAfter=new(now.AddHours(2));Assert(GuardedQueryClient.RetryAfter(response,now)==now.AddHours(2));return Task.CompletedTask;
});
await Test("Settings corruption preserved and writes blocked",async ()=>
{
    var folder=Path.Combine(root,"corrupt");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"settings.json");await File.WriteAllTextAsync(path,"{broken");var store=new SettingsStore(folder);await store.LoadAsync();Assert(store.ReadOnly);bool blocked=false;try{await store.SaveAsync([account]);}catch(InvalidOperationException){blocked=true;}Assert(blocked && await File.ReadAllTextAsync(path)=="{broken");
});
await Test("Invalid budgets rejected before reading credentials",async ()=>
{
    foreach(var rule in new[]{tripo.Definition.Endpoint! with {MaxBytes=0},tripo.Definition.Endpoint! with {TimeoutSeconds=0},tripo.Definition.Endpoint! with {Header="X-Unknown"}})
    {
        var definition=tripo.Definition with {Endpoint=rule}; var secrets=new FakeSecrets();
        using var client=new GuardedQueryClient(secrets,[definition],new FakeHandler(_=>new(HttpStatusCode.OK)));
        bool blocked=false;try{await client.ReadAsync(definition,account,CancellationToken.None);}catch(QueryException e){blocked=e.Kind==FailureKind.Policy;}
        Assert(blocked && secrets.Reads==0);
    }
});
await Test("Chunked body without Content-Length still obeys byte limit",async ()=>
{
    var content=new StreamContent(new MemoryStream(new byte[70000])); content.Headers.ContentType=new("application/json");
    using var client=new GuardedQueryClient(new FakeSecrets(),adapters.Select(a=>a.Definition),new FakeHandler(_=>new(HttpStatusCode.OK){Content=content}));
    Assert((await tripo.RefreshAsync(account,client,CancellationToken.None)).Failure==FailureKind.TooLarge);
});
await Test("Slow response body times out after headers",async ()=>
{
    var definition=tripo.Definition with {Endpoint=tripo.Definition.Endpoint! with {TimeoutSeconds=1}};
    var content=new StreamContent(new SlowStream()); content.Headers.ContentType=new("application/json");
    using var client=new GuardedQueryClient(new FakeSecrets(),[definition],new FakeHandler(_=>new(HttpStatusCode.OK){Content=content}));
    Assert((await new ApiAdapter(definition,Parsers.Tripo).RefreshAsync(account,client,CancellationToken.None)).Failure==FailureKind.Timeout);
});
await Test("Authentication stops scheduled retries and Retry-After blocks manual refresh",async ()=>
{
    var adapter=new OutcomeAdapter("control"); var config=new AccountConfig("control",Guid.NewGuid(),"test",true);
    await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemoryStore()); await coordinator.ConfigureAsync(adapter,config);
    adapter.Outcome=AdapterOutcome.Fail(FailureKind.Unauthorized); await coordinator.RefreshAsync("control"); await coordinator.RefreshAsync("control",false); Assert(adapter.Calls==1);
    adapter.Outcome=AdapterOutcome.Fail(FailureKind.RateLimited,DateTimeOffset.UtcNow.AddDays(3)); await coordinator.RefreshAsync("control"); await coordinator.RefreshAsync("control"); Assert(adapter.Calls==2);
});
await Test("Unknown fields and optional nulls remain safe without changing required values",()=>
{
    var values=Parse(Parsers.Tripo,"{\"code\":0,\"future\":123,\"data\":{\"balance\":0,\"frozen\":200,\"unknown\":null}}"); Assert(values[0].Display=="0 credits" && values[1].Value==200);
    Assert(new Metric("x","x",null,"USD").Display=="Not reported"); Assert(new Metric("x","x",0,"credits",MetricKind.Quota,null).Percent==null);
    return Task.CompletedTask;
});
await Test("DPAPI encryption, provider binding and fresh-process restart",async ()=>
{
    var store=new DpapiSecretStore(root);var reference=await store.CreateAsync(canary,account.ProviderId,account.SlotId);var config=account with {CredentialRef=reference};Assert(store.Read(config)==canary);
    Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(root,"secrets",reference.ToString("N")+".bin"))).Contains(canary));
    bool mismatch=false;try{store.Read(config with {ProviderId="deepseek"});}catch(QueryException){mismatch=true;}Assert(mismatch);
    await new SettingsStore(root).SaveAsync([config]);Assert(!(await File.ReadAllTextAsync(Path.Combine(root,"settings.json"))).Contains(canary));
    await File.WriteAllTextAsync(Path.Combine(root,"test-config.json"),JsonSerializer.Serialize(config));
    var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
    if(string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),"dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(Program).Assembly.Location);
    start.ArgumentList.Add("dpapi-child");start.ArgumentList.Add(root);
    using var child=Process.Start(start)!;await child.WaitForExitAsync();Assert(child.ExitCode==0);
});
await Test("Cache binding changes on key, account and endpoint policy",()=>
{
    var binding=account.Binding(tripo.Definition);Assert(binding!=(account with {CredentialRevision=Guid.NewGuid()}).Binding(tripo.Definition));Assert(binding!=(account with {SlotId=Guid.NewGuid()}).Binding(tripo.Definition));Assert(binding!=account.Binding(tripo.Definition with {Endpoint=tripo.Definition.Endpoint! with {Path="/v3/account/other"}}));return Task.CompletedTask;
});
await Test("Independent providers and coalesced single-flight",async ()=>
{
    var slow=new BlockingAdapter("slow");var fast=new ImmediateAdapter("fast");await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemoryStore());
    await coordinator.ConfigureAsync(slow,new("slow",Guid.NewGuid(),"a",true));await coordinator.ConfigureAsync(fast,new("fast",Guid.NewGuid(),"b",true));
    var first=coordinator.RefreshAsync("slow");await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));var joined=coordinator.RefreshAsync("slow");Assert(ReferenceEquals(first,joined));
    await coordinator.RefreshAsync("fast").WaitAsync(TimeSpan.FromSeconds(2));Assert(coordinator.Get("fast").LastSuccess!=null && slow.Calls==1);slow.Release.SetResult();await first;
});
await Test("Late result after key replacement cannot restore old values",async ()=>
{
    var slow=new BlockingAdapter("late");var config=new AccountConfig("late",Guid.NewGuid(),"a",true,Guid.NewGuid(),Guid.NewGuid());await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemoryStore());await coordinator.ConfigureAsync(slow,config);
    var flight=coordinator.RefreshAsync("late");await slow.Started.Task;await coordinator.ConfigureAsync(slow,config with {CredentialRevision=Guid.NewGuid()});Assert(coordinator.Get("late").LastSuccess==null);slow.Release.SetResult();await flight;Assert(coordinator.Get("late").LastSuccess==null);
});
await Test("Failure preserves stale success; disable stops queries",async ()=>
{
    var adapter=new ImmediateAdapter("stale");var config=new AccountConfig("stale",Guid.NewGuid(),"a",true);await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemoryStore());await coordinator.ConfigureAsync(adapter,config);await coordinator.RefreshAsync("stale");adapter.Fail=true;await coordinator.RefreshAsync("stale");Assert(coordinator.Get("stale").LastSuccess!=null && coordinator.Get("stale").Failure==FailureKind.Timeout);await coordinator.ConfigureAsync(adapter,config with {Enabled=false});var calls=adapter.Calls;await coordinator.RefreshAsync("stale");Assert(adapter.Calls==calls);
});
await Test("Persisted cache loads only for the exact new binding",async ()=>
{
    var store=new DiskSnapshotStore(Path.Combine(root,"cachetest"));var binding=account.Binding(tripo.Definition);var snapshot=new UsageSnapshot(binding,DataOrigin.Live,DateTimeOffset.UtcNow,[new("credits","Balance",0,"credits")]);await store.SaveAsync(snapshot);Assert((await store.LoadAsync(binding))?.Metrics[0].Value==0);Assert(await store.LoadAsync((account with {CredentialRevision=Guid.NewGuid()}).Binding(tripo.Definition))==null);
});
await Test("Providers assembly has no infrastructure, file, process or HTTP implementation dependency",()=>
{
    var references=typeof(ProviderCatalog).Assembly.GetReferencedAssemblies().Select(a=>a.Name??"").ToArray();Assert(!references.Any(n=>n.Contains("Infrastructure") || n.Contains("System.Net.Http") || n.Contains("System.Diagnostics.Process")));return Task.CompletedTask;
});
await Test("Presentation changes preserve 429 and 401 scheduling without extra calls",async ()=>
{
    foreach(var failure in new[]{FailureKind.RateLimited,FailureKind.Unauthorized}) {
        var adapter=new OutcomeAdapter("layout-"+failure) {Outcome=AdapterOutcome.Fail(failure,DateTimeOffset.UtcNow.AddDays(3))};
        var config=new AccountConfig(adapter.Definition.Id,Guid.NewGuid(),"fixture",true);
        await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemoryStore());
        await coordinator.ConfigureAsync(adapter,config);await coordinator.RefreshAsync(adapter.Definition.Id);
        var before=coordinator.Get(adapter.Definition.Id);var events=0;coordinator.Changed+=(_,_)=>events++;
        var store=new PresentationStore(Path.Combine(root,"layout-"+failure));
        using var preferences=new GameDevUsageBar.App.Presentation.PresentationPreferencesService(store,new());
        preferences.Update(p=>p with {CardOrder=["tripo",adapter.Definition.Id]});
        preferences.Update(p=>p with {StartInTray=true,Widget=p.WidgetOrDefault with {Visible=true,Topmost=true,Collapsed=true,Locked=true,CardIds=["tripo"],Placement=new("left",new(-1920,0,1920,1080),-300,20)}});
        await preferences.FlushAsync();await coordinator.RefreshAsync(adapter.Definition.Id,false);
        Assert(coordinator.Get(adapter.Definition.Id)==before && adapter.Calls==1 && events==0);
        Assert(config.Binding(adapter.Definition)==before.Config.Binding(adapter.Definition));
    }
});
await Test("Missing layout creates no startup write; future IDs survive round-trip",async ()=>
{
    var dir=Path.Combine(root,"layout-new");var store=new PresentationStore(dir);Assert(await store.LoadAsync()==null && !Directory.Exists(dir));
    using var service=new GameDevUsageBar.App.Presentation.PresentationPreferencesService(store,new(CardOrder:["future","tripo"],Widget:new(CardIds:["future","tripo"])));
    await service.FlushAsync();Assert(!Directory.Exists(dir));service.Update(p=>p with {StartInTray=true});await service.FlushAsync();
    var loaded=await new PresentationStore(dir).LoadAsync();Assert(loaded!.CardOrder!.SequenceEqual(new[]{"future","tripo"}) && loaded.WidgetOrDefault.CardIds!.Contains("future"));
});
await Test("Corrupt and higher-schema layout preserved; session edits remain available",async ()=>
{
    foreach(var body in new[]{"not-json","{\"schema\":2}","{\"schema\":1,\"widget\":{\"placement\":{\"monitorBounds\":null}}}"}) {
        var dir=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);var file=Path.Combine(dir,"presentation.json");await File.WriteAllTextAsync(file,body);
        var store=new PresentationStore(dir);Assert(await store.LoadAsync()==null && store.ReadOnly);
        using var service=new GameDevUsageBar.App.Presentation.PresentationPreferencesService(store,new());service.Update(p=>p with {Widget=new(Visible:true)});await service.FlushAsync();
        Assert(service.Current.WidgetOrDefault.Visible && await File.ReadAllTextAsync(file)==body);
    }
});
await Test("Layout reset preserves original bytes in a recovery file",async ()=>
{
    var dir=Path.Combine(root,"layout-reset");Directory.CreateDirectory(dir);await File.WriteAllTextAsync(Path.Combine(dir,"presentation.json"),"original-invalid-bytes");
    var store=new PresentationStore(dir);await store.LoadAsync();await store.ResetAsync(new());
    Assert(!store.ReadOnly && (await store.LoadAsync())!.Schema==1);
    Assert(await File.ReadAllTextAsync(Directory.GetFiles(dir,"presentation.unreadable-*.json").Single())=="original-invalid-bytes");
});
await Test("Layout writes leave account, cache and secret bytes unchanged",async ()=>
{
    var dir=Path.Combine(root,"layout-isolation");Directory.CreateDirectory(dir);
    var paths=new[]{Path.Combine(dir,"settings.json"),Path.Combine(dir,"secrets","fixture.bin"),Path.Combine(dir,"cache","fixture.json")};
    foreach(var file in paths){Directory.CreateDirectory(Path.GetDirectoryName(file)!);await File.WriteAllTextAsync(file,"unchanged-fixture");}
    var store=new PresentationStore(dir);await store.SaveAsync(new(CardOrder:["tripo"],Widget:new(Visible:true)));
    foreach(var file in paths)Assert(await File.ReadAllTextAsync(file)=="unchanged-fixture");
});
await Test("Value projections preserve zero, missing and signed tiny balances",()=>
{
    Assert(CardPresentation.Value(0,"credits")=="0 credits");Assert(CardPresentation.Value(null,"credits")=="Not reported");
    Assert(CardPresentation.Value(0.0001m,"USD")=="0.0001 USD");Assert(CardPresentation.Value(-0.0001m,"USD")=="-0.0001 USD");return Task.CompletedTask;
});
await Test("State classifier preserves failures before provenance and treats local save separately",()=>
{
    var now=DateTimeOffset.UtcNow;var c=new AccountConfig("demo",Guid.NewGuid(),"demo",true);var def=adapters.Single(a=>a.Definition.Id=="demo").Definition;
    var success=new UsageSnapshot(c.Binding(def),DataOrigin.Demo,now,[new("x","x",0,"credits")]);
    var state=new ProviderState(c,success,now.AddSeconds(-1),null,false,now.AddMinutes(10));
    Assert(CardPresentation.Classify(def,state,now).Kind==CardStateKind.Demo);
    Assert(CardPresentation.Classify(def,state with {Failure=FailureKind.Timeout},now).Kind==CardStateKind.Stale);
    Assert(CardPresentation.Classify(def,state with {Failure=FailureKind.RateLimited},now).Kind==CardStateKind.RateLimited);
    Assert(CardPresentation.Classify(def,state with {Failure=FailureKind.LocalStorage},now).Kind==CardStateKind.LocalSaveFailed);
    Assert(CardPresentation.Classify(def,state with {FromCache=true},now).Kind==CardStateKind.Cached);
    Assert(CardPresentation.Classify(def,state with {IsRefreshing=true},now).Kind==CardStateKind.Refreshing);
    Assert(CardPresentation.Classify(tripo.Definition,new(account with {Enabled=false},null,null,null,false,null),now).Kind==CardStateKind.Disabled);
    var gated=adapters.Single(a=>a.Definition.Id=="gemini").Definition;
    Assert(gated.CanConfigure && gated.HoldReason is null);
    Assert(adapters.Any(a=>a.Definition.Id=="gemini") && adapters.Any(a=>a.Definition.Id=="gemini-cli"));return Task.CompletedTask;
});
await Test("Tray gesture guard handles dismissal order, long hold and rapid distinct click",()=>
{
    var g=new PopupToggleGuard();g.Deactivated(10,true);g.PointerDown(10,false);Assert(!g.ShouldOpenOnClick(false));
    g.PointerDown(11,false);Assert(g.ShouldOpenOnClick(false));
    g.PointerDown(12,true);g.Deactivated(12,true);Assert(!g.ShouldOpenOnClick(false));
    g.Deactivated(13,true);g.PointerDown(14,false);Assert(g.ShouldOpenOnClick(false));
    g.Reset();Assert(g.ShouldOpenOnClick(false));return Task.CompletedTask;
});
await Test("Placement handles negative monitors, lost displays and 100/150/200 percent scale",()=>
{
    var main=new MonitorInfo("main",new(0,0,1920,1080),new(0,0,1920,1040),true);
    var left=new MonitorInfo("left",new(-1920,0,1920,1080),new(-1920,0,1920,1040),false);
    var saved=new SavedPlacement("left",left.Bounds,-400,24,280,360);
    foreach(var scale in new[]{1d,1.5,2}){var p=Placement.Resolve(saved,new[]{main,left},scale,false);Assert(p.Monitor==left && p.LeftPx>=left.Work.Left && p.LeftPx+p.WidthDip*scale<=left.Work.Right && p.TopPx+p.HeightDip*scale<=left.Work.Bottom);}
    var recovered=Placement.Resolve(saved,new[]{main},1,false);Assert(recovered.Recovered && recovered.LeftPx>=0 && saved.LeftPx==-400);
    Assert(Placement.Resolve(saved,new[]{main,left},1,false).LeftPx==saved.LeftPx);
    var tiny=new MonitorInfo("tiny",new(0,0,180,120),new(0,0,180,120),true);var fit=Placement.Resolve(null,new[]{tiny},2,false);Assert(fit.WidthDip*2<=180 && fit.HeightDip*2<=120);
    foreach(var point in new[]{(-1920d,0d),(-1d,1040d),(-1920d,1040d),(-1d,0d)}){var a=Placement.Anchor(point.Item1,point.Item2,540,900,left.Work);Assert(a.Left>=left.Work.Left && a.Right<=left.Work.Right && a.Top>=left.Work.Top && a.Bottom<=left.Work.Bottom);}
    return Task.CompletedTask;
});
await Test("Presentation service cannot reach refresh, account, secret or query state",()=>
{
    var fields=typeof(GameDevUsageBar.App.Presentation.PresentationPreferencesService).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
    Assert(!fields.Any(f=>f.FieldType==typeof(RefreshCoordinator)||typeof(IQueryClient).IsAssignableFrom(f.FieldType)||typeof(ISecretStore).IsAssignableFrom(f.FieldType)||f.FieldType==typeof(SettingsStore)));return Task.CompletedTask;
});
await Test("Single-row values keep remaining quota, native credits, zero, tiny amounts and failure provenance",()=> {
    var d=tripo.Definition;var now=DateTimeOffset.UtcNow;
    ProviderState S(params Metric[] metrics)=>new(account,new(account.Binding(d),DataOrigin.Live,now,metrics.ToImmutableArray()),now,null,false,null);
    Assert(BarPresentation.Value(d,S(new Metric("available","Available",1250.5m,"credits"),new("frozen","Frozen",40,"credits",MetricKind.Reserved)),now)=="1,250.5 cr");
    Assert(BarPresentation.Value(d,S(new Metric("q","Remaining",72,"characters",MetricKind.Quota,100)),now)=="72%");
    Assert(BarPresentation.Value(d,S(new Metric("q","Remaining",.0001m,"characters",MetricKind.Quota,100)),now)=="0.0001%");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",0,"USD")),now)=="$0.00");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",-.0001m,"USD")),now)=="-$0.0001");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",93.38m,"CNY")),now)=="¥93.38");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",10,"USD")),now)=="$10.00");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",null,"USD")),now)=="—");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",4,"USD")) with {FromCache=true},now)=="~$4.00");
    Assert(BarPresentation.Value(d,S(new Metric("x","Balance",4,"USD")) with {Failure=FailureKind.Unauthorized},now)=="401 $4.00");
    Assert(BarPresentation.Value(d with {HoldReason="Pending contract"},S(new Metric("x","Balance",4,"USD")),now)=="Pending");
    Assert(BarPresentation.Value(d with {Id="demo"},S(new Metric("x","Balance",4,"credits")),now)=="DEMO 4 cr");
    return Task.CompletedTask;
});
await Test("Single-row placement ignores legacy expanded height and fits 100/150/200 percent DPI",()=> {
    var monitor=new MonitorInfo("main",new(0,0,1920,1080),new(0,0,1920,1040),true);
    var saved=new SavedPlacement("main",monitor.Bounds,1900,1020,280,1000);
    foreach(var scale in new[]{1d,1.5,2}){var box=Placement.ResolveBar(saved,new[]{monitor},scale,400);Assert(box.HeightDip==36 && box.WidthDip==400 && box.LeftPx+box.WidthDip*scale<=1920 && box.TopPx+box.HeightDip*scale<=1040);}
    return Task.CompletedTask;
});
await Test("Bilingual catalogue keeps format placeholders, unknown text and native numeric units",()=> {
    var catalogue=Localizer.Catalogue;
    Assert(catalogue.Count>150 && catalogue.Values.All(t=>!string.IsNullOrWhiteSpace(t)));
    Assert(catalogue.Keys.Select(Localizer.Key).Distinct().Count()==catalogue.Count);
    foreach(var (english,chinese) in catalogue){
        var placeholders=System.Text.RegularExpressions.Regex.Matches(english,@"\{\d+\}").Select(m=>m.Value).Order().ToArray();
        Assert(placeholders.SequenceEqual(System.Text.RegularExpressions.Regex.Matches(chinese,@"\{\d+\}").Select(m=>m.Value).Order()));
    }
    try {
        Localizer.SetLanguage("zh-CN");
        Assert(Localizer.T("Available")=="可用" && Localizer.T("Tripo")=="Tripo");
        Assert(Localizer.F("{0} enabled · {1} current · Preview: live account validation pending",3,2).Contains("已启用 3 个"));
        Assert(Localizer.Compact("~DEMO -0.0001 cr")=="~演示 -0.0001 积分");
        Assert(Localizer.Compact("401 4 USD")=="401 4 USD" && Localizer.Compact("0.0001%")=="0.0001%");
        Localizer.SetLanguage("en-US");Assert(Localizer.Compact("~DEMO -0.0001 cr")=="~DEMO -0.0001 cr");
    } finally {Localizer.SetLanguage("en-US");}
    return Task.CompletedTask;
});
await Test("Language survives layout reload without changing account binding or saved position",async ()=> {
    var folder=Path.Combine(root,"language");Directory.CreateDirectory(folder);
    var position=new SavedPlacement("test",new(0,0,1920,1080),100,200);
    var binding=account.Binding(tripo.Definition);
    var store=new PresentationStore(folder);
    await store.SaveAsync(new(Language:"zh-CN",Widget:new(Placement:position,CardIds:["tripo","future"])));
    var loaded=await new PresentationStore(folder).LoadAsync();
    Assert(loaded?.Language=="zh-CN" && loaded.WidgetOrDefault.Placement==position && loaded.WidgetOrDefault.CardIds!.Contains("future"));
    Assert(binding==account.Binding(tripo.Definition));
});
await Test("Legacy Tripo accounts retain the global endpoint and exact previous cache binding",async ()=> {
    var legacyJson=JsonSerializer.Serialize(new {account.ProviderId,account.SlotId,account.Label,account.Enabled,account.CredentialRef,account.CredentialRevision,account.IntervalMinutes,account.Order,account.DemoScenario});
    var legacy=JsonSerializer.Deserialize<AccountConfig>(legacyJson)!;
    var previous=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{account.ProviderId,tripo.Definition.Channel,account.SlotId,account.CredentialRef,account.CredentialRevision,account.IntervalMinutes,account.DemoScenario,Policy=tripo.Definition.Endpoint!.Policy,Schema=1})))).ToLowerInvariant();
    Assert(legacy.TripoRegion=="global" && legacy.HasUsableCredential && legacy.Binding(tripo.Definition)==previous);
    var legacyFolder=Path.Combine(root,"legacy-settings");Directory.CreateDirectory(legacyFolder);
    var legacyFile=Path.Combine(legacyFolder,"settings.json");
    await File.WriteAllTextAsync(legacyFile,JsonSerializer.Serialize(new{Schema=1,Accounts=new[]{legacy}}));
    var legacyBytes=File.ReadAllBytes(legacyFile);
    Assert((await new SettingsStore(legacyFolder).LoadAsync()).Single()==legacy && legacyBytes.SequenceEqual(File.ReadAllBytes(legacyFile)));
    var folder=Path.Combine(root,"region-reload");var store=new SettingsStore(folder);
    var china=account with {TripoRegion="china",CredentialRegion="china"};await store.SaveAsync([china]);
    using(var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder,"settings.json"))))Assert(document.RootElement.GetProperty("Schema").GetInt32()==3);
    Assert((await new SettingsStore(folder).LoadAsync()).Single()==china);
});
await Test("Tripo region selects only the approved balance host and never falls back after failures",async ()=> {
    foreach(var region in new[]{"global","china"})foreach(var status in new[]{200,401,429}){
        Uri? destination=null;
        var handler=new FakeHandler(request=>{destination=request.RequestUri;return new((HttpStatusCode)status){Content=new StringContent("""{"code":0,"data":{"balance":0,"frozen":2.25}}""",Encoding.UTF8,"application/json")};});
        var secrets=new FakeSecrets();using var client=new GuardedQueryClient(secrets,adapters.Select(a=>a.Definition),handler);
        var outcome=await tripo.RefreshAsync(account with {TripoRegion=region,CredentialRegion=region},client,CancellationToken.None);
        Assert(destination?.AbsoluteUri==(region=="china"?"https://openapi.tripo3d.com/v3/account/balance":"https://openapi.tripo3d.ai/v3/account/balance"));
        Assert(handler.Calls==1 && secrets.Reads==1 && outcome.Failure==(status==200?null:status==401?FailureKind.Unauthorized:FailureKind.RateLimited));
        if(status==200)Assert(outcome.Metrics[0].Value==0 && outcome.Metrics[1].Value==2.25m);
    }
});
await Test("Invalid region and cross-region saved keys are blocked before secret reads",async ()=> {
    foreach(var config in new[]{account with {TripoRegion="china"},account with {TripoRegion="untrusted"},account with {TripoRegion="china",CredentialRegion="untrusted"}}){
        var secrets=new FakeSecrets();var handler=new FakeHandler(_=>new(HttpStatusCode.OK));
        using var client=new GuardedQueryClient(secrets,adapters.Select(a=>a.Definition),handler);
        var result=await tripo.RefreshAsync(config,client,CancellationToken.None);
        Assert(result.Failure is FailureKind.Policy or FailureKind.CredentialMissing && secrets.Reads==0 && handler.Calls==0);
    }
    var store=new SettingsStore(Path.Combine(root,"bad-region"));bool rejected=false;
    try{await store.SaveAsync([account with {TripoRegion="untrusted"}]);}catch(InvalidDataException){rejected=true;}Assert(rejected);
    var forged=tripo.Definition with {Endpoint=tripo.Definition.Endpoint! with {Host="example.invalid"}};
    bool blocked=false;try{TripoRegions.Endpoint(forged,account with {TripoRegion="china"});}catch(QueryException e){blocked=e.Kind==FailureKind.Policy;}Assert(blocked);
});
await Test("Tripo cache is isolated by region and key version",async ()=> {
    var global=account;var china=account with {TripoRegion="china",CredentialRegion="china"};
    Assert(global.Binding(tripo.Definition)!=china.Binding(tripo.Definition));
    var store=new DiskSnapshotStore(Path.Combine(root,"regional-cache"));
    await store.SaveAsync(new(global.Binding(tripo.Definition),DataOrigin.Live,DateTimeOffset.UtcNow,[new("available","Available",123,"credits")]));
    Assert(await store.LoadAsync(china.Binding(tripo.Definition)) is null);
    Assert((await store.LoadAsync(global.Binding(tripo.Definition)))!.Metrics[0].Value==123);
    Assert(china.Binding(tripo.Definition)!=(china with {CredentialRevision=Guid.NewGuid()}).Binding(tripo.Definition));
});
await Test("Late global Tripo response cannot restore data after switching to China",async ()=> {
    var query=new RegionalBlockingQuery();await using var coordinator=new RefreshCoordinator(query,new MemoryStore());
    await coordinator.ConfigureAsync(tripo,account);
    var flight=coordinator.RefreshAsync("tripo");await query.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await coordinator.ConfigureAsync(tripo,account with {TripoRegion="china",CredentialRegion="china"});
    Assert(coordinator.Get("tripo").LastSuccess is null);
    query.Release.SetResult();await flight;
    Assert(coordinator.Get("tripo").LastSuccess is null && coordinator.Get("tripo").Config.TripoRegion=="china");
});
foreach(var repair in ProviderRepairChecks.Cases(root,adapters))await Test(repair.Name,repair.Run);
foreach(var session in WebsiteSessionChecks.Cases(root))await Test(session.Name,session.Run);
foreach(var added in NetworkVpsChecks.Cases())await Test(added.Name,added.Run);
foreach(var api in QuotaApiChecks.Cases())await Test(api.Name,api.Run);
foreach(var item in MultiAccountChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in NativeAccountChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in ClaudeRenewalChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in ClaudeProfileChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in ProtectedClaudeLoginChecks.Cases(root))await Test(item.Name,item.Run);
foreach(var item in ClaudeMaintenanceCoordinatorChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in AccountApiChecks.Cases(root,adapters))await Test(item.Name,item.Run);
foreach(var item in RuntimeDiagnosticsChecks.Cases())await Test(item.Name,item.Run);
foreach(var item in RuntimeObserverChecks.Cases())await Test(item.Name,item.Run);
foreach(var item in StorageReadFailureChecks.Cases(root))await Test(item.Name,item.Run);
await File.WriteAllTextAsync(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new {passed,failed,results},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"RESULT {passed} passed, {failed} failed; {root}");return failed==0?0:1;

sealed class FakeSecrets : ISecretStore {public int Reads;public string Read(AccountConfig config){Reads++;return "nonfunctional-canary";}}
sealed class SlowStream : Stream
{
    public override bool CanRead=>true; public override bool CanSeek=>false; public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException(); public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default) { await Task.Delay(10000,ct); return 0; }
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException(); public override void Flush()=>throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
sealed class OutcomeAdapter(string id) : IProviderAdapter
{
    public ProviderDefinition Definition {get;}=new(id,id,"test","test","#ffffff",new("api.example.invalid",443,"/balance")); public int Calls; public AdapterOutcome Outcome=AdapterOutcome.Fail(FailureKind.Unauthorized);
    public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct) {Calls++;return Task.FromResult(Outcome);}
}
sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return Task.FromResult(respond(request));}}
sealed class NoQueries : IQueryClient {public Task<byte[]> ReadAsync(ProviderDefinition d,AccountConfig a,CancellationToken ct)=>throw new InvalidOperationException();}
sealed class RegionalBlockingQuery : IQueryClient {
    public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<byte[]> ReadAsync(ProviderDefinition d,AccountConfig a,CancellationToken ct){Started.SetResult();await Release.Task;return Encoding.UTF8.GetBytes("""{"code":0,"data":{"balance":123,"frozen":0}}""");}
}
sealed class MemoryStore : ISnapshotStore {public Task<UsageSnapshot?> LoadAsync(string binding)=>Task.FromResult<UsageSnapshot?>(null);public Task SaveAsync(UsageSnapshot value)=>Task.CompletedTask;public Task RemoveAsync(string binding)=>Task.CompletedTask;}
sealed class BlockingAdapter(string id) : IProviderAdapter
{
    public ProviderDefinition Definition {get;}=new(id,id,"test","test","#ffffff",new("api.example.invalid",443,"/balance"));
    public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);public int Calls;
    public async Task<AdapterOutcome> RefreshAsync(AccountConfig c,IQueryClient q,CancellationToken ct){Calls++;Started.TrySetResult();await Release.Task;return new([new("x","x",5,"credits")]);}
}
sealed class ImmediateAdapter(string id) : IProviderAdapter
{
    public ProviderDefinition Definition {get;}=new(id,id,"test","test","#ffffff",new("api.example.invalid",443,"/balance"));public bool Fail;public int Calls;
    public Task<AdapterOutcome> RefreshAsync(AccountConfig c,IQueryClient q,CancellationToken ct){Calls++;return Task.FromResult(Fail?AdapterOutcome.Fail(FailureKind.Timeout):new AdapterOutcome([new("x","x",4,"credits")]));}
}
