using System.Net;
using System.Net.Http.Headers;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

public sealed record SafeEvent(DateTimeOffset At, string Provider, string Kind, int? HttpStatus, long Bytes);
public sealed class GuardedQueryClient : IQueryClient, IDisposable
{
    private readonly ISecretStore secrets;
    private readonly IReadOnlyDictionary<string,ProviderDefinition> catalog;
    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly List<SafeEvent> events = [];
    public IReadOnlyList<SafeEvent> Events { get { lock(events) return events.ToArray(); } }
    public GuardedQueryClient(ISecretStore secrets, IEnumerable<ProviderDefinition> definitions, HttpMessageHandler? testHandler = null, TimeProvider? clock = null)
    {
        this.secrets = secrets; catalog = definitions.ToDictionary(d=>d.Id); time = clock ?? TimeProvider.System;
        client = new HttpClient(testHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static void Validate(Uri uri, EndpointRule rule, HttpMethod method)
    {
        if(rule.MaxBytes is <1 or >65536 || rule.TimeoutSeconds is <1 or >30 || rule.Header is not ("Authorization" or "xi-api-key") || rule.Prefix is not ("Bearer " or "")) throw new QueryException(FailureKind.Policy);
        if(!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || !string.Equals(uri.IdnHost,rule.Host,StringComparison.OrdinalIgnoreCase) || uri.Port != rule.Port || uri.AbsolutePath != rule.Path || method != HttpMethod.Get || rule.Port != 443 || rule.Host.Contains('*') || uri.IsLoopback || rule.Path.Contains('%') || rule.Path.Contains("..")) throw new QueryException(FailureKind.Policy);
    }
    public static DateTimeOffset? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if(header?.Date is { } date) return date < now ? now : date;
        if(header?.Delta is { } delta) { try { return now.Add(delta); } catch(ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; } }
        return null;
    }
    public async Task<byte[]> ReadAsync(ProviderDefinition definition, AccountConfig account, CancellationToken ct)
    {
        if(ProviderSources.IsRetired(definition.Id)||ProviderSources.IsExtended(definition.Id))throw new QueryException(FailureKind.Policy);
        try{account.Validate();}catch(InvalidDataException){throw new QueryException(FailureKind.Policy);}
        if(!catalog.TryGetValue(definition.Id,out var approved) || approved != definition || account.ProviderId != definition.Id || TripoRegions.Endpoint(definition,account) is not { } rule) throw new QueryException(FailureKind.Policy);
        Validate(rule.Uri,rule,HttpMethod.Get);
        if(definition.Id=="tripo" && !account.HasUsableCredential)throw new QueryException(FailureKind.CredentialMissing);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(rule.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get,rule.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var secret = secrets.Read(account);
        if(!request.Headers.TryAddWithoutValidation(rule.Header, rule.Prefix+secret)) throw new QueryException(FailureKind.Policy);
        try
        {
            using var response = await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            var status = (int)response.StatusCode;
            lock(events) { events.Add(new(time.GetUtcNow(),definition.Id,"query",status,0)); if(events.Count>200) events.RemoveAt(0); }
            if(status is 401 or 403) throw new QueryException(status==401 ? FailureKind.Unauthorized : FailureKind.Forbidden);
            if(status==429 || (status==503 && response.Headers.RetryAfter != null)) throw new QueryException(FailureKind.RateLimited, RetryAfter(response,time.GetUtcNow()));
            if(status is >=300 and <400) throw new QueryException(FailureKind.Redirect);
            if(!response.IsSuccessStatusCode) throw new QueryException(FailureKind.HttpError);
            var media = response.Content.Headers.ContentType?.MediaType;
            if(!string.Equals(media,"application/json",StringComparison.OrdinalIgnoreCase) && !(media?.EndsWith("+json",StringComparison.OrdinalIgnoreCase) ?? false)) throw new QueryException(FailureKind.SchemaMismatch);
            if(response.Content.Headers.ContentLength > rule.MaxBytes) throw new QueryException(FailureKind.TooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[4096]; int read;
            while((read=await stream.ReadAsync(chunk,timeout.Token))>0)
            {
                if(buffer.Length+read>rule.MaxBytes) throw new QueryException(FailureKind.TooLarge);
                await buffer.WriteAsync(chunk.AsMemory(0,read),timeout.Token);
            }
            return buffer.ToArray();
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { throw new QueryException(FailureKind.Timeout); }
        catch(HttpRequestException) { throw new QueryException(FailureKind.Network); }
        catch(IOException) { throw new QueryException(FailureKind.Network); }
    }
    public void Dispose() => client.Dispose();
}
