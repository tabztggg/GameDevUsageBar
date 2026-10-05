using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;

static class ProviderRepairChecks
{
    private const string Token="nonfunctional-fixture-access-token";
    private const string Cookie="session=nonfunctional-fixture-cookie";
    private const string Action="1234567890abcdef1234567890abcdef12345678";
    private const string CodexUsage="""{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":18000,"reset_at":1900000000},"secondary_window":{"used_percent":40,"limit_window_seconds":604800}},"credits":{"balance":"12.25"}}""";
    private const string ClaudeUsage="""{"five_hour":{"utilization":20,"resets_at":"2030-01-01T00:00:00Z"},"seven_day":{"utilization":50},"seven_day_opus":null,"extra_usage":{"is_enabled":true,"used_credits":123,"monthly_limit":1000}}""";
    private const string GeminiQuota="""{"buckets":[{"modelId":"fixture-fast","tokenType":"REQUESTS","remainingFraction":0.6,"resetTime":"2030-01-01T00:00:00Z"},{"modelId":"fixture-pro","tokenType":"REQUESTS","remainingFraction":0.25}]}""";
    private const string GrsaiBalance="""{"code":0,"data":{"credits":1250.50},"msg":"success"}""";
    private const string Billing="""{"balance":12.25,"spent":4.5,"cycleLabel":"Fixture","credits":[{"amount":10,"remaining":7,"expiresAt":"2099-01-01T00:00:00Z"},{"amount":10,"remaining":8,"expiresAt":"2000-01-01T00:00:00Z"}]}""";
    private const string Monitoring="""{"timeSeries":[{"points":[{"value":{"int64Value":"12"}},{"value":{"int64Value":"0"}},{"value":{"int64Value":"7"}}]}]}""";
    private static void Check(bool value){if(!value)throw new Exception("Provider repair assertion");}
    private static ImmutableArray<Metric> Parse(Func<JsonElement,ImmutableArray<Metric>> parser,string body){using var doc=JsonDocument.Parse(body);return parser(doc.RootElement);}
    private static AccountConfig Config(string id)=>new(id,Guid.NewGuid(),"Fixture",true,Guid.NewGuid(),Guid.NewGuid(),ProjectId:id=="gemini"?"fixture-project":"");
    private static HttpResponseMessage Json(string body)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Text(string body,string media)=>new(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,media)};
    public static IEnumerable<(string Name,Func<Task> Run)> Cases(string root,IReadOnlyList<IProviderAdapter> adapters)
    {
        yield return ("All declared services have configurable query adapters instead of Hold placeholders",()=>{Check(adapters.Where(a=>!a.Definition.IsDemo).All(a=>a.Definition.CanConfigure&&a.Definition.HoldReason is null));return Task.CompletedTask;});
        yield return ("Single OpenRouter API card reads account remaining balance and retires its duplicate",async()=>{
            Check(adapters.Count(a=>a.Definition.Id=="openrouter")==1 && adapters.All(a=>a.Definition.Id!="openrouter-account"));
            var adapter=adapters.Single(a=>a.Definition.Id=="openrouter");int calls=0;
            using var handler=new Handler((request,_)=>{calls++;Check(request.Method==HttpMethod.Get && request.RequestUri!.AbsoluteUri=="https://openrouter.ai/api/v1/credits" && request.Content is null && request.Headers.Authorization?.Parameter==Token);return Task.FromResult(Json("""{"data":{"total_credits":10,"total_usage":0.001438844}}"""));});
            using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);
            var result=await adapter.RefreshAsync(Config("openrouter"),queries,CancellationToken.None);Check(result.Failure is null&&result.Metrics[0].Value==9.998561156m&&result.Metrics[0].Kind==MetricKind.Balance&&calls==1);
            var config=Config("openrouter");var retired=Config("openrouter-account");var dir=Path.Combine(root,"retired-openrouter");Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir,"settings.json"),JsonSerializer.Serialize(new{Schema=3,Accounts=new[]{config,retired}}));var store=new SettingsStore(dir);Check((await store.LoadAsync()).SequenceEqual(new[]{config})&&!store.ReadOnly);
            var layout=new GameDevUsageBar.Core.Presentation.PresentationPreferences(CardOrder:["openrouter-account","openrouter","grsai"],Widget:new(CardIds:["openrouter-account","openrouter"])).Validate();Check(layout.CardOrder!.SequenceEqual(new[]{"openrouter","grsai"})&&layout.WidgetOrDefault.CardIds!.SequenceEqual(new[]{"openrouter"}));
            var obsolete=adapter.Definition with {Id="openrouter-account"};var secrets=new Secrets(Token);
            using var blocked=new GuardedQueryClient(secrets,[obsolete],handler);
            try{await blocked.ReadAsync(obsolete,retired,CancellationToken.None);throw new Exception("Retired provider queried");}catch(QueryException e){Check(e.Kind==FailureKind.Policy);}
            Check(secrets.Reads==0&&calls==1);
            int rejectedCalls=0;using var denied=new Handler((_,_)=>{rejectedCalls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));});using var deniedQueries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),denied);
            Check((await adapter.RefreshAsync(config,deniedQueries,CancellationToken.None)).Failure==FailureKind.Forbidden&&rejectedCalls==1);
        });
        yield return ("Retired GRSAI token card migrates without changing the primary key, layout or credentials",async()=>{
            Check(adapters.Count(a=>a.Definition.Id=="grsai")==1 && adapters.All(a=>a.Definition.Id!="grsai-account"));
            var dir=Path.Combine(root,"retired-grsai");Directory.CreateDirectory(dir);
            var main=Config("grsai") with {QueryRegion="china"};var retired=Config("grsai-account") with {QueryRegion="china"};
            var settings=Path.Combine(dir,"settings.json");await File.WriteAllTextAsync(settings,JsonSerializer.Serialize(new{Schema=3,Accounts=new[]{main,retired}}));
            var before=await File.ReadAllBytesAsync(settings);var store=new SettingsStore(dir);var loaded=await store.LoadAsync();
            var after=await File.ReadAllBytesAsync(settings);
            Check(!store.ReadOnly && loaded.SequenceEqual(new[]{main}) && before.SequenceEqual(after));
            await store.SaveAsync(new[]{main,retired});Check((await store.LoadAsync()).SequenceEqual(new[]{main}));
            var p=new GameDevUsageBar.Core.Presentation.PresentationPreferences(CardOrder:["codex","grsai-account","grsai"],StartInTray:true,Language:"zh-CN",Widget:new(Visible:true,Topmost:true,Locked:true,CardIds:["grsai-account","grsai"]));var next=p.Validate();
            Check(next.CardOrder!.SequenceEqual(new[]{"codex","grsai"}) && next.WidgetOrDefault.CardIds!.SequenceEqual(new[]{"grsai"}) && next.StartInTray && next.Language==p.Language && next.WidgetOrDefault.Topmost && next.WidgetOrDefault.Locked);
            var definition=new ProviderDefinition("grsai-account","retired","retired","retired","#ffffff",new("grsaiapi.com",443,"/client/openapi/getCredits"));var secrets=new Secrets(Token);int calls=0;
            using var handler=new Handler((_,_)=>{calls++;return Task.FromResult(Json(GrsaiBalance));});using var query=new ProviderQueryClient(secrets,[definition],handler);
            try {await query.ReadAsync(definition,retired,CancellationToken.None);throw new Exception("Retired source queried");}catch(QueryException e){Check(e.Kind==FailureKind.Policy);}
            Check(secrets.Reads==0&&calls==0);
        });
        yield return ("Codex parses remaining windows, reset inventory and native credit units",()=>{
            var data=Parse(UsageParsers.Codex,"{\"usage\":"+CodexUsage+",\"reset_credits\":{\"available_count\":3}}");
            Check(data[0].Value==90&&data[0].Percent==90&&data[0].Label=="5-hour remaining"&&data[0].WindowSeconds==18000&&data[1].Value==60&&data[1].Label=="Weekly remaining"&&data[1].WindowSeconds==604800&&data.Single(m=>m.Id=="reset-credits").Value==3&&data.Single(m=>m.Id=="credits").Value==12.25m);
            var zero=Parse(UsageParsers.Codex,"{\"usage\":"+CodexUsage+",\"reset_credits\":{\"available_count\":0}}");Check(zero.Single(m=>m.Id=="reset-credits").Value==0&&zero.Single(m=>m.Id=="reset-credits").Unit=="tickets");
            var missing=Parse(UsageParsers.Codex,"{\"usage\":"+CodexUsage+",\"reset_credits\":null}");Check(missing.All(m=>!m.Id.StartsWith("reset-",StringComparison.Ordinal))&&missing[0].Value==90&&missing.Single(m=>m.Id=="credits").Value==12.25m);return Task.CompletedTask;
        });
        yield return ("Claude percentages, nullable model quotas and extra-use cents retain their meaning",()=>{var data=Parse(UsageParsers.Claude,ClaudeUsage);Check(data[0].Value==80&&data[1].Value==50&&data.Single(m=>m.Id=="extra-used").Value==1.23m&&data.Single(m=>m.Id=="extra-limit").Value==10m&&data.All(m=>m.Id!="seven_day_opus"));return Task.CompletedTask;});
        yield return ("Gemini CLI keeps model/token quota buckets and shows the lowest remaining quota first",()=>{var data=Parse(UsageParsers.GeminiCli,GeminiQuota);Check(data.Length==2&&data[0].Value==25&&data[0].Scope.Contains("fixture-pro")&&data[0].Percent==25&&data[1].Value==60);return Task.CompletedTask;});
        yield return ("GRSAI unrestricted keys are not zero balances; limited and account zero remain numeric",()=>{
            var unlimited=Parse(UsageParsers.Grsai,"""{"code":0,"data":{"type":0,"credits":0}}""");
            Check(unlimited[0].Kind==MetricKind.Unlimited && unlimited[0].Value is null && unlimited[0].Display=="No key limit" && unlimited[0].Percent is null);
            var def=adapters.Single(a=>a.Definition.Id=="grsai").Definition;var config=Config("grsai");var now=DateTimeOffset.UtcNow;
            var state=new ProviderState(config,new(config.Binding(def),DataOrigin.Live,now,unlimited),now,null,false,null);
            Check(GameDevUsageBar.Core.Presentation.BarPresentation.Value(def,state,now)=="No key limit");
            foreach(var body in new[]{"""{"code":0,"data":{"type":1,"credits":0}}""","""{"code":0,"data":{"credits":0}}"""})Check(Parse(UsageParsers.Grsai,body)[0].Value==0);
            Check(Parse(UsageParsers.GrsaiAccount,"""{"code":0,"data":{"type":0,"credits":0}}""")[0].Value==0);
            foreach(var body in new[]{"""{"code":0,"data":{"type":2,"credits":0}}""","""{"code":0,"data":{"type":null,"credits":0}}""","""{"code":1,"data":{"type":0,"credits":0}}"""}){bool rejected=false;try{Parse(UsageParsers.Grsai,body);}catch{rejected=true;}Check(rejected);}
            return Task.CompletedTask;
        });
        yield return ("AI Studio project metrics distinguish zero, absent and truncated data",()=>{
            Check(Parse(UsageParsers.GeminiProject,Monitoring)[0].Value==19);
            Check(Parse(UsageParsers.GeminiProject,"""{"timeSeries":[{"points":[{"value":{"int64Value":"0"}}]}]}""")[0].Value==0);
            foreach(var body in new[]{"{}","{\"timeSeries\":[]}","{\"nextPageToken\":\"fixture\"}"}){bool rejected=false;try{Parse(UsageParsers.GeminiProject,body);}catch(QueryException e){rejected=e.Kind is FailureKind.NoData or FailureKind.IncompleteData;}Check(rejected);}return Task.CompletedTask;
        });
        yield return ("Malformed quota numbers and missing required balances never become fake zero",()=>{
            foreach(var pair in new (Func<JsonElement,ImmutableArray<Metric>>,string)[]{(UsageParsers.Claude,"{\"five_hour\":{\"utilization\":101}}"),(UsageParsers.GeminiCli,"{\"buckets\":[{\"remainingFraction\":-1}]}"),(UsageParsers.Grsai,"{\"code\":0,\"data\":{}}"),(UsageParsers.TypeSafe,"{\"balance\":null,\"spent\":1}")}){bool rejected=false;try{Parse(pair.Item1,pair.Item2);}catch{rejected=true;}Check(rejected);}return Task.CompletedTask;
        });
        foreach(var id in new[]{"codex","claude","gemini-cli","grsai","gemini"})
        {
            var current=id;
            yield return (current+" complete query workflow uses only its fixed method, endpoint and credentials",async ()=>{
                var calls=new List<(string Url,string Method,string Body,bool Cookie)>();
                using var handler=new Handler(async(request,ct)=>{
                    var body=request.Content is null?"":await request.Content.ReadAsStringAsync(ct);var url=request.RequestUri!.AbsoluteUri;
                    calls.Add((url,request.Method.Method,body,request.Headers.Contains("Cookie")));
                    Check(ProviderQueryClient.Allowed(current,request.RequestUri,request.Method));
                    if(current=="codex")return Json(url.EndsWith("reset-credits")?"{\"available_count\":3}":CodexUsage);
                    if(current=="claude"){Check(request.Headers.GetValues("anthropic-beta").Single()=="oauth-2025-04-20");return Json(ClaudeUsage);}
                    if(current=="gemini-cli"){Check(body=="{}");return Json(GeminiQuota);}
                    if(current=="grsai"){Check(request.Method==HttpMethod.Get && request.Content is null && request.RequestUri!.AbsolutePath=="/client/common/getCredits" && Uri.UnescapeDataString(request.RequestUri.Query[8..])==Token && !request.Headers.Contains("Authorization"));return Json(GrsaiBalance);}
                    if(current=="gemini"){Check(url.Contains("fixture-project/timeSeries?")&&url.Contains("pageSize=2000")&&request.Content is null);return Json(Monitoring);}
                    throw new Exception("Unexpected provider workflow");
                });
                var secrets=new Secrets(Token);using var queries=new ProviderQueryClient(secrets,adapters.Select(a=>a.Definition),handler);
                var adapter=adapters.Single(a=>a.Definition.Id==current);var result=await adapter.RefreshAsync(Config(current),queries,CancellationToken.None);
                Check(result.Failure is null&&result.Metrics.Length>0&&calls.Count==(current=="codex"?2:1)&&secrets.Reads==1);
                Check(!JsonSerializer.Serialize(queries.Events).Contains(Token)&&!JsonSerializer.Serialize(queries.Events).Contains(Cookie));
            });
        }
        yield return ("All added query sources stop after HTTP auth, limit, redirect and server failures",async()=>{
            foreach(var id in new[]{"codex","claude","gemini-cli","grsai","gemini"})foreach(var status in new[]{301,401,403,429,500})
            {
                var calls=0;using var handler=new Handler((_,_)=>{calls++;var response=new HttpResponseMessage((HttpStatusCode)status);if(status==429)response.Headers.TryAddWithoutValidation("Retry-After","120");return Task.FromResult(response);});
                using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);
                var result=await adapters.Single(a=>a.Definition.Id==id).RefreshAsync(Config(id),queries,CancellationToken.None);
                Check(calls==1&&result.Failure==(status==301?FailureKind.Redirect:status==401?FailureKind.Unauthorized:status==403?FailureKind.Forbidden:status==429?FailureKind.RateLimited:FailureKind.HttpError));if(status==429)Check(result.RetryNotBefore>DateTimeOffset.UtcNow.AddSeconds(100));
            }
        });
        yield return ("A failed optional Codex reset inventory cannot discard valid usage or trigger replay",async()=>{
            int calls=0;using var handler=new Handler((_,_)=>Task.FromResult(++calls==1?Json(CodexUsage):new HttpResponseMessage(HttpStatusCode.Forbidden)));
            using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);var result=await adapters.Single(a=>a.Definition.Id=="codex").RefreshAsync(Config("codex"),queries,CancellationToken.None);
            Check(result.Failure is null&&result.Metrics[0].Value==90&&result.Metrics.Single(m=>m.Id=="credits").Value==12.25m&&result.Metrics.All(m=>!m.Id.StartsWith("reset-",StringComparison.Ordinal))&&calls==2);
        });
        yield return ("GRSAI account-by-key guards query parameters and redacts failures",async()=>{
            const string path="https://grsaiapi.com/client/common/getCredits";
            Check(ProviderQueryClient.Allowed("grsai",new Uri(path+"?apikey=fixture%2Bkey"),HttpMethod.Get));
            foreach(var suffix in new[]{"","?apikey=","?token=fixture","?apikey=fixture&other=1","?apikey=fixture&apikey=second","?apikey=%0A","?apikey=%ZZ","?apikey=fixture#fragment"})Check(!ProviderQueryClient.Allowed("grsai",new Uri(path+suffix),HttpMethod.Get));
            Check(!ProviderQueryClient.Allowed("grsai",new Uri(path+"?apikey=fixture"),HttpMethod.Post));
            using var handler=new Handler((_,_)=>Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture URL with "+Token)));
            using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);
            var outcome=await adapters.Single(a=>a.Definition.Id=="grsai").RefreshAsync(Config("grsai"),queries,CancellationToken.None);
            Check(outcome.Failure==FailureKind.Network && !JsonSerializer.Serialize(outcome).Contains(Token) && !JsonSerializer.Serialize(queries.Events).Contains(Token));
        });
        yield return ("GRSAI China query uses only the documented China node with no fallback",async()=>{
            int calls=0;using var handler=new Handler((request,_)=>{calls++;Check(request.RequestUri!.Host=="grsai.dakka.com.cn");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));});
            using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);var result=await adapters.Single(a=>a.Definition.Id=="grsai").RefreshAsync(Config("grsai") with {QueryRegion="china"},queries,CancellationToken.None);Check(result.Failure==FailureKind.Unauthorized&&calls==1);
        });
        yield return ("Codex optional inventory network failure preserves quota and renewed access ignores old identity expiry",async()=>{
            int calls=0;using var handler=new Handler((_,_)=>++calls==1?Task.FromResult(Json(CodexUsage)):Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture")));
            using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler);var result=await adapters.Single(a=>a.Definition.Id=="codex").RefreshAsync(Config("codex"),queries,CancellationToken.None);
            Check(result.Failure is null&&result.Metrics[0].Value==90&&result.Metrics.Single(m=>m.Id=="credits").Value==12.25m&&result.Metrics.All(m=>!m.Id.StartsWith("reset-",StringComparison.Ordinal))&&calls==2);
            var home=Path.Combine(root,"codex-expiry-home");Directory.CreateDirectory(Path.Combine(home,".codex"));var path=Path.Combine(home,".codex","auth.json");
            string Jwt(long expiry)=>"e30."+Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"fixture-user\",\"exp\":"+expiry+"}")).TrimEnd('=').Replace('+','-').Replace('/','_')+".fixture";
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{tokens=new{access_token=Jwt(4070908800),id_token=Jwt(1),account_id="fixture-account"}}));var native=new NativeOAuthStore(home);Check(native.Read("codex").AccountId=="fixture-account");
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new{tokens=new{access_token=Jwt(1),id_token=Jwt(4070908800),account_id="fixture-account"}}));
            try{native.Read("codex");throw new Exception("Expired access accepted");}catch(QueryException error){Check(error.Kind==FailureKind.CredentialExpired);}
        });
        yield return ("Claude website Cookie is confined to the selected organization usage GET",async()=>{
            var org=Guid.NewGuid();int calls=0;using var handler=new Handler((request,_)=>{calls++;Check(request.RequestUri!.AbsoluteUri=="https://claude.ai/api/organizations/"+org+"/usage"&&request.Headers.GetValues("Cookie").Single()==Cookie&&!request.Headers.Contains("Authorization"));return Task.FromResult(Json(ClaudeUsage));});
            using var queries=new ProviderQueryClient(new Secrets(Cookie),adapters.Select(a=>a.Definition),handler);var result=await adapters.Single(a=>a.Definition.Id=="claude").RefreshAsync(Config("claude") with {SourceMode="web-cookie",CredentialSource="web-cookie",OrganizationId=org.ToString()},queries,CancellationToken.None);Check(result.Failure is null&&calls==1);
        });
        yield return ("Native OAuth identity changes and expiry block network and never rewrite native files",async()=>{
            var home=Path.Combine(root,"native-home");Directory.CreateDirectory(Path.Combine(home,".claude"));var path=Path.Combine(home,".claude",".credentials.json");
            var native=new NativeOAuthStore(home);await File.WriteAllTextAsync(path,"""{"claudeAiOauth":{"accessToken":"fixture-access","refreshToken":"fixture-refresh","expiresAt":4070908800000}}""");
            var first=native.Read("claude");var before=SHA256.HashData(await File.ReadAllBytesAsync(path));int calls=0;
            using var handler=new Handler((_,_)=>{calls++;return Task.FromResult(Json(ClaudeUsage));});using var queries=new ProviderQueryClient(new Secrets(Token),adapters.Select(a=>a.Definition),handler,native);
            var config=Config("claude") with {SourceMode="local-oauth",CredentialSource="local-oauth",NativeIdentity=first.Identity,CredentialRef=null};
            var adapter=adapters.Single(a=>a.Definition.Id=="claude");var result=await adapter.RefreshAsync(config,queries,CancellationToken.None);var afterBytes=await File.ReadAllBytesAsync(path);var after=SHA256.HashData(afterBytes);Check(result.Failure is null&&calls==1&&before.SequenceEqual(after));
            await File.WriteAllTextAsync(path,"""{"claudeAiOauth":{"accessToken":"fixture-second","refreshToken":"fixture-other-account","expiresAt":4070908800000}}""");
            Check((await adapter.RefreshAsync(config,queries,CancellationToken.None)).Failure==FailureKind.IdentityChanged&&calls==1);
            await File.WriteAllTextAsync(path,"""{"claudeAiOauth":{"accessToken":"fixture-second","refreshToken":"fixture-refresh","expiresAt":1}}""");
            Check((await adapter.RefreshAsync(config,queries,CancellationToken.None)).Failure==FailureKind.CredentialExpired&&calls==1);
            foreach(var id in new[]{"codex","gemini-cli"}){Check((await adapters.Single(a=>a.Definition.Id==id).RefreshAsync(Config(id) with {SourceMode="local-oauth",CredentialSource="local-oauth",NativeIdentity=first.Identity,CredentialRef=null},queries,CancellationToken.None)).Failure==FailureKind.CredentialMissing);}
        });
        yield return ("Source mode, project, organization and node changes isolate cache bindings",()=>{
            var definition=adapters.Single(a=>a.Definition.Id=="claude").Definition;var config=Config("claude");Check(config.Binding(definition)!=(config with {SourceMode="web-cookie",OrganizationId=Guid.NewGuid().ToString()}).Binding(definition));
            var gem=adapters.Single(a=>a.Definition.Id=="gemini").Definition;var gc=Config("gemini");Check(gc.Binding(gem)!=(gc with {ProjectId="other-project"}).Binding(gem));
            var grs=adapters.Single(a=>a.Definition.Id=="grsai").Definition;var gr=Config("grsai");Check(gr.Binding(grs)!=(gr with {QueryRegion="china"}).Binding(grs));return Task.CompletedTask;
        });
        yield return ("Malformed settings and missing project are rejected before credentials or network",async()=>{
            var secrets=new Secrets(Token);int calls=0;using var handler=new Handler((_,_)=>{calls++;return Task.FromResult(Json("{}"));});using var queries=new ProviderQueryClient(secrets,adapters.Select(a=>a.Definition),handler);
            foreach(var c in new[]{Config("gemini") with {ProjectId=""},Config("gemini") with {ProjectId="../escape"},Config("claude") with {SourceMode="web-cookie",CredentialSource="web-cookie",OrganizationId="bad"},Config("codex") with {AccountId="bad\r\nheader"},Config("claude") with {SourceMode="web-cookie",OrganizationId=Guid.NewGuid().ToString()}})
            {var result=await adapters.Single(a=>a.Definition.Id==c.ProviderId).RefreshAsync(c,queries,CancellationToken.None);Check(result.Failure is FailureKind.Policy or FailureKind.ProjectRequired or FailureKind.CredentialMissing);}
            Check(calls==0&&secrets.Reads==0);
        });
        yield return ("Custom query guard rejects generation, credit redemption and account mutations",()=>{
            foreach(var row in new[]{("codex","https://chatgpt.com/backend-api/wham/usage",HttpMethod.Post),("codex","https://chatgpt.com/backend-api/wham/rate-limit-reset",HttpMethod.Post),("gemini-cli","https://cloudcode-pa.googleapis.com/v1internal:generateContent",HttpMethod.Post),("grsai","https://grsaiapi.com/client/openapi/deleteAPIKey",HttpMethod.Post),("typesafe","https://console.typesafe.ai/settings/recharge",HttpMethod.Post),("claude","https://evil.invalid/api/oauth/usage",HttpMethod.Get)})Check(!ProviderQueryClient.Allowed(row.Item1,new Uri(row.Item2),row.Item3));return Task.CompletedTask;
        });
        yield return ("Native CLI sources never load unvalidated disk cache and clear values on account change",async()=>{
            var store=new CountingStore();var adapter=new SwitchingAdapter();await using var coordinator=new RefreshCoordinator(new NullQuery(),store);var config=Config("claude") with {SourceMode="local-oauth",CredentialSource="local-oauth",NativeIdentity=new string('a',64),CredentialRef=null};await coordinator.ConfigureAsync(adapter,config);Check(store.Loads==0);await coordinator.RefreshAsync("claude");Check(coordinator.Get("claude").LastSuccess is not null&&store.Saves==0);adapter.Changed=true;await coordinator.RefreshAsync("claude");Check(coordinator.Get("claude").LastSuccess is null&&coordinator.Get("claude").Failure==FailureKind.IdentityChanged);
        });
    }
    private sealed class Secrets(string token):ISecretStore{public int Reads;public string Read(AccountConfig c){Reads++;return token;}}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>run(request,ct);}
    private sealed class CountingStore:ISnapshotStore{public int Loads,Saves;public Task<UsageSnapshot?> LoadAsync(string b){Loads++;return Task.FromResult<UsageSnapshot?>(null);}public Task SaveAsync(UsageSnapshot s){Saves++;return Task.CompletedTask;}public Task RemoveAsync(string b)=>Task.CompletedTask;}
    private sealed class NullQuery:IQueryClient{public Task<byte[]> ReadAsync(ProviderDefinition d,AccountConfig c,CancellationToken ct)=>throw new Exception();}
    private sealed class SwitchingAdapter:IProviderAdapter{public bool Changed;public ProviderDefinition Definition=>new("claude","Claude","Subscription","Quota","#ffffff",new("api.anthropic.com",443,"/api/oauth/usage"));public Task<AdapterOutcome> RefreshAsync(AccountConfig c,IQueryClient q,CancellationToken ct)=>Task.FromResult(Changed?AdapterOutcome.Fail(FailureKind.IdentityChanged):new AdapterOutcome([new("remaining","Remaining",80,"%",MetricKind.Quota,100)]));}
}
