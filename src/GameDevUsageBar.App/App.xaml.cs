using System.IO;
using System.Threading;
using System.Windows;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.App.Presentation;
namespace GameDevUsageBar.App;
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private EventWaitHandle? openEvent;
    private RegisteredWaitHandle? eventWait;
    private TrayIconHost? tray;
    private ProviderStateHub? hub;
    private PresentationPreferencesService? preferences;
    private SurfaceManager? surfaces;
    private Task? exiting;
    private bool pendingOpen;
    public ApplicationHost? Host {get;private set;}
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);ThemeService.Apply();
        openEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\GameDevBar.Open");
        mutex=new Mutex(true,"Local\\GameDevBar",out var first);
        if(!first) {
            openEvent.Set();openEvent.Dispose();mutex.Dispose();
            Shutdown();return;
        }

        eventWait=ThreadPool.RegisterWaitForSingleObject(openEvent,(_,_)=> {
            if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(()=> {if(surfaces is null)pendingOpen=true;else if(exiting is null)surfaces.ShowOverview();});
        },null,Timeout.Infinite,false);
        DispatcherUnhandledException+=(_,error)=> {error.Handled=true;if(surfaces is not null)surfaces.Overview.ReportLocalError();};
        try {
            Host=new ApplicationHost(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GameDevBar"));
            await Host.InitializeAsync();
            var store=new PresentationStore(Host.Root);var loaded=await store.LoadAsync();
            var seed=Host.Accounts.OrderBy(a=>a.Order).Select(a=>a.ProviderId).ToArray();
            var initial=loaded ?? new PresentationPreferences();
            L.SetLanguage(initial.Language);
            initial=initial with {CardOrder=(initial.CardOrder ?? seed).Concat(seed).Distinct().ToArray()};
            preferences=new(store,initial);hub=new(Host.Adapters,Host.Coordinator,preferences,Dispatcher);
            var overview=new MainWindow(Host,hub,preferences);MainWindow=overview;
            surfaces=new(Host,hub,preferences,overview,()=>_ = ExitAsync());
            tray=new(surfaces,hub,preferences,()=>_ = ExitAsync());
            await Host.StartQuotaApiAsync(()=>hub.Network.Snapshot);
            if(Host.QuotaApiStatus!="Listening")overview.ReportQuotaApiError();
            Host.Coordinator.Start();
            if(!initial.StartInTray || pendingOpen)surfaces.ShowOverview();
            SessionEnding+=(_,_)=> {try {preferences.FlushAsync().Wait(TimeSpan.FromSeconds(2));}catch{}};
        } catch {
            System.Windows.MessageBox.Show(L.T("GameDevUsageBar could not initialize local files. Check access to the app data folder."),"GameDevUsageBar");
            await ExitAsync();
        }
    }
    public Task ExitAsync()=>exiting ??= ExitCoreAsync();
    private async Task ExitCoreAsync()
    {
        eventWait?.Unregister(null);openEvent?.Dispose();tray?.Dispose();
        if(preferences is not null) {try {await preferences.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));}catch{}preferences.Dispose();}
        hub?.Dispose();surfaces?.Dispose();
        if(Host is not null)await Host.DisposeAsync();
        mutex?.Dispose();Shutdown();
    }
}
public sealed partial class ApplicationHost : IAsyncDisposable
{
    public string Root { get; }
    public IReadOnlyList<IProviderAdapter> Adapters { get; }
    public SettingsStore Settings { get; }
    public DpapiSecretStore Secrets { get; }
    public ProviderQueryClient Queries { get; }
    public RefreshCoordinator Coordinator { get; }
    public QuotaApiServer? QuotaApi {get;private set;}
    public string QuotaApiStatus {get;private set;}="NotStarted";
    public async Task StartQuotaApiAsync(Func<NetworkSpeedSnapshot>? network=null)
    {
        if(QuotaApi is not null)return;
        var api=new QuotaApiServer(()=>UsageExporter.Export(Adapters,Coordinator,DateTimeOffset.UtcNow),network,()=>UsageExporter.Accounts(Adapters,Coordinator,DateTimeOffset.UtcNow));
        try {await api.StartAsync();QuotaApi=api;QuotaApiStatus="Listening";}
        catch {await api.DisposeAsync();QuotaApiStatus="Unavailable";}
    }
    public List<AccountConfig> Accounts { get; private set; } = [];
    private readonly SemaphoreSlim saving = new(1,1);
    partial void NativeCredentialRemoved(Guid reference);
    public IReadOnlyList<AccountConfig> GetAccounts(string id)=>Accounts.Where(account=>account.ProviderId==id).OrderBy(account=>account.Order).ThenBy(account=>account.SlotId).ToArray();
    public AccountConfig GetActiveAccount(string id)=>Accounts.Single(account=>account.ProviderId==id&&account.IsActive);
    public ApplicationHost(string root,IReadOnlyList<IProviderAdapter>? adapters = null,NativeOAuthStore? native = null)
    {
        Root=root; Adapters=adapters ?? ProviderCatalog.Create(); Settings=new(root); Secrets=new(root);
        var nativeStore=native??new NativeOAuthStore();
        var renewal=native is null?new NativeClaudeRenewal(root,nativeStore,CommitRenewalBindingAsync):null;
        Queries=new(Secrets,Adapters.Select(a=>a.Definition),native:nativeStore,nativeVault:new NativeAuthVault(root,nativeStore),renewal:renewal);
        Coordinator=new(Queries,new DiskSnapshotStore(root));
    }
    private async Task<AccountConfig> CommitRenewalBindingAsync(AccountConfig expected,AccountConfig proposed)
    {
        await saving.WaitAsync();
        try
        {
            var current=Accounts.SingleOrDefault(a=>a.SlotId==expected.SlotId);
            if(current is null||current.ProviderId!="claude"||!current.Enabled||current.SourceMode!="local-oauth"||current.NativeIdentity!=expected.NativeIdentity&&current.NativeIdentity!=proposed.NativeIdentity)
                throw new QueryException(FailureKind.IdentityChanged);
            if(current.NativeIdentity==proposed.NativeIdentity)return current;
            var updated=current with {NativeIdentity=proposed.NativeIdentity};
            var next=Accounts.Select(a=>a.SlotId==current.SlotId?updated:a).ToList();
            await Settings.SaveAsync(next);Accounts=next;return updated;
        }
        finally{saving.Release();}
    }
    public async Task InitializeAsync()
    {
        Accounts=await Settings.LoadAsync();
        int order=0;
        foreach(var adapter in Adapters)
        {
            var configs=GetAccounts(adapter.Definition.Id);
            if(configs.Count==0) { var created=new AccountConfig(adapter.Definition.Id,Guid.NewGuid(),"Personal",Order:order); Accounts.Add(created); configs=[created]; }
            foreach(var config in configs)await Coordinator.ConfigureAccountAsync(adapter,config);
            Coordinator.SelectAccount(adapter.Definition.Id,GetActiveAccount(adapter.Definition.Id).SlotId);
            order++;
        }
    }
    public async Task<AccountConfig> AddAccountAsync(string id,string? label=null)
    {
        var adapter=Adapters.Single(a=>a.Definition.Id==id);
        if(adapter.Definition.IsDemo||!adapter.Definition.CanConfigure)throw new InvalidOperationException("This source does not support accounts.");
        await saving.WaitAsync();
        try
        {
            var current=GetAccounts(id);
            var config=new AccountConfig(id,Guid.NewGuid(),string.IsNullOrWhiteSpace(label)?"Account "+(current.Count+1):label.Trim(),Order:current.Count==0?0:current.Max(a=>a.Order)+1,IsActive:current.Count==0);
            var next=Accounts.Append(config).ToList();await Settings.SaveAsync(next);Accounts=next;
            await Coordinator.ConfigureAccountAsync(adapter,config);return config;
        }
        finally{saving.Release();}
    }
    public async Task SelectAccountAsync(string id,Guid slot)
    {
        await saving.WaitAsync();
        try
        {
            if(!Accounts.Any(account=>account.ProviderId==id&&account.SlotId==slot))throw new KeyNotFoundException("Account is not configured.");
            if(GetActiveAccount(id).SlotId==slot)return;
            var next=Accounts.Select(account=>account.ProviderId==id?account with {IsActive=account.SlotId==slot}:account).ToList();
            await Settings.SaveAsync(next);Accounts=next;Coordinator.SelectAccount(id,slot);
        }
        finally{saving.Release();}
    }
    public async Task SaveAsync(AccountConfig config,string? newKey = null,AccountConfig? expectedCurrent=null)
    {
        config.Validate();
        if(expectedCurrent is not null)
        {
            var before=Accounts.SingleOrDefault(account=>account.SlotId==config.SlotId);
            if(before is null||before!=(expectedCurrent with {IsActive=before.IsActive}))throw new InvalidOperationException("Account changed while the credential was being captured.");
        }
        if(config.SourceMode=="local-oauth" && config.Enabled)
        {
            config=config with {NativeIdentity=Queries.Native.Read(config.ProviderId).Identity,CredentialSource="local-oauth"};
            await Queries.SourceSavedAsync(config);
        }
        if(config.ProviderId=="tripo" && config.Enabled && string.IsNullOrEmpty(newKey) && !config.HasUsableCredential)
            throw new InvalidDataException("A key saved for the selected Tripo version is required.");
        await saving.WaitAsync();
        Guid? newRef=null;
        try
        {
            var adapter=Adapters.Single(a=>a.Definition.Id==config.ProviderId);
            var old=Accounts.SingleOrDefault(account=>account.SlotId==config.SlotId);
            if(config.Enabled&&config.SourceMode=="local-oauth"&&Queries.Native.Read(config.ProviderId).Identity!=config.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            if(expectedCurrent is not null&&(old is null||old!=(expectedCurrent with {IsActive=old.IsActive})))
                throw new InvalidOperationException("Account changed while the credential was being captured.");
            if(old is not null&&old.ProviderId!=config.ProviderId)throw new InvalidDataException("An account cannot change providers.");
            // A settings editor may have been opened before a quick switch. Only an
            // explicit SelectAccountAsync call changes the current account.
            config=config with {IsActive=old?.IsActive??!Accounts.Any(account=>account.ProviderId==config.ProviderId)};
            if(!string.IsNullOrEmpty(newKey) && config.SourceMode is not ("local-oauth" or "saved-oauth" or "browser-session")) { newRef=await Secrets.CreateAsync(newKey,config.ProviderId,config.SlotId); config=config with { CredentialRef=newRef,CredentialRevision=Guid.NewGuid(),CredentialRegion=config.ProviderId=="tripo"?config.TripoRegion:null,CredentialSource=config.SourceMode,NativeIdentity=null,NativeAuthRef=null }; }
            if(config.Enabled && !config.HasUsableCredential && ProviderSources.IsExtended(config.ProviderId))throw new QueryException(FailureKind.CredentialMissing);
            var next=old is null?Accounts.Append(config).ToList():Accounts.Select(account=>account.SlotId==config.SlotId?config:account).ToList();
            // Persist new reference atomically before invalidating the old source. A crash cannot pair a new key with old binding metadata.
            await Settings.SaveAsync(next);
            Accounts=next;
            await Coordinator.ConfigureAccountAsync(adapter,config);
            if(old is not null)RemoveUnusedCredentials(old,config);
        }
        catch { if(newRef!=null && !Accounts.Any(a=>a.CredentialRef==newRef)) Secrets.Remove(newRef.Value); throw; }
        finally { saving.Release(); }
    }
    public async Task RemoveAsync(string id)
    {
        await RemoveAsync(id,GetActiveAccount(id).SlotId);
    }
    public async Task RemoveAsync(string id,Guid slot)
    {
        var config=Accounts.Single(account=>account.ProviderId==id&&account.SlotId==slot);
        await SaveAsync(config with {Enabled=false,CredentialRef=null,CredentialRevision=Guid.NewGuid(),NativeIdentity=null,NativeAuthRef=null,CredentialSource=null,SourceMode=config.SourceMode is "browser-session" or "saved-oauth"?"manual":config.SourceMode});
    }
    public async Task RemoveAccountAsync(string id,Guid slot)
    {
        await saving.WaitAsync();
        try
        {
            var old=Accounts.Single(account=>account.ProviderId==id&&account.SlotId==slot);
            var next=Accounts.Where(account=>account.SlotId!=slot).ToList();
            var remaining=next.Where(account=>account.ProviderId==id).OrderBy(account=>account.Order).ThenBy(account=>account.SlotId).ToArray();
            AccountConfig? replacement=null;
            if(remaining.Length==0)
            {
                replacement=new(id,Guid.NewGuid(),"Personal",Order:old.Order);next.Add(replacement);
            }
            else if(old.IsActive)
            {
                var selected=remaining[0].SlotId;
                next=next.Select(account=>account.ProviderId==id?account with {IsActive=account.SlotId==selected}:account).ToList();
            }
            await Settings.SaveAsync(next);Accounts=next;
            if(replacement is not null)await Coordinator.ConfigureAccountAsync(Adapters.Single(adapter=>adapter.Definition.Id==id),replacement);
            await Coordinator.RemoveAccountAsync(id,slot);
            Coordinator.SelectAccount(id,GetActiveAccount(id).SlotId);RemoveUnusedCredentials(old,null);
        }
        finally{saving.Release();}
    }
    private void RemoveUnusedCredentials(AccountConfig old,AccountConfig? replacement)
    {
        if(old.CredentialRef is {} credential&&credential!=replacement?.CredentialRef&&!Accounts.Any(account=>account.CredentialRef==credential))Secrets.Remove(credential);
        if(old.NativeAuthRef is {} native&&native!=replacement?.NativeAuthRef&&!Accounts.Any(account=>account.NativeAuthRef==native))NativeCredentialRemoved(native);
    }
    public string Diagnostics() => System.Text.Json.JsonSerializer.Serialize(new {
        app="GameDevUsageBar",version="0.9.1",framework=Environment.Version.ToString(),
        sources=Adapters.Select(a=>new { id=a.Definition.Id,channel=a.Definition.Channel,host=TripoRegions.Endpoint(a.Definition,Coordinator.Get(a.Definition.Id).Config)?.Host,status=Coordinator.Get(a.Definition.Id).Failure?.ToString(),lastAttempt=Coordinator.Get(a.Definition.Id).LastAttempt,lastSuccess=Coordinator.Get(a.Definition.Id).LastSuccess?.RetrievedAt }),
        localQuotaApi=new {status=QuotaApiStatus,address=QuotaApi?.Address},events=Queries.Events
    },new System.Text.Json.JsonSerializerOptions {WriteIndented=true});
    public async ValueTask DisposeAsync() {if(QuotaApi is not null)await QuotaApi.DisposeAsync(); await Coordinator.DisposeAsync(); Queries.Dispose(); saving.Dispose(); }
}
