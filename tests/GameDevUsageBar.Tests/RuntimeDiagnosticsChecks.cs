using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Infrastructure;

internal static class RuntimeDiagnosticsChecks
{
    private const string Canary="synthetic-runtime-secret-canary-710492";
    private static string FixtureRoot(){var root=Path.Combine(Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT")??Path.Combine(Path.GetTempPath(),"WorkBuddy-Tasks","work","gamedevusagebar-runtime-diagnostics-20261006","workspace"),"runtime-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);return root;}
    private static void Check(bool value,string reason="Runtime diagnostics check failed"){if(!value)throw new Exception(reason);}
    private static string Logs(string root)=>string.Join("\n",Directory.GetFiles(Path.Combine(root,"logs"),"runtime*.jsonl").Select(File.ReadAllText));
    private static string ZipText(string path){using var zip=ZipFile.OpenRead(path);return string.Join("\n",zip.Entries.Select(entry=>{using var stream=entry.Open();using var reader=new StreamReader(stream);return reader.ReadToEnd();}));}
    private static Exception Thrown(string message)
    {try{throw new InvalidOperationException(message,new ArgumentException(message));}catch(Exception error){error.Data["secret"]=message;return error;}}
    public static IEnumerable<(string Name,Func<Task> Run)> Cases()
    {
        yield return ("Runtime diagnostics distinguish clean exit, known fatal, observed unexpected exit, and unknown incomplete termination",()=>{
            var root=FixtureRoot();
            using(var log=new RuntimeDiagnostics(root,"0.9.2+0123456789abcdef")){Check(log.BeginSession());Check(log.GetSummary().PreviousSessionStatus=="no_previous_session");log.CompleteSession("tray_exit");}
            using(var log=new RuntimeDiagnostics(root,"0.9.2")){Check(log.BeginSession()&&log.GetSummary().PreviousSessionStatus=="graceful_exit");log.RecordException("appdomain_unhandled",Thrown(Canary));log.CompleteSession("fatal_exception",1);}
            using(var log=new RuntimeDiagnostics(root,"0.9.2")){Check(log.BeginSession()&&log.GetSummary().PreviousSessionStatus=="fatal_exception");log.CompleteSession("process_exit_without_shutdown");}
            using(var log=new RuntimeDiagnostics(root,"0.9.2")){Check(log.BeginSession()&&log.GetSummary().PreviousSessionStatus=="unexpected_exit");/* Simulate missing final receipt: Dispose must not imply a clean exit. */}
            using(var log=new RuntimeDiagnostics(root,"0.9.2")){Check(log.BeginSession()&&log.GetSummary().PreviousSessionStatus=="incomplete");Check(log.Recent.Any(e=>e.Event=="previous_session_incomplete"&&e.Reason=="unclean_termination_unknown"));log.CompleteSession("clean_exit");}
            return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics duplicate launch cannot replace the primary marker or end its session",()=>{
            var root=FixtureRoot();using var primary=new RuntimeDiagnostics(root,"0.9.2");Check(primary.BeginSession());
            var marker=Path.Combine(primary.LogDirectory,"session.json");var before=File.ReadAllText(marker);
            var logBefore=Logs(root);
            using(var duplicate=new RuntimeDiagnostics(root,"0.9.2")){duplicate.Record("duplicate_instance","duplicate_instance");Check(!duplicate.BeginSession());for(int i=0;i<3000;i++)duplicate.Record("duplicate_instance","duplicate_instance");duplicate.CompleteSession("duplicate_instance");Check(File.ReadAllText(marker)==before&&Logs(root)==logBefore);}
            primary.CompleteSession("overview_exit");using var doc=JsonDocument.Parse(File.ReadAllText(marker));Check(doc.RootElement.GetProperty("reason").GetString()=="overview_exit");return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics never persist exception message, Data, unapproved event/reason or exported arbitrary secret fields",()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());
            log.Record(Canary,Canary);log.RecordException("handled_exception",Thrown(Canary));
            var entry=log.Recent.Last();Check(entry.Exception?.Type=="System.InvalidOperationException"&&entry.Exception.Methods.Length>0&&entry.Exception.Inner.Single().Type=="System.ArgumentException");
            Check(!Logs(root).Contains(Canary,StringComparison.Ordinal));
            var body=JsonSerializer.Serialize(new{app=Canary,version=Canary,framework="10.0.12",token=Canary,auth=new{refresh_token=Canary},sources=new[]{new{id="codex",channel="Subscription",host="chatgpt.com",label=Canary,cookie=Canary,status=(string?)null,lastAttempt=DateTimeOffset.UtcNow,lastSuccess=(DateTimeOffset?)null}},events=new[]{new{Provider="codex",Kind="query",At=DateTimeOffset.UtcNow,HttpStatus=200,Bytes=100,Password=Canary},new{Provider=Canary,Kind=Canary,At=DateTimeOffset.UtcNow,HttpStatus=200,Bytes=0,Password=Canary}},localQuotaApi=new{status="Listening",address="http://127.0.0.1:17864/",apiKey=Canary},runtime=new{secret=Canary}});
            var zip=Path.Combine(root,"diagnostics.zip");Check(log.ExportBundle(zip,body));var text=ZipText(zip);Check(!text.Contains(Canary,StringComparison.Ordinal)&&text.Contains("chatgpt.com",StringComparison.Ordinal)&&text.Contains("System.InvalidOperationException",StringComparison.Ordinal));return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics rotate at their bound, retain a bounded recent ring and preserve flushed final exit evidence",()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2",4096);Check(log.BeginSession());
            for(int i=0;i<300;i++)log.Record("app_ready");log.CompleteSession("tray_exit");
            var files=Directory.GetFiles(log.LogDirectory,"runtime*.jsonl");Check(files.Length==RuntimeDiagnostics.MaximumLogFiles&&files.All(p=>new FileInfo(p).Length<=4096));Check(log.Recent.Count==RuntimeDiagnostics.MaximumRecentEntries&&log.Recent.Last().Event=="session_end");
            var active=Path.Combine(log.LogDirectory,"runtime.jsonl");using var last=JsonDocument.Parse(File.ReadLines(active).Last());Check(last.RootElement.GetProperty("reason").GetString()=="tray_exit"&&last.RootElement.GetProperty("processId").GetInt32()==Environment.ProcessId&&last.RootElement.GetProperty("timestampUtc").GetDateTimeOffset().Offset==TimeSpan.Zero&&last.RootElement.GetProperty("timestampLocal").TryGetDateTimeOffset(out _));return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics permission/path failures stay nonthrowing and preserve bounded in-memory fallback",()=>{
            var root=FixtureRoot();var blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"file blocks directory");
            using var log=new RuntimeDiagnostics(blocked,"0.9.2");Check(!log.BeginSession());for(int i=0;i<110;i++)log.RecordException("handled_exception",Thrown(Canary));log.SampleResources();log.CompleteSession("clean_exit");Check(!log.GetSummary().LoggingAvailable&&log.Recent.Count==RuntimeDiagnostics.MaximumRecentEntries&&log.Recent.Any(e=>e.Exception is not null));
            Check(log.ExportBundle(Path.Combine(root,"fallback.zip"),"{}")&&!ZipText(Path.Combine(root,"fallback.zip")).Contains(Canary,StringComparison.Ordinal));
            using var invalid=new RuntimeDiagnostics("invalid\0root","0.9.2");Check(!invalid.BeginSession());invalid.Record("app_ready");Check(!invalid.GetSummary().LoggingAvailable);return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics sample private/working/GC bytes, handles, threads and cache metadata without reading cached contents",()=>{
            var root=FixtureRoot();Directory.CreateDirectory(Path.Combine(root,"cache"));File.WriteAllText(Path.Combine(root,"cache","fixture.json"),Canary);File.WriteAllText(Path.Combine(root,"cache","ignored.txt"),Canary);
            using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());log.SampleResources();var sample=log.GetSummary().LastResources;
            Check(sample is not null&&sample.PrivateBytes>0&&sample.WorkingSetBytes>0&&sample.ManagedBytes>0&&sample.Handles>0&&sample.Threads>0&&sample.UptimeSeconds>=0&&sample.CacheFiles==1&&sample.CacheBytes==Encoding.UTF8.GetByteCount(Canary)&&!Logs(root).Contains(Canary,StringComparison.Ordinal));return Task.CompletedTask;
        });
        yield return ("Runtime diagnostic ZIP exports only bounded owned logs and safe summaries, excludes data/auth files and refuses replacement",()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());log.Record("app_ready");
            File.WriteAllText(Path.Combine(root,"auth.json"),Canary);File.WriteAllText(Path.Combine(log.LogDirectory,"unrelated.jsonl"),Canary);File.WriteAllText(Path.Combine(log.LogDirectory,"runtime.9.jsonl"),Canary);
            var destination=Path.Combine(root,"export.zip");Check(log.ExportBundle(destination,"{}"));
            using(var zip=ZipFile.OpenRead(destination)){Check(zip.Entries.Count<=RuntimeDiagnostics.MaximumLogFiles+3&&zip.Entries.All(e=>!e.FullName.Contains("auth",StringComparison.Ordinal)&&!e.FullName.Contains("unrelated",StringComparison.Ordinal)&&!e.FullName.Contains("runtime.9",StringComparison.Ordinal)));}
            Check(new FileInfo(destination).Length<=RuntimeDiagnostics.MaximumBundleBytes&&!ZipText(destination).Contains(Canary,StringComparison.Ordinal));var before=File.ReadAllBytes(destination);Check(!log.ExportBundle(destination,"{}")&&before.SequenceEqual(File.ReadAllBytes(destination)));return Task.CompletedTask;
        });
        yield return ("Runtime diagnostics cap deep/aggregate exception evidence and ignore oversized or malformed previous markers",()=>{
            var root=FixtureRoot();Directory.CreateDirectory(Path.Combine(root,"logs"));File.WriteAllText(Path.Combine(root,"logs","session.json"),new string('x',5000));
            using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession()&&log.GetSummary().PreviousSessionStatus=="unknown");
            Exception error=Thrown(Canary);for(int i=0;i<10;i++)error=new AggregateException(Canary,Enumerable.Repeat(error,20));log.RecordException("dispatcher_unhandled",error);
            var serialized=JsonSerializer.SerializeToUtf8Bytes(log.Recent.Last());Check(serialized.Length<=RuntimeDiagnostics.MaximumEntryBytes&&!Encoding.UTF8.GetString(serialized).Contains(Canary,StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(log.LogDirectory,"runtime.4.jsonl"),new byte[RuntimeDiagnostics.MaximumLogFileBytes+1]);var zip=Path.Combine(root,"bounded.zip");Check(log.ExportBundle(zip,"{}"));using(var archive=ZipFile.OpenRead(zip)){Check(archive.Entries.All(e=>e.FullName!="logs/runtime.4.jsonl"));}
            log.Record("app_ready");Check(Directory.GetFiles(log.LogDirectory,"runtime*.jsonl").All(p=>new FileInfo(p).Length<=RuntimeDiagnostics.MaximumLogFileBytes));return Task.CompletedTask;
        });
        yield return ("Runtime diagnostic export sanitizes externally injected recognized log records instead of copying arbitrary text or fields",()=>{
            var root=FixtureRoot();using var log=new RuntimeDiagnostics(root,"0.9.2");Check(log.BeginSession());log.Record("app_ready");
            var path=Path.Combine(log.LogDirectory,"runtime.jsonl");var valid=File.ReadLines(path).Last();
            using var original=JsonDocument.Parse(valid);var injected=JsonSerializer.Serialize(new{schema=1,timestampUtc=DateTimeOffset.UtcNow,timestampLocal=DateTimeOffset.Now,sequence=500,sessionId=Canary,processId=Environment.ProcessId,version=Canary,@event=Canary,reason=Canary,exitCode=0,message=Canary,requestUrl=Canary,exception=new{type=Canary,hResult=1,methods=new[]{Canary,"System.InvalidOperationException."+Canary},inner=Array.Empty<object>(),message=Canary},resources=(object?)null});
            File.AppendAllText(path,"\n"+Canary+"\n"+injected+"\n"+new string('x',RuntimeDiagnostics.MaximumEntryBytes+1)+"\n");
            var destination=Path.Combine(root,"sanitized.zip");Check(log.ExportBundle(destination,"{}"));var exported=ZipText(destination);Check(!exported.Contains(Canary,StringComparison.Ordinal)&&exported.Contains("app_ready",StringComparison.Ordinal)&&exported.Contains("unknown_exception",StringComparison.Ordinal));
            using var zip=ZipFile.OpenRead(destination);foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith("logs/",StringComparison.Ordinal))){using var stream=entry.Open();using var reader=new StreamReader(stream);while(reader.ReadLine() is {} line){using var doc=JsonDocument.Parse(line);Check(doc.RootElement.GetProperty("schema").GetInt32()==1);}}
            return Task.CompletedTask;
        });
    }
}
