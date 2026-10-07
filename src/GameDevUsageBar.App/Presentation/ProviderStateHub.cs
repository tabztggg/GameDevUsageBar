using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.App.Presentation;

public sealed class ProviderStateHub : IDisposable
{
    private readonly RefreshCoordinator coordinator;
    private readonly Dispatcher dispatcher;
    private readonly PresentationPreferencesService preferences;
    private readonly Dictionary<string, CardModel> byId;
    private readonly Dictionary<(string ProviderId,Guid SlotId),CardModel> accountModels=[];
    private readonly DispatcherTimer clock;
    private bool disposed;
    public ObservableCollection<CardModel> Models { get; } = [];
    public NetworkUsageModel Network {get;}=new();
    public event Action? Changed;
    public ProviderStateHub(IReadOnlyList<IProviderAdapter> adapters, RefreshCoordinator coordinator,
        PresentationPreferencesService preferences, Dispatcher dispatcher)
    {
        this.coordinator = coordinator; this.preferences = preferences; this.dispatcher = dispatcher;
        byId = adapters.ToDictionary(a => a.Definition.Id, a => new CardModel(a.Definition, coordinator.Get(a.Definition.Id)));
        foreach(var id in byId.Keys)SynchronizeAccounts(id);
        ApplyOrder();
        coordinator.Changed += StateChanged;
        coordinator.AccountChanged += AccountChanged;
        coordinator.AccountRemoved += AccountRemoved;
        preferences.Changed += PreferencesChanged;
        L.Changed+=LanguageChanged;
        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => {
            foreach(var m in accountModels.Values.Concat(Models))m.Tick();
            foreach(var id in byId.Keys)RefreshAccountChoices(id);
            var w=preferences.Current.WidgetOrDefault;Network.Update(w.NetworkAdapterId,true);
        }, dispatcher);
        clock.Start();
    }
    private void StateChanged(string id, ProviderState state)
    {
        if(disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => {
            if(disposed || !byId.TryGetValue(id, out var model)) return;
            // A queued provider event may belong to the previously selected
            // slot. It must not replace the newly selected account's values.
            var current=coordinator.Get(id);
            if(state.Config.SlotId!=current.Config.SlotId)return;
            model.Update(current);SynchronizeAccounts(id);Changed?.Invoke();
        });
    }
    private void AccountChanged(string id,ProviderState state)
    {
        if(disposed || dispatcher.HasShutdownStarted)return;
        dispatcher.BeginInvoke(() => {
            if(disposed || !byId.ContainsKey(id))return;
            SynchronizeAccounts(id);Changed?.Invoke();
        });
    }
    private void AccountRemoved(string id,Guid slotId)
    {
        if(disposed || dispatcher.HasShutdownStarted)return;
        dispatcher.BeginInvoke(() => {
            if(disposed || !byId.ContainsKey(id))return;
            accountModels.Remove((id,slotId));
            SynchronizeAccounts(id);Changed?.Invoke();
        });
    }
    private void SynchronizeAccounts(string id)
    {
        var model=byId[id];
        var states=coordinator.GetAccounts(id);
        var present=states.Select(s=>s.Config.SlotId).ToHashSet();
        foreach(var key in accountModels.Keys.Where(k=>k.ProviderId==id && !present.Contains(k.SlotId)).ToArray())accountModels.Remove(key);
        foreach(var state in states)
        {
            var key=(id,state.Config.SlotId);
            if(accountModels.TryGetValue(key,out var account))account.Update(state);
            else accountModels.Add(key,new CardModel(model.Definition,state));
        }
        model.Update(coordinator.Get(id));
        RefreshAccountChoices(id);
    }
    private void RefreshAccountChoices(string id)
    {
        var active=byId[id];
        var states=coordinator.GetAccounts(id);
        var choices=states.Select(state=> {
            var account=accountModels.GetValueOrDefault((id,state.Config.SlotId));
            return new AccountChoice(state.Config.SlotId,state.Config.Label,account?.PrimarySummary??"",account?.CompactBadge??"",state.Config.SlotId==active.SlotId);
        }).ToArray();
        var savedSlots=states.Where(s=>s.Config.SourceMode=="saved-oauth" && s.Config.NativeAuthRef is not null).Select(s=>s.Config.SlotId).ToHashSet();
        var savedCli=choices.Where(c=>savedSlots.Contains(c.SlotId)).ToArray();
        active.SetAccountChoices(choices,savedCli);
        foreach(var account in accountModels.Where(kv=>kv.Key.ProviderId==id).Select(kv=>kv.Value))account.SetAccountChoices(choices,savedCli);
    }
    public IReadOnlyList<CardModel> GetAccountModels(string id)=>Array.AsReadOnly(coordinator.GetAccounts(id)
        .Select(state=>accountModels.GetValueOrDefault((id,state.Config.SlotId))).OfType<CardModel>().ToArray());
    private void PreferencesChanged()
    {
        if(disposed || dispatcher.HasShutdownStarted) return;
        if(!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(PreferencesChanged); return; }
        ApplyOrder();Network.RefreshLanguage(); Changed?.Invoke();
    }
    private void ApplyOrder()
    {
        var ids = preferences.Current.CardOrder ?? [];
        var ordered = byId.Values.OrderBy(m => {
            var index = Array.IndexOf(ids, m.Id); return index < 0 ? int.MaxValue : index;
        }).ThenBy(m => m.State.Config.Order).ToList();
        if(Models.SequenceEqual(ordered)) return;
        Models.Clear(); foreach(var m in ordered) Models.Add(m);
    }
    public ICollectionView CreateView(Func<CardModel, bool> filter) => new ListCollectionView(Models) { Filter = item => filter((CardModel)item) };
    private void LanguageChanged()
    {
        if(disposed || dispatcher.HasShutdownStarted)return;
        if(!dispatcher.CheckAccess()){dispatcher.BeginInvoke(LanguageChanged);return;}
        foreach(var model in accountModels.Values.Concat(Models))model.RefreshLanguage();
        foreach(var id in byId.Keys)RefreshAccountChoices(id);
        Network.RefreshLanguage();
        Changed?.Invoke();
    }
    public Task RequestManualRefresh(string id) => coordinator.RefreshAsync(id);
    public Task RequestManualRefresh(string id,Guid slotId)=>coordinator.RefreshAsync(id,slotId,true);
    public void RefreshAll() { foreach(var id in byId.Keys)foreach(var account in coordinator.GetAccounts(id)) _ = RequestManualRefresh(id,account.Config.SlotId); }
    public void Dispose()
    {
        if(disposed) return; disposed = true;
        coordinator.Changed -= StateChanged;coordinator.AccountChanged -= AccountChanged;coordinator.AccountRemoved -= AccountRemoved;
        preferences.Changed -= PreferencesChanged;L.Changed-=LanguageChanged;clock.Stop();
    }
}
