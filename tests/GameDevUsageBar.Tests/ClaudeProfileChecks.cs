using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

// Only synthetic, newly created directories and injected HTTP transports.
static class ClaudeProfileChecks
{
    private const string AccountA="11111111-1111-4111-8111-111111111111",AccountB="33333333-3333-4333-8333-333333333333";
    private const string Organization="22222222-2222-4222-8222-222222222222",OtherOrganization="44444444-4444-4444-8444-444444444444";
    private sealed class Clock:TimeProvider
    {public DateTimeOffset Now=new(2030,1,1,0,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply):HttpMessageHandler
    {public int Posts,Profiles,Usage;protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){if(request.Method==HttpMethod.Post)Posts++;else if(request.RequestUri!.ToString()==NativeClaudeRenewal.ProfileEndpoint)Profiles++;else Usage++;return reply(request,ct);}}
    private sealed class NeverSecrets:ISecretStore{public string Read(AccountConfig c)=>throw new InvalidOperationException("Profile query read API-key store.");}
    private static bool Same(byte[] a,byte[] b)=>a.SequenceEqual(b);
    private static void Check(bool value,string detail="Claude profile fixture assertion"){if(!value)throw new InvalidOperationException(detail);}
    private static HttpResponseMessage Json(object value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Profile(string account=AccountA,string org=Organization)=>Json(new{account=new{uuid=account},organization=new{uuid=org}});
    private static HttpResponseMessage Token(string name)=>Json(new{access_token="nonfunctional-new-access-"+name,refresh_token="nonfunctional-new-refresh-"+name,expires_in=28800,scope="user:profile user:inference"});
    private static byte[] Document(Clock clock,string name,bool expired=false)=>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken="nonfunctional-access-"+name,refreshToken="nonfunctional-refresh-"+name,expiresAt=clock.Now.AddHours(expired?-1:8).ToUnixTimeMilliseconds(),refreshTokenExpiresAt=clock.Now.AddDays(30).ToUnixTimeMilliseconds(),scopes=new[]{"user:profile","user:inference"},subscriptionType="fixture-plan"},keep="unrelated-"+name});
    private static async Task<AccountConfig> Account(NativeOAuthStore native,string folder,Clock clock,string name,bool expired=false,bool active=true)
    {
        var directory=Path.Combine(folder,name);Directory.CreateDirectory(directory);var bytes=Document(clock,name,expired);
        var config=new AccountConfig("claude",Guid.NewGuid(),name,true,CredentialRevision:Guid.NewGuid(),SourceMode:"local-oauth",CredentialSource:"local-oauth",NativeIdentity:native.ParseDocument("claude",bytes,false).Identity,ClaudeConfigDirectory:directory,IsActive:active);
        await File.WriteAllBytesAsync(native.PathFor(config),bytes);return config;
    }
    private static async Task Fails(Func<Task> work,FailureKind expected)
    {try{await work();}catch(QueryException e){Check(e.Kind==expected,e.Kind+" instead of "+expected);return;}throw new InvalidOperationException("Expected synthetic failure.");}
    private static byte[] Transform(byte[] value,string operation)=>(byte[])typeof(DpapiSecretStore).GetMethod(operation,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[value])!;
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("Claude profiles query A and B independently and keep legacy default credentials untouched",async()=>{
            var folder=Path.Combine(root,"claude-profile-queries");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);
            Directory.CreateDirectory(Path.GetDirectoryName(native.PathFor("claude"))!);var legacy=Document(clock,"legacy");await File.WriteAllBytesAsync(native.PathFor("claude"),legacy);
            var a=await Account(native,folder,clock,"a");var b=await Account(native,folder,clock,"b",active:false);var seen=new List<string>();
            var environment=Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            using var handler=new Handler((request,_)=>{seen.Add(request.Headers.Authorization!.Parameter!);return Task.FromResult(Json(new{five_hour=new{utilization=20}}));});
            using var query=new ProviderQueryClient(new NeverSecrets(),adapters.Select(a=>a.Definition),handler,native,clock);
            var adapter=adapters.Single(a=>a.Definition.Id=="claude");
            Check((await adapter.RefreshAsync(a,query,CancellationToken.None)).Failure is null);
            Check((await adapter.RefreshAsync(b,query,CancellationToken.None)).Failure is null);
            Check(seen.SequenceEqual(new[]{"nonfunctional-access-a","nonfunctional-access-b"})&&handler.Usage==2&&handler.Posts==0);
            Check(Same(legacy,await File.ReadAllBytesAsync(native.PathFor("claude")))&&Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")==environment);
            Check(a.Binding(adapter.Definition)!=(a with {ClaudeConfigDirectory=b.ClaudeConfigDirectory}).Binding(adapter.Definition));
            var defaultAccount=a with {ClaudeConfigDirectory=null,NativeIdentity=native.Read("claude").Identity};
            Check(native.ForAccount(a).ForAccount(defaultAccount).Read("claude").Token=="nonfunctional-access-legacy");
        });
        yield return ("Claude profile invalid and nonlocal paths fail before credential reads or transport",async()=>{
            var folder=Path.Combine(root,"claude-profile-invalid");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");
            using var handler=new Handler((_,_)=>throw new InvalidOperationException("Invalid path reached network."));
            using var query=new ProviderQueryClient(new NeverSecrets(),adapters.Select(a=>a.Definition),handler,native,clock);
            foreach(var path in new[]{""," ","relative","C:relative",@"\rooted",@"\\server\share",@"\\?\C:\profile","https://example.invalid/profile",@"C:\bad*path",@"C:\bad.\profile",@"C:\NUL\profile","C:\\profile\n"})
                await Fails(()=>query.ReadAsync(adapters.Single(x=>x.Definition.Id=="claude").Definition,a with {ClaudeConfigDirectory=path},CancellationToken.None),FailureKind.Policy);
            foreach(var bad in new[]{a with {ProviderId="codex"},a with {SourceMode="manual"},a with {SourceMode="saved-oauth"}}){var denied=false;try{bad.Validate();}catch(InvalidDataException){denied=true;}Check(denied);}
            Check(handler.Posts==0&&handler.Profiles==0&&handler.Usage==0);
        });
        yield return ("Claude explicit profile settings use schema 5 and legacy null profiles retain schema 3",async()=>{
            var folder=Path.Combine(root,"claude-profile-schema");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");var settings=new SettingsStore(folder,native:native);
            await settings.SaveAsync([a]);var path=Path.Combine(folder,"settings.json");using(var json=JsonDocument.Parse(await File.ReadAllTextAsync(path)))Check(json.RootElement.GetProperty("Schema").GetInt32()==5);
            Check((await new SettingsStore(folder,native:native).LoadAsync()).Single()==a);
            await settings.SaveAsync([a with {ClaudeConfigDirectory=null}]);using(var json=JsonDocument.Parse(await File.ReadAllTextAsync(path)))Check(json.RootElement.GetProperty("Schema").GetInt32()==3);
            foreach(var schema in new[]{3,4,6}){
                await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{Schema=schema,Accounts=new[]{a}}));var before=await File.ReadAllBytesAsync(path);var invalid=new SettingsStore(folder,native:native);
                Check((await invalid.LoadAsync()).Count==0&&invalid.ReadOnly&&Same(before,await File.ReadAllBytesAsync(path)));
            }
        });
        yield return ("Claude duplicate normalized and legacy effective profiles are refused without settings changes",async()=>{
            var folder=Path.Combine(root,"claude-profile-duplicates");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");var b=await Account(native,folder,clock,"b",active:false);var settings=new SettingsStore(folder,native:native);
            await settings.SaveAsync([a,b]);var path=Path.Combine(folder,"settings.json");var before=await File.ReadAllBytesAsync(path);
            foreach(var pair in new[]{new[]{a,b with {ClaudeConfigDirectory=a.ClaudeConfigDirectory!.ToUpperInvariant()+"/../a/"}},new[]{a with {ClaudeConfigDirectory=null},b with {ClaudeConfigDirectory=Path.GetDirectoryName(native.PathFor("claude"))}}}){
                var denied=false;try{await settings.SaveAsync(pair);}catch(InvalidDataException){denied=true;}Check(denied&&Same(before,await File.ReadAllBytesAsync(path)));
            }
            await settings.SaveAsync([a with {Label="same slot renamed"},b]);Check((await settings.LoadAsync()).Single(c=>c.SlotId==a.SlotId).Label=="same slot renamed");
        });
        yield return ("Claude disabled pending profile saves without credentials and settings writes remain atomic",async()=>{
            var folder=Path.Combine(root,"claude-profile-pending");var native=new NativeOAuthStore(Path.Combine(folder,"home"));var config=new AccountConfig("claude",Guid.NewGuid(),"Pending name",SourceMode:"local-oauth",ClaudeConfigDirectory:Path.Combine(folder,"not-created"));var store=new SettingsStore(folder,native:native);
            await store.SaveAsync([config]);Check(!File.Exists(native.PathFor(config))&&(await store.LoadAsync()).Single()==config);
            var before=await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json"));await store.SaveAsync([config with {Label="Renamed pending"}]);
            Check(Same(before,await File.ReadAllBytesAsync(Path.Combine(folder,"settings.json.bak")))&&Directory.GetFiles(folder,"*.tmp").Length==0);
        });
        yield return ("Claude renewal rotates only selected A and B profiles with per-account journals and native leases",async()=>{
            var folder=Path.Combine(root,"claude-profile-renewal");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a",true);var b=await Account(native,folder,clock,"b",true,false);var current=new Dictionary<Guid,AccountConfig>{{a.SlotId,a},{b.SlotId,b}};
            Directory.CreateDirectory(Path.GetDirectoryName(native.PathFor("claude"))!);var legacy=Document(clock,"legacy");await File.WriteAllBytesAsync(native.PathFor("claude"),legacy);
            var posted=new List<string>();
            using var handler=new Handler(async(request,ct)=>{
                if(request.Method==HttpMethod.Post){using var body=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));var refresh=body.RootElement.GetProperty("refresh_token").GetString()!;posted.Add(refresh);return Token(refresh.EndsWith("-b",StringComparison.Ordinal)?"b":"a");}
                return Profile(request.Headers.Authorization!.Parameter!.EndsWith("-b",StringComparison.Ordinal)?AccountB:AccountA);
            });
            using var service=new NativeClaudeRenewal(folder,native,(old,next)=>{Check(current[old.SlotId]==old);current[next.SlotId]=next;return Task.FromResult(next);},handler,clock,()=>false);
            a=await service.MaintainAsync(a,CancellationToken.None);b=await service.MaintainAsync(b,CancellationToken.None);
            Check(posted.SequenceEqual(new[]{"nonfunctional-refresh-a","nonfunctional-refresh-b"})&&handler.Profiles==2&&handler.Posts==2);
            foreach(var c in new[]{a,b}){
                Check(native.ForAccount(c).Read("claude").Identity==c.NativeIdentity&&service.Observe(c)?.State=="ready"&&!service.BackgroundDue(c));
                var selected=c.Label;using var document=JsonDocument.Parse(await File.ReadAllBytesAsync(native.PathFor(c)));Check(document.RootElement.GetProperty("keep").GetString()=="unrelated-"+selected);
                var journalPath=Path.Combine(folder,"auth-renewal",c.SlotId.ToString("N")+".bin");using var journal=JsonDocument.Parse(Transform(await File.ReadAllBytesAsync(journalPath),"Unprotect"));
                Check(journal.RootElement.GetProperty("Schema").GetInt32()==2&&journal.RootElement.GetProperty("CredentialPath").GetString()==native.PathFor(c));
                Check(journal.RootElement.GetProperty("AccountUuid").GetString()==(selected=="b"?AccountB:AccountA)&&journal.RootElement.GetProperty("OrganizationUuid").GetString()==Organization);
                Check(!Directory.Exists(Path.Combine(c.ClaudeConfigDirectory!,".oauth_refresh.lock"))&&!Directory.Exists(c.ClaudeConfigDirectory+".lock")&&Directory.GetFiles(c.ClaudeConfigDirectory!,"*.tmp").Length==0);
            }
            Check(Same(legacy,await File.ReadAllBytesAsync(native.PathFor("claude"))));await service.MaintainAsync(a,CancellationToken.None);await service.MaintainAsync(b,CancellationToken.None);Check(handler.Posts==2);
        });
        yield return ("Claude pending response cannot write or send requests after the slot directory changes",async()=>{
            var folder=Path.Combine(root,"claude-profile-response-path");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a",true);var old=await File.ReadAllBytesAsync(native.PathFor(a));var failProfile=true;
            using var handler=new Handler((request,_)=>Task.FromResult(request.Method==HttpMethod.Post?Token("a"):failProfile?Json(new{error="synthetic"},HttpStatusCode.ServiceUnavailable):Profile()));
            using(var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false))await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            var directory=Path.Combine(folder,"changed");Directory.CreateDirectory(directory);var changed=a with {ClaudeConfigDirectory=directory};await File.WriteAllBytesAsync(native.PathFor(changed),old);
            var journalPath=Path.Combine(folder,"auth-renewal",a.SlotId.ToString("N")+".bin");var journal=await File.ReadAllBytesAsync(journalPath);failProfile=false;clock.Now=clock.Now.AddSeconds(10);
            using var resumed=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false);
            await Fails(async()=>{await resumed.MaintainAsync(changed,CancellationToken.None);},FailureKind.IdentityChanged);await Fails(()=>resumed.SourceSavedAsync(changed),FailureKind.IdentityChanged);
            Check(handler.Posts==1&&handler.Profiles==1&&Same(old,await File.ReadAllBytesAsync(native.PathFor(changed)))&&Same(old,await File.ReadAllBytesAsync(native.PathFor(a)))&&Same(journal,await File.ReadAllBytesAsync(journalPath)));
            a=await resumed.MaintainAsync(a,CancellationToken.None);Check(handler.Posts==1&&handler.Profiles==2&&native.ForAccount(a).Read("claude").Identity==a.NativeIdentity);
        });
        yield return ("Claude profile binding change during POST preserves response without a native write or replay",async()=>{
            var folder=Path.Combine(root,"claude-profile-live-binding");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a",true);var before=await File.ReadAllBytesAsync(native.PathFor(a));var bound=true;
            using var handler=new Handler((request,_)=>{if(request.Method==HttpMethod.Post){bound=false;return Task.FromResult(Token("a"));}return Task.FromResult(Profile());});
            using(var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false,currentBindingGuard:_=>bound))await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.IdentityChanged);
            Check(Same(before,await File.ReadAllBytesAsync(native.PathFor(a)))&&handler.Posts==1);
            bound=true;using var resumed=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false,currentBindingGuard:_=>bound);a=await resumed.MaintainAsync(a,CancellationToken.None);Check(handler.Posts==1&&native.ForAccount(a).Read("claude").Identity==a.NativeIdentity);
        });
        yield return ("Claude legacy unbound pending journal cannot recover into any profile",async()=>{
            var folder=Path.Combine(root,"claude-profile-legacy-journal");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a",true);
            using var handler=new Handler((request,_)=>request.Method==HttpMethod.Post?Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic timeout")):Task.FromResult(Profile()));
            using(var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false))await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            var path=Path.Combine(folder,"auth-renewal",a.SlotId.ToString("N")+".bin");var journal=JsonNode.Parse(Transform(await File.ReadAllBytesAsync(path),"Unprotect"))!.AsObject();journal["Schema"]=1;journal.Remove("CredentialPath");await File.WriteAllBytesAsync(path,Transform(JsonSerializer.SerializeToUtf8Bytes(journal),"Protect"));var before=await File.ReadAllBytesAsync(path);
            using var resumed=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false);await Fails(async()=>{await resumed.MaintainAsync(a,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            Check(handler.Posts==1&&handler.Profiles==0&&Same(before,await File.ReadAllBytesAsync(path)));
        });
        yield return ("Claude per-account profile cooldown does not borrow another account baseline or deadline",async()=>{
            var folder=Path.Combine(root,"claude-profile-cooldown");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");var b=await Account(native,folder,clock,"b",active:false);
            using var handler=new Handler((request,_)=>{Check(request.Method==HttpMethod.Get);var isA=request.Headers.Authorization!.Parameter!.EndsWith("-a",StringComparison.Ordinal);var response=isA?Json(new{error="rate_limit"},HttpStatusCode.TooManyRequests):Profile(AccountB);if(isA)response.Headers.RetryAfter=new(clock.Now.AddMinutes(20));return Task.FromResult(response);});
            using var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false);await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.AuthRenewalBusy);await service.MaintainAsync(b,CancellationToken.None);
            Check(handler.Posts==0&&handler.Profiles==2&&service.Observe(a)?.State=="scheduled"&&service.Observe(b)?.State=="ready");
            await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.AuthRenewalBusy);Check(handler.Profiles==2);
        });
        yield return ("Claude attended-login pause awaits current renewal and excludes maintenance until released",async()=>{
            var folder=Path.Combine(root,"claude-profile-pause");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler=new Handler(async(_,ct)=>{started.TrySetResult();await release.Task.WaitAsync(ct);return Profile();});
            var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false);
            using var query=new ProviderQueryClient(new NeverSecrets(),adapters.Select(a=>a.Definition),handler,native,clock,renewal:service);
            var first=query.MaintainAsync(a,CancellationToken.None);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pause=query.PauseClaudeRenewalAsync();Check(!pause.IsCompleted);release.SetResult();await first;var lease=await pause;
            var blocked=query.MaintainAsync(a,CancellationToken.None);Check(!blocked.IsCompleted&&handler.Profiles==1&&handler.Posts==0);
            lease.Dispose();lease.Dispose();await blocked;Check(handler.Profiles==1&&handler.Posts==0);
        });
        yield return ("Claude external profile rotation rejects organization mismatch even when account UUID matches",async()=>{
            var folder=Path.Combine(root,"claude-profile-org");var clock=new Clock();var native=new NativeOAuthStore(Path.Combine(folder,"home"),clock);var a=await Account(native,folder,clock,"a");var changed=false;
            using var handler=new Handler((request,_)=>Task.FromResult(Profile(org:changed?OtherOrganization:Organization)));
            using var service=new NativeClaudeRenewal(folder,native,(_,next)=>Task.FromResult(next),handler,clock,()=>false);await service.MaintainAsync(a,CancellationToken.None);changed=true;var external=Document(clock,"external");await File.WriteAllBytesAsync(native.PathFor(a),external);
            await Fails(async()=>{await service.MaintainAsync(a,CancellationToken.None);},FailureKind.IdentityChanged);Check(handler.Posts==0&&handler.Profiles==2&&Same(external,await File.ReadAllBytesAsync(native.PathFor(a))));
        });
    }
}
