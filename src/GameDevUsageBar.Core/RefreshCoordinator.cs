using System.Collections.Concurrent;

namespace GameDevUsageBar.Core;

public sealed class RefreshCoordinator(IQueryClient queries, ISnapshotStore snapshots, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<AccountKey, Entry> entries = new();
    private readonly Dictionary<string, Guid> active = new(StringComparer.Ordinal);
    private readonly object selectionSync = new();
    private readonly ConcurrentDictionary<Guid, Task> flights = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? ticker;
    public event Action<string, ProviderState>? Changed;
    public event Action<string, ProviderState>? AccountChanged;
    public event Action<string, Guid>? AccountRemoved;
    public event Action<Exception>? ErrorObserved;
    private void ReportError(Exception error){try{ErrorObserved?.Invoke(error);}catch{/* Diagnostics cannot change refresh behavior. */}}
    private readonly record struct AccountKey(string ProviderId, Guid SlotId);
    private sealed class Entry(IProviderAdapter adapter, AccountConfig config)
    {
        public readonly object Sync = new();
        public IProviderAdapter Adapter = adapter;
        public ProviderState State = new(config, null, null, null, false, null);
        public Task? Flight;
        public CancellationTokenSource? Cancellation;
        public long Epoch;
        public int Failures;
        public DateTimeOffset? UsageNotBefore;
        public bool Removed;
    }
    public ProviderState Get(string id)
    {
        lock(selectionSync)return active.TryGetValue(id,out var slot)?Get(id,slot):throw new KeyNotFoundException("Provider is not configured.");
    }
    public ProviderState Get(string id,Guid slot)
    {
        var e=entries[new(id,slot)];lock(e.Sync)return WithMaintenance(e.State);
    }
    public IReadOnlyList<ProviderState> GetAccounts(string id)
    {
        lock(selectionSync)return entries.Where(pair=>pair.Key.ProviderId==id)
            .Select(pair=>{lock(pair.Value.Sync)return pair.Value.Removed?null:WithMaintenance(pair.Value.State);}).OfType<ProviderState>()
            .OrderBy(state=>state.Config.Order).ThenBy(state=>state.Config.SlotId).ToArray();
    }
    private void Notify(AccountKey key)
    {
        // Serialize selection and notifications so a completed inactive query cannot
        // enqueue an active-provider update for a different account.
        lock(selectionSync)
        {
            if(!entries.TryGetValue(key,out var e))return;
            ProviderState state;lock(e.Sync){if(e.Removed)return;state=WithMaintenance(e.State);}
            AccountChanged?.Invoke(key.ProviderId,state);
            if(active.TryGetValue(key.ProviderId,out var slot)&&slot==key.SlotId)Changed?.Invoke(key.ProviderId,state);
        }
    }
    private ProviderState WithMaintenance(ProviderState state)=>state with {AuthMaintenance=(queries as IAuthMaintenance)?.Observe(state.Config)};
    private void SelectCore(string id,Guid slot)
    {
        if(!entries.ContainsKey(new(id,slot)))throw new KeyNotFoundException("Account is not configured.");
        active[id]=slot;
        foreach(var pair in entries.Where(pair=>pair.Key.ProviderId==id))
            lock(pair.Value.Sync)pair.Value.State=pair.Value.State with {Config=pair.Value.State.Config with {IsActive=pair.Key.SlotId==slot}};
    }
    public void SelectAccount(string id,Guid slot)
    {
        lock(selectionSync)
        {
            SelectCore(id,slot);
            foreach(var key in entries.Keys.Where(key=>key.ProviderId==id))Notify(key);
        }
    }
    // Compatibility entry: this method intentionally replaces the provider's one
    // account. Multi-account callers use ConfigureAccountAsync instead.
    public async Task ConfigureAsync(IProviderAdapter adapter,AccountConfig config)
    {
        foreach(var key in entries.Keys.Where(key=>key.ProviderId==adapter.Definition.Id&&key.SlotId!=config.SlotId).ToArray())
            await RemoveAccountAsync(key.ProviderId,key.SlotId);
        await ConfigureAccountAsync(adapter,config with {IsActive=true});
    }
    public async Task ConfigureAccountAsync(IProviderAdapter adapter,AccountConfig config)
    {
        if(config.ProviderId!=adapter.Definition.Id||config.SlotId==Guid.Empty)throw new InvalidDataException("Invalid account identity.");
        var key=new AccountKey(config.ProviderId,config.SlotId);
        var e=entries.GetOrAdd(key,_=>new Entry(adapter,config));
        var binding=config.Binding(adapter.Definition);string previousBinding;long epoch;bool changed;
        lock(e.Sync)
        {
            if(e.Removed)return;
            previousBinding=e.State.Config.Binding(e.Adapter.Definition);
            // Labels and selection are presentation metadata. Keep an existing
            // account's flight, failure and backoff when only those fields change.
            changed=e.Epoch==0||previousBinding!=binding||e.State.Config.Enabled!=config.Enabled||e.Adapter.Definition!=adapter.Definition;
            if(changed)
            {
                e.Epoch++;e.Cancellation?.Cancel();e.Flight=null;e.Failures=0;e.UsageNotBefore=null;
                e.State=new(config,null,null,null,false,null);
            }
            else e.State=e.State with {Config=config};
            e.Adapter=adapter;epoch=e.Epoch;
        }
        lock(selectionSync)
        {
            if(config.IsActive||!active.ContainsKey(key.ProviderId))SelectCore(key.ProviderId,key.SlotId);
            else lock(e.Sync)e.State=e.State with {Config=e.State.Config with {IsActive=active[key.ProviderId]==key.SlotId}};
        }
        Notify(key);
        if(!changed)return;
        try
        {
            if(previousBinding!=binding)await snapshots.RemoveAsync(previousBinding);
            // Live native credentials are mutable outside this app. Validate their
            // identity in a fresh query before displaying cached data.
            var cached=config.Enabled&&config.SourceMode is not ("local-oauth" or "browser-session")?await snapshots.LoadAsync(binding):null;
            lock(e.Sync)
            {
                if(e.Removed||e.Epoch!=epoch)return;
                if(cached is not null&&cached.Binding==binding&&cached.Origin==(adapter.Definition.IsDemo?DataOrigin.Demo:DataOrigin.Live))
                    e.State=e.State with {LastSuccess=cached,FromCache=true};
            }
        }
        catch(Exception error){ReportError(error);lock(e.Sync)if(!e.Removed&&e.Epoch==epoch)e.State=e.State with {Failure=FailureKind.LocalStorage};}
        Notify(key);
    }
    public Task RefreshAsync(string id,bool manual=true)
    {
        lock(selectionSync)return active.TryGetValue(id,out var slot)?RefreshAsync(id,slot,manual):Task.CompletedTask;
    }
    public Task RefreshAsync(string id,Guid slot,bool manual=true)
    {
        var key=new AccountKey(id,slot);
        if(!entries.TryGetValue(key,out var e))return Task.CompletedTask;
        lock(e.Sync)
        {
            var state=e.State;var now=time.GetUtcNow();
            if(e.Removed||!state.Config.Enabled||!e.Adapter.Definition.CanConfigure)return Task.CompletedTask;
            if(e.Flight is {IsCompleted:false})return e.Flight;
            var authDue=(queries as IAuthMaintenance)?.BackgroundDue(state.Config)==true;
            if(!authDue&&e.UsageNotBefore>now)return Task.CompletedTask;
            if(!authDue&&state.NextAttempt>now&&(!manual||state.Failure==FailureKind.RateLimited))return Task.CompletedTask;
            if(!manual&&!authDue&&state.Failure is FailureKind.Unauthorized or FailureKind.Forbidden or FailureKind.CredentialUnreadable or FailureKind.CredentialMissing or FailureKind.CredentialExpired or FailureKind.IdentityChanged or FailureKind.AuthRenewalBusy or FailureKind.AuthRenewalUnknown or FailureKind.AuthRenewalRequired or FailureKind.ProjectRequired or FailureKind.Unsupported or FailureKind.SchemaMismatch or FailureKind.IncompleteData or FailureKind.Policy or FailureKind.BrowserVerificationRequired or FailureKind.BrowserLoginRequired or FailureKind.BrowserRuntimeMissing)return Task.CompletedTask;
            var source=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            e.Cancellation=source;e.State=state with {IsRefreshing=true,LastAttempt=now};
            var epoch=e.Epoch;var adapter=e.Adapter;var flightId=Guid.NewGuid();
            var maintenanceOnly=e.UsageNotBefore>now||state.Failure==FailureKind.RateLimited&&state.NextAttempt>now;
            var flight=Task.Run(()=>RunAsync(key,e,adapter,state.Config,epoch,source,maintenanceOnly));e.Flight=flight;flights[flightId]=flight;
            _=flight.ContinueWith(_=>flights.TryRemove(flightId,out var ignored),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            return flight;
        }
    }
    private async Task RunAsync(AccountKey key,Entry e,IProviderAdapter adapter,AccountConfig config,long epoch,CancellationTokenSource source,bool maintenanceOnly=false)
    {
        Notify(key);AdapterOutcome outcome;
        using(source)
        {
            try
            {
                if(queries is IAuthMaintenance maintenance)
                {
                    config=await maintenance.MaintainAsync(config,source.Token);
                    lock(e.Sync)
                    {
                        if(e.Removed||e.Epoch!=epoch)throw new OperationCanceledException(source.Token);
                        // Updating the binding inside this flight does not cancel
                        // or recursively reconfigure its own credential owner.
                        var changed=e.State.Config.NativeIdentity!=config.NativeIdentity;
                        e.State=e.State with {Config=e.State.Config with {NativeIdentity=config.NativeIdentity},LastSuccess=changed?null:e.State.LastSuccess,FromCache=changed?false:e.State.FromCache};
                        config=e.State.Config;
                    }
                }
                if(maintenanceOnly)
                {
                    lock(e.Sync)
                    {
                        if(e.Removed||e.Epoch!=epoch)return;
                        e.State=e.State with {IsRefreshing=false,Failure=FailureKind.RateLimited,NextAttempt=e.UsageNotBefore??e.State.NextAttempt};e.Cancellation=null;
                    }
                    Notify(key);return;
                }
                outcome=await adapter.RefreshAsync(config,queries,source.Token);
            }
            catch(QueryException error){ReportError(error);outcome=AdapterOutcome.Fail(error.Kind,error.RetryNotBefore);}
            catch(OperationCanceledException){outcome=AdapterOutcome.Fail(FailureKind.Cancelled);}
            catch(Exception error){ReportError(error);outcome=AdapterOutcome.Fail(FailureKind.SchemaMismatch);}
            UsageSnapshot? success=null;
            lock(e.Sync)
            {
                if(e.Removed||e.Epoch!=epoch){if(ReferenceEquals(e.Cancellation,source))e.Cancellation=null;return;}
                var now=time.GetUtcNow();
                if(outcome.Failure is null)
                {
                    success=new(config.Binding(e.Adapter.Definition),e.Adapter.Definition.IsDemo?DataOrigin.Demo:DataOrigin.Live,now,outcome.Metrics,outcome.Plan);
                    e.Failures=0;e.UsageNotBefore=null;e.State=e.State with {LastSuccess=success,Failure=null,IsRefreshing=false,FromCache=false,NextAttempt=now.AddMinutes(config.IntervalMinutes)};
                }
                else
                {
                    if(config.SourceMode is "local-oauth" or "saved-oauth" or "browser-session"&&outcome.Failure is FailureKind.IdentityChanged or FailureKind.CredentialMissing or FailureKind.CredentialUnreadable or FailureKind.CredentialExpired or FailureKind.AuthRenewalUnknown or FailureKind.AuthRenewalRequired or FailureKind.AuthRenewalBusy or FailureKind.BrowserLoginRequired)
                        e.State=e.State with {LastSuccess=null,FromCache=false};
                    e.Failures++;
                    var backoff=Math.Min(3600,config.IntervalMinutes*60*Math.Pow(2,Math.Min(e.Failures,5))*(0.8+Random.Shared.NextDouble()*0.4));
                    e.State=e.State with {Failure=outcome.Failure,IsRefreshing=false,NextAttempt=outcome.RetryNotBefore??now.AddSeconds(backoff)};
                    if(outcome.Failure==FailureKind.RateLimited)e.UsageNotBefore=e.State.NextAttempt;
                }
                e.Cancellation=null;
            }
            if(success is not null&&config.SourceMode is not ("local-oauth" or "browser-session"))
            {
                try
                {
                    await snapshots.SaveAsync(success);
                    bool current;lock(e.Sync)current=!e.Removed&&e.Epoch==epoch&&e.State.Config.Binding(e.Adapter.Definition)==success.Binding;
                    if(!current)await snapshots.RemoveAsync(success.Binding);
                }
                catch(Exception error){ReportError(error);lock(e.Sync)if(!e.Removed&&e.Epoch==epoch)e.State=e.State with {Failure=FailureKind.LocalStorage};}
            }
            Notify(key);
        }
    }
    public async Task RemoveAccountAsync(string id,Guid slot)
    {
        var key=new AccountKey(id,slot);string? binding=null;
        lock(selectionSync)
        {
            if(!entries.TryRemove(key,out var e))return;
            lock(e.Sync){e.Removed=true;e.Epoch++;e.Cancellation?.Cancel();binding=e.State.Config.Binding(e.Adapter.Definition);}
            if(active.TryGetValue(id,out var current)&&current==slot)
            {
                var replacement=GetAccounts(id).FirstOrDefault();
                if(replacement is null)active.Remove(id);else SelectCore(id,replacement.Config.SlotId);
            }
            AccountRemoved?.Invoke(id,slot);
            foreach(var remaining in entries.Keys.Where(account=>account.ProviderId==id))Notify(remaining);
        }
        if(binding is not null)await snapshots.RemoveAsync(binding);
    }
    public void Start()
    {
        ticker??=Task.Run(async()=>
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2),time);
            try{while(await timer.WaitForNextTickAsync(lifetime.Token))foreach(var key in entries.Keys)_=RefreshAsync(key.ProviderId,key.SlotId,false);}
            catch(OperationCanceledException){}
        });
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        foreach(var e in entries.Values)lock(e.Sync){e.Epoch++;e.Cancellation?.Cancel();}
        if(ticker is not null)await ticker;
        await Task.WhenAll(flights.Values.ToArray());lifetime.Dispose();
    }
}
