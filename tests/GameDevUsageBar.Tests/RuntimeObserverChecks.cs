using System.Collections.Immutable;
using System.Net;
using System.Text;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

internal static class RuntimeObserverChecks
{
    private const string Canary="synthetic-observer-secret-canary-882641";
    private static string FixtureRoot(){var root=Path.Combine(Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT")??Path.Combine(Path.GetTempPath(),"WorkBuddy-Tasks","work","gamedevusagebar-runtime-diagnostics-20261006","workspace"),"observers-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static void Check(bool value){if(!value)throw new Exception("Runtime observer check failed");}
    private sealed class Secrets:ISecretStore{public int Reads;public string Read(AccountConfig account){Reads++;return "synthetic-nonfunctional-key";}}
    private sealed class ThrowingHandler:HttpMessageHandler
    {public int Calls;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;throw new HttpRequestException(Canary,new IOException(Canary));}}
    private sealed class BytesQuery:IQueryClient
    {public int Calls;public Task<byte[]> ReadAsync(ProviderDefinition definition,AccountConfig account,CancellationToken ct){Calls++;return Task.FromResult(Encoding.UTF8.GetBytes("{}"));}}
    private sealed class BrokenSnapshots:ISnapshotStore
    {public int Saves;public Task<UsageSnapshot?> LoadAsync(string binding)=>Task.FromResult<UsageSnapshot?>(null);public Task SaveAsync(UsageSnapshot snapshot){Saves++;throw new IOException(Canary);}public Task RemoveAsync(string binding)=>Task.CompletedTask;}
    public static IEnumerable<(string Name,Func<Task> Run)> Cases()
    {
        yield return ("Runtime observers retain corruption errors without changing preserved/read-only settings, layout, cache or credential outcomes",async()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());int observed=0;
            void Observe(Exception error){observed++;log.RecordException("handled_exception",error);throw new InvalidOperationException(Canary);}
            File.WriteAllText(Path.Combine(root,"settings.json"),"{broken"+Canary);var settings=new SettingsStore(root,Observe);Check((await settings.LoadAsync()).Count==0&&settings.ReadOnly);
            File.WriteAllText(Path.Combine(root,"presentation.json"),"{broken"+Canary);var presentation=new PresentationStore(root,Observe);Check(await presentation.LoadAsync() is null&&presentation.ReadOnly);
            var binding=new string('a',64);Directory.CreateDirectory(Path.Combine(root,"cache"));var cached=Path.Combine(root,"cache",binding+".json");File.WriteAllText(cached,"{broken"+Canary);Check(await new DiskSnapshotStore(root,Observe).LoadAsync(binding) is null);
            var config=new AccountConfig("tripo",Guid.NewGuid(),"Fixture",true,Guid.NewGuid(),Guid.NewGuid());var secrets=new DpapiSecretStore(root,Observe);
            try{secrets.Read(config);throw new Exception("Credential failure missing");}catch(QueryException error){Check(error.Kind==FailureKind.CredentialUnreadable);}
            Check(observed==4&&File.ReadAllText(cached)=="{broken"+Canary&&File.ReadAllText(Path.Combine(root,"settings.json"))=="{broken"+Canary&&log.Recent.Count(e=>e.Exception is not null)==4);
            Check(!string.Join("\n",Directory.GetFiles(log.LogDirectory,"runtime*.jsonl").Select(File.ReadAllText)).Contains(Canary,StringComparison.Ordinal));
        });
        yield return ("Runtime observers capture original network exceptions before typed remapping and cannot cause request retries when they throw",async()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());
            foreach(var id in new[]{"tripo","grsai"})
            {
                var adapter=ProviderCatalog.Create().Single(a=>a.Definition.Id==id);var config=new AccountConfig(id,Guid.NewGuid(),"Fixture",true,Guid.NewGuid(),Guid.NewGuid());var secrets=new Secrets();using var handler=new ThrowingHandler();int observed=0;
                void Observe(Exception error){observed++;Check(error is HttpRequestException);log.RecordException("handled_exception",error);throw new IOException(Canary);}
                using var queries=new ProviderQueryClient(secrets,[adapter.Definition],handler,onError:Observe);
                try{await queries.ReadAsync(adapter.Definition,config,CancellationToken.None);throw new Exception("Network failure missing");}catch(QueryException error){Check(error.Kind==FailureKind.Network);}
                Check(observed==1&&handler.Calls==1&&secrets.Reads==1&&log.Recent.Last().Exception?.Type=="System.Net.Http.HttpRequestException"&&log.Recent.Last().Exception!.Methods.Length>0);
            }
            Check(!string.Join("\n",Directory.GetFiles(log.LogDirectory,"runtime*.jsonl").Select(File.ReadAllText)).Contains(Canary,StringComparison.Ordinal));
        });
        yield return ("Runtime adapter and coordinator observers preserve schema/storage failures and last successful quotas even when observers throw",async()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());var definition=new ProviderDefinition("tripo","Fixture","API credits","Fixture","#ffffff",new("openapi.tripo3d.ai",443,"/v3/account/balance"));
            var config=new AccountConfig("tripo",Guid.NewGuid(),"Fixture",true,Guid.NewGuid(),Guid.NewGuid());var query=new BytesQuery();int parserErrors=0;
            var bad=new ApiAdapter(definition,_=>throw new FormatException(Canary));bad.ErrorObserved+=error=>{parserErrors++;log.RecordException("handled_exception",error);throw new IOException(Canary);};
            var parsed=await bad.RefreshAsync(config,query,CancellationToken.None);Check(parsed.Failure==FailureKind.SchemaMismatch&&parserErrors==1&&query.Calls==1);
            var good=new ApiAdapter(definition,_=>ImmutableArray.Create(new Metric("remaining","Remaining",100,"credits")));var snapshots=new BrokenSnapshots();int coordinatorErrors=0;
            await using var coordinator=new RefreshCoordinator(query,snapshots);coordinator.ErrorObserved+=error=>{coordinatorErrors++;log.RecordException("handled_exception",error);throw new IOException(Canary);};
            await coordinator.ConfigureAsync(good,config);await coordinator.RefreshAsync("tripo");var state=coordinator.Get("tripo");Check(state.Failure==FailureKind.LocalStorage&&state.LastSuccess?.Metrics.Single().Value==100&&snapshots.Saves==1&&coordinatorErrors==1&&query.Calls==2);
            Check(!string.Join("\n",Directory.GetFiles(log.LogDirectory,"runtime*.jsonl").Select(File.ReadAllText)).Contains(Canary,StringComparison.Ordinal));
        });
    }
}
