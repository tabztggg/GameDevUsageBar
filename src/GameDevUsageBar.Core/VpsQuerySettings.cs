using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDevUsageBar.Core;

// Legacy persisted configuration only. VPS is retired; no query transport or UI is registered.
public sealed record VpsQuerySettings(string Endpoint="",string ServerId="",string Auth="bearer",string Header="X-API-Key",string RemainingPointer="/data/remaining",string TotalPointer="",string UsedPointer="",string ResetPointer="",string Unit="GB")
{
    public Uri QueryUri()
    {
        var url=Endpoint.Replace("{server_id}",Uri.EscapeDataString(ServerId),StringComparison.Ordinal);
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length>0||uri.Fragment.Length>0||uri.IsLoopback||!uri.IdnHost.Contains('.')||IPAddress.TryParse(uri.IdnHost,out _)||url.Length>2048)
            throw new InvalidDataException("Enter a public HTTPS read-only API endpoint.");
        // Query IDs are allowed; credentials belong only in the protected header input.
        if(Regex.IsMatch(uri.Query,@"(?:[?&])(?:api_?key|token|access_token|password|secret|authorization)=",RegexOptions.IgnoreCase))throw new InvalidDataException("Do not put credentials in the endpoint URL.");
        return uri;
    }
    public VpsQuerySettings Validate(bool enabled)
    {
        if(ServerId.Length>100||ServerId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_' or '.'))||Auth is not ("bearer" or "header" or "none")||Unit is not ("bytes" or "MB" or "MiB" or "GB" or "GiB" or "TB" or "TiB"))throw new InvalidDataException("Invalid VPS settings.");
        if(!Regex.IsMatch(Header,"^[A-Za-z][A-Za-z0-9-]{0,63}$")||new[]{"Host","Cookie","Content-Length","Connection","Proxy-Authorization"}.Contains(Header,StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("Invalid authentication header.");
        foreach(var pointer in new[]{RemainingPointer,TotalPointer,UsedPointer,ResetPointer})if(pointer.Length>256||(pointer.Length>0&&!pointer.StartsWith('/'))||pointer.Any(char.IsControl)||Regex.IsMatch(pointer,@"~(?![01])"))throw new InvalidDataException("Use JSON pointers such as /data/remaining.");
        if(enabled||Endpoint.Length>0){if(Endpoint.Contains("{server_id}")&&ServerId.Length==0)throw new InvalidDataException("Enter the server ID used by the URL placeholder.");_=QueryUri();if(RemainingPointer.Length==0&&(TotalPointer.Length==0||UsedPointer.Length==0))throw new InvalidDataException("Map remaining traffic, or both total and used traffic.");}
        return this;
    }
    public static JsonElement? Select(JsonElement root,string pointer)
    {
        if(pointer.Length==0)return null;
        var value=root;
        foreach(var part in pointer[1..].Split('/').Select(s=>s.Replace("~1","/").Replace("~0","~")))
        {
            if(value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(part,out var child))value=child;
            else if(value.ValueKind==JsonValueKind.Array&&int.TryParse(part,out var index)&&index>=0&&index<value.GetArrayLength())value=value[index];
            else return null;
        }
        return value.ValueKind==JsonValueKind.Null?null:value;
    }
    public decimal BytesPerUnit=>Unit switch {"MB"=>1_000_000m,"MiB"=>1_048_576m,"GB"=>1_000_000_000m,"GiB"=>1_073_741_824m,"TB"=>1_000_000_000_000m,"TiB"=>1_099_511_627_776m,_=>1m};
    public string? CredentialBinding()=>Endpoint.Length==0?null:QueryUri().IdnHost+"|"+Auth+"|"+(Auth=="header"?Header.ToLowerInvariant():"");
}
