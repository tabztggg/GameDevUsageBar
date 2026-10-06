using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

// The existing UsageBar loop is the only proactive owner. No CLI/model process
// is launched; GET consumers cannot trigger this operation.
public sealed class NativeClaudeRenewal : IDisposable
{
    public const string TokenEndpoint="https://platform.claude.com/v1/oauth/token";
    public const string ProfileEndpoint="https://api.anthropic.com/api/oauth/profile";
    private const string ClientId="9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private readonly string root;
    private readonly NativeOAuthStore native;
    private readonly Func<AccountConfig,AccountConfig,Task<AccountConfig>> bind;
    private readonly Func<bool> busy;
    private readonly TimeProvider clock;
    private readonly Action<Exception>? onError;
    private readonly HttpClient http;
    private readonly SemaphoreSlim flight=new(1,1);
    private readonly object sync=new();
    private readonly Dictionary<Guid,(string Stamp,AuthMaintenance View)> states=[];
    private DateTimeOffset? profileNotBefore;
    private sealed record Journal(int Schema,Guid Slot,string NativeIdentity,string BaselineHash,string RefreshHash,string Phase,byte[]? Desired,string? NewIdentity,string? AccountUuid,string? OrganizationUuid,DateTimeOffset? ProfileNotBefore=null);
    public NativeClaudeRenewal(string root,NativeOAuthStore native,Func<AccountConfig,AccountConfig,Task<AccountConfig>> binding,
        HttpMessageHandler? fixtureHandler=null,TimeProvider? time=null,Func<bool>? busyGuard=null,Action<Exception>? onError=null)
    {
        this.root=Path.Combine(root,"auth-renewal");this.native=native;bind=binding;clock=time??TimeProvider.System;busy=busyGuard??(()=>NativeLoginSwitcher.HasRunningCli("claude"));this.onError=onError;
        http=new(fixtureHandler??CreateSystemProxyHandler()){Timeout=Timeout.InfiniteTimeSpan};
    }
    // Leaving Proxy unset uses HttpClient.DefaultProxy: process proxy settings,
    // then the current Windows user's proxy. Never force a developer's LAN hop.
    // An uncertain OAuth POST still uses the durable journal, without fallback.
    public static SocketsHttpHandler CreateSystemProxyHandler()=>new(){UseProxy=true,AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None};
    private static bool Eligible(AccountConfig c)=>c.ProviderId=="claude"&&c.SourceMode=="local-oauth"&&c.Enabled&&c.NativeIdentity is not null;
    private string PathFor(AccountConfig c)=>Path.Combine(root,c.SlotId.ToString("N")+".bin");
    private string Stamp()
    {
        try{var f=new FileInfo(native.PathFor("claude"));return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}catch{return "unreadable";}
    }
    public AuthMaintenance? Observe(AccountConfig c)
    {if(!Eligible(c))return null;lock(sync)return states.GetValueOrDefault(c.SlotId).View;}
    public bool BackgroundDue(AccountConfig c)
    {
        if(!Eligible(c))return false;
        lock(sync)
        {
            if(!states.TryGetValue(c.SlotId,out var s))return false;
            return Stamp()!=s.Stamp||(s.View.NextAttemptAt is {} at&&at<=clock.GetUtcNow());
        }
    }
    private void State(AccountConfig c,string state,DateTimeOffset? next=null)
    {lock(sync)states[c.SlotId]=(Stamp(),new("GameDevUsageBar",true,state,next));}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Hash(string text)=>Hash(System.Text.Encoding.UTF8.GetBytes(text));
    private static string Text(JsonObject node,string key)=>node[key]?.GetValue<string>()??"";
    private static DateTimeOffset? Date(JsonObject node,string key)=>node[key] is JsonValue value&&value.TryGetValue<long>(out var ms)?DateTimeOffset.FromUnixTimeMilliseconds(ms):null;
    private Journal? ReadJournal(AccountConfig c)
    {
        var path=PathFor(c);if(!File.Exists(path))return null;byte[]? plain=null;
        try
        {
            if(new FileInfo(path).Length>262144)throw new QueryException(FailureKind.AuthRenewalUnknown);
            plain=DpapiSecretStore.Unprotect(File.ReadAllBytes(path));var j=JsonSerializer.Deserialize<Journal>(plain);
            if(j is null||j.Schema!=1||j.Slot!=c.SlotId||j.Phase is not ("posting" or "response" or "complete" or "rejected"))throw new QueryException(FailureKind.AuthRenewalUnknown);
            return j;
        }
        catch(QueryException){throw;}catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.AuthRenewalUnknown);}
        finally{if(plain is not null)CryptographicOperations.ZeroMemory(plain);}
    }
    private async Task SaveJournal(AccountConfig c,Journal j)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(j with {ProfileNotBefore=profileNotBefore});byte[]? encrypted=null;
        try{encrypted=DpapiSecretStore.Protect(plain);await AtomicFile.WriteAsync(PathFor(c),encrypted);}
        finally{CryptographicOperations.ZeroMemory(plain);if(encrypted is not null)CryptographicOperations.ZeroMemory(encrypted);}
    }
    public async Task SourceSavedAsync(AccountConfig approved,CancellationToken ct=default)
    {
        if(!Eligible(approved))return;
        // Called before ApplicationHost takes its settings semaphore. The normal
        // maintenance path takes flight first and then commits that semaphore.
        await flight.WaitAsync(ct);byte[]? document=null;Journal? journal=null;
        try
        {
            document=native.ReadDocument("claude");var credential=native.ParseDocument("claude",document);
            if(credential.Identity!=approved.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            var oauth=(JsonNode.Parse(document) as JsonObject)?["claudeAiOauth"] as JsonObject??throw new QueryException(FailureKind.CredentialUnreadable);
            var refreshHash=Hash(Text(oauth,"refreshToken"));journal=ReadJournal(approved);
            if(journal is null)return;
            if(journal is {Phase:"response",Desired:not null,NewIdentity:not null}&&approved.NativeIdentity==journal.NewIdentity)
            {
                var desired=(JsonNode.Parse(journal.Desired) as JsonObject)?["claudeAiOauth"] as JsonObject;
                if(desired is not null&&Text(oauth,"accessToken")==Text(desired,"accessToken")&&Text(oauth,"refreshToken")==Text(desired,"refreshToken"))return;
            }
            if(journal.NativeIdentity==approved.NativeIdentity&&journal.RefreshHash==refreshHash)return;
            // A source-save is not permission to retry an uncertain rotation of
            // the same grant, even if a different access token changes its hash.
            if(refreshHash==journal.RefreshHash)throw new QueryException(FailureKind.AuthRenewalUnknown);
            if(!Matches(Hash(document)))throw new QueryException(FailureKind.AuthRenewalBusy);
            var encrypted=File.ReadAllBytes(PathFor(approved));
            try{await AtomicFile.WriteAsync(PathFor(approved)+".history-"+Guid.NewGuid().ToString("N")+".bin",encrypted);}
            finally{CryptographicOperations.ZeroMemory(encrypted);}
            await SaveJournal(approved,new(1,approved.SlotId,approved.NativeIdentity!,Hash(document),refreshHash,"complete",null,approved.NativeIdentity,null,null));
            State(approved,"scheduled",clock.GetUtcNow());
        }
        finally{if(document is not null)CryptographicOperations.ZeroMemory(document);if(journal?.Desired is {} desired)CryptographicOperations.ZeroMemory(desired);flight.Release();}
    }
    public async Task<AccountConfig> MaintainAsync(AccountConfig config,CancellationToken ct)
    {
        if(!Eligible(config))return config;
        await flight.WaitAsync(ct);byte[]? document=null;Journal? journal=null;
        try
        {
            journal=ReadJournal(config);
            if(journal?.ProfileNotBefore>profileNotBefore||profileNotBefore is null)profileNotBefore=journal?.ProfileNotBefore;
            document=native.ReadDocument("claude");
            var credential=native.ParseDocument("claude",document,false);
            var node=(JsonNode.Parse(document) as JsonObject)?["claudeAiOauth"] as JsonObject??throw new QueryException(FailureKind.CredentialUnreadable);
            var expiry=Date(node,"expiresAt")??throw new QueryException(FailureKind.CredentialUnreadable);
            if(journal is {Phase:"response"})return await CommitAsync(config,journal,ct);
            if(journal is not null&&journal.NativeIdentity!=config.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            if(credential.Identity!=config.NativeIdentity||journal is {Phase:"posting"}&&journal.RefreshHash!=Hash(Text(node,"refreshToken")))
            {
                // Opaque-token rotation is not proof of the same account. Only a
                // live profile matching a previously trusted UUID can bridge it.
                if(journal?.AccountUuid is null||journal.OrganizationUuid is null||expiry<=clock.GetUtcNow())throw new QueryException(FailureKind.IdentityChanged);
                if(busy())throw new QueryException(FailureKind.AuthRenewalBusy);
                await using var identityLease=NativeRefreshLocks.TryAcquire(Path.GetDirectoryName(native.PathFor("claude"))!,root);
                if(identityLease is null||!identityLease.Verify())throw new QueryException(FailureKind.AuthRenewalBusy);
                var baseline=Hash(document);var profile=await ProfileAsync(credential.Token,ct,identityLease.Compromised);
                if(profile.Account!=journal.AccountUuid||profile.Organization!=journal.OrganizationUuid||!Matches(baseline)||!identityLease.Verify())throw new QueryException(FailureKind.IdentityChanged);
                var oldRefreshHash=Hash(Text(node,"refreshToken"));
                if(journal.Phase=="posting"&&oldRefreshHash==journal.RefreshHash)throw new QueryException(FailureKind.AuthRenewalUnknown);
                // An external verified new pair ends uncertainty about using the
                // current pair; preserve the old encrypted pending record first.
                if(journal.Phase=="posting")await AtomicFile.WriteAsync(PathFor(config)+".unknown-history",File.ReadAllBytes(PathFor(config)));
                journal=new(1,config.SlotId,config.NativeIdentity!,baseline,oldRefreshHash,"response",document.ToArray(),credential.Identity,profile.Account,profile.Organization);
                // Persist verified continuity before advancing settings. A crash
                // after binding still has a recoverable local transition.
                await SaveJournal(config,journal);
                return await CommitAsync(config,journal,ct,identityLease);
            }
            if(journal is {Phase:"posting"})throw new QueryException(FailureKind.AuthRenewalUnknown);
            if(journal is {Phase:"rejected"}&&journal.RefreshHash==Hash(Text(node,"refreshToken")))throw new QueryException(FailureKind.AuthRenewalRequired);
            var now=clock.GetUtcNow();var due=expiry-TimeSpan.FromMinutes(5);
            if(now<due)
            {
                // Establish the stable profile while the original bound pair is
                // still valid, before the native CLI can rotate it independently.
                if(journal?.AccountUuid is null)
                {
                    if(busy())throw new QueryException(FailureKind.AuthRenewalBusy);
                    await using var seed=NativeRefreshLocks.TryAcquire(Path.GetDirectoryName(native.PathFor("claude"))!,root);
                    if(seed is null||!seed.Verify()||!Matches(Hash(document)))throw new QueryException(FailureKind.AuthRenewalBusy);
                    var profile=await ProfileAsync(credential.Token,ct,seed.Compromised);
                    if(!seed.Verify()||!Matches(Hash(document)))throw new QueryException(FailureKind.AuthRenewalBusy);
                    journal=new(1,config.SlotId,config.NativeIdentity!,Hash(document),Hash(Text(node,"refreshToken")),"complete",null,config.NativeIdentity,profile.Account,profile.Organization);
                    await SaveJournal(config,journal);
                }
                State(config,"ready",due);return config;
            }
            var refresh=Text(node,"refreshToken");
            if(refresh.Length==0||refresh.Length>8192||refresh.Any(char.IsControl)||Date(node,"refreshTokenExpiresAt") is {} refreshExpiry&&refreshExpiry<=now)throw new QueryException(FailureKind.AuthRenewalRequired);
            if(credential.Identity!=config.NativeIdentity)throw new QueryException(FailureKind.IdentityChanged);
            if(busy())throw new QueryException(FailureKind.AuthRenewalBusy);
            await using var lease=NativeRefreshLocks.TryAcquire(Path.GetDirectoryName(native.PathFor("claude"))!,root);
            if(lease is null||!lease.Verify())throw new QueryException(FailureKind.AuthRenewalBusy);
            // Both native locks are held before the authoritative re-read.
            if(!Matches(Hash(document)))throw new QueryException(FailureKind.AuthRenewalBusy);
            var scopes=node["scopes"]?.AsArray().Select(x=>x?.GetValue<string>()??"").ToArray()??[];
            if(scopes.Length==0||scopes.Any(s=>s.Length==0||s.Length>200||s.Any(c=>char.IsControl(c)||char.IsWhiteSpace(c))))throw new QueryException(FailureKind.AuthRenewalRequired);
            var clientId=Text(node,"clientId");if(clientId.Length==0)clientId=ClientId;
            if(!Guid.TryParse(clientId,out _))throw new QueryException(FailureKind.CredentialUnreadable);
            journal=new(1,config.SlotId,config.NativeIdentity!,Hash(document),Hash(refresh),"posting",null,null,journal?.AccountUuid,journal?.OrganizationUuid);
            await SaveJournal(config,journal);
            State(config,"refreshing");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,lease.Compromised);timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var body=JsonSerializer.SerializeToUtf8Bytes(new{grant_type="refresh_token",refresh_token=refresh,client_id=clientId,scope=string.Join(" ",scopes)});
            try
            {
                if(busy()||!lease.Verify()||!Matches(journal.BaselineHash))throw new BeforePostException();
                using var request=new HttpRequestMessage(HttpMethod.Post,TokenEndpoint){Content=new ByteArrayContent(body)};
                request.Content.Headers.ContentType=new("application/json");request.Headers.Accept.Add(new("application/json"));
                using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
                if(!response.IsSuccessStatusCode)
                {
                    if(await InvalidGrantAsync(response,timeout.Token))
                    {journal=journal with {Phase="rejected"};await SaveJournal(config,journal);throw new QueryException(FailureKind.AuthRenewalRequired);}
                    // No generic POST retry, including 429/5xx/redirect responses.
                    throw new QueryException(response.StatusCode==HttpStatusCode.Forbidden?FailureKind.Forbidden:response.StatusCode==HttpStatusCode.Unauthorized?FailureKind.Unauthorized:FailureKind.AuthRenewalUnknown);
                }
                var bytes=await BoundedAsync(response,timeout.Token);
                try
                {
                    var answer=JsonNode.Parse(bytes) as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
                    var access=Text(answer,"access_token");var rotated=Text(answer,"refresh_token");if(rotated.Length==0)rotated=refresh;
                    if(access.Length==0||access.Length>8192||rotated.Length>8192||access.Any(char.IsControl)||rotated.Any(char.IsControl))throw new QueryException(FailureKind.AuthRenewalUnknown);
                    if(answer["expires_in"] is not JsonValue expiresValue||!expiresValue.TryGetValue<long>(out var seconds)||seconds<=0)throw new QueryException(FailureKind.AuthRenewalUnknown);
                    if(answer["scope"] is {} returnedScope)
                    {
                        var granted=returnedScope.GetValue<string>().Split(' ',StringSplitOptions.RemoveEmptyEntries);
                        if(granted.Length==0||granted.Any(s=>!scopes.Contains(s,StringComparer.Ordinal)))throw new QueryException(FailureKind.AuthRenewalUnknown);
                        node["scopes"]=new JsonArray(granted.Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());
                    }
                    var issued=clock.GetUtcNow();node["accessToken"]=access;node["refreshToken"]=rotated;node["expiresAt"]=issued.AddSeconds(seconds).ToUnixTimeMilliseconds();
                    if(answer["refresh_token_expires_in"] is JsonValue refreshValue&&refreshValue.TryGetValue<long>(out var refreshSeconds)&&refreshSeconds>0)
                        node["refreshTokenExpiresAt"]=issued.AddSeconds(refreshSeconds).ToUnixTimeMilliseconds();
                    // Absent refresh lifetime preserves the CLI's prior metadata.
                    var desiredRoot=JsonNode.Parse(document) as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);desiredRoot["claudeAiOauth"]=node.DeepClone();
                    var desired=JsonSerializer.SerializeToUtf8Bytes(desiredRoot);
                    var newCredential=native.ParseDocument("claude",desired);
                    journal=journal with {Phase="response",Desired=desired,NewIdentity=newCredential.Identity};
                    await SaveJournal(config,journal); // Persist success before any further network or file operation.
                    if(!lease.Verify())throw new QueryException(FailureKind.AuthRenewalUnknown);
                    if(journal.AccountUuid is null)
                    {
                        var profile=await ProfileAsync(access,ct,lease.Compromised);
                        journal=journal with {AccountUuid=profile.Account,OrganizationUuid=profile.Organization};await SaveJournal(config,journal);
                    }
                    return await CommitAsync(config,journal,ct,lease);
                }
                finally{CryptographicOperations.ZeroMemory(bytes);}
            }
            catch(BeforePostException)
            {
                // The transport was never called. Preserve prior stable identity,
                // but no token has an uncertain rotation outcome in this branch.
                journal=journal with {Phase="complete"};await SaveJournal(config,journal);throw new QueryException(FailureKind.AuthRenewalBusy);
            }
            catch(QueryException){throw;}
            catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.AuthRenewalUnknown);}
            finally{CryptographicOperations.ZeroMemory(body);}
        }
        catch(QueryException error)
        {
            if(profileNotBefore>clock.GetUtcNow()&&document is not null)
            {
                if(journal is null)
                {
                    var oauth=(JsonNode.Parse(document) as JsonObject)?["claudeAiOauth"] as JsonObject;
                    if(oauth is not null)journal=new(1,config.SlotId,config.NativeIdentity!,Hash(document),Hash(Text(oauth,"refreshToken")),"complete",null,config.NativeIdentity,null,null);
                }
                if(journal is not null)await SaveJournal(config,journal);
            }
            var recoverable=journal is {Phase:"response"}&&error.Kind!=FailureKind.IdentityChanged||error.RetryNotBefore is not null;
            State(config,error.Kind==FailureKind.AuthRenewalBusy||recoverable?"scheduled":error.Kind is FailureKind.AuthRenewalUnknown or FailureKind.IdentityChanged||journal is {Phase:"posting"}?"unknown":"reauth_required",
                error.Kind==FailureKind.AuthRenewalBusy||recoverable?error.RetryNotBefore??clock.GetUtcNow().AddSeconds(5):null);
            throw;
        }
        catch(Exception error){ErrorObserver.Report(onError,error);State(config,"unknown");throw new QueryException(FailureKind.AuthRenewalUnknown);}
        finally{if(document is not null)CryptographicOperations.ZeroMemory(document);if(journal?.Desired is {} desired)CryptographicOperations.ZeroMemory(desired);flight.Release();}
    }
    private sealed class BeforePostException:Exception;
    private async Task<AccountConfig> CommitAsync(AccountConfig config,Journal journal,CancellationToken ct,NativeRefreshLocks? held=null)
    {
        if(journal.Desired is null||journal.NewIdentity is null||journal.Slot!=config.SlotId||config.NativeIdentity!=journal.NativeIdentity&&config.NativeIdentity!=journal.NewIdentity)throw new QueryException(FailureKind.AuthRenewalUnknown);
        NativeRefreshLocks? acquired=null;
        byte[]? mergedBytes=null;
        try
        {
            if(held is null)
            {
                if(busy())throw new QueryException(FailureKind.AuthRenewalBusy);
                acquired=NativeRefreshLocks.TryAcquire(Path.GetDirectoryName(native.PathFor("claude"))!,root);held=acquired;
            }
            if(held is null||!held.Verify()||busy())throw new QueryException(FailureKind.AuthRenewalBusy);
            byte[] current=native.ReadDocument("claude");
            try
            {
                var actual=native.ParseDocument("claude",current,false);
                var currentRoot=JsonNode.Parse(current) as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
                var currentOauth=currentRoot["claudeAiOauth"] as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
                var desiredRoot=JsonNode.Parse(journal.Desired) as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
                var desiredOauth=desiredRoot["claudeAiOauth"] as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
                var isDesired=actual.Identity==journal.NewIdentity&&Text(currentOauth,"accessToken")==Text(desiredOauth,"accessToken")&&Text(currentOauth,"refreshToken")==Text(desiredOauth,"refreshToken");
                if(journal.NewIdentity!=journal.NativeIdentity&&config.NativeIdentity==journal.NewIdentity&&!isDesired)throw new QueryException(FailureKind.IdentityChanged);
                if(!isDesired&&(actual.Identity!=journal.NativeIdentity||Hash(Text(currentOauth,"refreshToken"))!=journal.RefreshHash))throw new QueryException(FailureKind.IdentityChanged);
                // Preserve unrelated native credential blocks added after the
                // POST. The token chain and a final full-document CAS protect it.
                if(!isDesired)
                {
                    var mergedOauth=(JsonObject)currentOauth.DeepClone();
                    foreach(var key in new[]{"accessToken","refreshToken","expiresAt","refreshTokenExpiresAt","scopes"})
                        if(desiredOauth.ContainsKey(key))mergedOauth[key]=desiredOauth[key]?.DeepClone();
                    currentRoot["claudeAiOauth"]=mergedOauth;
                    mergedBytes=JsonSerializer.SerializeToUtf8Bytes(currentRoot);
                    journal=journal with {Desired=mergedBytes,BaselineHash=Hash(current)};
                    await SaveJournal(config,journal);
                }
                if(!isDesired)
                {
                var path=native.PathFor("claude");var temp=path+"."+Guid.NewGuid().ToString("N")+".renewal.tmp";
                try
                {
                    await using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                    {await stream.WriteAsync(journal.Desired,ct);stream.Flush(true);}
                    if(!held.Verify()||busy()||!Matches(journal.BaselineHash))throw new QueryException(FailureKind.AuthRenewalUnknown);
                    File.Replace(temp,path,null,true);
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
                if(!Matches(Hash(journal.Desired)))throw new QueryException(FailureKind.AuthRenewalUnknown);
                }
            }
            finally{CryptographicOperations.ZeroMemory(current);}
            var desiredOauthForTime=(JsonNode.Parse(journal.Desired) as JsonObject)?["claudeAiOauth"] as JsonObject??throw new QueryException(FailureKind.AuthRenewalUnknown);
            var confirmedExpiry=Date(desiredOauthForTime,"expiresAt")??throw new QueryException(FailureKind.AuthRenewalUnknown);
            if(journal.AccountUuid is null&&confirmedExpiry>clock.GetUtcNow())
            {
                var credential=native.ParseDocument("claude",journal.Desired,false);
                var profile=await ProfileAsync(credential.Token,ct,held.Compromised);journal=journal with {AccountUuid=profile.Account,OrganizationUuid=profile.Organization};await SaveJournal(config,journal);
            }
            if(!held.Verify()||busy())throw new QueryException(FailureKind.AuthRenewalBusy);
            var next=await bind(config,config with {NativeIdentity=journal.NewIdentity});
            await SaveJournal(next,journal with {Phase="complete",NativeIdentity=next.NativeIdentity!,BaselineHash=Hash(journal.Desired),Desired=null});
            // A confirmed response may be recovered after its access lifetime.
            // Complete only this known local chain, then renew the confirmed new
            // grant in the next loop. Never call this expired credential ready.
            State(next,confirmedExpiry>clock.GetUtcNow()?"ready":"scheduled",confirmedExpiry>clock.GetUtcNow()?confirmedExpiry-TimeSpan.FromMinutes(5):clock.GetUtcNow());return next;
        }
        catch(QueryException){throw;}catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.AuthRenewalUnknown);}
        finally{if(mergedBytes is not null)CryptographicOperations.ZeroMemory(mergedBytes);if(acquired is not null)await acquired.DisposeAsync();}
    }
    private bool Matches(string hash)
    {byte[]? actual=null;try{actual=native.ReadDocument("claude");return Hash(actual)==hash;}catch{return false;}finally{if(actual is not null)CryptographicOperations.ZeroMemory(actual);}}
    private async Task<(string Account,string Organization)> ProfileAsync(string access,CancellationToken ct,CancellationToken compromised=default)
    {
        try
        {
        if(profileNotBefore>clock.GetUtcNow())throw new QueryException(FailureKind.AuthRenewalBusy,profileNotBefore);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,compromised);timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request=new HttpRequestMessage(HttpMethod.Get,ProfileEndpoint){Content=new ByteArrayContent([])};request.Headers.Authorization=new("Bearer",access);request.Headers.CacheControl=new(){NoCache=true};request.Content.Headers.ContentType=new("application/json");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        if(response.StatusCode==HttpStatusCode.TooManyRequests)
        {
            profileNotBefore=GuardedQueryClient.RetryAfter(response,clock.GetUtcNow())??clock.GetUtcNow().AddSeconds(5);
            throw new QueryException(FailureKind.AuthRenewalBusy,profileNotBefore);
        }
        if(!response.IsSuccessStatusCode)throw new QueryException(FailureKind.AuthRenewalUnknown);
        var bytes=await BoundedAsync(response,timeout.Token);
        try
        {
            using var profile=JsonDocument.Parse(bytes);var a=profile.RootElement.GetProperty("account").GetProperty("uuid").GetString();var o=profile.RootElement.GetProperty("organization").GetProperty("uuid").GetString();
            if(!Guid.TryParse(a,out var account)||!Guid.TryParse(o,out var org))throw new QueryException(FailureKind.AuthRenewalUnknown);
            profileNotBefore=null;return(account.ToString("D"),org.ToString("D"));
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
        }
        catch(QueryException error){throw new QueryException(error.Kind,error.RetryNotBefore??clock.GetUtcNow().AddSeconds(5));}
        catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.AuthRenewalUnknown,clock.GetUtcNow().AddSeconds(5));}
    }
    private static async Task<bool> InvalidGrantAsync(HttpResponseMessage response,CancellationToken ct)
    {
        byte[]? bytes=null;
        try
        {
            bytes=await BoundedAsync(response,ct);using var doc=JsonDocument.Parse(bytes);
            if(!doc.RootElement.TryGetProperty("error",out var error))return false;
            return error.ValueKind==JsonValueKind.String?error.GetString()=="invalid_grant":error.ValueKind==JsonValueKind.Object&&error.TryGetProperty("type",out var type)&&type.GetString()=="invalid_grant";
        }
        catch{return false;}finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
    }
    private static async Task<byte[]> BoundedAsync(HttpResponseMessage response,CancellationToken ct)
    {
        var media=response.Content.Headers.ContentType?.MediaType;
        if(media!="application/json"&&!(media?.EndsWith("+json",StringComparison.Ordinal)??false)||response.Content.Headers.ContentLength>65536)throw new QueryException(FailureKind.AuthRenewalUnknown);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);using var result=new MemoryStream();var buffer=new byte[4096];int n;
        try{while((n=await stream.ReadAsync(buffer,ct))>0){if(result.Length+n>65536)throw new QueryException(FailureKind.AuthRenewalUnknown);result.Write(buffer,0,n);}return result.ToArray();}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    public void Dispose(){http.Dispose();flight.Dispose();}
}
