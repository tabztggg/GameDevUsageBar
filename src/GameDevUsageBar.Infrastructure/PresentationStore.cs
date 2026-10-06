using System.Text.Json;
using GameDevUsageBar.Core.Presentation;

namespace GameDevUsageBar.Infrastructure;

public sealed class PresentationStore(string root,Action<Exception>? onError=null)
{
    private readonly string path = Path.Combine(root, "presentation.json");
    private readonly SemaphoreSlim writing = new(1, 1);
    public bool ReadOnly { get; private set; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public async Task<PresentationPreferences?> LoadAsync()
    {
        try {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path).ConfigureAwait(false));
            if(!json.RootElement.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1) throw new InvalidDataException();
            return (JsonSerializer.Deserialize<PresentationPreferences>(json, Options) ?? throw new InvalidDataException()).Validate();
        } catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException) {
            if(StorageReadPath.IsConfirmedMissing(path,out var metadataError))return null;
            ErrorObserver.Report(onError,metadataError??error);ReadOnly=true;return null;
        }
        catch(Exception error) {ErrorObserver.Report(onError,error); ReadOnly = true; return null; }
    }
    public async Task SaveAsync(PresentationPreferences preferences)
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try {
            if(ReadOnly) throw new InvalidOperationException("Layout file is preserved; reset explicitly to save changes");
            await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(preferences.Validate(), Options)).ConfigureAwait(false);
        } finally { writing.Release(); }
    }
    public async Task ResetAsync(PresentationPreferences preferences)
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try {
            if(File.Exists(path)) File.Move(path, Path.Combine(root, "presentation.unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json"));
            await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(preferences.Validate(), Options)).ConfigureAwait(false);
            ReadOnly = false;
        } finally { writing.Release(); }
    }
}
