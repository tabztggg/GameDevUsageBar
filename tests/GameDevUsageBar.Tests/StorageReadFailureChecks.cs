using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

internal static class StorageReadFailureChecks
{
    private const string Canary="synthetic-storage-read-canary-73528";
    private static readonly string Binding=new('b',64);
    private static void Check(bool condition){if(!condition)throw new Exception("Storage read failure check failed.");}
    private static string Root(string root,string name)=>Path.Combine(root,"storage-read-"+name+"-"+Guid.NewGuid().ToString("N"));
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root)
    {
        yield return ("Missing settings, layout and cache files or directories retain first-start behavior without writes or errors",async()=>{
            foreach(var existingDirectory in new[]{false,true}){
                var directory=Root(root,existingDirectory?"missing-files":"missing-directory");if(existingDirectory)Directory.CreateDirectory(Path.Combine(directory,"cache"));
                int errors=0;void Observe(Exception _){errors++;}
                var settings=new SettingsStore(directory,Observe);var presentation=new PresentationStore(directory,Observe);var cache=new DiskSnapshotStore(directory,Observe);
                Check((await settings.LoadAsync()).Count==0&&await presentation.LoadAsync() is null&&await cache.LoadAsync(Binding) is null&&!settings.ReadOnly&&!presentation.ReadOnly&&errors==0);
                Check(existingDirectory?Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Length==0:!Directory.Exists(directory));
                var account=new AccountConfig("tripo",Guid.NewGuid(),Canary,true);await settings.SaveAsync([account]);await presentation.SaveAsync(new(Language:"zh-CN"));
                Check((await new SettingsStore(directory).LoadAsync()).Single()==account&&(await new PresentationStore(directory).LoadAsync())?.Language=="zh-CN");
            }
        });
        yield return ("Directories occupying configuration and cache file paths are unreadable, preserve contents and block configuration saves",async()=>{
            var directory=Root(root,"directory-targets");
            foreach(var path in Paths(directory)){Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"preserved.txt"),Canary);Check(!File.Exists(path));}
            await Unreadable(directory,typeof(UnauthorizedAccessException));
            foreach(var path in Paths(directory))Check(Directory.Exists(path)&&File.ReadAllText(Path.Combine(path,"preserved.txt"))==Canary);
        });
        yield return ("A file occupying an ancestor path is not mistaken for missing first-start directories",async()=>{
            var directory=Root(root,"ancestor-file");await File.WriteAllTextAsync(directory,Canary);var before=await File.ReadAllBytesAsync(directory);
            foreach(var path in Paths(directory))Check(!File.Exists(path));
            await Unreadable(directory,typeof(DirectoryNotFoundException));
            var after=await File.ReadAllBytesAsync(directory);Check(before.SequenceEqual(after)&&!File.Exists(directory+".bak"));
        });
        yield return ("Windows ACL-denied existing configuration and cache files retain safe errors and cannot be overwritten by defaults",async()=>{
            if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
            var directory=Root(root,"acl-denied");var before=await Seed(directory);
            using var identity=WindowsIdentity.GetCurrent();var sid=identity.User??throw new InvalidOperationException();
            var original=new List<(FileInfo File,FileSecurity Security)>();
            try{
                foreach(var path in Paths(directory)){
                    var file=new FileInfo(path);var security=file.GetAccessControl();original.Add((file,security));var denied=file.GetAccessControl();
                    denied.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.ReadData|FileSystemRights.ReadAttributes|FileSystemRights.ReadExtendedAttributes,AccessControlType.Deny));file.SetAccessControl(denied);
                    bool rejected=false;try{await File.ReadAllBytesAsync(path);}catch(UnauthorizedAccessException){rejected=true;}Check(rejected);
                }
                await Unreadable(directory,typeof(UnauthorizedAccessException));
            }finally{foreach(var (file,security) in original){var restored=new FileSecurity();restored.SetSecurityDescriptorBinaryForm(security.GetSecurityDescriptorBinaryForm(),AccessControlSections.Access);file.SetAccessControl(restored);}}
            await Preserved(directory,before);
        });
        yield return ("Locked existing configuration and cache files preserve bytes, enter read-only state and retain I/O errors",async()=>{
            var directory=Root(root,"locked");var before=await Seed(directory);var locks=new List<FileStream>();
            try{
                foreach(var path in Paths(directory)){Check(File.Exists(path));locks.Add(new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None));}
                await Unreadable(directory,typeof(IOException));
            }finally{foreach(var stream in locks)stream.Dispose();}
            await Preserved(directory,before);
        });
    }
    private static string[] Paths(string root)=>[Path.Combine(root,"settings.json"),Path.Combine(root,"presentation.json"),Path.Combine(root,"cache",Binding+".json")];
    private static async Task<Dictionary<string,byte[]>> Seed(string root)
    {
        await new SettingsStore(root).SaveAsync([new AccountConfig("tripo",Guid.NewGuid(),Canary,true)]);
        await new PresentationStore(root).SaveAsync(new(Language:"zh-CN",Widget:new(Visible:true)));
        await new DiskSnapshotStore(root).SaveAsync(new(Binding,DataOrigin.Live,DateTimeOffset.UtcNow,[new("balance","Available",777,"credits")]));
        var bytes=new Dictionary<string,byte[]>();foreach(var path in Paths(root))bytes[path]=await File.ReadAllBytesAsync(path);return bytes;
    }
    private static async Task Unreadable(string root,Type expectedError)
    {
        using var log=new RuntimeDiagnostics(Path.Combine(Path.GetDirectoryName(root)!,"storage-read-safe-evidence-"+Guid.NewGuid().ToString("N")),UsageExporter.AppVersion);Check(log.BeginSession());
        var errors=new List<(Type Type,int HResult)>();void Observe(Exception error){errors.Add((error.GetType(),error.HResult));log.RecordException("handled_exception",error);}
        var settings=new SettingsStore(root,Observe);var presentation=new PresentationStore(root,Observe);var cache=new DiskSnapshotStore(root,Observe);
        Check((await settings.LoadAsync()).Count==0&&settings.ReadOnly&&await presentation.LoadAsync() is null&&presentation.ReadOnly&&await cache.LoadAsync(Binding) is null);
        Check(errors.Count==3&&errors.All(error=>error.Type==expectedError&&error.HResult!=0));
        await Blocked(()=>settings.SaveAsync([new("tripo",Guid.NewGuid(),"Default",true)]));await Blocked(()=>presentation.SaveAsync(new(Language:"en-US")));
        Check(errors.Count==3);
        var evidence=JsonSerializer.Serialize(log.Recent);Check(log.Recent.Count(entry=>entry.Exception is not null)==3&&!evidence.Contains(Canary)&&!evidence.Contains(root)&&!evidence.Contains("Message",StringComparison.OrdinalIgnoreCase)&&!evidence.Contains("Data",StringComparison.OrdinalIgnoreCase));
    }
    private static async Task Blocked(Func<Task> save)
    {
        bool blocked=false;try{await save();}catch(InvalidOperationException){blocked=true;}Check(blocked);
    }
    private static async Task Preserved(string root,Dictionary<string,byte[]> before)
    {
        foreach(var (path,bytes) in before){var after=await File.ReadAllBytesAsync(path);Check(bytes.SequenceEqual(after)&&!File.Exists(path+".bak"));}
        Check((await new SettingsStore(root).LoadAsync()).Single().Label==Canary&&(await new PresentationStore(root).LoadAsync())?.Language=="zh-CN"&&(await new DiskSnapshotStore(root).LoadAsync(Binding))?.Metrics.Single().Value==777);
    }
}
