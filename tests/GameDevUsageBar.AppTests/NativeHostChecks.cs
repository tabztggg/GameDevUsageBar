using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GameDevUsageBar.App;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

// ApplicationHost integration using only explicitly injected fixture homes. No
// default credential location, real account query, CLI launch or actual switch.
internal static class NativeHostChecks
{
    public static async Task Run(string root)
    {
        Directory.CreateDirectory(root);
        var results=new List<object>();int failed=0;
        async Task Check(string name,Func<Task> test)
        {
            try{await test();results.Add(new{name,status="PASS"});Console.WriteLine("PASS "+name);}
            catch(Exception error){failed++;results.Add(new{name,status="FAIL",error=error.ToString()});Console.WriteLine("FAIL "+name+" "+error.Message);}
        }
        string Folder(string name)=>Path.Combine(root,"native-host-fixtures",name+"-"+Guid.NewGuid().ToString("N"));
        await Check("Native Host capture replaces a manual key with an encrypted saved login and schema 4",async()=>{
            var folder=Folder("manual-capture");var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            await using var host=Host(folder,native);await host.InitializeAsync();
            var config=host.GetActiveAccount("claude");await host.SaveAsync(config with {Enabled=true},"nonfunctional-manual-token");
            var manual=host.GetActiveAccount("claude");var key=manual.CredentialRef??throw new Exception("Fixture manual key was not saved");
            var document=Claude("alpha");await Write(native,"claude",document);
            var result=await host.CaptureCurrentLoginAsync("claude",manual.SlotId);Assert(result.Status==NativeAccountStatus.Captured,"Capture did not succeed");
            var captured=host.GetActiveAccount("claude");Assert(captured.Enabled&&captured.SourceMode=="saved-oauth"&&captured.CredentialSource=="saved-oauth"&&captured.HasUsableCredential,"Saved source was not enabled/bound");
            Assert(captured.CredentialRef is null&&captured.NativeAuthRef is not null&&captured.CredentialRevision!=manual.CredentialRevision,"Manual key was retained or credential revision was reused");
            Assert(!File.Exists(Path.Combine(folder,"secrets",key.ToString("N")+".bin")),"Replaced manual key file was retained");
            Assert(host.Queries.NativeVault!.Read(captured).Token=="nonfunctional-access-alpha","Captured credential cannot be read for its slot");
            Assert((await File.ReadAllBytesAsync(native.PathFor("claude"))).SequenceEqual(document),"Capture rewrote the CLI auth file");
            Assert(host.Queries.Events.Count==0,"Capture caused an account/network query");
            Assert(await Schema(folder)==4,"Saved native source did not use schema 4");
            var settings=await File.ReadAllTextAsync(Path.Combine(folder,"settings.json"));Assert(!settings.Contains("nonfunctional"),"A token leaked into account settings");
            foreach(var path in Directory.GetFiles(Path.Combine(folder,"native-auth"),"*.bin"))Assert(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("nonfunctional"),"Native document was stored in plaintext");
        });
        await Check("Native Host captures two independent slots and recapture removes only the replaced secret",async()=>{
            var folder=Folder("independent-slots");var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            await using var host=Host(folder,native);await host.InitializeAsync();
            var alpha=host.GetActiveAccount("claude");var codexBefore=host.GetActiveAccount("codex");
            await Write(native,"claude",Claude("alpha"));Assert((await host.CaptureCurrentLoginAsync("claude",alpha.SlotId)).Succeeded,"Alpha capture failed");alpha=host.GetActiveAccount("claude");
            var alphaRef=alpha.NativeAuthRef??throw new Exception("Missing alpha reference");var beta=await host.AddAccountAsync("claude","Beta");
            await Write(native,"claude",Claude("beta"));Assert((await host.CaptureCurrentLoginAsync("claude",beta.SlotId)).Succeeded,"Beta capture failed");beta=host.GetAccounts("claude").Single(a=>a.SlotId==beta.SlotId);
            var betaRef=beta.NativeAuthRef??throw new Exception("Missing beta reference");
            Assert(alphaRef!=betaRef&&alpha.NativeIdentity!=beta.NativeIdentity,"Two CLI accounts share a binding");
            Assert(host.GetActiveAccount("claude").SlotId==alpha.SlotId&&!beta.IsActive,"Importing a second account selected it implicitly");
            Assert(host.GetActiveAccount("codex")==codexBefore,"Capture changed another provider configuration");
            Assert(host.Queries.NativeVault!.Read(alpha).Token=="nonfunctional-access-alpha"&&host.Queries.NativeVault.Read(beta).Token=="nonfunctional-access-beta","Saved slots mixed their credentials");
            var betaBytes=await File.ReadAllBytesAsync(Path.Combine(folder,"native-auth",betaRef.ToString("N")+".bin"));
            var rotated=Claude("alpha",access:"nonfunctional-renewed-alpha");await Write(native,"claude",rotated);
            Assert((await host.CaptureCurrentLoginAsync("claude",alpha.SlotId)).Succeeded,"Recapture failed");var recaptured=host.GetActiveAccount("claude");
            Assert(recaptured.NativeAuthRef!=alphaRef&&recaptured.CredentialRevision!=alpha.CredentialRevision,"Recapture reused the old secret reference/revision");
            Assert(!File.Exists(Path.Combine(folder,"native-auth",alphaRef.ToString("N")+".bin"))&&!File.Exists(Path.Combine(folder,"native-auth",alphaRef.ToString("N")+".bin.bak")),"Replaced native secret remains on disk");
            Assert((await File.ReadAllBytesAsync(Path.Combine(folder,"native-auth",betaRef.ToString("N")+".bin"))).SequenceEqual(betaBytes),"Recapture changed the other slot secret");
            Assert(host.Queries.NativeVault.Read(recaptured).Token=="nonfunctional-renewed-alpha"&&host.Queries.Events.Count==0,"Recapture did not retain the renewed credential or queried a service");
        });
        await Check("Native Host display selection and saved-slot removal never modify CLI authentication",async()=>{
            var folder=Folder("selection-removal");var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            await using var host=Host(folder,native);await host.InitializeAsync();
            var alpha=host.GetActiveAccount("claude");await Write(native,"claude",Claude("alpha"));await host.CaptureCurrentLoginAsync("claude",alpha.SlotId);alpha=host.GetActiveAccount("claude");
            var beta=await host.AddAccountAsync("claude","Beta");await Write(native,"claude",Claude("beta"));await host.CaptureCurrentLoginAsync("claude",beta.SlotId);beta=host.GetAccounts("claude").Single(a=>a.SlotId==beta.SlotId);
            var nativeBytes=await File.ReadAllBytesAsync(native.PathFor("claude"));
            await host.SelectAccountAsync("claude",beta.SlotId);Assert(host.GetActiveAccount("claude").SlotId==beta.SlotId&&host.Coordinator.Get("claude").Config.SlotId==beta.SlotId,"Display selection was not applied");
            Assert((await File.ReadAllBytesAsync(native.PathFor("claude"))).SequenceEqual(nativeBytes),"Display selection switched the actual auth file");
            await host.RemoveAccountAsync("claude",alpha.SlotId);
            Assert(host.GetAccounts("claude").Count==1&&host.GetActiveAccount("claude").SlotId==beta.SlotId,"Removal changed the remaining selected slot");
            var alphaRef=alpha.NativeAuthRef??throw new Exception("Missing removed reference");Assert(!File.Exists(Path.Combine(folder,"native-auth",alphaRef.ToString("N")+".bin")),"Removed slot credential was retained");
            Assert(host.Queries.NativeVault!.Read(host.GetActiveAccount("claude")).Token=="nonfunctional-access-beta","Removing alpha damaged beta");
            Assert((await File.ReadAllBytesAsync(native.PathFor("claude"))).SequenceEqual(nativeBytes)&&host.Queries.Events.Count==0,"Removing a saved account altered native auth or queried a provider");
        });
        await Check("Native Host restart preserves both saved slots, selected account and read-only auth bytes",async()=>{
            var folder=Folder("restart");var native=new NativeOAuthStore(Path.Combine(folder,"home"));Guid alphaSlot,betaSlot;AccountConfig[] saved;byte[] auth,settings;
            await using(var host=Host(folder,native))
            {
                await host.InitializeAsync();var alpha=host.GetActiveAccount("codex");alphaSlot=alpha.SlotId;await Write(native,"codex",Codex("alpha"));await host.CaptureCurrentLoginAsync("codex",alphaSlot);
                var beta=await host.AddAccountAsync("codex","Beta");betaSlot=beta.SlotId;await Write(native,"codex",Codex("beta"));await host.CaptureCurrentLoginAsync("codex",betaSlot);await host.SelectAccountAsync("codex",betaSlot);
                saved=host.GetAccounts("codex").ToArray();auth=await File.ReadAllBytesAsync(native.PathFor("codex"));settings=await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"));
            }
            await using var restarted=Host(folder,native);await restarted.InitializeAsync();
            Assert(restarted.GetAccounts("codex").SequenceEqual(saved)&&restarted.GetActiveAccount("codex").SlotId==betaSlot,"Restart changed native account configuration or selection");
            Assert(restarted.Queries.NativeVault!.Read(restarted.GetAccounts("codex").Single(a=>a.SlotId==alphaSlot)).AccountId=="alpha"&&restarted.Queries.NativeVault.Read(restarted.GetActiveAccount("codex")).AccountId=="beta","Restart cannot read both isolated native accounts");
            Assert((await File.ReadAllBytesAsync(native.PathFor("codex"))).SequenceEqual(auth)&&(await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"))).SequenceEqual(settings),"Restart rewrote saved accounts or native auth");
            Assert(restarted.Queries.Events.Count==0,"Restart queried actual services");
        });
        await Check("Native Host expectedCurrent rejects a changed or deleted account without resurrecting it",async()=>{
            var folder=Folder("expected-current");var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            await using var host=Host(folder,native);await host.InitializeAsync();
            var original=await host.AddAccountAsync("claude","Temporary");await host.SaveAsync(original with {Label="Changed"});
            var before=await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"));
            await MustReject(()=>host.SaveAsync(original with {Label="Stale overwrite"},expectedCurrent:original));
            Assert(host.GetAccounts("claude").Single(a=>a.SlotId==original.SlotId).Label=="Changed"&&(await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"))).SequenceEqual(before),"Stale expectedCurrent overwrote an account");
            var deleted=host.GetAccounts("claude").Single(a=>a.SlotId==original.SlotId);await host.RemoveAccountAsync("claude",deleted.SlotId);before=await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"));
            await MustReject(()=>host.SaveAsync(deleted,expectedCurrent:deleted));
            Assert(!host.GetAccounts("claude").Any(a=>a.SlotId==deleted.SlotId)&&(await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"))).SequenceEqual(before),"Deleted account was resurrected by capture persistence");
            var active=host.GetActiveAccount("claude");var inactive=await host.AddAccountAsync("claude","Selection only");await host.SelectAccountAsync("claude",inactive.SlotId);
            await host.SaveAsync(inactive with {Label="Selection preserved"},expectedCurrent:inactive);
            Assert(host.GetActiveAccount("claude").SlotId==inactive.SlotId&&!host.GetAccounts("claude").Single(a=>a.SlotId==active.SlotId).IsActive,"Harmless selection change was rejected or overwritten");
        });
        await Check("Native Host missing and expired captures leave all saved account data untouched",async()=>{
            var folder=Folder("failed-capture");var native=new NativeOAuthStore(Path.Combine(folder,"home"));
            await using var host=Host(folder,native);await host.InitializeAsync();var account=host.GetActiveAccount("claude");await Write(native,"claude",Claude("alpha"));await host.CaptureCurrentLoginAsync("claude",account.SlotId);account=host.GetActiveAccount("claude");
            var before=await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"));var secrets=EncryptedFiles(folder);
            await Write(native,"claude",Claude("expired",1));var expiredAuth=await File.ReadAllBytesAsync(native.PathFor("claude"));
            Assert((await host.CaptureCurrentLoginAsync("claude",account.SlotId)).Status==NativeAccountStatus.Expired,"Expired import was accepted");
            Assert((await File.ReadAllBytesAsync(native.PathFor("claude"))).SequenceEqual(expiredAuth),"Expired capture mutated the fixture auth file");
            File.Delete(native.PathFor("claude"));Assert((await host.CaptureCurrentLoginAsync("claude",account.SlotId)).Status==NativeAccountStatus.Missing,"Missing auth file was accepted");
            Assert(host.GetActiveAccount("claude")==account&&(await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"))).SequenceEqual(before)&&EncryptedFiles(folder).OrderBy(p=>p.Key).SequenceEqual(secrets.OrderBy(p=>p.Key)),"Rejected capture changed saved accounts or encrypted credentials");
            Assert(host.Queries.Events.Count==0,"Rejected capture caused a network request");
        });
        await File.WriteAllTextAsync(Path.Combine(root,"native-host-results.json"),JsonSerializer.Serialize(new{passed=results.Count-failed,failed,results},new JsonSerializerOptions{WriteIndented=true}));
        if(failed>0)throw new Exception($"Native host checks failed: {failed} of {results.Count}");
    }
    private static ApplicationHost Host(string root,NativeOAuthStore native)=>new(root,ProviderCatalog.Create().Where(a=>a.Definition.Id is "claude" or "codex").ToArray(),native);
    private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task MustReject(Func<Task> action)
    {bool rejected=false;try{await action();}catch(InvalidOperationException){rejected=true;}Assert(rejected,"Concurrent account change was accepted");}
    private static async Task<int> Schema(string root){using var document=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,"settings.json")));return document.RootElement.GetProperty("Schema").GetInt32();}
    private static Dictionary<string,string> EncryptedFiles(string root)=>Directory.GetFiles(Path.Combine(root,"native-auth"),"*",SearchOption.TopDirectoryOnly).ToDictionary(path=>Path.GetFileName(path)??throw new Exception("Missing fixture filename"),path=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private static async Task Write(NativeOAuthStore native,string provider,byte[] bytes)
    {var path=native.PathFor(provider);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,bytes);}
    private static byte[] Claude(string account,long expires=4070908800000,string access="")=>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken=access.Length>0?access:"nonfunctional-access-"+account,refreshToken="nonfunctional-refresh-"+account,expiresAt=expires}});
    private static string Jwt(string account)=>"e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{sub=account,exp=4070908800})).TrimEnd('=').Replace('+','-').Replace('/','_')+".fixture";
    private static byte[] Codex(string account)=>JsonSerializer.SerializeToUtf8Bytes(new{tokens=new{access_token=Jwt(account),id_token=Jwt(account),refresh_token="nonfunctional-refresh-"+account,account_id=account}});
}
