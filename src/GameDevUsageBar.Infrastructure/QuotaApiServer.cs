using System.Net;
using System.Text.Json;
using GameDevUsageBar.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameDevUsageBar.Infrastructure;

public sealed class QuotaApiServer(Func<UsageExport> export,Func<NetworkSpeedSnapshot>? network=null,Func<AccountUsageExport>? accounts=null) : IAsyncDisposable
{
    public const int DefaultPort=17864;
    public const string DefaultAddress="http://127.0.0.1:17864";
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    private WebApplication? application;
    public string? Address {get;private set;}
    public async Task StartAsync(int port=DefaultPort)
    {
        if(application is not null)throw new InvalidOperationException("Quota API is already started.");
        if(port is <0 or >65535)throw new ArgumentOutOfRangeException(nameof(port));
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions {Args=[],ContentRootPath=AppContext.BaseDirectory,EnvironmentName="Production",ApplicationName=typeof(QuotaApiServer).Assembly.GetName().Name});
        builder.Configuration.Sources.Clear();builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options=> {
            options.AddServerHeader=false;options.Listen(IPAddress.Loopback,port,listen=>listen.Protocols=HttpProtocols.Http1);
            options.Limits.MaxConcurrentConnections=16;options.Limits.MaxRequestBodySize=0;options.Limits.MaxRequestHeadersTotalSize=8192;
            options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(3);options.Limits.KeepAliveTimeout=TimeSpan.FromSeconds(5);
        });
        var app=builder.Build();app.Run(HandleAsync);
        try {
            await app.StartAsync();
            Address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();application=app;
        } catch {await app.DisposeAsync();throw;}
    }
    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl="no-store";context.Response.Headers.XContentTypeOptions="nosniff";
        context.Response.Headers.ContentSecurityPolicy="default-src 'none'";
        async Task Reply(int code,object body){context.Response.StatusCode=code;await context.Response.WriteAsJsonAsync(body,Json,context.RequestAborted);}
        if(context.Connection.RemoteIpAddress is not {} ip||!IPAddress.IsLoopback(ip)||context.Request.Host.Host!="127.0.0.1"||context.Request.Host.Port!=context.Connection.LocalPort||context.Request.Headers.ContainsKey("Origin")||context.Request.Headers["Sec-Fetch-Site"]=="cross-site")
        {await Reply(403,new {error="local_client_required"});return;}
        if(context.Request.Method!="GET"){context.Response.Headers.Allow="GET";await Reply(405,new {error="read_only_get_required"});return;}
        if(context.Request.QueryString.HasValue||context.Request.ContentLength>0||context.Request.Headers.ContainsKey("Transfer-Encoding")){await Reply(400,new {error="query_and_body_not_supported"});return;}
        var path=context.Request.Path.Value;
        if(path=="/v1/health"){await Reply(200,new {schemaVersion=1,app="GameDevUsageBar",appVersion=UsageExporter.AppVersion,status="ready",readOnly=true});return;}
        if(path=="/v1/network"){if(network is null){await Reply(503,new {error="network_sampler_unavailable"});return;}await Reply(200,network());return;}
        if(path!="/v1/accounts"&&path!="/v1/usage"&&!(path?.StartsWith("/v1/usage/",StringComparison.Ordinal)??false)){await Reply(404,new {error="not_found"});return;}
        try {
            if(path=="/v1/accounts"){if(accounts is null){await Reply(503,new {error="accounts_unavailable"});return;}await Reply(200,accounts());return;}
            var segments=path!["/v1/usage".Length..].Split('/',StringSplitOptions.RemoveEmptyEntries);
            if(segments.Length is 2 or 3&&segments[1]=="accounts"){
                if(accounts is null){await Reply(503,new {error="accounts_unavailable"});return;}
                var accountSnapshot=accounts();var providerAccounts=accountSnapshot.Accounts.Where(a=>a.ProviderId==segments[0]).ToArray();
                if(providerAccounts.Length==0){await Reply(404,new {error="provider_not_found"});return;}
                if(segments.Length==2){await Reply(200,new {accountSnapshot.SchemaVersion,accountSnapshot.App,accountSnapshot.AppVersion,accountSnapshot.GeneratedAt,accounts=providerAccounts});return;}
                if(!Guid.TryParseExact(segments[2],"D",out var slot)){await Reply(404,new {error="account_not_found"});return;}
                var account=providerAccounts.FirstOrDefault(a=>a.SlotId==slot);
                if(account is null){await Reply(404,new {error="account_not_found"});return;}
                await Reply(200,new {accountSnapshot.SchemaVersion,accountSnapshot.App,accountSnapshot.AppVersion,accountSnapshot.GeneratedAt,account});return;
            }
            var snapshot=export();
            if(path=="/v1/usage"){await Reply(200,snapshot);return;}
            var id=path!["/v1/usage/".Length..];var provider=snapshot.Providers.FirstOrDefault(p=>p.Id==id);
            if(provider is null){await Reply(404,new {error="provider_not_found"});return;}
            await Reply(200,new {snapshot.SchemaVersion,snapshot.App,snapshot.AppVersion,snapshot.GeneratedAt,snapshot.SharedAccountState,provider});
        } catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested) { }
        catch {if(!context.Response.HasStarted)await Reply(503,new {error="snapshot_unavailable"});}
    }
    public async ValueTask DisposeAsync()
    {
        var app=application;application=null;Address=null;if(app is null)return;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try{await app.StopAsync(timeout.Token);}catch(OperationCanceledException){}finally{await app.DisposeAsync();}
    }
}
