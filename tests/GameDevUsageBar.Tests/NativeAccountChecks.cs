using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

static class NativeAccountChecks
{
    private static void Check(bool value){if(!value)throw new Exception("Native account fixture assertion");}
    private static byte[] Claude(string account,long expires=4070908800000,string access="")=>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken=access.Length>0?access:"nonfunctional-access-"+account,refreshToken="nonfunctional-refresh-"+account,expiresAt=expires},capturedNonOAuth="must-not-transfer"});
    private static string Jwt(string account,long expires=4070908800)=>"e30."+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{sub=account,exp=expires})).TrimEnd('=').Replace('+','-').Replace('/','_')+".fixture";
    private static byte[] Codex(string account,long expires=4070908800)=>JsonSerializer.SerializeToUtf8Bytes(new{auth_mode="chatgpt",tokens=new{access_token=Jwt(account,expires),refresh_token="nonfunctional-refresh-"+account,id_token=Jwt(account),account_id=account}});
    private static async Task<AccountConfig> Saved(NativeAuthVault vault,NativeOAuthStore native,string id,byte[] document)
    {
        var slot=Guid.NewGuid();var reference=await vault.CreateAsync(id,slot,document);var oauth=native.ParseDocument(id,document);
        return new(id,slot,"Fixture",Enabled:true,CredentialRevision:Guid.NewGuid(),SourceMode:"saved-oauth",CredentialSource:"saved-oauth",NativeIdentity:oauth.Identity,AccountId:oauth.AccountId,NativeAuthRef:reference);
    }
    private static async Task Write(NativeOAuthStore native,string id,byte[] document)
    {var path=native.PathFor(id);Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,document);}
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("Saved native credentials are DPAPI encrypted and bound to provider, slot and reference",async()=>{
            var folder=Path.Combine(root,"native-vault-isolation");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var account=await Saved(vault,native,"claude",Claude("alpha"));Check(account.HasUsableCredential);
            Check(vault.Read(account).Token=="nonfunctional-access-alpha");
            var encrypted=await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(folder,"native-auth"),"*.bin").Single());
            Check(!Encoding.UTF8.GetString(encrypted).Contains("nonfunctional-access-alpha")&&!Encoding.UTF8.GetString(encrypted).Contains("nonfunctional-refresh-alpha"));
            foreach(var wrong in new[]{account with {SlotId=Guid.NewGuid()},account with {ProviderId="codex"},account with {NativeIdentity=new string('f',64)},account with {NativeAuthRef=Guid.NewGuid()}})
            {
                bool denied=false;try{vault.Read(wrong);}catch(QueryException){denied=true;}Check(denied);
            }
            var reference=account.NativeAuthRef!.Value;vault.Remove(reference);Check(!File.Exists(Path.Combine(folder,"native-auth",reference.ToString("N")+".bin")));
        });
        yield return ("Saved account queries use isolated tokens without swapping native files or reading API keys",async()=>{
            foreach(var id in new[]{"claude","codex"})
            {
                var folder=Path.Combine(root,"native-query-"+id);var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
                var original=id=="claude"?Claude("local"):Codex("local");await Write(native,id,original);
                var alpha=await Saved(vault,native,id,id=="claude"?Claude("alpha"):Codex("alpha"));var beta=await Saved(vault,native,id,id=="claude"?Claude("beta"):Codex("beta"));
                var seen=new List<string>();var accounts=new List<string>();
                using var handler=new Handler(request=>{
                    seen.Add(request.Headers.Authorization!.Parameter!);
                    if(request.Headers.TryGetValues("ChatGPT-Account-Id",out var accountIds))accounts.Add(accountIds.Single());
                    return new(HttpStatusCode.OK){Content=new StringContent(request.RequestUri!.AbsolutePath.EndsWith("reset-credits")?"{\"available_count\":0}":id=="claude"?"{\"five_hour\":{\"utilization\":20}}":"{\"rate_limit\":{\"primary_window\":{\"used_percent\":20,\"limit_window_seconds\":18000}}}",Encoding.UTF8,"application/json")};
                });
                using var query=new ProviderQueryClient(new NeverSecrets(),adapters.Select(a=>a.Definition),handler,native,nativeVault:vault);
                var adapter=adapters.Single(a=>a.Definition.Id==id);
                Check((await adapter.RefreshAsync(alpha,query,CancellationToken.None)).Failure is null);
                Check((await adapter.RefreshAsync(beta,query,CancellationToken.None)).Failure is null);
                Check(seen.SequenceEqual(id=="claude"?new[]{"nonfunctional-access-alpha","nonfunctional-access-beta"}:new[]{Jwt("alpha"),Jwt("alpha"),Jwt("beta"),Jwt("beta")}));
                if(id=="codex")Check(accounts.SequenceEqual(new[]{"alpha","alpha","beta","beta"}));
                Check(Enumerable.SequenceEqual<byte>(original,await File.ReadAllBytesAsync(native.PathFor(id))));
                Check(alpha.Binding(adapter.Definition)!=beta.Binding(adapter.Definition));
                var text=JsonSerializer.Serialize(query.Events);Check(!text.Contains("nonfunctional")&&!text.Contains("fixture"));
            }
        });
        yield return ("Saved credential expiry and mismatched identity block before all network calls",async()=>{
            var folder=Path.Combine(root,"native-query-expired");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var account=await Saved(vault,native,"claude",Claude("alpha"));
            await vault.UpdateRecognizedAsync(account,Claude("alpha",1));int calls=0;
            using var handler=new Handler(_=>{calls++;return new(HttpStatusCode.OK);});
            using var query=new ProviderQueryClient(new NeverSecrets(),adapters.Select(a=>a.Definition),handler,native,nativeVault:vault);
            var adapter=adapters.Single(a=>a.Definition.Id=="claude");Check((await adapter.RefreshAsync(account,query,CancellationToken.None)).Failure==FailureKind.CredentialExpired&&calls==0);
            await vault.UpdateRecognizedAsync(account,Claude("alpha"));Check((await adapter.RefreshAsync(account with {NativeIdentity=new string('f',64)},query,CancellationToken.None)).Failure==FailureKind.IdentityChanged&&calls==0);
        });
        yield return ("Claude switching preserves destination non-OAuth data and encrypted recoverable old file",async()=>{
            var folder=Path.Combine(root,"native-claude-switch");var home=Path.Combine(folder,"home");var native=new NativeOAuthStore(home);var vault=new NativeAuthVault(folder,native);
            var alpha=await Saved(vault,native,"claude",Claude("alpha"));var beta=await Saved(vault,native,"claude",Claude("beta"));
            var old=JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken="nonfunctional-rotated-alpha",refreshToken="nonfunctional-refresh-alpha",expiresAt=4070908800000},mcpOAuth=new{keep="destination-only"},other=new[]{1,2,3}});
            await Write(native,"claude",old);var config=Path.Combine(home,".claude.json");await File.WriteAllTextAsync(config,"unchanged-non-auth-config");
            var result=await new NativeLoginSwitcher(native,vault,_=>false).SwitchAsync(beta,[alpha,beta]);
            Check(result.Status==NativeAccountStatus.Switched&&result.RecoveryRef is not null);
            var recovery=result.RecoveryRef??throw new Exception("Missing encrypted recovery reference");
            using var published=JsonDocument.Parse(await File.ReadAllBytesAsync(native.PathFor("claude")));
            Check(published.RootElement.GetProperty("claudeAiOauth").GetProperty("accessToken").GetString()=="nonfunctional-access-beta");
            Check(published.RootElement.GetProperty("mcpOAuth").GetProperty("keep").GetString()=="destination-only"&&published.RootElement.GetProperty("other").GetArrayLength()==3&&!published.RootElement.TryGetProperty("capturedNonOAuth",out _));
            Check(vault.Read(alpha).Token=="nonfunctional-rotated-alpha");
            Check(vault.ReadRecovery(recovery,"claude").SequenceEqual(old));
            Check(await File.ReadAllTextAsync(config)=="unchanged-non-auth-config"&&!File.Exists(native.PathFor("claude")+".bak"));
            Check(!Directory.GetFiles(Path.GetDirectoryName(native.PathFor("claude"))!,"*.tmp").Any());
            foreach(var path in Directory.GetFiles(Path.Combine(folder,"native-auth"),"*.bin"))Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("nonfunctional"));
        });
        yield return ("Codex switches only auth.json and recognizes already current without replay",async()=>{
            var folder=Path.Combine(root,"native-codex-switch");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var alpha=await Saved(vault,native,"codex",Codex("alpha"));var beta=await Saved(vault,native,"codex",Codex("beta"));await Write(native,"codex",Codex("alpha"));
            var config=Path.Combine(Path.GetDirectoryName(native.PathFor("codex"))!,"config.toml");await File.WriteAllTextAsync(config,"keep-config");
            var switcher=new NativeLoginSwitcher(native,vault,_=>false);Check((await switcher.SwitchAsync(beta,[alpha,beta])).Status==NativeAccountStatus.Switched);
            Check(native.Read("codex").AccountId=="beta"&&await File.ReadAllTextAsync(config)=="keep-config");
            var before=Directory.GetFiles(Path.Combine(folder,"native-auth"),"*.bin").Length;
            Check((await switcher.SwitchAsync(beta,[alpha,beta])).Status==NativeAccountStatus.AlreadyCurrent&&Directory.GetFiles(Path.Combine(folder,"native-auth"),"*.bin").Length==before);
        });
        yield return ("Busy and concurrent auth writes are refused and never stop or overwrite another writer",async()=>{
            var folder=Path.Combine(root,"native-switch-guards");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var alpha=await Saved(vault,native,"codex",Codex("alpha"));var beta=await Saved(vault,native,"codex",Codex("beta"));var old=Codex("alpha");await Write(native,"codex",old);
            Check((await new NativeLoginSwitcher(native,vault,_=>true).SwitchAsync(beta,[alpha,beta])).Status==NativeAccountStatus.Busy&&Enumerable.SequenceEqual<byte>(old,await File.ReadAllBytesAsync(native.PathFor("codex"))));
            var concurrent=Codex("other-writer");
            var guarded=new NativeLoginSwitcher(native,vault,_=>false,()=>File.WriteAllBytes(native.PathFor("codex"),concurrent));
            Check((await guarded.SwitchAsync(beta,[alpha,beta])).Status==NativeAccountStatus.ConcurrentChange&&Enumerable.SequenceEqual<byte>(concurrent,await File.ReadAllBytesAsync(native.PathFor("codex"))));
        });
        yield return ("Publication exceptions classify target, old and unknown through one readback without retry",async()=>{
            foreach(var mode in new[]{"target","old","unknown"})
            {
                var folder=Path.Combine(root,"native-publication-"+mode);var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
                var alpha=await Saved(vault,native,"codex",Codex("alpha"));var beta=await Saved(vault,native,"codex",Codex("beta"));await Write(native,"codex",Codex("alpha"));int writes=0;
                var switcher=new NativeLoginSwitcher(native,vault,_=>false,publish:(temp,path)=>{writes++;if(mode=="target")File.Replace(temp,path,null);else if(mode=="unknown")File.WriteAllBytes(path,Codex("unexpected"));throw new IOException("fixture publication failure");});
                var result=await switcher.SwitchAsync(beta,[alpha,beta]);Check(writes==1&&result.Status==(mode=="target"?NativeAccountStatus.Switched:mode=="old"?NativeAccountStatus.Failed:NativeAccountStatus.Unknown));
                Check(native.Read("codex").AccountId==(mode=="target"?"beta":mode=="old"?"alpha":"unexpected"));
                Check(vault.HasPendingSwitch("codex")==(mode=="unknown"));
                if(mode=="unknown")
                {
                    var blocked=await new NativeLoginSwitcher(native,vault,_=>false,publish:(_,_)=>{writes++;}).SwitchAsync(beta,[alpha,beta]);
                    Check(blocked.Status==NativeAccountStatus.Unknown&&writes==1);
                }
            }
        });
        yield return ("Expired targets and unsupported providers cannot publish native authentication",async()=>{
            var folder=Path.Combine(root,"native-switch-expired");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var alpha=await Saved(vault,native,"codex",Codex("alpha"));var beta=await Saved(vault,native,"codex",Codex("beta"));var old=Codex("alpha");await Write(native,"codex",old);await vault.UpdateRecognizedAsync(beta,Codex("beta",1));
            var switcher=new NativeLoginSwitcher(native,vault,_=>false);
            Check((await switcher.SwitchAsync(beta,[alpha,beta])).Status==NativeAccountStatus.Expired&&Enumerable.SequenceEqual<byte>(old,await File.ReadAllBytesAsync(native.PathFor("codex"))));
            Check((await switcher.SwitchAsync(beta with {ProviderId="gemini-cli"},[beta])).Status==NativeAccountStatus.Unsupported);
        });
        yield return ("Native source configuration and bounded documents reject forged provider and auth bindings",async()=>{
            var folder=Path.Combine(root,"native-policy");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var vault=new NativeAuthVault(folder,native);
            var account=await Saved(vault,native,"claude",Claude("alpha"));
            foreach(var wrong in new[]{account with {SourceMode="manual"},account with {ProviderId="gemini-cli"},account with {NativeAuthRef=Guid.Empty}})
            {bool denied=false;try{wrong.Validate();}catch(InvalidDataException){denied=true;}Check(denied);}
            await Write(native,"claude",new byte[65537]);bool rejected=false;try{native.ReadDocument("claude");}catch(QueryException){rejected=true;}Check(rejected);
            Check(ProviderSources.Modes("claude").Contains("saved-oauth")&&ProviderSources.Modes("codex").Contains("saved-oauth")&&!ProviderSources.Modes("gemini-cli").Contains("saved-oauth"));
            Check(NativeLoginSwitcher.IsNodeCliCandidate("claude","node C:\\fixture\\node_modules\\@anthropic-ai\\claude-code\\cli.js"));
            Check(NativeLoginSwitcher.IsNodeCliCandidate("codex","node C:/fixture/node_modules/@openai/codex/bin/codex.js"));
            Check(!NativeLoginSwitcher.IsNodeCliCandidate("claude",null)&&!NativeLoginSwitcher.IsNodeCliCandidate("claude","node unrelated-server.js"));
            Check(!NativeLoginSwitcher.IsNodeCliCandidate("codex","node C:/fixture/node_modules/@openai/codex/bin/codex.js app-server"));
        });
    }
    private sealed class NeverSecrets:ISecretStore{public string Read(AccountConfig account)=>throw new Exception("Saved OAuth queried API-key store");}
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> action):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(action(request));}
}
