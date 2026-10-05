using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

static class AccountApiChecks
{
    private static void Check(bool condition,string detail="Account API assertion failed"){if(!condition)throw new InvalidOperationException(detail);}
    private static JsonElement Account(JsonElement list,Guid slot)=>list.EnumerateArray().Single(account=>account.GetProperty("slot_id").GetGuid()==slot);
    private static decimal Value(JsonElement usage,string metric)=>usage.GetProperty("metrics").EnumerateArray().Single(item=>item.GetProperty("id").GetString()==metric).GetProperty("value").GetDecimal();
    private static async Task<JsonDocument> Read(HttpClient client,string path)
    {
        using var response=await client.GetAsync(path);Check(response.StatusCode==HttpStatusCode.OK,"Expected account route to succeed: "+path);
        Check(response.Headers.CacheControl?.NoStore==true&&response.Headers.GetValues("X-Content-Type-Options").Single()=="nosniff");
        Check(!response.Headers.Contains("Set-Cookie")&&!response.Headers.Contains("Access-Control-Allow-Origin"));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("Account API all, provider and slot routes preserve independent quotas, errors and regions",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            using(var all=await Read(client,"/v1/accounts"))
            {
                var node=all.RootElement;var accounts=node.GetProperty("accounts");Check(node.GetProperty("schema_version").GetInt32()==1&&node.GetProperty("app").GetString()=="GameDevUsageBar"&&accounts.GetArrayLength()==6);
                var first=Account(accounts,fixture.CodexA.SlotId);var second=Account(accounts,fixture.CodexB.SlotId);var failed=Account(accounts,fixture.CodexFailure.SlotId);var disabled=Account(accounts,fixture.CodexDisabled.SlotId);
                Check(first.GetProperty("provider_id").GetString()=="codex"&&first.GetProperty("label").GetString()==fixture.CodexA.Label&&first.GetProperty("selected").GetBoolean()&&!second.GetProperty("selected").GetBoolean());
                Check(Value(first.GetProperty("usage"),"five_hour")==77&&Value(first.GetProperty("usage"),"seven_day")==55&&Value(second.GetProperty("usage"),"five_hour")==88&&Value(second.GetProperty("usage"),"seven_day")==33);
                Check(Value(second.GetProperty("usage"),"reset-credits")==2&&second.GetProperty("usage").GetProperty("metrics").EnumerateArray().Single(metric=>metric.GetProperty("id").GetString()=="reset-ticket-expiry-0").GetProperty("date_meaning").GetString()=="expiry");
                Check(first.GetProperty("usage").GetProperty("can_use_for_budget").GetBoolean()&&second.GetProperty("usage").GetProperty("can_use_for_budget").GetBoolean());
                Check(failed.GetProperty("usage").GetProperty("error").GetString()=="Unauthorized"&&!failed.GetProperty("usage").GetProperty("can_use_for_budget").GetBoolean()&&failed.GetProperty("usage").GetProperty("metrics").GetArrayLength()==0);
                Check(!disabled.GetProperty("usage").GetProperty("enabled").GetBoolean()&&disabled.GetProperty("usage").GetProperty("freshness").GetString()=="disabled"&&disabled.GetProperty("usage").GetProperty("metrics").GetArrayLength()==0);
                var global=Account(accounts,fixture.TripoGlobal.SlotId).GetProperty("usage");var china=Account(accounts,fixture.TripoChina.SlotId).GetProperty("usage");
                Check(global.GetProperty("region").GetString()=="global"&&china.GetProperty("region").GetString()=="china"&&Value(global,"available")==5440&&Value(china,"available")==12345.678901234m);
            }
            using(var provider=await Read(client,"/v1/usage/codex/accounts"))
            {
                var accounts=provider.RootElement.GetProperty("accounts");Check(accounts.GetArrayLength()==4&&accounts.EnumerateArray().All(account=>account.GetProperty("provider_id").GetString()=="codex")&&accounts.EnumerateArray().Count(account=>account.GetProperty("selected").GetBoolean())==1);
            }
            using(var slot=await Read(client,"/v1/usage/codex/accounts/"+fixture.CodexB.SlotId.ToString("D")))
            {
                var account=slot.RootElement.GetProperty("account");Check(account.GetProperty("slot_id").GetGuid()==fixture.CodexB.SlotId&&Value(account.GetProperty("usage"),"five_hour")==88&&!account.GetProperty("selected").GetBoolean());
            }
            fixture.AssertNoQuerySideEffects();
        });
        yield return ("Selected-provider API compatibility follows quick selection while account routes retain every slot",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            using(var original=await Read(client,"/v1/usage/codex"))Check(Value(original.RootElement.GetProperty("provider"),"five_hour")==77&&original.RootElement.GetProperty("schema_version").GetInt32()==1&&original.RootElement.GetProperty("shared_account_state").GetBoolean());
            fixture.Coordinator.SelectAccount("codex",fixture.CodexB.SlotId);
            using(var selected=await Read(client,"/v1/usage/codex"))Check(Value(selected.RootElement.GetProperty("provider"),"five_hour")==88&&selected.RootElement.GetProperty("provider").GetProperty("auth_source").GetString()=="saved-oauth");
            using(var usage=await Read(client,"/v1/usage"))
            {
                var providers=usage.RootElement.GetProperty("providers");Check(providers.GetArrayLength()==2&&Value(providers.EnumerateArray().Single(provider=>provider.GetProperty("id").GetString()=="codex"),"five_hour")==88);
                Check(!usage.RootElement.TryGetProperty("accounts",out _)&&providers.EnumerateArray().All(provider=>!provider.TryGetProperty("slot_id",out _)&&!provider.TryGetProperty("label",out _)));
            }
            using(var all=await Read(client,"/v1/accounts"))
            {
                var accounts=all.RootElement.GetProperty("accounts");Check(Account(accounts,fixture.CodexB.SlotId).GetProperty("selected").GetBoolean()&&!Account(accounts,fixture.CodexA.SlotId).GetProperty("selected").GetBoolean()&&Value(Account(accounts,fixture.CodexA.SlotId).GetProperty("usage"),"five_hour")==77);
            }
            fixture.AssertNoQuerySideEffects();
        });
        yield return ("Account API DTOs expose account labels and slots but never secret references, identities or native login fields",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            foreach(var path in new[]{"/v1/accounts","/v1/usage/codex/accounts","/v1/usage/codex/accounts/"+fixture.CodexB.SlotId.ToString("D"),"/v1/usage","/v1/usage/codex"})
            {
                using var response=await client.GetAsync(path);Check(response.StatusCode==HttpStatusCode.OK);var json=await response.Content.ReadAsStringAsync();using var document=JsonDocument.Parse(json);
                AssertSafeFields(document.RootElement);
                foreach(var config in fixture.Configs)
                {
                    foreach(var privateValue in new[]{config.CredentialRef?.ToString("D"),config.CredentialRevision?.ToString("D"),config.NativeAuthRef?.ToString("D"),config.NativeIdentity,config.AccountId,config.OrganizationId,config.Binding(fixture.Adapters.Single(adapter=>adapter.Definition.Id==config.ProviderId).Definition)})
                        if(!string.IsNullOrEmpty(privateValue))Check(!json.Contains(privateValue,StringComparison.Ordinal));
                }
                if(path=="/v1/accounts")Check(json.Contains(fixture.CodexA.Label,StringComparison.Ordinal)&&json.Contains(fixture.CodexB.SlotId.ToString("D"),StringComparison.Ordinal));
            }
            fixture.AssertNoQuerySideEffects();
        });
        yield return ("Every account API route rejects mutation methods before exporting a snapshot",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            foreach(var path in Paths(fixture))foreach(var method in new[]{HttpMethod.Post,HttpMethod.Put,HttpMethod.Delete,HttpMethod.Patch,HttpMethod.Head})
            {
                using var request=new HttpRequestMessage(method,path);using var response=await client.SendAsync(request);Check(response.StatusCode==HttpStatusCode.MethodNotAllowed);
            }
            Check(fixture.AccountExports==0&&fixture.SelectedExports==0);fixture.AssertNoQuerySideEffects();
        });
        yield return ("Account API rejects browser Origin, cross-site fetch and forged Host on all account routes",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);var uri=new Uri(server.Address!);Check(uri.Host=="127.0.0.1");
            foreach(var path in Paths(fixture))foreach(var header in new (string Name,string Value)[]{("Origin","https://example.invalid"),("Origin","null"),("Sec-Fetch-Site","cross-site"),("Host","example.invalid:"+uri.Port),("Host","localhost:"+uri.Port),("Host","127.0.0.1:"+(uri.Port==65535?1:uri.Port+1))})
            {
                using var request=new HttpRequestMessage(HttpMethod.Get,path);Check(request.Headers.TryAddWithoutValidation(header.Name,header.Value));using var response=await client.SendAsync(request);
                Check(response.StatusCode==HttpStatusCode.Forbidden&&!response.Headers.Contains("Access-Control-Allow-Origin"));
            }
            Check(fixture.AccountExports==0&&fixture.SelectedExports==0);fixture.AssertNoQuerySideEffects();
        });
        yield return ("Account API rejects refresh or account-selection query parameters without invoking exports",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            foreach(var path in Paths(fixture))foreach(var suffix in new[]{"?refresh=true","?account="+fixture.CodexB.SlotId.ToString("D"),"?region=china"})
            {
                using var response=await client.GetAsync(path+suffix);Check(response.StatusCode==HttpStatusCode.BadRequest);
            }
            Check(fixture.AccountExports==0&&fixture.SelectedExports==0);fixture.AssertNoQuerySideEffects();
        });
        yield return ("Account API uses typed missing-provider, missing-slot and unavailable-source responses",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            foreach(var sample in new (string Path,string Error)[]{
                ("/v1/usage/unknown-provider/accounts","provider_not_found"),
                ("/v1/usage/codex/accounts/"+Guid.NewGuid().ToString("D"),"account_not_found"),
                ("/v1/usage/codex/accounts/"+fixture.TripoGlobal.SlotId.ToString("D"),"account_not_found"),
                ("/v1/usage/codex/accounts/not-a-guid","account_not_found"),
                ("/v1/usage/codex/accounts/"+fixture.CodexA.SlotId.ToString("N"),"account_not_found")})
            {
                using var response=await client.GetAsync(sample.Path);Check(response.StatusCode==HttpStatusCode.NotFound);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Check(body.RootElement.GetProperty("error").GetString()==sample.Error);
            }
            await using var absent=new QuotaApiServer(fixture.ExportSelected);await absent.StartAsync(0);using var absentClient=Client(absent);
            foreach(var path in Paths(fixture))
            {
                using var response=await absentClient.GetAsync(path);Check(response.StatusCode==HttpStatusCode.ServiceUnavailable);using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());Check(body.RootElement.GetProperty("error").GetString()=="accounts_unavailable");
            }
            using(var compatible=await Read(absentClient,"/v1/usage/codex"))Check(Value(compatible.RootElement.GetProperty("provider"),"five_hour")==77);
            await using var unavailable=new QuotaApiServer(fixture.ExportSelected,accounts:()=>throw new InvalidOperationException("nonfunctional-private-exception-canary"));await unavailable.StartAsync(0);using var unavailableClient=Client(unavailable);
            using(var response=await unavailableClient.GetAsync("/v1/accounts"))
            {Check(response.StatusCode==HttpStatusCode.ServiceUnavailable);var text=await response.Content.ReadAsStringAsync();Check(!text.Contains("nonfunctional-private-exception-canary",StringComparison.Ordinal));using var body=JsonDocument.Parse(text);Check(body.RootElement.GetProperty("error").GetString()=="snapshot_unavailable");}
            fixture.AssertNoQuerySideEffects();
        });
        yield return ("Concurrent account API reads neither refresh providers nor modify settings, caches or selection",async()=>
        {
            await using var fixture=await Fixture.Create(root,adapters);await using var server=fixture.Server();await server.StartAsync(0);using var client=Client(server);
            var selectedBefore=fixture.Coordinator.Get("codex").Config.SlotId;var snapshotsBefore=fixture.Configs.ToDictionary(config=>config.SlotId,config=>fixture.Coordinator.Get(config.ProviderId,config.SlotId));
            using(var health=await Read(client,"/v1/health"))Check(health.RootElement.GetProperty("read_only").GetBoolean()&&fixture.AccountExports==0&&fixture.SelectedExports==0);
            var paths=Paths(fixture).Concat(new[]{"/v1/usage","/v1/usage/codex"}).ToArray();await Task.WhenAll(Enumerable.Range(0,12).Select(async index=>{using var result=await Read(client,paths[index%paths.Length]);}));
            Check(fixture.AccountExports>0&&fixture.SelectedExports>0&&fixture.Coordinator.Get("codex").Config.SlotId==selectedBefore);
            foreach(var config in fixture.Configs)Check(fixture.Coordinator.Get(config.ProviderId,config.SlotId)==snapshotsBefore[config.SlotId]);
            fixture.AssertNoQuerySideEffects();
        });
    }
    private static string[] Paths(Fixture fixture)=>["/v1/accounts","/v1/usage/codex/accounts","/v1/usage/codex/accounts/"+fixture.CodexB.SlotId.ToString("D")];
    private static HttpClient Client(QuotaApiServer server)=>new(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new(server.Address!),Timeout=TimeSpan.FromSeconds(5)};
    private static void AssertSafeFields(JsonElement node)
    {
        if(node.ValueKind==JsonValueKind.Object)foreach(var field in node.EnumerateObject())
        {
            Check(field.Name is not ("binding" or "credential_ref" or "credential_revision" or "native_auth_ref" or "native_identity" or "account_id" or "organization_id" or "project_id" or "credential_source" or "access_token" or "refresh_token" or "id_token" or "tokens" or "secret" or "authorization" or "cookie"));AssertSafeFields(field.Value);
        }
        else if(node.ValueKind==JsonValueKind.Array)foreach(var value in node.EnumerateArray())AssertSafeFields(value);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public AccountConfig CodexA {get;}=new("codex",Guid.NewGuid(),"Fixture Codex Pro",true,Guid.NewGuid(),Guid.NewGuid(),AccountId:"private-account-a",NativeIdentity:new string('a',64),CredentialSource:"manual");
        public AccountConfig CodexB {get;}=new("codex",Guid.NewGuid(),"Fixture Codex Plus",true,CredentialRevision:Guid.NewGuid(),SourceMode:"saved-oauth",AccountId:"private-account-b",NativeIdentity:new string('b',64),CredentialSource:"saved-oauth",IsActive:false,NativeAuthRef:Guid.NewGuid());
        public AccountConfig CodexFailure {get;}=new("codex",Guid.NewGuid(),"Fixture rejected account",true,Guid.NewGuid(),Guid.NewGuid(),IsActive:false);
        public AccountConfig CodexDisabled {get;}=new("codex",Guid.NewGuid(),"Fixture disabled account",false,Guid.NewGuid(),Guid.NewGuid(),IsActive:false);
        public AccountConfig TripoGlobal {get;}=new("tripo",Guid.NewGuid(),"Fixture global API",true,Guid.NewGuid(),Guid.NewGuid());
        public AccountConfig TripoChina {get;}=new("tripo",Guid.NewGuid(),"Fixture China API",true,Guid.NewGuid(),Guid.NewGuid(),TripoRegion:"china",CredentialRegion:"china",IsActive:false);
        public AccountConfig[] Configs=>[CodexA,CodexB,CodexFailure,CodexDisabled,TripoGlobal,TripoChina];
        public IReadOnlyList<IProviderAdapter> Adapters {get;private set;}=[];
        public RefreshCoordinator Coordinator {get;private set;}=null!;
        public int SelectedExports=>selectedExports;public int AccountExports=>accountExports;
        private int selectedExports,accountExports,baselineCalls,baselineSaves,baselineRemovals;
        private readonly NoQueries queries=new();private readonly MemorySnapshots memory=new();private string settingsPath="";private byte[] settingsBytes=[];private DateTime settingsWriteTime;
        public static async Task<Fixture> Create(string root,IReadOnlyList<IProviderAdapter> catalog)
        {
            var fixture=new Fixture();var now=DateTimeOffset.UtcNow;
            var codex=new FixtureAdapter(catalog.Single(adapter=>adapter.Definition.Id=="codex").Definition);
            codex.Outcomes[fixture.CodexA.SlotId]=new([new("five_hour","5-hour remaining",77,"%",MetricKind.Quota,100,now.AddHours(2),WindowSeconds:18000),new("seven_day","Weekly remaining",55,"%",MetricKind.Quota,100,now.AddDays(3),WindowSeconds:604800)],Plan:"pro");
            codex.Outcomes[fixture.CodexB.SlotId]=new([new("five_hour","5-hour remaining",88,"%",MetricKind.Quota,100,now.AddHours(4),WindowSeconds:18000),new("seven_day","Weekly remaining",33,"%",MetricKind.Quota,100,now.AddDays(6),WindowSeconds:604800),new("reset-credits","Reset credits",2,"tickets",MetricKind.Count),new("reset-ticket-expiry-0","Reset credit expiry",2,"tickets",MetricKind.Count,ResetAt:now.AddDays(10),DateMeaning:"expiry")],Plan:"plus");
            codex.Outcomes[fixture.CodexFailure.SlotId]=AdapterOutcome.Fail(FailureKind.Unauthorized);
            var tripo=new FixtureAdapter(catalog.Single(adapter=>adapter.Definition.Id=="tripo").Definition);
            tripo.Outcomes[fixture.TripoGlobal.SlotId]=new([new("available","Available",5440,"credits"),new("frozen","Frozen",0,"credits",MetricKind.Reserved)]);
            tripo.Outcomes[fixture.TripoChina.SlotId]=new([new("available","Available",12345.678901234m,"credits"),new("frozen","Frozen",12,"credits",MetricKind.Reserved)]);
            fixture.Adapters=[codex,tripo];fixture.Coordinator=new(fixture.queries,fixture.memory);
            foreach(var config in fixture.Configs)await fixture.Coordinator.ConfigureAccountAsync(fixture.Adapters.Single(adapter=>adapter.Definition.Id==config.ProviderId),config);
            foreach(var config in fixture.Configs)await fixture.Coordinator.RefreshAsync(config.ProviderId,config.SlotId);
            var directory=Path.Combine(root,"account-api-"+Guid.NewGuid().ToString("N"));await new SettingsStore(directory).SaveAsync(fixture.Configs);fixture.settingsPath=Path.Combine(directory,"settings.json");fixture.settingsBytes=await File.ReadAllBytesAsync(fixture.settingsPath);fixture.settingsWriteTime=File.GetLastWriteTimeUtc(fixture.settingsPath);
            fixture.baselineCalls=fixture.Adapters.Cast<FixtureAdapter>().Sum(adapter=>adapter.Calls);fixture.baselineSaves=fixture.memory.Saves;fixture.baselineRemovals=fixture.memory.Removals;
            return fixture;
        }
        public UsageExport ExportSelected(){Interlocked.Increment(ref selectedExports);return UsageExporter.Export(Adapters,Coordinator,DateTimeOffset.UtcNow);}
        public AccountUsageExport ExportAccounts(){Interlocked.Increment(ref accountExports);return UsageExporter.Accounts(Adapters,Coordinator,DateTimeOffset.UtcNow);}
        public QuotaApiServer Server()=>new(ExportSelected,accounts:ExportAccounts);
        public void AssertNoQuerySideEffects()
        {
            Check(queries.Calls==0&&Adapters.Cast<FixtureAdapter>().Sum(adapter=>adapter.Calls)==baselineCalls&&memory.Saves==baselineSaves&&memory.Removals==baselineRemovals);
            Check(settingsBytes.SequenceEqual(File.ReadAllBytes(settingsPath))&&File.GetLastWriteTimeUtc(settingsPath)==settingsWriteTime&&!File.Exists(settingsPath+".bak"));
        }
        public ValueTask DisposeAsync()=>Coordinator.DisposeAsync();
    }
    private sealed class NoQueries:IQueryClient
    {
        public int Calls;public Task<byte[]> ReadAsync(ProviderDefinition definition,AccountConfig config,CancellationToken ct){Interlocked.Increment(ref Calls);throw new InvalidOperationException("Account API fixture cannot send an external query.");}
    }
    private sealed class FixtureAdapter(ProviderDefinition definition):IProviderAdapter
    {
        public ProviderDefinition Definition {get;}=definition;public Dictionary<Guid,AdapterOutcome> Outcomes {get;}=[];public int Calls;
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct){Interlocked.Increment(ref Calls);return Task.FromResult(Outcomes[config.SlotId]);}
    }
    private sealed class MemorySnapshots:ISnapshotStore
    {
        private readonly ConcurrentDictionary<string,UsageSnapshot> snapshots=new();public int Saves,Removals;
        public Task<UsageSnapshot?> LoadAsync(string binding)=>Task.FromResult(snapshots.GetValueOrDefault(binding));
        public Task SaveAsync(UsageSnapshot snapshot){Interlocked.Increment(ref Saves);snapshots[snapshot.Binding]=snapshot;return Task.CompletedTask;}
        public Task RemoveAsync(string binding){Interlocked.Increment(ref Removals);snapshots.TryRemove(binding,out _);return Task.CompletedTask;}
    }
}
