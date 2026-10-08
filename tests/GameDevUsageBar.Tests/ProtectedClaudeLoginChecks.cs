using System.Text;
using System.Text.Json.Nodes;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

// This transport executes only an authored PowerShell receipt writer. It never
// calls Bridge, Claude, Python, a browser, a credential store or a network API.
static class ProtectedClaudeLoginChecks
{
    private static void Check(bool value,string message="Protected launcher fixture assertion"){if(!value)throw new InvalidOperationException(message);}
    private static AccountConfig Account(string directory)=>new("claude",Guid.NewGuid(),"Synthetic launcher",SourceMode:"local-oauth",ClaudeConfigDirectory:directory);
    private static JsonObject Receipt(AccountConfig c)=>new(){["schema_version"]=1,["status"]="completed_unverified",["process_closed"]=true,["config_dir_sha256"]=ProtectedClaudeLoginLauncher.DirectoryHash(c.ClaudeConfigDirectory!),["attempt_id"]=Guid.NewGuid().ToString("D"),["slot_id"]=c.SlotId.ToString("D"),["automatic_retry"]=false,["cli_started"]=true,["root_exit_observed"]=true,["job_tree_exit_observed"]=true,["exit_code"]=0,["credential_change_verified"]=true,["credential_changed"]=true};
    private static async Task<ClaudeLoginOutcome> Execute(string root,AccountConfig config,string name,string? body)
    {
        var folder=Path.Combine(root,name);Directory.CreateDirectory(folder);var script=Path.Combine(folder,"fixture receipt writer.ps1");
        var expected=Convert.ToBase64String(Encoding.UTF8.GetBytes(config.ClaudeConfigDirectory!));
        var data=body is null?null:Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var source="param([string]$ProfileDirectory,[string]$ResultPath)\n$ErrorActionPreference='Stop'\n"+
            "if($ProfileDirectory -cne [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+expected+"'))){exit 11}\n"+
            "if([IO.File]::Exists($ResultPath)){exit 12}\n"+
            (data is null?"exit 3\n":"[IO.File]::WriteAllBytes($ResultPath,[Convert]::FromBase64String('"+data+"'))\nexit 0\n");
        await File.WriteAllTextAsync(script,source,new UTF8Encoding(false));
        return await new ProtectedClaudeLoginLauncher(folder,script).LoginAsync(config);
    }
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root)
    {
        yield return ("Protected Claude launcher preserves literal paths and accepts only an observed successful synthetic receipt",async()=>{
            var folder=Path.Combine(root,"protected-launcher","space ' 路径");Directory.CreateDirectory(folder);var config=Account(folder);var before=Directory.GetFiles(folder);
            var outcome=await Execute(folder,config,"success",Receipt(config).ToJsonString());Check(outcome==new ClaudeLoginOutcome("completed_unverified",true));
            Check(!File.Exists(Path.Combine(folder,".credentials.json"))&&before.Length==0);
            var results=Directory.GetFiles(Path.Combine(folder,"success","claude-login-results"),"*.json");Check(results.Length==1);
            var result=JsonNode.Parse(await File.ReadAllBytesAsync(results[0]))!.AsObject();Check(result["slot_id"]!.GetValue<string>()==config.SlotId.ToString("D"));
        });
        yield return ("Protected Claude launcher rejects mismatched, incomplete and replay-marked synthetic receipts",async()=>{
            var folder=Path.Combine(root,"protected-launcher","invalid");Directory.CreateDirectory(folder);var config=Account(folder);
            var changes=new (string Field,JsonNode? Value)[]{("schema_version",JsonValue.Create(2)),("config_dir_sha256",JsonValue.Create(new string('0',64))),("slot_id",JsonValue.Create(Guid.NewGuid().ToString("D"))),("attempt_id",JsonValue.Create(Guid.Empty.ToString("D"))),("attempt_id",JsonValue.Create("bad-id")),("automatic_retry",JsonValue.Create(true)),("cli_started",JsonValue.Create(false)),("root_exit_observed",JsonValue.Create(false)),("job_tree_exit_observed",JsonValue.Create(false)),("exit_code",JsonValue.Create(1)),("credential_change_verified",JsonValue.Create(false)),("credential_changed",JsonValue.Create(false)),("status",JsonValue.Create("unsupported")),("slot_id",null)};
            for(var i=0;i<changes.Length;i++){var receipt=Receipt(config);var change=changes[i];if(change.Value is null)receipt.Remove(change.Field);else receipt[change.Field]=change.Value;var outcome=await Execute(folder,config,"invalid-"+i,receipt.ToJsonString());Check(outcome.Status=="unknown"&&outcome.ProcessClosed,"Invalid receipt was accepted: "+change.Field);}
            foreach(var body in new string?[]{null,"{ invalid synthetic",new string('x',16385)}){var outcome=await Execute(folder,config,"unreadable-"+Guid.NewGuid().ToString("N"),body);Check(outcome==new ClaudeLoginOutcome("unknown",true));}
        });
        yield return ("Protected Claude launcher retains original failed, blocked and uncertain outcomes without an automatic retry",async()=>{
            var folder=Path.Combine(root,"protected-launcher","terminal-states");Directory.CreateDirectory(folder);var config=Account(folder);
            foreach(var status in new[]{"failed","blocked","unknown"}){var receipt=Receipt(config);receipt["status"]=status;var outcome=await Execute(folder,config,status,receipt.ToJsonString());Check(outcome==new ClaudeLoginOutcome(status,true));Check(Directory.GetFiles(Path.Combine(folder,status,"claude-login-results"),"*.json").Length==1);}
            var open=Receipt(config);open["process_closed"]=false;var unknown=await Execute(folder,config,"open",open.ToJsonString());Check(unknown==new ClaudeLoginOutcome("unknown",false));
            var noScript=await new ProtectedClaudeLoginLauncher(folder,Path.Combine(folder,"missing.ps1")).LoginAsync(config);Check(noScript==new ClaudeLoginOutcome("blocked",true));
            var unsupported=await new ProtectedClaudeLoginLauncher(folder).LoginAsync(config with {ProviderId="codex"});Check(unsupported==new ClaudeLoginOutcome("blocked",true));
        });
    }
}
