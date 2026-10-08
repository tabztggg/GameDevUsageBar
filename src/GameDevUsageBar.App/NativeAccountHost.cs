using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json;
using System.Collections.Concurrent;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

namespace GameDevUsageBar.App;

public sealed partial class ApplicationHost
{
    private readonly SemaphoreSlim nativeActions=new(1,1);
    private readonly HashSet<string> unknownNativeWrites=[];
    private readonly HashSet<Guid> unknownClaudeLogins=[];
    private readonly Dictionary<Guid,string?> claudeLoginBaselines=[];
    private readonly ConcurrentDictionary<Guid,byte> pausedClaudeLogins=[];
    private const string DuplicateClaudeLoginMessage="This login uses the same saved credential identity as another Claude account. Check the intended account on the authorization page. The selected account binding was not changed.";
    internal Func<AccountConfig,CancellationToken,Task<ClaudeLoginOutcome>>? ClaudeLoginFixture {get;set;}

    internal bool IsClaudeLoginUnknown(Guid slot)
    {
        if(unknownClaudeLogins.Contains(slot))return true;
        var config=GetAccounts("claude").SingleOrDefault(a=>a.SlotId==slot);
        if(config?.ClaudeConfigDirectory is not {} directory)return false;
        var state=Path.Combine(directory,".bridge-auth-login-state.json");
        if(!File.Exists(state))return false;
        try
        {
            EnsureClaudeProfileDirectory(slot,directory,create:false);
            if(new FileInfo(state).Length>16384||(File.GetAttributes(state)&FileAttributes.ReparsePoint)!=0)return true;
            using var document=JsonDocument.Parse(File.ReadAllBytes(state));
            var value=document.RootElement;
            return value.GetProperty("schema_version").GetInt32()!=1||value.GetProperty("slot_id").GetString()!=slot.ToString("D")||
                value.GetProperty("config_dir_sha256").GetString()!=ProtectedClaudeLoginLauncher.DirectoryHash(directory)||
                value.GetProperty("status").GetString() is not ("completed_unverified" or "blocked" or "failed")||
                !value.GetProperty("process_closed").GetBoolean();
        }
        catch{return true;}
    }

    public async Task<AccountConfig> AddClaudeAccountAsync(string label)
    {
        label=label.Trim();
        if(string.IsNullOrWhiteSpace(label)||label.Length>200||label.Any(char.IsControl))
            throw new ArgumentException("Enter a name for the new Claude account.");
        if(Settings.ReadOnly)throw new InvalidOperationException("Account settings are read-only.");
        await saving.WaitAsync();
        try
        {
            var accounts=GetAccounts("claude");var slot=Guid.NewGuid();
            var directory=Path.GetFullPath(Path.Combine(Root,"claude-profiles",slot.ToString("N")));
            var pending=new AccountConfig("claude",slot,label,Order:accounts.Count==0?0:accounts.Max(a=>a.Order)+1,
                IsActive:accounts.Count==0,SourceMode:"local-oauth",ClaudeConfigDirectory:directory);
            EnsureClaudeProfileDirectory(slot,directory,create:true);
            var next=Accounts.Append(pending).ToList();
            await Settings.SaveAsync(next);Accounts=next;
            await Coordinator.ConfigureAccountAsync(Adapters.Single(a=>a.Definition.Id=="claude"),pending);
            return pending;
        }
        finally{saving.Release();}
    }

    public async Task<NativeAccountResult> LoginClaudeAccountAsync(Guid slot,CancellationToken cancellationToken=default)
    {
        if(!await nativeActions.WaitAsync(0,cancellationToken))return new(NativeAccountStatus.Busy,"Another account operation is in progress.");
        try
        {
            if(Settings.ReadOnly)return new(NativeAccountStatus.Failed,"Account settings are read-only. CLI auth files were not changed.");
            if(IsClaudeLoginUnknown(slot))return new(NativeAccountStatus.Unknown,"The previous sign-in result is unknown. Do not repeat sign-in. You can read the current account login.");
            var config=GetAccounts("claude").Single(a=>a.SlotId==slot);
            if(config.SourceMode!="local-oauth"||config.ClaudeConfigDirectory is null)
                return new(NativeAccountStatus.Unsupported,"Choose an independent Claude account profile first.");
            EnsureClaudeProfileDirectory(slot,config.ClaudeConfigDirectory,create:false);
            ClaudeLoginOutcome outcome;
            using(var renewalPause=await Queries.PauseClaudeRenewalAsync(cancellationToken))
            {
                // Acquiring the existing renewal owner's gate waits for any
                // rotation already in flight. Bind this attempt to its fresh state.
                var current=GetAccounts("claude").Single(a=>a.SlotId==slot);
                if(current.SourceMode!=config.SourceMode||current.ClaudeConfigDirectory!=config.ClaudeConfigDirectory)
                    return NativeLoginSwitcher.Result(NativeAccountStatus.ConcurrentChange);
                config=current;
                pausedClaudeLogins[slot]=0;
                if((nativeCliBusyGuard??NativeLoginSwitcher.HasRunningCli)("claude"))
                    return new(NativeAccountStatus.Busy,"Close active Claude CLI sessions before signing in to this account.");
                claudeLoginBaselines[slot]=ClaudeCredentialDigest(config);
                outcome=ClaudeLoginFixture is {} fixture
                    ?await fixture(config,cancellationToken)
                    :await new ProtectedClaudeLoginLauncher(Root).LoginAsync(config,cancellationToken);
            }
            if(outcome.Status=="unknown"||!outcome.ProcessClosed)
            {
                unknownClaudeLogins.Add(slot);
                return new(NativeAccountStatus.Unknown,"The previous sign-in result is unknown. Do not repeat sign-in. You can read the current account login.");
            }
            if(outcome.Status!="completed_unverified")return new(NativeAccountStatus.Failed,
                outcome.Status=="blocked"?"Protected sign-in is unavailable. Check the Claude Code Bridge installation and network protection.":"Sign-in did not finish. This account remains available to try again.");
            return await CompleteClaudeLoginCoreAsync(config);
        }
        catch(QueryException error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(error.Kind==FailureKind.CredentialExpired?NativeAccountStatus.Expired:NativeAccountStatus.Invalid);}
        catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(NativeAccountStatus.Failed);}
        finally{pausedClaudeLogins.TryRemove(slot,out _);nativeActions.Release();}
    }

    public async Task<NativeAccountResult> CompleteClaudeAccountLoginAsync(Guid slot)
    {
        if(!await nativeActions.WaitAsync(0))return new(NativeAccountStatus.Busy,"Another account operation is in progress.");
        try
        {
            if(Settings.ReadOnly)return new(NativeAccountStatus.Failed,"Account settings are read-only. CLI auth files were not changed.");
            if((nativeCliBusyGuard??NativeLoginSwitcher.HasRunningCli)("claude"))
                return new(NativeAccountStatus.Busy,"Close active Claude CLI sessions before reading the account login.");
            var config=GetAccounts("claude").Single(a=>a.SlotId==slot);
            if(config.SourceMode!="local-oauth"||config.ClaudeConfigDirectory is null)return NativeLoginSwitcher.Result(NativeAccountStatus.Unsupported);
            EnsureClaudeProfileDirectory(slot,config.ClaudeConfigDirectory,create:false);
            if(IsClaudeLoginUnknown(slot)&&!(claudeLoginBaselines.TryGetValue(slot,out var baseline)&&ClaudeCredentialDigest(config) is {} current&&current!=baseline)&&!HasChangedClosedClaudeReceipt(config))
                return new(NativeAccountStatus.Unknown,"The previous sign-in result is unknown. Do not repeat sign-in. You can read the current account login.");
            var result=await CompleteClaudeLoginCoreAsync(config);
            // Reading a new credential does not settle or authorize replay of
            // an earlier uncertain OAuth operation. Preserve that attempt.
            if(result.Succeeded&&IsClaudeLoginUnknown(slot))
                return new(NativeAccountStatus.Unknown,"The current login was read, but the earlier sign-in outcome is unresolved. Do not repeat sign-in.");
            return result;
        }
        catch(QueryException error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(error.Kind==FailureKind.CredentialExpired?NativeAccountStatus.Expired:NativeAccountStatus.Invalid);}
        catch(Exception error){RuntimeLog?.RecordException("handled_exception",error);return NativeLoginSwitcher.Result(NativeAccountStatus.Failed);}
        finally{nativeActions.Release();}
    }

    private bool HasChangedClosedClaudeReceipt(AccountConfig config)
    {
        if(config.ClaudeConfigDirectory is not {} directory)return false;
        var path=Path.Combine(directory,".bridge-auth-login-state.json");
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>16384||(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return false;
            using var document=JsonDocument.Parse(File.ReadAllBytes(path));var value=document.RootElement;
            return value.GetProperty("schema_version").GetInt32()==1&&value.GetProperty("slot_id").GetString()==config.SlotId.ToString("D")&&
                value.GetProperty("config_dir_sha256").GetString()==ProtectedClaudeLoginLauncher.DirectoryHash(directory)&&
                value.GetProperty("cli_started").GetBoolean()&&value.GetProperty("process_closed").GetBoolean()&&
                value.GetProperty("credential_change_verified").GetBoolean()&&value.GetProperty("credential_changed").GetBoolean();
        }
        catch{return false;}
    }

    private string? ClaudeCredentialDigest(AccountConfig config)
    {
        var path=Queries.Native.PathFor(config);
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>524288)throw new InvalidDataException("The account credential file is too large.");
        var document=File.ReadAllBytes(path);
        try{return Convert.ToHexString(SHA256.HashData(document));}
        finally{CryptographicOperations.ZeroMemory(document);}
    }

    private async Task<NativeAccountResult> CompleteClaudeLoginCoreAsync(AccountConfig expected)
    {
        var current=GetAccounts("claude").Single(a=>a.SlotId==expected.SlotId);
        if(current!=(expected with {IsActive=current.IsActive}))return NativeLoginSwitcher.Result(NativeAccountStatus.ConcurrentChange);
        var identity=Queries.Native.ForAccount(current).Read("claude").Identity;
        if(HasDuplicateClaudeIdentity(current.SlotId,identity))return new(NativeAccountStatus.Invalid,DuplicateClaudeLoginMessage);
        var next=current with {Enabled=true,NativeIdentity=identity,CredentialSource="local-oauth",CredentialRevision=Guid.NewGuid(),NativeAuthRef=null,CredentialRef=null};
        try{await SaveAsync(next,expectedCurrent:current,approveClaudeLogin:true);}
        catch(QueryException error)when(error.Kind==FailureKind.IdentityChanged&&HasDuplicateClaudeIdentity(current.SlotId,identity))
        {return new(NativeAccountStatus.Invalid,DuplicateClaudeLoginMessage);}
        pausedClaudeLogins.TryRemove(expected.SlotId,out _);
        await Coordinator.RefreshAsync("claude",expected.SlotId);
        return new(NativeAccountStatus.Captured,"Claude login connected to this saved account. Usage is read from its own login.");
    }

    // NativeIdentity may identify an OAuth grant rather than a stable account.
    // Equality is duplicate evidence; inequality does not prove distinct users.
    private bool HasDuplicateClaudeIdentity(Guid slot,string identity)=>GetAccounts("claude").Any(account=>account.SlotId!=slot&&(account.SourceMode is "local-oauth" or "saved-oauth")&&account.NativeIdentity==identity);

    private void EnsureClaudeProfileDirectory(Guid slot,string directory,bool create)
    {
        var expected=Path.GetFullPath(Path.Combine(Root,"claude-profiles",slot.ToString("N")));
        if(!string.Equals(expected,Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The account profile is outside its owned directory.");
        for(var parent=new DirectoryInfo(expected);parent is not null;parent=parent.Parent)
            if(parent.Exists&&(parent.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("An account profile cannot use a linked directory.");
        var marker=Path.Combine(expected,"gamedevusagebar-profile.json");
        if(create)
        {
            if(Directory.Exists(expected))throw new IOException("The new account profile already exists.");
            Directory.CreateDirectory(expected);
            using var output=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            JsonSerializer.Serialize(output,new {schema_version=1,provider="claude",slot_id=slot.ToString("D")});
            output.Flush(true);
        }
        if(!File.Exists(marker)||new FileInfo(marker).Length>4096||(File.GetAttributes(marker)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidDataException("The account profile marker is unavailable.");
        using var ownership=JsonDocument.Parse(File.ReadAllBytes(marker));
        if(ownership.RootElement.GetProperty("schema_version").GetInt32()!=1||ownership.RootElement.GetProperty("provider").GetString()!="claude"||
            !Guid.TryParse(ownership.RootElement.GetProperty("slot_id").GetString(),out var owner)||owner!=slot)
            throw new InvalidDataException("The account profile belongs to another account.");
    }
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
            var captured=config with {Enabled=true,SourceMode="saved-oauth",CredentialSource="saved-oauth",CredentialRef=null,NativeAuthRef=reference,NativeIdentity=credential.Identity,AccountId=credential.AccountId,CredentialRevision=Guid.NewGuid(),ClaudeConfigDirectory=null};
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
