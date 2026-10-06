using System.Security.Cryptography;
using System.Text.Json;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

// Separate from API keys. Every document is encrypted by Windows DPAPI and bound
// to its provider, account slot and random reference before it reaches disk.
public sealed class NativeAuthVault(string root,NativeOAuthStore? native=null,Action<Exception>? onError=null)
{
    private readonly NativeOAuthStore parser=native??new(onError:onError);
    private string PathFor(Guid reference)=>Path.Combine(root,"native-auth",reference.ToString("N")+".bin");
    private string PendingPath(string provider)
    {
        if(provider is not ("codex" or "claude"))throw new QueryException(FailureKind.Policy);
        return Path.Combine(root,"native-auth",provider+".pending.bin");
    }
    public bool HasPendingSwitch(string provider)=>File.Exists(PendingPath(provider));
    public async Task MarkSwitchPendingAsync(string provider,Guid? recovery)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(new{Schema=1,Provider=provider,RecoveryRef=recovery,StartedAt=DateTimeOffset.UtcNow});
        try{await AtomicFile.WriteAsync(PendingPath(provider),DpapiSecretStore.Protect(bytes));}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public void ClearPendingSwitch(string provider)
    {
        var path=PendingPath(provider);if(File.Exists(path))File.Delete(path);
        if(File.Exists(path+".bak"))File.Delete(path+".bak");
    }
    private sealed record Envelope(int Schema,string Provider,Guid Slot,Guid Reference,string Purpose,byte[] Document);
    public async Task<Guid> CreateAsync(string provider,Guid slot,byte[] document)
    {
        if(provider is not ("codex" or "claude"))throw new QueryException(FailureKind.Policy);
        parser.ParseDocument(provider,document);
        var reference=Guid.NewGuid();await Write(reference,new(1,provider,slot,reference,"account",document));return reference;
    }
    public byte[] ReadDocument(AccountConfig account)
    {
        if(account.NativeAuthRef is not {} reference)throw new QueryException(FailureKind.CredentialMissing);
        var envelope=Read(reference);
        if(envelope.Purpose!="account"||envelope.Provider!=account.ProviderId||envelope.Slot!=account.SlotId)
        {CryptographicOperations.ZeroMemory(envelope.Document);throw new QueryException(FailureKind.CredentialUnreadable);}
        return envelope.Document;
    }
    public NativeOAuth Read(AccountConfig account)
    {
        var document=ReadDocument(account);
        try
        {
            var credential=parser.ParseDocument(account.ProviderId,document);
            if(credential.Identity!=account.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            return credential;
        }
        finally{CryptographicOperations.ZeroMemory(document);}
    }
    // Refresh rotations are retained only for a recognized saved account. This
    // does not renew a token and does not change account settings or identity.
    public async Task UpdateRecognizedAsync(AccountConfig account,byte[] document)
    {
        if(account.NativeAuthRef is not {} reference)throw new QueryException(FailureKind.CredentialMissing);
        var old=ReadDocument(account);CryptographicOperations.ZeroMemory(old);
        if(parser.ParseDocument(account.ProviderId,document,false).Identity!=account.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
        await Write(reference,new(1,account.ProviderId,account.SlotId,reference,"account",document));
    }
    public async Task<Guid> SaveRecoveryAsync(string provider,byte[] document)
    {
        if(provider is not ("codex" or "claude")||document.Length>65536)throw new QueryException(FailureKind.Policy);
        var reference=Guid.NewGuid();await Write(reference,new(1,provider,Guid.Empty,reference,"recovery",document));return reference;
    }
    public byte[] ReadRecovery(Guid reference,string provider)
    {
        var envelope=Read(reference);
        if(envelope.Provider!=provider||envelope.Purpose!="recovery")
        {CryptographicOperations.ZeroMemory(envelope.Document);throw new QueryException(FailureKind.CredentialUnreadable);}
        return envelope.Document;
    }
    public void Remove(Guid reference)
    {
        var path=PathFor(reference);if(File.Exists(path))File.Delete(path);
        if(File.Exists(path+".bak"))File.Delete(path+".bak");
    }
    private Envelope Read(Guid reference)
    {
        byte[]? bytes=null;
        try
        {
            var path=PathFor(reference);
            if(!File.Exists(path))throw new QueryException(FailureKind.CredentialMissing);
            var length=new FileInfo(path).Length;if(length is <=0 or >131072)throw new QueryException(FailureKind.CredentialUnreadable);
            bytes=DpapiSecretStore.Unprotect(File.ReadAllBytes(path));
            var envelope=JsonSerializer.Deserialize<Envelope>(bytes);
            if(envelope is null||envelope.Schema!=1||envelope.Reference!=reference||envelope.Document.Length is <=0 or >65536)
                throw new QueryException(FailureKind.CredentialUnreadable);
            return envelope;
        }
        catch(QueryException){throw;}catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.CredentialUnreadable);}
        finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
    }
    private async Task Write(Guid reference,Envelope envelope)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(envelope);byte[]? encrypted=null;
        try{encrypted=DpapiSecretStore.Protect(bytes);await AtomicFile.WriteAsync(PathFor(reference),encrypted);}
        finally{CryptographicOperations.ZeroMemory(bytes);if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);}
    }
}
