using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

public enum NativeAccountStatus {Captured,Switched,AlreadyCurrent,Busy,ConcurrentChange,Missing,Expired,Invalid,Unsupported,Unknown,Failed}
public sealed record NativeAccountResult(NativeAccountStatus Status,string MessageKey,Guid? RecoveryRef=null)
{
    public bool Succeeded=>Status is NativeAccountStatus.Captured or NativeAccountStatus.Switched or NativeAccountStatus.AlreadyCurrent;
}

// This operation publishes only the selected CLI auth file. It does not start,
// stop or restart any process, edit desktop/config/account metadata, or log in.
public sealed class NativeLoginSwitcher(NativeOAuthStore native,NativeAuthVault vault,
    Func<string,bool>? busyGuard=null,Action? beforePublish=null,Action<string,string>? publish=null)
{
    private static readonly SemaphoreSlim switching=new(1,1);
    public async Task<NativeAccountResult> SwitchAsync(AccountConfig target,IEnumerable<AccountConfig> accounts)
    {
        if(target.ProviderId is not ("codex" or "claude"))return Result(NativeAccountStatus.Unsupported);
        if(target.SourceMode!="saved-oauth"||target.NativeAuthRef is null)return Result(NativeAccountStatus.Missing);
        await switching.WaitAsync();
        byte[]? old=null,targetDocument=null,desired=null;Guid? recovery=null;string? temporary=null;bool attempted=false;
        try
        {
            if(vault.HasPendingSwitch(target.ProviderId))return Result(NativeAccountStatus.Unknown);
            if((busyGuard??HasRunningCli)(target.ProviderId))return Result(NativeAccountStatus.Busy);
            var path=native.PathFor(target.ProviderId);
            if(File.Exists(path))
            {
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)return Result(NativeAccountStatus.Invalid);
                old=native.ReadDocument(target.ProviderId);
                NativeOAuth? current=null;
                try{current=native.ParseDocument(target.ProviderId,old,false);}catch(QueryException){/* Preserve unreadable old documents in encrypted recovery. */}
                if(current is not null)
                {
                    var known=accounts.Where(a=>a.ProviderId==target.ProviderId&&a.SourceMode=="saved-oauth"&&a.NativeAuthRef is not null&&a.NativeIdentity==current.Identity).ToArray();
                    foreach(var account in known)await vault.UpdateRecognizedAsync(account,old);
                }
            }
            targetDocument=vault.ReadDocument(target);
            var credential=native.ParseDocument(target.ProviderId,targetDocument);
            if(credential.Identity!=target.NativeIdentity)return Result(NativeAccountStatus.Invalid);
            desired=Merge(target.ProviderId,old,targetDocument);
            if(old is not null&&old.AsSpan().SequenceEqual(desired))return Result(NativeAccountStatus.AlreadyCurrent);
            if(old is not null)recovery=await vault.SaveRecoveryAsync(target.ProviderId,old);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary=path+"."+Guid.NewGuid().ToString("N")+".gamedevusagebar.tmp";
            await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {await stream.WriteAsync(desired);stream.Flush(true);}
            beforePublish?.Invoke();
            if((busyGuard??HasRunningCli)(target.ProviderId))return Result(NativeAccountStatus.Busy,recovery);
            if(!Matches(path,old))return Result(NativeAccountStatus.ConcurrentChange,recovery);
            await vault.MarkSwitchPendingAsync(target.ProviderId,recovery);
            if(!Matches(path,old)){vault.ClearPendingSwitch(target.ProviderId);return Result(NativeAccountStatus.ConcurrentChange,recovery);}
            attempted=true;
            if(publish is not null)publish(temporary,path);
            else if(old is not null)File.Replace(temporary,path,null,true);else File.Move(temporary,path);
            if(Matches(path,desired)){vault.ClearPendingSwitch(target.ProviderId);return Result(NativeAccountStatus.Switched,recovery);}
            return Result(NativeAccountStatus.Unknown,recovery);
        }
        catch(QueryException error)when(!attempted)
        {return Result(error.Kind switch {FailureKind.CredentialMissing=>NativeAccountStatus.Missing,FailureKind.CredentialExpired=>NativeAccountStatus.Expired,_=>NativeAccountStatus.Invalid},recovery);}
        catch
        {
            if(!attempted)return Result(NativeAccountStatus.Failed,recovery);
            var path=native.PathFor(target.ProviderId);
            // A failed call can still have published. Read back once and never
            // replay or roll back an uncertain write over another process.
            if(desired is not null&&Matches(path,desired))
            {try{vault.ClearPendingSwitch(target.ProviderId);}catch{}return Result(NativeAccountStatus.Switched,recovery);}
            if(Matches(path,old))
            {try{vault.ClearPendingSwitch(target.ProviderId);}catch{}return Result(NativeAccountStatus.Failed,recovery);}
            return Result(NativeAccountStatus.Unknown,recovery);
        }
        finally
        {
            if(temporary is not null&&File.Exists(temporary)){try{File.Delete(temporary);}catch{/* No plaintext backup is created; an inaccessible staging file cannot be safely manipulated further. */}}
            if(old is not null)CryptographicOperations.ZeroMemory(old);
            if(targetDocument is not null)CryptographicOperations.ZeroMemory(targetDocument);
            if(desired is not null)CryptographicOperations.ZeroMemory(desired);
            switching.Release();
        }
    }
    private static byte[] Merge(string provider,byte[]? old,byte[] document)
    {
        if(provider=="codex")return document.ToArray();
        var incoming=JsonNode.Parse(document) as JsonObject??throw new QueryException(FailureKind.CredentialUnreadable);
        var destination=old is null?new JsonObject():JsonNode.Parse(old) as JsonObject??throw new QueryException(FailureKind.CredentialUnreadable);
        destination["claudeAiOauth"]=incoming["claudeAiOauth"]?.DeepClone()??throw new QueryException(FailureKind.CredentialUnreadable);
        var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(destination);
        if(bytes.Length>65536){CryptographicOperations.ZeroMemory(bytes);throw new QueryException(FailureKind.CredentialUnreadable);}
        return bytes;
    }
    private static bool Matches(string path,byte[]? expected)
    {
        byte[]? actual=null;
        try
        {
            if(expected is null)return !File.Exists(path);
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length!=expected.Length)return false;
            actual=new byte[expected.Length];stream.ReadExactly(actual);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual),SHA256.HashData(expected));
        }
        catch{return false;}finally{if(actual is not null)CryptographicOperations.ZeroMemory(actual);}
    }
    public static NativeAccountResult Result(NativeAccountStatus status,Guid? recovery=null)=>new(status,status switch {
        NativeAccountStatus.Captured=>"Current CLI login saved for this account. Its credential is encrypted for this Windows user.",
        NativeAccountStatus.Switched=>"CLI auth file switched and verified. Existing CLI sessions and desktop apps are unchanged; start a new CLI session to use it.",
        NativeAccountStatus.AlreadyCurrent=>"The selected account is already in the CLI auth file. Existing sessions and desktop apps are unchanged.",
        NativeAccountStatus.Busy=>"A CLI session is running or its status could not be checked. Close CLI sessions yourself before switching; desktop apps are not switched.",
        NativeAccountStatus.ConcurrentChange=>"The CLI auth file changed during switching. No account was published; try again only after other writers have stopped.",
        NativeAccountStatus.Missing=>"No usable saved CLI login exists for this account. Save the current CLI login first.",
        NativeAccountStatus.Expired=>"The saved CLI login has expired. Renew it in the CLI yourself, then save the current login again.",
        NativeAccountStatus.Unsupported=>"CLI auth-file switching is supported only for Codex and Claude.",
        NativeAccountStatus.Invalid=>"The credential could not be validated for this account. The CLI auth file was not changed.",
        NativeAccountStatus.Unknown=>"The CLI auth-file result is unknown. Do not retry automatically. Check the CLI login, then use Save current CLI login to reconcile it before switching again.",
        _=>"The CLI login operation failed. No automatic retry or rollback was attempted."
    },recovery);
    public static bool HasRunningCli(string provider)
    {
        if(!OperatingSystem.IsWindows())return true;
        foreach(var process in Process.GetProcessesByName("node"))
        {
            using(process)
            {
                try
                {
                    if(process.HasExited)continue;
                    var command=CommandLine(process);
                    // npm CLIs can run under node.exe. Unknown, unrelated Node
                    // processes do not become CLI candidates or block switching.
                    if(IsNodeCliCandidate(provider,command))return true;
                }
                catch{/* An unidentifiable Node process is not treated as this CLI. */}
            }
        }
        var name=provider=="codex"?"codex":"claude";
        foreach(var process in Process.GetProcessesByName(name))
        {
            using(process)
            {
                try
                {
                    if(process.HasExited)continue;
                    var path=process.MainModule?.FileName??"";
                    var command=CommandLine(process);
                    // Electron desktops and Codex desktop's app-server have
                    // separate live authentication; never stop or alter them.
                    if(command?.Contains("--type=",StringComparison.Ordinal)==true)continue;
                    if(provider=="codex"&&(Regex.IsMatch(command??"",@"(?:^|\s)app-server(?:\s|$)")
                        ||(path.Contains("OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)&&!path.Contains("\\resources\\",StringComparison.OrdinalIgnoreCase))))continue;
                    if(provider=="claude"&&(path.Contains("\\AnthropicClaude\\",StringComparison.OrdinalIgnoreCase)
                        ||Regex.IsMatch(path,@"\\(?:Programs\\)?Claude\\(?:app-[^\\]+\\)?Claude\.exe$",RegexOptions.IgnoreCase)))continue;
                    return true;
                }
                catch{return true;}
            }
        }
        return false;
    }
    public static bool IsNodeCliCandidate(string provider,string? command)
    {
        if(command is null||provider is not ("codex" or "claude"))return false;
        var pattern=provider=="codex"?@"@openai[\\/]+codex[\\/]+":@"@anthropic-ai[\\/]+claude-code[\\/]+";
        return Regex.IsMatch(command,pattern,RegexOptions.IgnoreCase)
            &&!(provider=="codex"&&Regex.IsMatch(command,@"(?:^|\s)app-server(?:\s|$)"));
    }
    [StructLayout(LayoutKind.Sequential)]private struct UnicodeString{public ushort Length;public ushort MaximumLength;public IntPtr Buffer;}
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(IntPtr process,int informationClass,IntPtr information,int size,out int needed);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,int processId);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    private static string? CommandLine(Process process)
    {
        var handle=OpenProcess(0x1000,false,process.Id);if(handle==IntPtr.Zero)return null;
        try
        {
            NtQueryInformationProcess(handle,60,IntPtr.Zero,0,out var needed);
            if(needed is <=0 or >131072)return null;
            var buffer=Marshal.AllocHGlobal(needed);
            try
            {
                if(NtQueryInformationProcess(handle,60,buffer,needed,out _)!=0)return null;
                var text=Marshal.PtrToStructure<UnicodeString>(buffer);
                return text.Length>0?Marshal.PtrToStringUni(text.Buffer,text.Length/2):null;
            }
            finally{Marshal.FreeHGlobal(buffer);}
        }
        finally{CloseHandle(handle);}
    }
}
