using System.IO;
using System.Security.Cryptography;
using System.Threading;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

namespace GameDevUsageBar.App;

public sealed partial class ApplicationHost
{
    private readonly SemaphoreSlim nativeActions=new(1,1);
    private readonly HashSet<string> unknownNativeWrites=[];
    partial void NativeCredentialRemoved(Guid reference)=>Queries.NativeVault?.Remove(reference);
    public async Task<NativeAccountResult> CaptureCurrentLoginAsync(string provider,Guid slot)
    {
        if(provider is not ("codex" or "claude"))return NativeLoginSwitcher.Result(NativeAccountStatus.Unsupported);
        await nativeActions.WaitAsync();byte[]? document=null,current=null;Guid? reference=null;
        try
        {
            var config=GetAccounts(provider).Single(account=>account.SlotId==slot);
            var vault=Queries.NativeVault??throw new QueryException(FailureKind.CredentialMissing);
            document=Queries.Native.ReadDocument(provider);
            var credential=Queries.Native.ParseDocument(provider,document);
            reference=await vault.CreateAsync(provider,slot,document);
            current=Queries.Native.ReadDocument(provider);
            if(!CryptographicOperations.FixedTimeEquals(SHA256.HashData(document),SHA256.HashData(current)))return NativeLoginSwitcher.Result(NativeAccountStatus.ConcurrentChange);
            var captured=config with {Enabled=true,SourceMode="saved-oauth",CredentialSource="saved-oauth",CredentialRef=null,NativeAuthRef=reference,NativeIdentity=credential.Identity,AccountId=credential.AccountId,CredentialRevision=Guid.NewGuid()};
            await SaveAsync(captured,expectedCurrent:config);
            vault.ClearPendingSwitch(provider);
            unknownNativeWrites.Remove(provider);
            return NativeLoginSwitcher.Result(NativeAccountStatus.Captured);
        }
        catch(QueryException error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(error.Kind switch {FailureKind.CredentialExpired=>NativeAccountStatus.Expired,FailureKind.CredentialMissing=>NativeAccountStatus.Missing,_=>NativeAccountStatus.Invalid});}
        catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(NativeAccountStatus.Failed);}
        finally
        {
            if(reference is {} unused&&!Accounts.Any(account=>account.NativeAuthRef==unused))
            {try{Queries.NativeVault?.Remove(unused);}catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);/* The inaccessible encrypted blob has no settings reference. */}}
            if(document is not null)CryptographicOperations.ZeroMemory(document);
            if(current is not null)CryptographicOperations.ZeroMemory(current);
            nativeActions.Release();
        }
    }
    public async Task<NativeAccountResult> SwitchCliLoginAsync(string provider,Guid slot)
    {
        await nativeActions.WaitAsync();
        try
        {
            if(unknownNativeWrites.Contains(provider))return NativeLoginSwitcher.Result(NativeAccountStatus.Unknown);
            if(Queries.NativeVault is not {} vault)return NativeLoginSwitcher.Result(NativeAccountStatus.Missing);
            NativeAccountResult result;
            await saving.WaitAsync();
            try
            {
                // The CLI auth write must not precede a known settings failure:
                // switching also retains recognized credentials in the vault.
                if(Settings.ReadOnly)return new(NativeAccountStatus.Failed,"Account settings are read-only. CLI auth files were not changed.");
                var target=GetAccounts(provider).Single(account=>account.SlotId==slot);
                result=await new NativeLoginSwitcher(Queries.Native,vault,busyGuard:nativeCliBusyGuard,onError:error=>RuntimeLog?.RecordException("handled_exception",error)).SwitchAsync(target,GetAccounts(provider));
            }
            finally{saving.Release();}
            if(result.Status==NativeAccountStatus.Unknown)unknownNativeWrites.Add(provider);
            if(result.Succeeded)
            {
                try{await SelectAccountAsync(provider,slot);}
                catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);return result with {MessageKey="CLI auth file verified, but the displayed account could not be saved. Check the selected account in settings; do not repeat the CLI switch."};}
            }
            return result;
        }
        catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(NativeAccountStatus.Failed);}
        finally{nativeActions.Release();}
    }
}
