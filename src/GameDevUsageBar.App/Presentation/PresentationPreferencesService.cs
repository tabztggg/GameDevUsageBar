using System.Threading;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

namespace GameDevUsageBar.App.Presentation;

// Deliberately has no account, coordinator, credential or query-client reference.
public sealed class PresentationPreferencesService(PresentationStore store, PresentationPreferences initial,Action<Exception>? onError=null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? debounce;
    private long version, savedVersion;
    private bool disposed;
    public PresentationPreferences Current { get; private set; } = initial.Validate();
    public string Notice { get; private set; } = store.ReadOnly
        ? "Layout preferences could not be read. Session changes are allowed; the original file is preserved." : "";
    public bool ReadOnly => store.ReadOnly;
    public event Action? Changed;
    public void Update(Func<PresentationPreferences, PresentationPreferences> change)
    {
        if(disposed) return;
        var previousLanguage=Current.Language;
        Current = change(Current).Validate();
        if(previousLanguage!=Current.Language)Localizer.SetLanguage(Current.Language);
        Interlocked.Increment(ref version);
        Changed?.Invoke();
        debounce?.Cancel(); debounce?.Dispose(); debounce = new();
        _ = SaveLaterAsync(debounce.Token);
    }
    private async Task SaveLaterAsync(CancellationToken token)
    {
        try { await Task.Delay(500, token).ConfigureAwait(false); await FlushAsync().ConfigureAwait(false); }
        catch(OperationCanceledException) { }
        catch(Exception error) {ReportError(error);}
    }
    public async Task FlushAsync()
    {
        if(store.ReadOnly || Interlocked.Read(ref version) == Interlocked.Read(ref savedVersion)) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            var savingVersion = Interlocked.Read(ref version);
            if(savingVersion == Interlocked.Read(ref savedVersion)) return;
            var snapshot = Current;
            await store.SaveAsync(snapshot).ConfigureAwait(false);
            Interlocked.Exchange(ref savedVersion, savingVersion);
            if(!disposed && Notice.Length != 0) { Notice = ""; Changed?.Invoke(); }
        } catch(Exception error) {
            ReportError(error);
            Notice = "Layout could not be saved. Current session changes remain active; no automatic retry is scheduled.";
            if(!disposed) Changed?.Invoke();
        } finally { gate.Release(); }
    }
    public async Task ResetAsync()
    {
        // Reset is only invoked after the explicit reset confirmation in Display settings.
        var defaults = new PresentationPreferences(Widget: new(),Language:Current.Language);
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            await store.ResetAsync(defaults).ConfigureAwait(false);
            Current = defaults; Interlocked.Increment(ref version); Interlocked.Exchange(ref savedVersion, Interlocked.Read(ref version));
            Notice = "";
        } catch(Exception error) {ReportError(error);throw;}
        finally { gate.Release(); }
        Changed?.Invoke();
    }
    private void ReportError(Exception error)
    {
        try{onError?.Invoke(error);}catch{/* Reporting an error must not change the persistence outcome. */}
    }
    public void Dispose() { disposed = true; debounce?.Cancel(); debounce?.Dispose(); }
}
