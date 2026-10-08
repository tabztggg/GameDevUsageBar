using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

internal static class StorageReadPath
{
    // NotFound can also mean a parent component is a file or a link target is
    // unavailable. Inspect only path metadata before permitting first-start saves.
    public static bool IsConfirmedMissing(string path,out Exception? metadataError)
    {
        metadataError=null;
        try{
            string? candidate=Path.GetFullPath(path);bool target=true;
            while(candidate is not null){
                try{
                    var attributes=File.GetAttributes(candidate);
                    return !target&&(attributes&FileAttributes.Directory)!=0&&(attributes&FileAttributes.ReparsePoint)==0;
                }catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException){}
                target=false;candidate=Path.GetDirectoryName(candidate);
            }
        }catch(Exception error){metadataError=error;}
        return false;
    }
}
public static class AtomicFile
{
    public static async Task WriteAsync(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using(var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes); stream.Flush(true); }
            if(File.Exists(path)) File.Replace(temp, path, path + ".bak", true); else File.Move(temp, path);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class SettingsStore(string root,Action<Exception>? onError=null,NativeOAuthStore? native=null)
{
    private readonly string path = Path.Combine(root, "settings.json");
    public bool ReadOnly { get; private set; }
    public async Task<List<AccountConfig>> LoadAsync()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path));
            if(settings is null || settings.Schema is not (1 or 2 or 3 or 4 or 5) || settings.Accounts is null) throw new InvalidDataException();
            if(settings.Schema<5&&settings.Accounts.Any(c=>c.ClaudeConfigDirectory is not null))throw new InvalidDataException();
            settings.Accounts.RemoveAll(c=>ProviderSources.IsRetired(c.ProviderId));
            if(settings.Schema<4 && settings.Accounts.GroupBy(c=>c.ProviderId).Any(g=>g.Count()>1))throw new InvalidDataException();
            ValidateAccounts(settings.Accounts);
            return settings.Accounts;
        }
        catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException) {
            if(StorageReadPath.IsConfirmedMissing(path,out var metadataError))return [];
            ErrorObserver.Report(onError,metadataError??error);ReadOnly=true;return [];
        }
        catch(Exception error) {ErrorObserver.Report(onError,error); ReadOnly = true; return []; }
    }
    public Task SaveAsync(IEnumerable<AccountConfig> accounts)
    {
        if(ReadOnly) throw new InvalidOperationException("Settings are unreadable. Saving is disabled.");
        var saved=accounts.Where(a=>!ProviderSources.IsRetired(a.ProviderId)).Select(a=>a.Validate()).ToList();
        ValidateAccounts(saved);
        // Older builds must refuse regional settings instead of silently sending a
        // China-bound key to their hard-coded global host.
        var schema=saved.Any(a=>a.ClaudeConfigDirectory is not null)?5:saved.GroupBy(a=>a.ProviderId).Any(g=>g.Count()>1)||saved.Any(a=>!a.IsActive||a.NativeAuthRef is not null||a.SourceMode=="saved-oauth")?4:3;
        return AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(new Settings(schema, saved), new JsonSerializerOptions { WriteIndented = true }));
    }
    private void ValidateAccounts(IReadOnlyCollection<AccountConfig> accounts)
    {
        foreach(var account in accounts)account.Validate();
        if(accounts.GroupBy(a=>a.SlotId).Any(g=>g.Count()>1)
            || accounts.GroupBy(a=>a.ProviderId).Any(g=>g.Count(a=>a.IsActive)!=1)
            || accounts.Where(a=>a.CredentialRef is not null).GroupBy(a=>a.CredentialRef).Any(g=>g.Count()>1)
            || accounts.Where(a=>a.NativeAuthRef is not null).GroupBy(a=>a.NativeAuthRef).Any(g=>g.Count()>1))
            throw new InvalidDataException("Ambiguous account configuration.");
        var profiles=accounts.Where(a=>a.ProviderId=="claude"&&a.SourceMode=="local-oauth")
            .Select(a=>Path.GetFullPath((native??new NativeOAuthStore()).PathFor(a)));
        if(profiles.GroupBy(path=>path,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
            throw new InvalidDataException("A Claude configuration directory is already bound to another account.");
    }
    private sealed record Settings(int Schema, List<AccountConfig> Accounts);
}
public interface ISecretStore { string Read(AccountConfig config); }
public sealed class DpapiSecretStore(string root,Action<Exception>? onError=null) : ISecretStore
{
    private string FilePath(Guid reference) => Path.Combine(root, "secrets", reference.ToString("N") + ".bin");
    public async Task<Guid> CreateAsync(string value, string provider, Guid slot)
    {
        if(string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl)) throw new ArgumentException("Invalid key format.");
        var reference = Guid.NewGuid();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Secret(provider, slot, value));
        try { await AtomicFile.WriteAsync(FilePath(reference), Protect(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return reference;
    }
    public string Read(AccountConfig config)
    {
        if(config.ProviderId=="tripo" && !config.HasUsableCredential)throw new QueryException(FailureKind.CredentialMissing);
        if(config.CredentialRef is not { } reference) throw new QueryException(FailureKind.CredentialMissing);
        byte[]? bytes = null;
        try
        {
            bytes = Unprotect(File.ReadAllBytes(FilePath(reference)));
            var stored = JsonSerializer.Deserialize<Secret>(bytes);
            if(stored == null || stored.Provider != config.ProviderId || stored.Slot != config.SlotId) throw new InvalidDataException();
            return stored.Value;
        }
        catch(Exception error) {ErrorObserver.Report(onError,error); throw new QueryException(FailureKind.CredentialUnreadable); }
        finally { if(bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Remove(Guid reference) { var path = FilePath(reference); if(File.Exists(path)) File.Delete(path); }
    private sealed record Secret(string Provider, Guid Slot, string Value);
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] value) => Transform(value, true);
    internal static byte[] Unprotect(byte[] value) => Transform(value, false);
    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            Blob output;
            var ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if(!ok) throw new CryptographicException("Windows could not protect or read the credential.");
            try { var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes; }
            finally { for(int i=0;i<output.Size;i++) Marshal.WriteByte(output.Data,i,0); LocalFree(output.Data); }
        }
        finally { for(int i=0;i<value.Length;i++) Marshal.WriteByte(input.Data,i,0); Marshal.FreeHGlobal(input.Data); }
    }
}
public sealed class DiskSnapshotStore(string root,Action<Exception>? onError=null) : ISnapshotStore
{
    private string FilePath(string binding)
    {
        if(binding.Length != 64 || !binding.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid binding.");
        return Path.Combine(root, "cache", binding + ".json");
    }
    public async Task<UsageSnapshot?> LoadAsync(string binding)
    {
        var path = FilePath(binding);
        try
        {
            var cache = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(path));
            if(cache is not { Schema: 1 } || cache.Value.Binding != binding || cache.Value.Metrics.IsDefault || cache.Value.RetrievedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return null;
            return cache.Value;
        }
        catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException) {
            if(StorageReadPath.IsConfirmedMissing(path,out var metadataError))return null;
            ErrorObserver.Report(onError,metadataError??error);return null;
        }
        catch(Exception error) {ErrorObserver.Report(onError,error); return null; }
    }
    public Task SaveAsync(UsageSnapshot snapshot) => AtomicFile.WriteAsync(FilePath(snapshot.Binding), JsonSerializer.SerializeToUtf8Bytes(new Cache(1, snapshot)));
    public Task RemoveAsync(string binding)
    {
        var path = FilePath(binding);
        if(File.Exists(path)) File.Delete(path);
        if(File.Exists(path+".bak")) File.Delete(path+".bak");
        return Task.CompletedTask;
    }
    private sealed record Cache(int Schema, UsageSnapshot Value);
}
