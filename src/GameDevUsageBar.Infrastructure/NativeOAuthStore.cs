using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;

namespace GameDevUsageBar.Infrastructure;

// Reads only known, explicitly selected CLI credential files. Never starts a CLI,
// refreshes tokens, writes native credentials, or scans browser/account stores.
public sealed record NativeOAuth(string Token,string AccountId,string Identity);
public sealed class NativeOAuthStore(string? testHome=null,TimeProvider? clock=null,Action<Exception>? onError=null)
{
    private readonly TimeProvider time=clock ?? TimeProvider.System;
    private string? claudeConfigDirectory;
    public NativeOAuthStore ForAccount(AccountConfig account)
    {
        if(account.ClaudeConfigDirectory is null)return claudeConfigDirectory is null?this:new NativeOAuthStore(testHome,time,onError);
        try
        {
            account.Validate();
            var directory=ProviderSources.NormalizeClaudeConfigDirectory(account.ClaudeConfigDirectory);
            ValidateLocalDirectory(directory);
            return new NativeOAuthStore(testHome,time,onError){claudeConfigDirectory=directory};
        }
        catch(QueryException){throw;}catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.Policy);}
    }
    public string PathFor(AccountConfig account)=>ForAccount(account).PathFor(account.ProviderId);
    public static void ValidateLocalDirectory(string directory)
    {
        var root=Path.GetPathRoot(directory)??throw new InvalidDataException("Invalid local configuration directory.");
        if(new DriveInfo(root).DriveType==DriveType.Network)throw new InvalidDataException("A network configuration directory is unsupported.");
        // Junctions/symlinks could make a frozen pathname point to another login.
        // Check metadata only; a not-yet-created account directory is permitted.
        string? candidate=directory;
        while(candidate is not null)
        {
            try{var attributes=File.GetAttributes(candidate);if((attributes&FileAttributes.ReparsePoint)!=0||(attributes&FileAttributes.Directory)==0)throw new InvalidDataException("An aliased configuration directory is unsupported.");}
            catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException){}
            candidate=Path.GetDirectoryName(candidate);
        }
    }
    public string PathFor(string id)
    {
        if(id=="claude"&&claudeConfigDirectory is {} selected)ValidateLocalDirectory(selected);
        var home=testHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return id switch {
            "codex"=>Path.Combine(testHome is null ? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home,".codex") : Path.Combine(home,".codex"),"auth.json"),
            "claude"=>Path.Combine(claudeConfigDirectory ?? (testHome is null ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home,".claude") : Path.Combine(home,".claude")),".credentials.json"),
            "gemini-cli"=>Path.Combine(home,".gemini","oauth_creds.json"),_=>throw new QueryException(FailureKind.Policy)
        };
    }
    public NativeOAuth Read(string id)
    {
        byte[]? bytes=null;
        try
        {
            bytes=ReadDocument(id);return ParseDocument(id,bytes);
        }
        finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
    }
    public byte[] ReadDocument(string id)
    {
        try
        {
            var path=PathFor(id);if(!File.Exists(path))throw new QueryException(FailureKind.CredentialMissing);
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            if(stream.Length is <=0 or >65536)throw new QueryException(FailureKind.CredentialUnreadable);
            var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);return bytes;
        }
        catch(QueryException){throw;}catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.CredentialUnreadable);}
    }
    public NativeOAuth ParseDocument(string id,byte[] bytes,bool validateExpiry=true)
    {
        try
        {
            if(!ProviderSources.SupportsLocal(id)||bytes.Length is <=0 or >65536)throw new QueryException(FailureKind.CredentialUnreadable);
            using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
            var node=id=="codex" ? root.GetProperty("tokens") : id=="claude" ? root.GetProperty("claudeAiOauth") : root;
            var token=Text(node,id=="claude"?"accessToken":"access_token");
            if(token.Length==0 || token.Length>8192 || token.Any(char.IsControl))throw new QueryException(FailureKind.CredentialMissing);
            var account=id=="codex"?Text(node,"account_id"):"";
            if(account.Length>100 || account.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('-' or '_')))throw new QueryException(FailureKind.CredentialUnreadable);
            var expireField=id=="claude"?"expiresAt":id=="gemini-cli"?"expiry_date":null;
            if(validateExpiry&&expireField is not null && node.TryGetProperty(expireField,out var expires) && expires.TryGetInt64(out var ms) && DateTimeOffset.FromUnixTimeMilliseconds(ms)<=time.GetUtcNow())throw new QueryException(FailureKind.CredentialExpired);
            string subject="";
            try
            {
                var jwt=(Text(node,"id_token") is {Length:>0} identityToken ? identityToken : token).Split('.');
                if(jwt.Length==3)
                {
                    var raw=jwt[1].Replace('-','+').Replace('_','/');raw=raw.PadRight((raw.Length+3)/4*4,'=');
                    using var claims=JsonDocument.Parse(Convert.FromBase64String(raw));subject=Text(claims.RootElement,"sub");
                }
            }
            catch(QueryException){throw;}catch{ /* Opaque tokens are supported. */ }
            if(validateExpiry&&id=="codex")
            {
                // Identity-token expiry does not determine access-token validity.
                // A CLI may retain an old identity token after renewing access.
                try {
                    var jwt=token.Split('.');if(jwt.Length==3) {
                        var raw=jwt[1].Replace('-','+').Replace('_','/');raw=raw.PadRight((raw.Length+3)/4*4,'=');
                        using var access=JsonDocument.Parse(Convert.FromBase64String(raw));
                        if(access.RootElement.TryGetProperty("exp",out var expiry)&&expiry.TryGetInt64(out var seconds)&&DateTimeOffset.FromUnixTimeSeconds(seconds)<=time.GetUtcNow())throw new QueryException(FailureKind.CredentialExpired);
                    }
                } catch(QueryException){throw;}catch{ /* An opaque access token is checked by the server. */ }
            }
            var refresh=Text(node,id=="claude"?"refreshToken":"refresh_token");
            var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id+"|"+account+"|"+(subject.Length>0?subject:refresh.Length>0?refresh:token)))).ToLowerInvariant();
            return new(token,account,identity);
        }
        catch(QueryException){throw;}
        catch(Exception error){ErrorObserver.Report(onError,error);throw new QueryException(FailureKind.CredentialUnreadable);}
    }
    private static string Text(JsonElement node,string key)=>node.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
}
