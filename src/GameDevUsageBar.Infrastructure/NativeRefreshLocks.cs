using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GameDevUsageBar.Infrastructure;

// Compatible with the audited Claude 2.1.285 proper-lockfile directories. Never
// steals a stale/foreign lock or touches a foreign .owner record.
public sealed class NativeRefreshLocks : IAsyncDisposable
{
    private readonly List<Owned> owned=[];
    private readonly CancellationTokenSource compromised=new();
    private readonly CancellationTokenSource stopping=new();
    private Task? heartbeat;
    private string? proofPath;
    private sealed record Owned(string Path,DateTime Birth,DateTime Modified);
    private sealed record Proof(int Schema,int Pid,DateTime ProcessStart,Owned[] Paths);
    public CancellationToken Compromised=>compromised.Token;
    public static NativeRefreshLocks? TryAcquire(string config,string? recoveryRoot=null)
    {
        var lease=new NativeRefreshLocks();
        try
        {
            var first=Path.Combine(config,".oauth_refresh.lock");
            var second=RealPath(config)+".lock";
            if(recoveryRoot is not null)
            {
                lease.proofPath=Path.Combine(recoveryRoot,"lease-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(second))).ToLowerInvariant()+".bin");
                lease.RecoverOwn([first,second]);
            }
            foreach(var path in new[]{first,second})
            {
                if(!CreateDirectory(NativePath(path),IntPtr.Zero))
                {
                    var error=Marshal.GetLastWin32Error();
                    lease.Release();
                    if(error is 183 or 80){lease.compromised.Dispose();lease.stopping.Dispose();return null;}
                    throw new Win32Exception(error);
                }
                var info=new DirectoryInfo(path);
                if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Unusable refresh lock.");
                lease.owned.Add(new(path,info.CreationTimeUtc,info.LastWriteTimeUtc));
                lease.SaveProof();
            }
            lease.heartbeat=Task.Run(lease.KeepAlive);
            return lease;
        }
        catch {lease.Release();lease.compromised.Dispose();lease.stopping.Dispose();throw;}
    }
    private void SaveProof()
    {
        if(proofPath is null)return;
        using var process=Process.GetCurrentProcess();
        var plain=JsonSerializer.SerializeToUtf8Bytes(new Proof(1,process.Id,process.StartTime.ToUniversalTime(),owned.ToArray()));byte[]? encrypted=null;
        try{encrypted=DpapiSecretStore.Protect(plain);AtomicFile.WriteAsync(proofPath,encrypted).GetAwaiter().GetResult();}
        finally{CryptographicOperations.ZeroMemory(plain);if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);}
    }
    private void RecoverOwn(string[] allowed)
    {
        if(proofPath is null||!File.Exists(proofPath))return;
        byte[]? plain=null;
        try
        {
            if(new FileInfo(proofPath).Length>16384)return;
            plain=DpapiSecretStore.Unprotect(File.ReadAllBytes(proofPath));var proof=JsonSerializer.Deserialize<Proof>(plain);
            if(proof is null||proof.Schema!=1||proof.Pid<=0||proof.Paths.Length is <1 or >2||proof.Paths.Select(p=>p.Path).Distinct().Count()!=proof.Paths.Length||proof.Paths.Any(p=>!allowed.Contains(p.Path,StringComparer.OrdinalIgnoreCase)))return;
            // PID absence or a different start identity proves this particular
            // owner exited. An unreadable process or foreign lock is not proof.
            try{using var old=Process.GetProcessById(proof.Pid);if(old.StartTime.ToUniversalTime()==proof.ProcessStart)return;}
            catch(ArgumentException){}catch{return;}
            foreach(var item in proof.Paths.Reverse())
            {
                var info=new DirectoryInfo(item.Path);
                if(!info.Exists)continue;
                if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.CreationTimeUtc!=item.Birth||info.LastWriteTimeUtc!=item.Modified||Directory.EnumerateFileSystemEntries(item.Path).Any())return;
            }
            foreach(var item in proof.Paths.Reverse())
            {
                var info=new DirectoryInfo(item.Path);
                if(info.Exists&&info.CreationTimeUtc==item.Birth&&info.LastWriteTimeUtc==item.Modified)Directory.Delete(item.Path,false);
            }
        }
        catch{/* Ambiguous ownership remains busy; never steals a native lock. */}
        finally{if(plain is not null)CryptographicOperations.ZeroMemory(plain);}
    }
    public bool Verify()
    {
        lock(owned)
        {
            foreach(var item in owned)
            {
                var info=new DirectoryInfo(item.Path);
                if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0||info.CreationTimeUtc!=item.Birth||info.LastWriteTimeUtc!=item.Modified)
                {compromised.Cancel();return false;}
                // A suspended owner cannot rely on a lease the native CLI could
                // already have declared stale, even if the path still exists.
                if(DateTime.UtcNow-item.Modified>TimeSpan.FromSeconds(60))
                {compromised.Cancel();return false;}
            }
            return owned.Count==2;
        }
    }
    private async Task KeepAlive()
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
            while(await timer.WaitForNextTickAsync(stopping.Token))
            {
                lock(owned)
                {
                    if(!Verify())return;
                    for(var i=0;i<owned.Count;i++)
                    {
                        var item=owned[i];Directory.SetLastWriteTimeUtc(item.Path,DateTime.UtcNow);
                        owned[i]=item with {Modified=Directory.GetLastWriteTimeUtc(item.Path)};
                    }
                    SaveProof();
                }
            }
        }
        catch(OperationCanceledException)when(stopping.IsCancellationRequested){}
        catch{compromised.Cancel();}
    }
    private void Release()
    {
        lock(owned)
        {
            foreach(var item in owned.AsEnumerable().Reverse())
            {
                try
                {
                    var info=new DirectoryInfo(item.Path);
                    if(info.Exists&&(info.Attributes&FileAttributes.ReparsePoint)==0&&info.CreationTimeUtc==item.Birth&&info.LastWriteTimeUtc==item.Modified)
                        Directory.Delete(item.Path,false);
                }
                catch{/* Foreign/nonempty/changed paths stay intact. */}
            }
            owned.Clear();
        }
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();if(heartbeat is not null)await heartbeat;Release();compromised.Dispose();stopping.Dispose();
    }
    private static string RealPath(string directory)
    {
        using var handle=CreateFile(NativePath(directory),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer=new char[32768];var length=GetFinalPathNameByHandle(handle,buffer,(uint)buffer.Length,0);
        if(length==0||length>=buffer.Length)throw new IOException("Unusable native configuration path.");
        var path=new string(buffer,0,(int)length);
        return path.StartsWith(@"\\?\UNC\",StringComparison.Ordinal)?@"\\"+path[8..]:path.StartsWith(@"\\?\",StringComparison.Ordinal)?path[4..]:path;
    }
    // Managed file APIs already support long paths. These Win32 entrypoints
    // need an extended absolute path as well, without changing lease identity.
    private static string NativePath(string path)
    {
        var full=Path.GetFullPath(path);
        return full.StartsWith(@"\\?\",StringComparison.Ordinal)?full:
            full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full[2..]:@"\\?\"+full;
    }
    [DllImport("kernel32.dll",EntryPoint="CreateDirectoryW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateDirectory(string path,IntPtr security);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint sharing,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,[Out]char[] path,uint length,uint flags);
}
