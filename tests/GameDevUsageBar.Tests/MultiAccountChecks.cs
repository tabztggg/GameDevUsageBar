using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

static class MultiAccountChecks
{
    private static void Check(bool condition,string detail="Multi-account assertion failed"){if(!condition)throw new InvalidOperationException(detail);}
    private static AccountConfig Config(string provider="multi-fixture",bool active=true)=>new(provider,Guid.NewGuid(),active?"Primary fixture":"Secondary fixture",true,IsActive:active);
    private static AdapterOutcome Balance(decimal value)=>new([new("balance","Available",value,"credits")]);
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("Legacy account settings load without changing bytes, slot, key revision or region",async()=>
        {
            foreach(var schema in new[]{1,2,3})
            {
                var directory=Path.Combine(root,"multi-legacy-"+schema);Directory.CreateDirectory(directory);var path=Path.Combine(directory,"settings.json");
                var config=Config("tripo") with {CredentialRef=Guid.NewGuid(),CredentialRevision=Guid.NewGuid(),CredentialRegion="china",TripoRegion="china"};
                // Deliberately omit the newly added fields to exercise constructor defaults.
                var legacy=new{config.ProviderId,config.SlotId,config.Label,config.Enabled,config.CredentialRef,config.CredentialRevision,config.IntervalMinutes,config.TripoRegion,config.CredentialRegion};
                await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{Schema=schema,Accounts=new[]{legacy}}));var before=await File.ReadAllBytesAsync(path);
                var settings=new SettingsStore(directory);var loaded=await settings.LoadAsync();
                Check(!settings.ReadOnly&&loaded.Count==1&&loaded[0].IsActive&&loaded[0].SlotId==config.SlotId&&loaded[0].CredentialRef==config.CredentialRef&&loaded[0].CredentialRevision==config.CredentialRevision&&loaded[0].TripoRegion=="china"&&loaded[0].CredentialRegion=="china");
                var after=await File.ReadAllBytesAsync(path);Check(before.SequenceEqual(after)&&!File.Exists(path+".bak"));
            }
        });
        yield return ("Multi-account settings schema persists one active slot and preserves ordinary schema compatibility",async()=>
        {
            var directory=Path.Combine(root,"multi-settings");var settings=new SettingsStore(directory);var first=Config("tripo");var second=Config("tripo",false) with {TripoRegion="china"};
            await settings.SaveAsync([first]);using(var single=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"settings.json"))))Check(single.RootElement.GetProperty("Schema").GetInt32()==3);
            await settings.SaveAsync([first,second]);using(var multiple=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory,"settings.json"))))Check(multiple.RootElement.GetProperty("Schema").GetInt32()==4);
            var loaded=await new SettingsStore(directory).LoadAsync();Check(loaded.SequenceEqual(new[]{first,second}));
            await settings.SaveAsync([first with {IsActive=false},second with {IsActive=true}]);loaded=await new SettingsStore(directory).LoadAsync();
            Check(loaded.Single(account=>account.IsActive).SlotId==second.SlotId&&loaded.Select(account=>account.SlotId).SequenceEqual(new[]{first.SlotId,second.SlotId}));
        });
        yield return ("Ambiguous duplicate slots, shared references and active choices stay read-only without repair writes",async()=>
        {
            var first=Config("tripo") with {CredentialRef=Guid.NewGuid()};var second=Config("tripo",false);
            var invalid=new[]{
                new[]{first,second with {IsActive=true}},new[]{first with {IsActive=false},second},new[]{first,second with {SlotId=first.SlotId}},
                new[]{first,second with {CredentialRef=first.CredentialRef}},new[]{first with {SlotId=Guid.Empty}},
                new[]{first with {NativeAuthRef=Guid.NewGuid()},second}
            };
            // The final case tests a shared native reference across independent slots.
            invalid[^1]=[invalid[^1][0],invalid[^1][1] with {NativeAuthRef=invalid[^1][0].NativeAuthRef}];
            for(var index=0;index<invalid.Length;index++)
            {
                var directory=Path.Combine(root,"multi-invalid-"+index);Directory.CreateDirectory(directory);var path=Path.Combine(directory,"settings.json");
                await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{Schema=4,Accounts=invalid[index]}));var before=await File.ReadAllBytesAsync(path);var settings=new SettingsStore(directory);
                Check((await settings.LoadAsync()).Count==0&&settings.ReadOnly&&Enumerable.SequenceEqual<byte>(before,await File.ReadAllBytesAsync(path)));
                var rejected=false;try{await settings.SaveAsync([first]);}catch(InvalidOperationException){rejected=true;}Check(rejected&&Enumerable.SequenceEqual<byte>(before,await File.ReadAllBytesAsync(path)));
            }
            var legacyDirectory=Path.Combine(root,"multi-invalid-legacy");Directory.CreateDirectory(legacyDirectory);
            await File.WriteAllTextAsync(Path.Combine(legacyDirectory,"settings.json"),JsonSerializer.Serialize(new{Schema=3,Accounts=new[]{first,second}}));var legacyStore=new SettingsStore(legacyDirectory);
            Check((await legacyStore.LoadAsync()).Count==0&&legacyStore.ReadOnly);
        });
        yield return ("Switching accounts preserves independent quota, cache and Retry-After backoff",async()=>
        {
            var adapter=new OutcomesAdapter();var first=Config();var second=Config(active:false);var memory=new MemorySnapshots();
            var retry=DateTimeOffset.UtcNow.AddDays(3);adapter.Outcomes[first.SlotId]=AdapterOutcome.Fail(FailureKind.RateLimited,retry);adapter.Outcomes[second.SlotId]=Balance(222);
            await using var coordinator=new RefreshCoordinator(new NoQueries(),memory);
            await coordinator.ConfigureAccountAsync(adapter,first);await coordinator.ConfigureAccountAsync(adapter,second);
            await coordinator.RefreshAsync(first.ProviderId,first.SlotId);await coordinator.RefreshAsync(second.ProviderId,second.SlotId);
            var secondSuccess=coordinator.Get(second.ProviderId,second.SlotId).LastSuccess;var saves=memory.Values.Count;var removals=memory.Removed.Count;
            coordinator.SelectAccount(second.ProviderId,second.SlotId);Check(coordinator.Get(second.ProviderId).LastSuccess==secondSuccess&&coordinator.GetAccounts(second.ProviderId).Count(account=>account.Config.IsActive)==1);
            coordinator.SelectAccount(first.ProviderId,first.SlotId);await coordinator.RefreshAsync(first.ProviderId);
            Check(coordinator.Get(first.ProviderId).Failure==FailureKind.RateLimited&&coordinator.Get(first.ProviderId).NextAttempt==retry&&adapter.Calls[first.SlotId]==1);
            Check(coordinator.Get(second.ProviderId,second.SlotId).LastSuccess==secondSuccess&&memory.Values.Count==saves&&memory.Removed.Count==removals);
        });
        yield return ("Parallel account queries stay single-flight and inactive completions cannot replace the selected account",async()=>
        {
            var first=Config();var second=Config(active:false);var adapter=new DelayedAdapter(first.SlotId,second.SlotId);var activeEvents=new ConcurrentQueue<Guid>();var accountEvents=new ConcurrentQueue<Guid>();
            await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemorySnapshots());
            await coordinator.ConfigureAccountAsync(adapter,first);await coordinator.ConfigureAccountAsync(adapter,second);
            var firstFlight=coordinator.RefreshAsync(first.ProviderId,first.SlotId);Check(ReferenceEquals(firstFlight,coordinator.RefreshAsync(first.ProviderId,first.SlotId)));
            var secondFlight=coordinator.RefreshAsync(second.ProviderId,second.SlotId);Check(!ReferenceEquals(firstFlight,secondFlight)&&ReferenceEquals(secondFlight,coordinator.RefreshAsync(second.ProviderId,second.SlotId)));
            await Task.WhenAll(adapter.Started[first.SlotId].Task,adapter.Started[second.SlotId].Task).WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.SelectAccount(second.ProviderId,second.SlotId);coordinator.Changed+=(_,state)=>activeEvents.Enqueue(state.Config.SlotId);coordinator.AccountChanged+=(_,state)=>accountEvents.Enqueue(state.Config.SlotId);
            adapter.Pending[second.SlotId].SetResult(Balance(222));await secondFlight;adapter.Pending[first.SlotId].SetResult(Balance(111));await firstFlight;
            Check(adapter.Calls[first.SlotId]==1&&adapter.Calls[second.SlotId]==1&&coordinator.Get(second.ProviderId).Config.SlotId==second.SlotId&&coordinator.Get(second.ProviderId).LastSuccess!.Metrics[0].Value==222);
            Check(coordinator.Get(first.ProviderId,first.SlotId).LastSuccess!.Metrics[0].Value==111&&activeEvents.All(slot=>slot==second.SlotId)&&accountEvents.Contains(first.SlotId)&&accountEvents.Contains(second.SlotId));
        });
        yield return ("Removing an account cancels its visible state and rejects a late response without touching its sibling",async()=>
        {
            var first=Config();var second=Config(active:false);var adapter=new DelayedAdapter(first.SlotId,second.SlotId);var memory=new MemorySnapshots();
            await using var coordinator=new RefreshCoordinator(new NoQueries(),memory);await coordinator.ConfigureAccountAsync(adapter,first);await coordinator.ConfigureAccountAsync(adapter,second);
            var firstFlight=coordinator.RefreshAsync(first.ProviderId,first.SlotId);var secondFlight=coordinator.RefreshAsync(second.ProviderId,second.SlotId);
            await Task.WhenAll(adapter.Started[first.SlotId].Task,adapter.Started[second.SlotId].Task).WaitAsync(TimeSpan.FromSeconds(5));
            adapter.Pending[second.SlotId].SetResult(Balance(222));await secondFlight;var sibling=coordinator.Get(second.ProviderId,second.SlotId).LastSuccess;
            await coordinator.RemoveAccountAsync(first.ProviderId,first.SlotId);adapter.Pending[first.SlotId].SetResult(Balance(111));await firstFlight;
            Check(coordinator.GetAccounts(first.ProviderId).Count==1&&coordinator.Get(first.ProviderId).Config.SlotId==second.SlotId&&coordinator.Get(second.ProviderId).LastSuccess==sibling);
            Check(!memory.Values.ContainsKey(first.Binding(adapter.Definition))&&memory.Values.ContainsKey(second.Binding(adapter.Definition)));
        });
        yield return ("Restart restores each slot's identity-bound cached quota and legacy configure still replaces one account",async()=>
        {
            var adapter=new OutcomesAdapter();var first=Config();var second=Config(active:false);var memory=new MemorySnapshots();
            memory.Values[first.Binding(adapter.Definition)]=new(first.Binding(adapter.Definition),DataOrigin.Live,DateTimeOffset.UtcNow,[new("balance","Available",111,"credits")]);
            memory.Values[second.Binding(adapter.Definition)]=new(second.Binding(adapter.Definition),DataOrigin.Live,DateTimeOffset.UtcNow,[new("balance","Available",222,"credits")]);
            await using var coordinator=new RefreshCoordinator(new NoQueries(),memory);await coordinator.ConfigureAccountAsync(adapter,first);await coordinator.ConfigureAccountAsync(adapter,second);coordinator.SelectAccount(second.ProviderId,second.SlotId);
            Check(coordinator.Get(first.ProviderId,first.SlotId).FromCache&&coordinator.Get(second.ProviderId).FromCache&&coordinator.Get(second.ProviderId).LastSuccess!.Metrics[0].Value==222&&adapter.Calls.Count==0);
            await coordinator.ConfigureAsync(adapter,first);Check(coordinator.GetAccounts(first.ProviderId).Count==1&&coordinator.Get(first.ProviderId).Config.SlotId==first.SlotId&&!memory.Values.ContainsKey(second.Binding(adapter.Definition)));
        });
        yield return ("Renaming a slot preserves its in-flight query and does not change credential binding",async()=>
        {
            var config=Config();var adapter=new DelayedAdapter(config.SlotId);await using var coordinator=new RefreshCoordinator(new NoQueries(),new MemorySnapshots());
            await coordinator.ConfigureAccountAsync(adapter,config);var flight=coordinator.RefreshAsync(config.ProviderId,config.SlotId);await adapter.Started[config.SlotId].Task.WaitAsync(TimeSpan.FromSeconds(5));
            var renamed=config with {Label="Renamed fixture"};await coordinator.ConfigureAccountAsync(adapter,renamed);
            Check(renamed.Binding(adapter.Definition)==config.Binding(adapter.Definition)&&ReferenceEquals(flight,coordinator.RefreshAsync(config.ProviderId,config.SlotId)));
            adapter.Pending[config.SlotId].SetResult(Balance(111));await flight;Check(coordinator.Get(config.ProviderId).Config.Label=="Renamed fixture"&&coordinator.Get(config.ProviderId).LastSuccess!.Metrics[0].Value==111&&adapter.Calls[config.SlotId]==1);
        });
        yield return ("DPAPI API keys remain slot-bound and deleting one credential does not delete another",async()=>
        {
            var directory=Path.Combine(root,"multi-dpapi");var secrets=new DpapiSecretStore(directory);var first=Config("tripo");var second=Config("tripo",false);
            const string firstCanary="nonfunctional-multi-account-canary-first";const string secondCanary="nonfunctional-multi-account-canary-second";
            first=first with {CredentialRef=await secrets.CreateAsync(firstCanary,first.ProviderId,first.SlotId)};second=second with {CredentialRef=await secrets.CreateAsync(secondCanary,second.ProviderId,second.SlotId)};
            Check(secrets.Read(first)==firstCanary&&secrets.Read(second)==secondCanary);
            var blocked=false;try{secrets.Read(second with {CredentialRef=first.CredentialRef});}catch(QueryException error){blocked=error.Kind==FailureKind.CredentialUnreadable;}Check(blocked);
            foreach(var file in Directory.GetFiles(Path.Combine(directory,"secrets")))
            {var encrypted=Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));Check(!encrypted.Contains(firstCanary)&&!encrypted.Contains(secondCanary));}
            secrets.Remove(first.CredentialRef!.Value);Check(secrets.Read(second)==secondCanary);
        });
    }
    private sealed class NoQueries:IQueryClient
    {public Task<byte[]> ReadAsync(ProviderDefinition definition,AccountConfig config,CancellationToken ct)=>throw new InvalidOperationException("Multi-account coordinator fixtures cannot perform a network request.");}
    private sealed class MemorySnapshots:ISnapshotStore
    {
        public ConcurrentDictionary<string,UsageSnapshot> Values {get;}=new();public ConcurrentBag<string> Removed {get;}=[];
        public Task<UsageSnapshot?> LoadAsync(string binding)=>Task.FromResult(Values.GetValueOrDefault(binding));
        public Task SaveAsync(UsageSnapshot snapshot){Values[snapshot.Binding]=snapshot;return Task.CompletedTask;}
        public Task RemoveAsync(string binding){Values.TryRemove(binding,out _);Removed.Add(binding);return Task.CompletedTask;}
    }
    private sealed class OutcomesAdapter:IProviderAdapter
    {
        public ProviderDefinition Definition {get;}=new("multi-fixture","Fixture","API","Fixture","#FFFFFF",new("fixture.invalid",443,"/usage"));
        public ConcurrentDictionary<Guid,AdapterOutcome> Outcomes {get;}=new();public ConcurrentDictionary<Guid,int> Calls {get;}=new();
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct){Calls.AddOrUpdate(config.SlotId,1,(_,count)=>count+1);return Task.FromResult(Outcomes.GetValueOrDefault(config.SlotId)??Balance(0));}
    }
    private sealed class DelayedAdapter:IProviderAdapter
    {
        public ProviderDefinition Definition {get;}=new("multi-fixture","Fixture","API","Fixture","#FFFFFF",new("fixture.invalid",443,"/usage"));
        public Dictionary<Guid,TaskCompletionSource<AdapterOutcome>> Pending {get;}=[];public Dictionary<Guid,TaskCompletionSource> Started {get;}=[];public ConcurrentDictionary<Guid,int> Calls {get;}=new();
        public DelayedAdapter(params Guid[] slots){foreach(var slot in slots){Pending[slot]=new(TaskCreationOptions.RunContinuationsAsynchronously);Started[slot]=new(TaskCreationOptions.RunContinuationsAsynchronously);}}
        // Deliberately ignore cancellation to prove epoch/removal guards reject late responses.
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config,IQueryClient queries,CancellationToken ct){Calls.AddOrUpdate(config.SlotId,1,(_,count)=>count+1);Started[config.SlotId].TrySetResult();return Pending[config.SlotId].Task;}
    }
}
