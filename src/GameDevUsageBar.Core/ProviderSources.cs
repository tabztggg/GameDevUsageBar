using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameDevUsageBar.Core;

public static class ProviderSources
{
    public static bool IsRetired(string id)=>id is "grsai-account" or "openrouter-account" or "vps" or "typesafe";
    public static bool IsExtended(string id)=>id is "codex" or "claude" or "gemini-cli" or "gemini" or "grsai";
    public static bool SupportsLocal(string id)=>id is "codex" or "claude" or "gemini-cli";
    public static string[] Modes(string id)=>id=="typesafe" ? ["browser-session","manual"] : id=="claude" ? ["manual","local-oauth","saved-oauth","web-cookie"] : id=="codex" ? ["manual","local-oauth","saved-oauth"] : SupportsLocal(id) ? ["manual","local-oauth"] : ["manual"];
    public static string ModeLabel(string id,string mode)=>mode switch {
        "browser-session"=>"Built-in browser session (recommended)","local-oauth"=>id=="claude"?"Local CLI account (automatic renewal)":"Use local CLI login (read only)","saved-oauth"=>"Saved CLI account (encrypted)","web-cookie"=>"Website Cookie",_=>id switch {
            "typesafe"=>"Website Cookie","codex" or "claude" or "gemini-cli" or "gemini"=>"OAuth access token",_=>"API key"
        }
    };
    public static string Policy(AccountConfig c)=>(c.SourceMode=="saved-oauth"
        ?JsonSerializer.Serialize(new {Version="saved-oauth-v1",c.SourceMode,c.QueryRegion,c.ProjectId,c.OrganizationId,c.AccountId,c.NativeIdentity,c.NativeAuthRef,c.CredentialSource,c.Vps,c.VpsCredentialBinding})
        :JsonSerializer.Serialize(new {Version=c.ProviderId=="grsai"?"grsai-api-key-account-v3":"usage-query-v1",c.SourceMode,c.QueryRegion,c.ProjectId,c.OrganizationId,c.AccountId,c.NativeIdentity,c.CredentialSource,c.Vps,c.VpsCredentialBinding}))
        +(c.ClaudeConfigDirectory is {} directory?"|claude-profile:"+NormalizeClaudeConfigDirectory(directory).ToUpperInvariant():"");
    // Configuration paths are local drive paths, never URLs, UNC shares, device
    // paths or drive-relative names. Resolve aliases before binding and deduping.
    public static string NormalizeClaudeConfigDirectory(string directory)
    {
        if(string.IsNullOrWhiteSpace(directory)||directory.Length>32000||directory.Any(char.IsControl)
            ||!Regex.IsMatch(directory,"^[A-Za-z]:[\\\\/]"))throw new InvalidDataException("Invalid Claude configuration directory.");
        var local=directory.Replace('/','\\');
        if(local[2..].Any(c=>c is ':' or '<' or '>' or '"' or '|' or '?' or '*')
            ||local[3..].Split('\\',StringSplitOptions.RemoveEmptyEntries).Any(part=>part is not ("." or "..")&&(part.EndsWith(' ')||part.EndsWith('.')||Regex.IsMatch(part,"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)",RegexOptions.IgnoreCase))))
            throw new InvalidDataException("Invalid Claude configuration directory.");
        try{return Path.TrimEndingDirectorySeparator(Path.GetFullPath(local));}
        catch(Exception error)when(error is ArgumentException or NotSupportedException or PathTooLongException){throw new InvalidDataException("Invalid Claude configuration directory.",error);}
    }
    public static void Validate(AccountConfig c)
    {
        if(!Modes(c.ProviderId).Contains(c.SourceMode) || c.CredentialSource is not (null or "manual" or "local-oauth" or "saved-oauth" or "web-cookie" or "browser-session") || !TripoRegions.IsValid(c.QueryRegion)
            || (c.QueryRegion!="global" && c.ProviderId != "grsai")
            || (c.ProjectId.Length>63 || (c.ProjectId.Length>0 && !Regex.IsMatch(c.ProjectId,"^[a-z][a-z0-9-]{4,61}[a-z0-9]$")))
            || (c.ProjectId.Length>0 && c.ProviderId!="gemini")
            || (c.OrganizationId.Length>0 && (c.ProviderId!="claude" || !Guid.TryParse(c.OrganizationId,out _)))
            || c.AccountId.Length>100 || c.AccountId.Any(ch=>!char.IsAsciiLetterOrDigit(ch)&&ch is not ('-' or '_'))
            || (c.AccountId.Length>0 && c.ProviderId!="codex")
            || (c.NativeIdentity is not null && (c.NativeIdentity.Length!=64 || !c.NativeIdentity.All(Uri.IsHexDigit)))
            || (c.NativeAuthRef is not null&&(c.ProviderId is not ("codex" or "claude")||c.SourceMode!="saved-oauth"))
            || c.NativeAuthRef==Guid.Empty
            || c.ClaudeConfigDirectory is not null&&(c.ProviderId!="claude"||c.SourceMode!="local-oauth"))throw new InvalidDataException("Invalid source configuration");
        if(c.ClaudeConfigDirectory is {} directory)NormalizeClaudeConfigDirectory(directory);
    }
    public static string Description(AccountConfig c)=>c.ProviderId switch {
        "codex"=>"Read-only ChatGPT usage and reset-credit inventory. No Codex process, prompt, or credit redemption. Local login reads only the Codex auth file; an expired token requires renewing the CLI login.",
        "claude"=>c.SourceMode=="web-cookie" ? "Read-only Claude website usage. Sign in with Firefox, import or paste the Cookie header, and enter the organization UUID from your own usage page." : c.SourceMode=="local-oauth"?"Read-only Claude usage with automatic renewal of the bound local CLI account. Added accounts keep separate configuration directories. A revoked or unavailable refresh grant may require signing in again.":"Read-only Claude OAuth usage. This needs a subscription OAuth token, not an Anthropic API key. Saved or pasted expired tokens require renewing the CLI login and updating this source.",
        "gemini-cli"=>"Read-only Code Assist quota query using Gemini CLI OAuth. This is separate from AI Studio API usage. Unsupported consumer accounts cannot be fixed by reinstalling the CLI. No CLI launch or generation call.",
        "gemini"=>"Google Cloud Monitoring: completed Gemini API requests over the last 24 hours, including errors. Enter a project ID and a Google OAuth token with Monitoring read permission. A Gemini API key cannot read these metrics. Data may be delayed or unavailable; no inferred balance or percentage.",
        "typesafe"=>c.SourceMode=="browser-session" ? "Read-only visible TypeSafe account balance through the built-in browser. Sign in and complete verification yourself. No billing-cycle spend is inferred from the balance; no payment or model call." : "Read-only TypeSafe website billing with Cookie. Website security verification may block this method even when the account has access. Use the built-in browser session when blocked.",
        "grsai"=>"Read-only GET /client/common/getCredits using your API key to query account credits. The key is sent only to the selected HTTPS node, never logged, with redirects disabled. No generation or key-management call.",
        _=>""
    };
}
