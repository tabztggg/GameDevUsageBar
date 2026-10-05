using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;

// Every credential, journal and transport in these checks belongs to a new QA
// directory. No real CLI, account, environment credential or network is used.
static class ClaudeRenewalChecks
{
    private const string Account="11111111-1111-4111-8111-111111111111";
    private const string Organization="22222222-2222-4222-8222-222222222222";
    private const string OtherAccount="33333333-3333-4333-8333-333333333333";
    private static readonly string[] Scopes=["user:profile","user:inference","user:file_upload"];
    private static void Check(bool value,string message="Claude renewal fixture assertion")
    {if(!value)throw new InvalidOperationException(message);}
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now {get;set;}=new(2030,1,1,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private sealed class Transport:HttpMessageHandler
    {
        public int Posts,Profiles;
        public readonly List<string> PostedRefresh=[];
        public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? Reply;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Check(request.RequestUri?.Scheme=="https"&&request.RequestUri.UserInfo.Length==0);
            if(request.Method==HttpMethod.Post)
            {
                Check(request.RequestUri!.ToString()==NativeClaudeRenewal.TokenEndpoint);
                Posts++;
                using var body=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
                var json=body.RootElement;
                Check(json.GetProperty("grant_type").GetString()=="refresh_token");
                Check(json.GetProperty("refresh_token").GetString()!.StartsWith("nonfunctional-",StringComparison.Ordinal));
                PostedRefresh.Add(json.GetProperty("refresh_token").GetString()!);
                Check(json.GetProperty("scope").GetString()==string.Join(" ",Scopes));
                Check(request.Content.Headers.ContentType?.MediaType=="application/json");
                Check(!request.Headers.Contains("Authorization"));
            }
            else
            {
                Check(request.Method==HttpMethod.Get&&request.RequestUri!.ToString()==NativeClaudeRenewal.ProfileEndpoint);
                Profiles++;
                Check(request.Headers.Authorization?.Scheme=="Bearer"&&request.Headers.Authorization.Parameter!.StartsWith("nonfunctional-",StringComparison.Ordinal));
                Check(request.Headers.CacheControl?.NoCache==true);
            }
            return Reply is null?request.Method==HttpMethod.Post?Token():Profile():await Reply(request,ct);
        }
    }
    private sealed class Fixture
    {
        public readonly string Root;
        public readonly Clock Time=new();
        public readonly Transport Http=new();
        public readonly NativeOAuthStore Native;
        public AccountConfig Current;
        public int Binds;
        public Func<AccountConfig,AccountConfig,Task<AccountConfig>>? Binding;
        public Fixture(string root,string name,bool expired=true)
        {
            Root=Path.Combine(root,"claude-renewal",name+"-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);Native=new(Path.Combine(Root,"home"),Time);
            var path=Native.PathFor("claude");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path,Document(expired?Time.Now.AddMinutes(-1):Time.Now.AddHours(8),Time.Now.AddDays(30)));
            Current=new("claude",Guid.NewGuid(),"Synthetic",Enabled:true,SourceMode:"local-oauth",CredentialSource:"local-oauth",NativeIdentity:Native.ParseDocument("claude",File.ReadAllBytes(path),false).Identity);
        }
        public NativeClaudeRenewal Service(Func<bool>? busy=null)=>new(Root,Native,async(old,next)=>
        {
            Binds++;
            if(Binding is not null)return await Binding(old,next);
            Check(old.SlotId==Current.SlotId&&old.NativeIdentity==Current.NativeIdentity);
            Current=next;return next;
        },Http,Time,busy??(()=>false));
        public JsonObject ReadRoot()=>JsonNode.Parse(File.ReadAllBytes(Native.PathFor("claude")))!.AsObject();
        public JsonObject OAuth()=>ReadRoot()["claudeAiOauth"]!.AsObject();
        public void Write(JsonObject document)=>File.WriteAllBytes(Native.PathFor("claude"),JsonSerializer.SerializeToUtf8Bytes(document));
        public string JournalPath=>Path.Combine(Root,"auth-renewal",Current.SlotId.ToString("N")+".bin");
    }
    private static byte[] Document(DateTimeOffset expiry,DateTimeOffset refreshExpiry,string refresh="nonfunctional-old-refresh",string access="nonfunctional-old-access")
        =>JsonSerializer.SerializeToUtf8Bytes(new{claudeAiOauth=new{accessToken=access,refreshToken=refresh,expiresAt=expiry.ToUnixTimeMilliseconds(),refreshTokenExpiresAt=refreshExpiry.ToUnixTimeMilliseconds(),scopes=Scopes,subscriptionType="fixture-plan",rateLimitTier="fixture-tier"},mcpOAuth=new{keep="destination-value"},other=new[]{1,2,3}});
    private static HttpResponseMessage Json(object value,HttpStatusCode status=HttpStatusCode.OK)
        =>new(status){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Token(bool refreshLifetime=true,string? scope=null)
    {
        var body=new Dictionary<string,object>{["access_token"]="nonfunctional-new-access",["refresh_token"]="nonfunctional-new-refresh",["expires_in"]=28800,["scope"]=scope??string.Join(" ",Scopes)};
        if(refreshLifetime)body["refresh_token_expires_in"]=86400;
        return Json(body);
    }
    private static HttpResponseMessage Profile(string account=Account)=>Json(new{account=new{uuid=account},organization=new{uuid=Organization}});
    private static async Task<FailureKind> Failure(Func<Task> work,FailureKind expected)
    {
        try{await work();}catch(QueryException error){Check(error.Kind==expected);return error.Kind;}
        throw new InvalidOperationException("Expected typed synthetic failure.");
    }
    private static byte[] Protect(byte[] plain,string operation="Protect")
        =>(byte[])typeof(DpapiSecretStore).GetMethod(operation,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[plain])!;
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("Claude renewal follows system proxy without hardcoded LAN routing, cookies or redirects",()=>
        {
            using var handler=NativeClaudeRenewal.CreateSystemProxyHandler();
            Check(handler.UseProxy&&handler.Proxy is null);
            Check(!handler.AllowAutoRedirect&&!handler.UseCookies&&handler.AutomaticDecompression==DecompressionMethods.None&&handler.DefaultProxyCredentials is null);
            return Task.CompletedTask;
        });
        yield return ("Claude expired access refreshes once and atomically preserves unrelated native data",async()=>
        {
            var f=new Fixture(root,"normal");var old=f.Current.NativeIdentity;
            using var service=f.Service();f.Current=await service.MaintainAsync(f.Current,CancellationToken.None);
            var json=f.ReadRoot();var oauth=json["claudeAiOauth"]!.AsObject();
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&f.Binds==1&&f.Current.NativeIdentity!=old);
            Check(oauth["accessToken"]!.GetValue<string>()=="nonfunctional-new-access"&&oauth["refreshToken"]!.GetValue<string>()=="nonfunctional-new-refresh");
            Check(oauth["expiresAt"]!.GetValue<long>()==f.Time.Now.AddHours(8).ToUnixTimeMilliseconds());
            Check(oauth["refreshTokenExpiresAt"]!.GetValue<long>()==f.Time.Now.AddDays(1).ToUnixTimeMilliseconds());
            Check(oauth["subscriptionType"]!.GetValue<string>()=="fixture-plan"&&oauth["rateLimitTier"]!.GetValue<string>()=="fixture-tier");
            Check(json["mcpOAuth"]!["keep"]!.GetValue<string>()=="destination-value"&&json["other"]!.AsArray().Count==3);
            Check(service.Observe(f.Current)?.State=="ready"&&!service.BackgroundDue(f.Current));
            Check(Directory.GetFiles(Path.GetDirectoryName(f.Native.PathFor("claude"))!,"*.tmp").Length==0);
            foreach(var file in Directory.GetFiles(Path.Combine(f.Root,"auth-renewal"),"*.bin"))
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains("nonfunctional-",StringComparison.Ordinal));
            await service.MaintainAsync(f.Current,CancellationToken.None);Check(f.Http.Posts==1);
        });
        yield return ("Claude refresh missing refresh lifetime preserves existing metadata and uses actual granted scopes",async()=>
        {
            var f=new Fixture(root,"scope");var previous=f.OAuth()["refreshTokenExpiresAt"]!.GetValue<long>();
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token(false,"user:profile user:inference"):Profile());
            using var service=f.Service();f.Current=await service.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.OAuth()["refreshTokenExpiresAt"]!.GetValue<long>()==previous);
            Check(f.OAuth()["scopes"]!.AsArray().Select(s=>s!.GetValue<string>()).SequenceEqual(new[]{"user:profile","user:inference"}));
        });
        yield return ("Claude valid first snapshot seeds trusted profile without refreshing",async()=>
        {
            var f=new Fixture(root,"valid",false);var before=File.ReadAllBytes(f.Native.PathFor("claude"));
            using var service=f.Service();await service.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==0&&f.Http.Profiles==1&&before.SequenceEqual(File.ReadAllBytes(f.Native.PathFor("claude"))));
            await service.MaintainAsync(f.Current,CancellationToken.None);Check(f.Http.Posts==0&&f.Http.Profiles==1);
        });
        foreach(var matching in new[]{true,false})
        yield return ("Claude external native rotation "+(matching?"adopts matching live account":"rejects changed live account"),async()=>
        {
            var f=new Fixture(root,"external-"+matching,false);var changed=false;
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token():Profile(changed&&!matching?OtherAccount:Account));
            using var service=f.Service();await service.MaintainAsync(f.Current,CancellationToken.None);Check(f.Http.Profiles==1);
            changed=true;File.WriteAllBytes(f.Native.PathFor("claude"),Document(f.Time.Now.AddHours(8),f.Time.Now.AddDays(30),"nonfunctional-external-refresh","nonfunctional-external-access"));
            var old=f.Current.NativeIdentity;var actual=f.Native.Read("claude").Identity;
            if(matching){f.Current=await service.MaintainAsync(f.Current,CancellationToken.None);Check(f.Current.NativeIdentity==actual&&actual!=old);}
            else{await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.IdentityChanged);Check(f.Current.NativeIdentity==old);}
            Check(f.Http.Posts==0&&f.Http.Profiles==2);
        });
        foreach(var mode in new[]{"timeout","malformed-success","forbidden","redirect"})
        yield return ("Claude "+mode+" leaves uncertain POST unreplayed across calls and restart",async()=>
        {
            var f=new Fixture(root,mode);
            f.Http.Reply=(r,_)=>r.Method!=HttpMethod.Post?Task.FromResult(Profile()):mode=="timeout"?Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic timeout")):Task.FromResult(mode=="malformed-success"?Json(new{access_token="nonfunctional-new-access",refresh_token="nonfunctional-new-refresh",expires_in="invalid"}):Json(new{error="unclassified"},mode=="forbidden"?HttpStatusCode.Forbidden:HttpStatusCode.TemporaryRedirect));
            var original=File.ReadAllBytes(f.Native.PathFor("claude"));
            using(var service=f.Service())
            {
                await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},mode=="forbidden"?FailureKind.Forbidden:FailureKind.AuthRenewalUnknown);
                Check(service.Observe(f.Current)?.State=="unknown"&&service.Observe(f.Current)?.NextAttemptAt is null);
                await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            }
            using(var resumed=f.Service())await Failure(async()=>{await resumed.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            Check(f.Http.Posts==1&&f.Http.Profiles==0&&original.SequenceEqual(File.ReadAllBytes(f.Native.PathFor("claude"))));
        });
        yield return ("Claude explicit invalid_grant requires reauthentication without replay",async()=>
        {
            var f=new Fixture(root,"invalid-grant");f.Http.Reply=(_,_)=>Task.FromResult(Json(new{error="invalid_grant",error_description="Refresh token expired"},HttpStatusCode.BadRequest));
            using var service=f.Service();
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalRequired);
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalRequired);
            Check(f.Http.Posts==1&&service.Observe(f.Current)?.State=="reauth_required");
        });
        yield return ("Claude known refresh success recovers profile failure with GET and no second POST",async()=>
        {
            var f=new Fixture(root,"profile-recovery");var failProfile=true;
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token():failProfile?Json(new{error="temporary"},HttpStatusCode.ServiceUnavailable):Profile());
            using(var service=f.Service())
            {
                await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
                Check(service.Observe(f.Current)?.NextAttemptAt is not null);
                f.Time.Now=f.Time.Now.AddSeconds(10);Check(service.BackgroundDue(f.Current));
            }
            failProfile=false;using(var resumed=f.Service())f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==2&&f.OAuth()["accessToken"]!.GetValue<string>()=="nonfunctional-new-access");
        });
        yield return ("Claude response journal recovers after settings accepted new binding before interruption",async()=>
        {
            var f=new Fixture(root,"binding-recovery");var failOnce=true;
            f.Binding=(old,next)=>{f.Current=next;if(failOnce){failOnce=false;throw new IOException("Synthetic settings completion interruption");}return Task.FromResult(next);};
            using(var service=f.Service())await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            Check(f.Current.NativeIdentity==f.Native.Read("claude").Identity);
            using(var resumed=f.Service())f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&f.Current.NativeIdentity==f.Native.Read("claude").Identity);
        });
        foreach(var rejected in new[]{false,true})
        yield return ("Claude explicit source save accepts a different grant after "+(rejected?"invalid_grant":"uncertain refresh"),async()=>
        {
            var f=new Fixture(root,"source-new-"+rejected);
            f.Http.Reply=(_,_)=>rejected?Task.FromResult(Json(new{error="invalid_grant"},HttpStatusCode.BadRequest)):Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic timeout"));
            using var service=f.Service();
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},rejected?FailureKind.AuthRenewalRequired:FailureKind.AuthRenewalUnknown);
            var previous=File.ReadAllBytes(f.JournalPath);
            File.WriteAllBytes(f.Native.PathFor("claude"),Document(f.Time.Now.AddHours(8),f.Time.Now.AddDays(30),"nonfunctional-confirmed-refresh","nonfunctional-confirmed-access"));
            f.Current=f.Current with {NativeIdentity=f.Native.Read("claude").Identity};
            await service.SourceSavedAsync(f.Current);
            Check(service.Observe(f.Current)?.State=="scheduled"&&service.BackgroundDue(f.Current));
            var archives=Directory.GetFiles(Path.GetDirectoryName(f.JournalPath)!,"*.history-*.bin");
            Check(archives.Length==1&&previous.SequenceEqual(File.ReadAllBytes(archives[0])));
            var newGeneration=File.ReadAllBytes(f.JournalPath);
            await service.SourceSavedAsync(f.Current);
            Check(Directory.GetFiles(Path.GetDirectoryName(f.JournalPath)!,"*.history-*.bin").Length==1&&newGeneration.SequenceEqual(File.ReadAllBytes(f.JournalPath)));
            // Explicitly saving a different, valid grant authorizes its new
            // account baseline. Ordinary unapproved external changes still fail.
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token():Profile(OtherAccount));
            f.Current=await service.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&service.Observe(f.Current)?.State=="ready");
            using var resumed=f.Service();await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==1);
        });
        foreach(var mode in new[]{"unchanged identity","changed opaque access","changed identity claims"})
        yield return ("Claude source save preserves uncertain same grant with "+mode,async()=>
        {
            var f=new Fixture(root,"source-same-"+mode.Replace(' ','-'));
            f.Http.Reply=(_,_)=>Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic timeout"));
            using var service=f.Service();
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            var previous=File.ReadAllBytes(f.JournalPath);
            var changedIdentity=mode=="changed identity claims";
            var claim=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new{sub="different-synthetic-subject"})).TrimEnd('=').Replace('+','-').Replace('/','_');
            var access=changedIdentity?"nonfunctional."+claim+".signature":mode=="changed opaque access"?"nonfunctional-changed-access":"nonfunctional-old-access";
            File.WriteAllBytes(f.Native.PathFor("claude"),Document(f.Time.Now.AddHours(8),f.Time.Now.AddDays(30),access:access));
            var approved=f.Current with {NativeIdentity=f.Native.Read("claude").Identity};
            Check(changedIdentity?approved.NativeIdentity!=f.Current.NativeIdentity:approved.NativeIdentity==f.Current.NativeIdentity);
            if(changedIdentity)await Failure(()=>service.SourceSavedAsync(approved),FailureKind.AuthRenewalUnknown);
            else await service.SourceSavedAsync(approved);
            Check(previous.SequenceEqual(File.ReadAllBytes(f.JournalPath))&&Directory.GetFiles(Path.GetDirectoryName(f.JournalPath)!,"*.history-*.bin").Length==0);
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},changedIdentity?FailureKind.IdentityChanged:FailureKind.AuthRenewalUnknown);
            Check(f.Http.Posts==1&&f.Http.Profiles==0);
        });
        yield return ("Claude source save rejects an approval that does not match current native pair",async()=>
        {
            var f=new Fixture(root,"source-mismatch");
            f.Http.Reply=(_,_)=>Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic timeout"));
            using var service=f.Service();await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            var previous=File.ReadAllBytes(f.JournalPath);
            File.WriteAllBytes(f.Native.PathFor("claude"),Document(f.Time.Now.AddHours(8),f.Time.Now.AddDays(30),"nonfunctional-unapproved-refresh","nonfunctional-unapproved-access"));
            await Failure(()=>service.SourceSavedAsync(f.Current),FailureKind.IdentityChanged);
            Check(previous.SequenceEqual(File.ReadAllBytes(f.JournalPath))&&Directory.GetFiles(Path.GetDirectoryName(f.JournalPath)!,"*.history-*.bin").Length==0&&f.Http.Posts==1&&f.Http.Profiles==0);
        });
        yield return ("Claude source save preserves known response after new binding was committed",async()=>
        {
            var f=new Fixture(root,"source-known-response");var failOnce=true;
            f.Binding=(old,next)=>{f.Current=next;if(failOnce){failOnce=false;throw new IOException("Synthetic settings interruption");}return Task.FromResult(next);};
            using var service=f.Service();await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            Check(f.Current.NativeIdentity==f.Native.Read("claude").Identity);
            var response=File.ReadAllBytes(f.JournalPath);await service.SourceSavedAsync(f.Current);
            Check(response.SequenceEqual(File.ReadAllBytes(f.JournalPath))&&Directory.GetFiles(Path.GetDirectoryName(f.JournalPath)!,"*.history-*.bin").Length==0);
            using var resumed=f.Service();f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&resumed.Observe(f.Current)?.State=="ready");
        });
        foreach(var interruptedBinding in new[]{false,true})
        yield return ("Claude expired known response recovers locally from "+(interruptedBinding?"already committed new pair":"old pair")+" before renewing only its new grant",async()=>
        {
            var f=new Fixture(root,"expired-response-"+interruptedBinding);var failOnce=true;
            if(interruptedBinding)f.Binding=(old,next)=>{f.Current=next;if(failOnce){failOnce=false;throw new IOException("Synthetic committed binding interruption");}return Task.FromResult(next);};
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token():!interruptedBinding&&failOnce?Json(new{error="temporary"},HttpStatusCode.ServiceUnavailable):Profile());
            using(var first=f.Service())await Failure(async()=>{await first.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            Check(f.Http.Posts==1&&f.Http.Profiles==1);
            failOnce=false;f.Time.Now=f.Time.Now.AddHours(9);
            using var resumed=f.Service();f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&resumed.Observe(f.Current)?.State=="scheduled"&&resumed.Observe(f.Current)?.NextAttemptAt==f.Time.Now&&resumed.BackgroundDue(f.Current));
            Check(f.Native.ParseDocument("claude",File.ReadAllBytes(f.Native.PathFor("claude")),false).Identity==f.Current.NativeIdentity);
            Check(f.OAuth()["expiresAt"]!.GetValue<long>()<f.Time.Now.ToUnixTimeMilliseconds());
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Json(new{access_token="nonfunctional-next-access",refresh_token="nonfunctional-next-refresh",expires_in=28800,scope=string.Join(" ",Scopes)}):Profile());
            f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==2&&f.Http.PostedRefresh.SequenceEqual(new[]{"nonfunctional-old-refresh","nonfunctional-new-refresh"}));
            Check(f.OAuth()["refreshToken"]!.GetValue<string>()=="nonfunctional-next-refresh"&&resumed.Observe(f.Current)?.State=="ready");
            Check(f.Http.Profiles==2-(interruptedBinding?1:0));
        });
        yield return ("Claude expired response recovery rejects a changed native grant without overwrite or POST",async()=>
        {
            var f=new Fixture(root,"expired-response-foreign");
            f.Http.Reply=(r,_)=>Task.FromResult(r.Method==HttpMethod.Post?Token():Json(new{error="temporary"},HttpStatusCode.ServiceUnavailable));
            using(var first=f.Service())await Failure(async()=>{await first.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalUnknown);
            f.Time.Now=f.Time.Now.AddHours(9);
            var foreign=Document(f.Time.Now.AddHours(8),f.Time.Now.AddDays(30),"nonfunctional-foreign-refresh","nonfunctional-foreign-access");File.WriteAllBytes(f.Native.PathFor("claude"),foreign);
            using var resumed=f.Service();await Failure(async()=>{await resumed.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.IdentityChanged);
            Check(f.Http.Posts==1&&f.Http.Profiles==1&&foreign.SequenceEqual(File.ReadAllBytes(f.Native.PathFor("claude")))&&resumed.Observe(f.Current)?.State=="unknown");
        });
        foreach(var initialValid in new[]{false,true})
        yield return ("Claude profile 429 retains real Retry-After for "+(initialValid?"first valid baseline":"known response recovery"),async()=>
        {
            var f=new Fixture(root,"profile-limit-"+initialValid,!initialValid);var retry=f.Time.Now.AddMinutes(20);var limited=true;
            f.Http.Reply=(r,_)=>
            {
                if(r.Method==HttpMethod.Post)return Task.FromResult(Token());
                var response=limited?Json(new{error="rate_limit"},HttpStatusCode.TooManyRequests):Profile();
                if(limited)response.Headers.RetryAfter=new(retry);
                return Task.FromResult(response);
            };
            using var service=f.Service();await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalBusy);
            Check(f.Http.Posts==(initialValid?0:1)&&f.Http.Profiles==1&&service.Observe(f.Current)?.State=="scheduled"&&service.Observe(f.Current)?.NextAttemptAt==retry);
            f.Time.Now=retry.AddSeconds(-1);Check(!service.BackgroundDue(f.Current));
            // Direct/manual invocation cannot bypass the provider deadline, and
            // the encrypted recovery record retains it across service restart.
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalBusy);
            Check(f.Http.Profiles==1&&f.Http.Posts==(initialValid?0:1)&&service.Observe(f.Current)?.NextAttemptAt==retry);
            using var resumed=f.Service();await Failure(async()=>{await resumed.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalBusy);
            Check(f.Http.Profiles==1&&f.Http.Posts==(initialValid?0:1)&&resumed.Observe(f.Current)?.NextAttemptAt==retry&&!resumed.BackgroundDue(f.Current));
            f.Time.Now=retry;Check(resumed.BackgroundDue(f.Current));limited=false;
            f.Current=await resumed.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==(initialValid?0:1)&&f.Http.Profiles==2&&resumed.Observe(f.Current)?.State=="ready");
        });
        yield return ("Claude successful token CAS merges concurrent unrelated native data",async()=>
        {
            var f=new Fixture(root,"merge");f.Http.Reply=(r,_)=>
            {
                if(r.Method==HttpMethod.Post){var changed=f.ReadRoot();changed["concurrentKeep"]="new-value";f.Write(changed);return Task.FromResult(Token());}
                return Task.FromResult(Profile());
            };
            using var service=f.Service();f.Current=await service.MaintainAsync(f.Current,CancellationToken.None);
            Check(f.Http.Posts==1&&f.ReadRoot()["concurrentKeep"]!.GetValue<string>()=="new-value"&&f.OAuth()["accessToken"]!.GetValue<string>()=="nonfunctional-new-access");
        });
        yield return ("Claude busy native process causes no POST and schedules maintenance",async()=>
        {
            var f=new Fixture(root,"busy");using var service=f.Service(()=>true);
            await Failure(async()=>{await service.MaintainAsync(f.Current,CancellationToken.None);},FailureKind.AuthRenewalBusy);
            Check(f.Http.Posts==0&&service.Observe(f.Current)?.NextAttemptAt is not null);
            f.Time.Now=f.Time.Now.AddSeconds(10);Check(service.BackgroundDue(f.Current));
        });
        yield return ("Claude native refresh directories are exclusive and changed leases cancel",async()=>
        {
            var f=new Fixture(root,"exclusive");var config=Path.GetDirectoryName(f.Native.PathFor("claude"))!;
            await using var lease=NativeRefreshLocks.TryAcquire(config,Path.Combine(f.Root,"proof"));Check(lease is not null&&lease.Verify());
            var competing=NativeRefreshLocks.TryAcquire(config,Path.Combine(f.Root,"proof"));Check(competing is null);
            Directory.SetLastWriteTimeUtc(Path.Combine(config,".oauth_refresh.lock"),DateTime.UtcNow.AddSeconds(-80));
            Check(!lease!.Verify()&&lease.Compromised.IsCancellationRequested);
        });
        foreach(var mode in new[]{"own-dead","live-owner","foreign","identity-changed"})
        yield return ("Claude stale native lock recovery respects "+mode,async()=>
        {
            var f=new Fixture(root,"lock-"+mode);var config=Path.GetDirectoryName(f.Native.PathFor("claude"))!;
            var recovery=Path.Combine(f.Root,"proof");Directory.CreateDirectory(recovery);
            var paths=new[]{Path.Combine(config,".oauth_refresh.lock"),Path.GetFullPath(config)+".lock"};
            foreach(var path in paths){Directory.CreateDirectory(path);Directory.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddSeconds(-80));}
            var nativeOwner=Path.Combine(config,".oauth_refresh.lock.owner");File.WriteAllText(nativeOwner,"foreign-native-owner-sentinel");
            if(mode!="foreign")
            {
                using var current=Process.GetCurrentProcess();
                var proof=JsonSerializer.SerializeToUtf8Bytes(new{Schema=1,Pid=mode=="live-owner"?current.Id:int.MaxValue,ProcessStart=current.StartTime.ToUniversalTime(),Paths=paths.Select(path=>new{Path=path,Birth=Directory.GetCreationTimeUtc(path),Modified=Directory.GetLastWriteTimeUtc(path)})});
                var proofPath=Path.Combine(recovery,"lease-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths[1]))).ToLowerInvariant()+".bin");
                File.WriteAllBytes(proofPath,Protect(proof));
                if(mode=="identity-changed")Directory.SetLastWriteTimeUtc(paths[0],DateTime.UtcNow);
            }
            await using var lease=NativeRefreshLocks.TryAcquire(config,recovery);
            Check(mode=="own-dead"?lease is not null&&lease.Verify():lease is null);
            Check(File.ReadAllText(nativeOwner)=="foreign-native-owner-sentinel");
            if(mode!="own-dead")Check(paths.All(Directory.Exists));
        });
    }
}
