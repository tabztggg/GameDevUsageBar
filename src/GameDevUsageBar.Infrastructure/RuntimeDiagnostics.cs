using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDevUsageBar.Infrastructure;

public sealed record RuntimeExceptionInfo(string Type, int HResult, string[] Methods, RuntimeExceptionInfo[] Inner);
public sealed record RuntimeResourceSample(long PrivateBytes,long WorkingSetBytes,long ManagedBytes,int Handles,int Threads,double UptimeSeconds,int CacheFiles,long CacheBytes);
public sealed record RuntimeLogEntry(int Schema,DateTimeOffset TimestampUtc,DateTimeOffset TimestampLocal,long Sequence,string? SessionId,int ProcessId,string Version,string Event,string? Reason,int? ExitCode,RuntimeExceptionInfo? Exception,RuntimeResourceSample? Resources);
public sealed record RuntimeLoggingFailure(string Stage,string Type,int HResult,DateTimeOffset TimestampUtc);
public sealed record RuntimeDiagnosticsSummary(string Version,bool LoggingAvailable,bool SessionStarted,string PreviousSessionStatus,string? SessionId,int ProcessId,int LogFileCount,long LogBytes,RuntimeResourceSample? LastResources,RuntimeLoggingFailure? FirstLoggingFailure=null,RuntimeLoggingFailure? LastLoggingFailure=null,int LoggingFailureCount=0);

/// <summary>Bounded local evidence. Exception messages, values, paths and request contents are never logged.</summary>
public sealed class RuntimeDiagnostics : IDisposable
{
    public const int MaximumLogFileBytes=1024*1024;
    public const int MaximumLogFiles=5;
    public const int MaximumRecentEntries=100;
    public const int MaximumEntryBytes=32*1024;
    public const int MaximumBundleBytes=6*1024*1024;
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=16};
    private static readonly HashSet<string> Events=new(StringComparer.Ordinal){"app_start","app_ready","application_starting","startup_exception","dispatcher_unhandled","appdomain_unhandled","winforms_unhandled","task_unobserved","handled_exception","exit_requested","app_exit","application_shutdown","session_end","session_begin","session_ending","previous_session_incomplete","previous_session_fatal","duplicate_instance","resource_sample","resource_sample_failed","logging_write_failed","export_failed","system_shutdown","shutdown_cleanup_exception","preferences_flush_failed"};
    private static readonly HashSet<string> Reasons=new(StringComparer.Ordinal){"tray_exit","main_window_exit","overview_exit","tray_popup_exit","explicit_exit","os_session_end","clean_exit","startup_failure","fatal_exception","duplicate_instance","process_exit_unknown","process_exit_without_shutdown","unclean_termination_unknown","disposal","session_shutdown","session_logoff","application_shutdown","shutdown_failure","update_restart","test","unspecified"};
    private static readonly HashSet<string> Providers=new(StringComparer.Ordinal){"claude","codex","tripo","grsai","deepseek","gemini","gemini-cli","elevenlabs","openrouter","demo"};
    private static readonly HashSet<string> Channels=new(StringComparer.Ordinal){"Subscription","API credits","Account credits (API key)","Official API balance","Project API requests","CLI quota","Account balance (API key)","Demo data"};
    private static readonly HashSet<string> Hosts=new(StringComparer.Ordinal){"api.anthropic.com","chatgpt.com","openapi.tripo3d.ai","openapi.tripo3d.com","grsaiapi.com","grsaiapi.com.cn","grsai.ai","api.deepseek.com","monitoring.googleapis.com","cloudcode-pa.googleapis.com","api.elevenlabs.io","openrouter.ai"};
    private readonly object gate=new();
    private readonly string cacheDirectory;
    private readonly string version;
    private readonly Queue<RuntimeLogEntry> recent=new();
    private readonly int fileLimit;
    private readonly bool diskEnabled;
    private readonly long created=Stopwatch.GetTimestamp();
    private FileStream? sessionLock;
    private string? sessionId;
    private SessionMarker? marker;
    private bool disposed,sessionStarted,loggingAvailable=true;
    private string previousStatus="no_previous_session";
    private long sequence;
    private RuntimeResourceSample? lastResources;
    private RuntimeLoggingFailure? firstLoggingFailure,lastLoggingFailure;
    private int loggingFailureCount;
    private sealed record SessionMarker(int Schema,string SessionId,int ProcessId,DateTimeOffset StartedUtc,DateTimeOffset? EndedUtc,string? Reason,int? ExitCode,bool Fatal);

    public string LogDirectory {get;}
    public bool SessionLoggingAvailable=>Volatile.Read(ref loggingAvailable)&&Volatile.Read(ref sessionStarted);
    // A smaller bound is useful for deterministic rotation tests. Production uses the hard maximum.
    public RuntimeDiagnostics(string root,string version,int maximumLogFileBytes=MaximumLogFileBytes)
    {
        try{LogDirectory=Path.Combine(Path.GetFullPath(root),"logs");cacheDirectory=Path.Combine(Path.GetFullPath(root),"cache");diskEnabled=true;}
        catch(Exception error){LogDirectory="";cacheDirectory="";CaptureLoggingFailure("normalize_paths",error);}
        this.version=SafeVersion(version);fileLimit=Math.Clamp(maximumLogFileBytes,4096,MaximumLogFileBytes);
    }
    public bool BeginSession()
    {
        lock(gate)
        {
            if(disposed)return false;if(sessionStarted)return true;
            var stage="prepare_log_directory";
            var failuresBefore=loggingFailureCount;
            try
            {
                EnsureDirectory();
                // Held until Dispose: a second launcher cannot replace the active process's marker.
                stage="acquire_session_lease";
                sessionLock=new FileStream(Path.Combine(LogDirectory,"session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                stage="read_previous_marker";
                var prior=ReadMarker();
                previousStatus=prior is null?(File.Exists(MarkerPath)?"unknown":"no_previous_session"):prior.Fatal?"fatal_exception":prior.EndedUtc is null?"incomplete":prior.Reason=="process_exit_without_shutdown"?"unexpected_exit":prior.Reason is "unspecified" or "process_exit_unknown"?"unknown_exit":"graceful_exit";
                sessionId=Guid.NewGuid().ToString("N");marker=new(1,sessionId,Environment.ProcessId,DateTimeOffset.UtcNow,null,null,null,false);
                stage="write_session_marker";
                WriteMarker();sessionStarted=true;
                if(previousStatus=="incomplete")WriteEntry("previous_session_incomplete","unclean_termination_unknown");
                else if(previousStatus=="fatal_exception")WriteEntry("previous_session_fatal","fatal_exception");
                WriteEntry("session_begin",null);return true;
            }
            catch(Exception error)
            {
                // WriteMarker captures its more precise stage itself. Do not
                // replace that evidence with a second outer failure record.
                if(loggingFailureCount==failuresBefore)CaptureLoggingFailure(stage,error);
                loggingAvailable=false;sessionLock?.Dispose();sessionLock=null;return false;
            }
        }
    }
    public void Record(string eventName,string? reason=null){lock(gate){if(!disposed)WriteEntry(EventCode(eventName),ReasonCode(reason));}}
    public void RecordException(string eventName,Exception error)
    {
        lock(gate)
        {
            if(disposed)return;
            RuntimeExceptionInfo? safe=null;
            try{int budget=8;safe=DescribeException(error,0,ref budget);}catch{/* Capturing evidence must not cause another failure. */}
            WriteEntry(EventCode(eventName),null,safe);
        }
    }
    public void SampleResources()
    {
        lock(gate)
        {
            if(disposed)return;
            try
            {
                using var process=Process.GetCurrentProcess();process.Refresh();int count=0;long bytes=0;
                if(Directory.Exists(cacheDirectory)&&!IsReparse(cacheDirectory))
                    foreach(var path in Directory.EnumerateFiles(cacheDirectory,"*.json").Take(10000))
                    {var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0)continue;count++;bytes=checked(bytes+info.Length);}
                lastResources=new(process.PrivateMemorySize64,process.WorkingSet64,GC.GetTotalMemory(false),process.HandleCount,process.Threads.Count,Stopwatch.GetElapsedTime(created).TotalSeconds,count,bytes);
                WriteEntry("resource_sample",null,resources:lastResources);
            }
            catch{WriteEntry("resource_sample_failed",null);}
        }
    }
    public void CompleteSession(string reason,int exitCode=0)
    {
        lock(gate)
        {
            if(disposed||!sessionStarted||marker is null||marker.EndedUtc is not null)return;
            var code=ReasonCode(reason)??"unspecified";
            WriteEntry("session_end",code,exitCode:exitCode);
            marker=marker with{EndedUtc=DateTimeOffset.UtcNow,Reason=code,ExitCode=exitCode,Fatal=code is "fatal_exception" or "startup_failure" or "shutdown_failure"};
            try{WriteMarker();}catch{loggingAvailable=false;}
        }
    }
    public RuntimeDiagnosticsSummary GetSummary()
    {
        lock(gate)
        {
            int count=0;long bytes=0;
            try{foreach(var path in LogPaths()){if(File.Exists(path)&&!IsReparse(path)){count++;bytes+=new FileInfo(path).Length;}}}catch(Exception error){CaptureLoggingFailure("read_log_metadata",error);}
            return new(version,loggingAvailable,sessionStarted,previousStatus,sessionId,Environment.ProcessId,count,bytes,lastResources,firstLoggingFailure,lastLoggingFailure,loggingFailureCount);
        }
    }
    public IReadOnlyList<RuntimeLogEntry> Recent {get{lock(gate)return recent.ToArray();}}
    public bool ExportBundle(string path,string hostDiagnosticsJson)
    {
        lock(gate)
        {
            if(disposed)return false;
            // CreateNew avoids destroying an existing file when export fails midway.
            bool createdFile=false;
            try
            {
                var diagnostics=SafeHostDiagnostics(hostDiagnosticsJson);
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    createdFile=true;
                    using(var zip=new ZipArchive(file,ZipArchiveMode.Create,true))
                    {
                        AddZip(zip,"diagnostics.json",diagnostics);AddZip(zip,"runtime-summary.json",JsonSerializer.SerializeToUtf8Bytes(GetSummary(),JsonOptions));
                        foreach(var log in LogPaths())
                        {
                            if(!File.Exists(log)||IsReparse(log))continue;
                            // Only files created by this component, with a strict per-file cap. No auth/settings/cache files.
                            using var input=new FileStream(log,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                            if(input.Length>MaximumLogFileBytes)continue;
                            AddZip(zip,"logs/"+Path.GetFileName(log),SanitizeLog(input));
                        }
                        AddZip(zip,"recent-events.json",JsonSerializer.SerializeToUtf8Bytes(recent.ToArray(),JsonOptions));
                    }
                    file.Flush(true);if(file.Length>MaximumBundleBytes)throw new InvalidDataException();
                }
                return true;
            }
            catch(Exception error)
            {
                if(createdFile){try{File.Delete(path);}catch{/* Keep failure contained. */}}
                RecordException("export_failed",error);return false;
            }
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            if(disposed)return;disposed=true;
            // Dispose alone is not evidence of a graceful shutdown. Only CompleteSession closes the marker.
            try{sessionLock?.Dispose();}catch{}sessionLock=null;
        }
    }
    private string MarkerPath=>Path.Combine(LogDirectory,"session.json");
    private static bool IsReparse(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
    private void EnsureDirectory(){if(!diskEnabled)throw new IOException();Directory.CreateDirectory(LogDirectory);if(IsReparse(LogDirectory))throw new IOException();}
    private IEnumerable<string> LogPaths(){if(!diskEnabled)yield break;yield return Path.Combine(LogDirectory,"runtime.jsonl");for(int i=1;i<MaximumLogFiles;i++)yield return Path.Combine(LogDirectory,$"runtime.{i}.jsonl");}
    private SessionMarker? ReadMarker()
    {
        try
        {
            if(!File.Exists(MarkerPath)||IsReparse(MarkerPath)||new FileInfo(MarkerPath).Length>4096)return null;
            var value=JsonSerializer.Deserialize<SessionMarker>(File.ReadAllBytes(MarkerPath),JsonOptions);
            return value is {Schema:1}&&Guid.TryParseExact(value.SessionId,"N",out _)?value:null;
        }
        catch{return null;}
    }
    private void WriteMarker()
    {
        var stage="prepare_log_directory";
        try
        {
            EnsureDirectory();var staging=Path.Combine(LogDirectory,"session.tmp");
            stage="validate_marker_targets";
            if(File.Exists(staging)&&IsReparse(staging)||File.Exists(MarkerPath)&&IsReparse(MarkerPath))throw new IOException();
            stage="open_marker_staging";
            using(var file=new FileStream(staging,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                stage="serialize_session_marker";JsonSerializer.Serialize(file,marker,JsonOptions);
                stage="flush_marker_staging";file.Flush(true);
            }
            stage="replace_session_marker";File.Move(staging,MarkerPath,true);
        }
        catch(Exception error){CaptureLoggingFailure(stage,error);throw;}
    }
    private void WriteEntry(string eventName,string? reason,RuntimeExceptionInfo? exception=null,RuntimeResourceSample? resources=null,int? exitCode=null)
    {
        var utc=DateTimeOffset.UtcNow;var entry=new RuntimeLogEntry(1,utc,utc.ToLocalTime(),++sequence,sessionId,Environment.ProcessId,version,eventName,reason,exitCode,exception,resources);
        var stage="serialize_log_entry";
        try
        {
            var data=JsonSerializer.SerializeToUtf8Bytes(entry,JsonOptions);
            if(data.Length>MaximumEntryBytes){entry=entry with{Exception=exception is null?null:new(exception.Type,exception.HResult,exception.Methods.Take(4).ToArray(),[])};data=JsonSerializer.SerializeToUtf8Bytes(entry,JsonOptions);}
            recent.Enqueue(entry);while(recent.Count>MaximumRecentEntries)recent.Dequeue();
            if(data.Length+1>fileLimit)return;
            stage="prepare_log_directory";EnsureDirectory();var logs=LogPaths().ToArray();
            // A launcher that has not begun a primary session may write only when no primary owns the log lease.
            // The lease covers rotation too, so a duplicate cannot delete or displace primary evidence.
            stage="acquire_transient_lease";
            using var temporaryLease=sessionLock is null?new FileStream(Path.Combine(LogDirectory,"session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None):null;
            stage="validate_log_targets";
            if(logs.Any(p=>File.Exists(p)&&IsReparse(p)))throw new IOException();
            stage="trim_log_files";
            foreach(var log in logs)if(File.Exists(log)&&new FileInfo(log).Length>fileLimit)File.Delete(log);
            if(File.Exists(logs[0])&&new FileInfo(logs[0]).Length+data.Length+1>fileLimit)
            {
                stage="rotate_log_files";
                if(File.Exists(logs[^1]))File.Delete(logs[^1]);
                for(int i=logs.Length-2;i>=0;i--)if(File.Exists(logs[i]))File.Move(logs[i],logs[i+1],true);
            }
            stage="open_log_entry";
            using(var file=new FileStream(logs[0],FileMode.Append,FileAccess.Write,FileShare.Read))
            {stage="append_log_entry";file.Write(data);file.WriteByte((byte)'\n');stage="flush_log_entry";file.Flush(true);}
            loggingAvailable=true;
        }
        catch(Exception error){CaptureLoggingFailure(stage,error);}
    }
    // Failure evidence must remain available even when all disk writes fail.
    // Do not call Record/WriteEntry/WriteMarker, serialize, retain the exception
    // object, invoke observers, or retry from this memory-only path.
    private void CaptureLoggingFailure(string stage,Exception error)
    {
        loggingAvailable=false;
        try
        {
            var failure=new RuntimeLoggingFailure(stage,SafeMethod(error.GetType().FullName??"unknown_exception"),error.HResult,DateTimeOffset.UtcNow);
            firstLoggingFailure??=failure;lastLoggingFailure=failure;
            if(loggingFailureCount<int.MaxValue)loggingFailureCount++;
        }
        catch{/* Logging diagnostics must not create another exception path. */}
    }
    private static string EventCode(string? value)=>value is not null&&Events.Contains(value)?value:"handled_exception";
    private static string? ReasonCode(string? value)=>value is null?null:Reasons.Contains(value)?value:"unspecified";
    private static string SafeVersion(string value)=>Regex.IsMatch(value,@"^\d{1,4}(\.\d{1,4}){1,3}(\+[a-fA-F0-9]{7,64}|-(preview|alpha|beta|rc)(\.\d{1,4})?)?$")?value:"unknown";
    private static RuntimeExceptionInfo DescribeException(Exception error,int depth,ref int budget)
    {
        budget--;
        var methods=new StackTrace(error,false).GetFrames()?.Take(16).Select(frame=>frame.GetMethod()).Where(method=>method is not null).Select(method=>SafeMethod((method!.DeclaringType?.FullName??"unknown")+"."+method.Name)).ToArray()??[];
        var inner=new List<RuntimeExceptionInfo>();
        if(depth<3&&budget>0)
        {
            var nested=error is AggregateException aggregate?aggregate.InnerExceptions.Take(4):error.InnerException is {} one?new[]{one}:Array.Empty<Exception>();
            foreach(var child in nested){if(budget==0)break;inner.Add(DescribeException(child,depth+1,ref budget));}
        }
        return new(SafeMethod(error.GetType().FullName??error.GetType().Name),error.HResult,methods,inner.ToArray());
    }
    private static string SafeMethod(string value)=>value.Length<=160&&Regex.IsMatch(value,@"^[a-zA-Z0-9_.+<>`\[\],\-]+$")?value:"unknown_method";
    private static Type? LoadedType(string? name)
    {
        if(name is null||SafeMethod(name)!=name)return null;
        foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
            try{if(assembly.GetType(name,false,false) is {} type)return type;}catch{/* Some generated assemblies do not support type lookup. */}
        return null;
    }
    private static string KnownMethod(string name)
    {
        if(SafeMethod(name)!=name)return "unknown_method";
        string typeName,methodName;
        if(name.EndsWith("..ctor",StringComparison.Ordinal)){typeName=name[..^6];methodName=".ctor";}
        else if(name.EndsWith("..cctor",StringComparison.Ordinal)){typeName=name[..^7];methodName=".cctor";}
        else{int separator=name.LastIndexOf('.');if(separator<=0)return "unknown_method";typeName=name[..separator];methodName=name[(separator+1)..];}
        var type=LoadedType(typeName);if(type is null)return "unknown_method";
        const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        try{return type.GetMethods(flags).Any(method=>method.Name==methodName)||type.GetConstructors(flags).Any(method=>method.Name==methodName)?name:"unknown_method";}
        catch{return "unknown_method";}
    }
    private static RuntimeExceptionInfo? SafeStoredException(RuntimeExceptionInfo? input,int depth,ref int budget)
    {
        if(input is null||budget==0||depth>3)return null;budget--;
        var type=LoadedType(input.Type);var safeType=type is not null&&typeof(Exception).IsAssignableFrom(type)?type.FullName??"unknown_exception":"unknown_exception";
        var methods=(input.Methods??[]).Take(16).Select(KnownMethod).ToArray();var inner=new List<RuntimeExceptionInfo>();
        foreach(var nested in (input.Inner??[]).Take(4)){if(budget==0)break;if(SafeStoredException(nested,depth+1,ref budget) is {} safe)inner.Add(safe);}
        return new(safeType,input.HResult,methods,inner.ToArray());
    }
    private static byte[] SanitizeLog(Stream input)
    {
        using var bounded=new MemoryStream();var buffer=new byte[8192];int remaining=MaximumLogFileBytes;
        while(remaining>0){int read=input.Read(buffer,0,Math.Min(buffer.Length,remaining));if(read==0)break;bounded.Write(buffer,0,read);remaining-=read;}
        bounded.Position=0;
        using var output=new MemoryStream();using var reader=new StreamReader(bounded);
        for(int count=0;count<4096&&!reader.EndOfStream;count++)
        {
            var line=reader.ReadLine();if(line is null||line.Length>MaximumEntryBytes)continue;
            try
            {
                var value=JsonSerializer.Deserialize<RuntimeLogEntry>(line,JsonOptions);
                if(value is not {Schema:1}||value.ProcessId<=0||value.Sequence<=0)continue;
                int budget=8;var safe=value with{Version=SafeVersion(value.Version??""),SessionId=Guid.TryParseExact(value.SessionId,"N",out var id)?id.ToString("N"):null,Event=EventCode(value.Event),Reason=ReasonCode(value.Reason),Exception=SafeStoredException(value.Exception,0,ref budget),TimestampUtc=value.TimestampUtc.ToUniversalTime()};
                var data=JsonSerializer.SerializeToUtf8Bytes(safe,JsonOptions);
                if(data.Length>MaximumEntryBytes||output.Length+data.Length+1>MaximumLogFileBytes)continue;
                output.Write(data);output.WriteByte((byte)'\n');
            }
            catch{/* Corrupt and foreign records are omitted, never copied as arbitrary text. */}
        }
        return output.ToArray();
    }
    private static void AddZip(ZipArchive zip,string name,byte[] contents){using var output=zip.CreateEntry(name,CompressionLevel.Optimal).Open();output.Write(contents);}
    private byte[] SafeHostDiagnostics(string body)
    {
        if(body.Length>512*1024)throw new InvalidDataException();
        using var doc=JsonDocument.Parse(body,new(){MaxDepth=16});var input=doc.RootElement;
        if(input.ValueKind!=JsonValueKind.Object)throw new InvalidDataException();
        using var output=new MemoryStream();using(var json=new Utf8JsonWriter(output,new(){Indented=true}))
        {
            json.WriteStartObject();json.WriteString("app","GameDevUsageBar");json.WriteString("version",version);
            if(input.TryGetProperty("framework",out var framework)&&framework.ValueKind==JsonValueKind.String&&Version.TryParse(framework.GetString(),out var parsedVersion))json.WriteString("framework",parsedVersion.ToString());
            if(input.TryGetProperty("sources",out var sources)&&sources.ValueKind==JsonValueKind.Array)
            {
                json.WriteStartArray("sources");foreach(var source in sources.EnumerateArray().Take(32))
                {
                    if(source.ValueKind!=JsonValueKind.Object)continue;json.WriteStartObject();
                    CopyAllowed(json,source,"id",Providers);CopyAllowed(json,source,"channel",Channels);CopyAllowed(json,source,"host",Hosts);CopyStatus(json,source,"status");CopyDate(json,source,"lastAttempt");CopyDate(json,source,"lastSuccess");json.WriteEndObject();
                }json.WriteEndArray();
            }
            if(input.TryGetProperty("localQuotaApi",out var api)&&api.ValueKind==JsonValueKind.Object)
            {
                json.WriteStartObject("localQuotaApi");CopyStatus(json,api,"status");
                if(api.TryGetProperty("address",out var address)&&address.ValueKind==JsonValueKind.String&&Uri.TryCreate(address.GetString(),UriKind.Absolute,out var uri)&&uri.Scheme=="http"&&uri.Host=="127.0.0.1"&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath=="/")json.WriteString("address",uri.ToString());
                json.WriteEndObject();
            }
            if(input.TryGetProperty("events",out var events)&&events.ValueKind==JsonValueKind.Array)
            {
                json.WriteStartArray("events");foreach(var e in events.EnumerateArray().Take(200))
                {
                    if(e.ValueKind!=JsonValueKind.Object)continue;json.WriteStartObject();
                    CopyAllowed(json,e,"Provider",Providers);CopyAllowed(json,e,"Kind",new(StringComparer.Ordinal){"query"});CopyDate(json,e,"At");
                    foreach(var name in new[]{"HttpStatus","Bytes"})if(e.TryGetProperty(name,out var number)&&number.ValueKind==JsonValueKind.Number&&number.TryGetInt64(out var value))json.WriteNumber(name,value);
                    json.WriteEndObject();
                }json.WriteEndArray();
            }
            json.WritePropertyName("runtime");JsonSerializer.Serialize(json,GetSummary(),JsonOptions);json.WriteEndObject();
        }
        return output.ToArray();
    }
    private static void CopyAllowed(Utf8JsonWriter writer,JsonElement source,string name,HashSet<string> values)
    {if(source.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String&&field.GetString() is {} value&&values.Contains(value))writer.WriteString(name,value);}
    private static void CopyStatus(Utf8JsonWriter writer,JsonElement source,string name)
    {
        if(!source.TryGetProperty(name,out var field))return;
        if(field.ValueKind==JsonValueKind.Null){writer.WriteNull(name);return;}
        if(field.ValueKind==JsonValueKind.String&&field.GetString() is {} value&&(value is "NotStarted" or "Listening" or "Unavailable"||Enum.TryParse<GameDevUsageBar.Core.FailureKind>(value,out var kind)&&Enum.IsDefined(kind)))writer.WriteString(name,value);
    }
    private static void CopyDate(Utf8JsonWriter writer,JsonElement source,string name)
    {if(source.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String&&field.TryGetDateTimeOffset(out var date))writer.WriteString(name,date.ToUniversalTime());}
}
