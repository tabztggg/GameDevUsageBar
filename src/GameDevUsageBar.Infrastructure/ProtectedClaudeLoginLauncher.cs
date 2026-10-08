using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameDevUsageBar.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GameDevUsageBar.Tests")]

namespace GameDevUsageBar.Infrastructure;

public sealed record ClaudeLoginOutcome(string Status, bool ProcessClosed);

// Only a user click calls the maintained Bridge's attended auth-login entry.
// This application never constructs a native Claude command or logs child output.
public sealed class ProtectedClaudeLoginLauncher
{
    private readonly string root;
    private readonly string? launcherPath;
    private readonly string bridgePath;

    public ProtectedClaudeLoginLauncher(string root, string? launcherPath = null)
        : this(root, launcherPath, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "skills", "claude-code-bridge", "scripts", "bridge.py")) { }

    // Only the fixture assembly can substitute an owned dependency marker.
    // The application always uses the maintained user-level Bridge path above.
    internal ProtectedClaudeLoginLauncher(string root, string? launcherPath, string bridgePath)
    {
        this.root = root; this.launcherPath = launcherPath; this.bridgePath = bridgePath;
    }
    public static string DirectoryHash(string directory) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(directory).Replace('/', '\\').TrimEnd('\\').ToLowerInvariant()))).ToLowerInvariant();

    public async Task<ClaudeLoginOutcome> LoginAsync(AccountConfig account, CancellationToken cancellationToken = default)
    {
        if (account.ProviderId != "claude" || account.SourceMode != "local-oauth" || account.ClaudeConfigDirectory is null)
            return new("blocked", true);
        var script = launcherPath ?? Path.Combine(AppContext.BaseDirectory, "tools", "Start-ClaudeAccountLogin.ps1");
        if (!File.Exists(script) || !File.Exists(bridgePath)) return new("blocked", true);
        var receipts = Path.Combine(root, "claude-login-results");
        Directory.CreateDirectory(receipts);
        var receipt = Path.Combine(receipts, Guid.NewGuid().ToString("N") + ".json");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = launcherPath is not null };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-ProfileDirectory", account.ClaudeConfigDirectory, "-ResultPath", receipt })
            start.ArgumentList.Add(arg);
        // The interpreter must not inherit another project's Python injection.
        start.Environment.Remove("PYTHONPATH"); start.Environment.Remove("PYTHONHOME");
        Process? terminal;
        try { terminal = Process.Start(start); }
        catch { return new("blocked", true); }
        if (terminal is null) return new("blocked", true);
        using (terminal)
        {
            try { await terminal.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { return new("unknown", false); }
            try
            {
                if (!File.Exists(receipt)) return new("unknown", true);
                if (new FileInfo(receipt).Length is <= 0 or > 16384) return new("unknown", true);
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(receipt, CancellationToken.None));
                var result = document.RootElement;
                if (result.GetProperty("schema_version").GetInt32() != 1 ||
                    result.GetProperty("config_dir_sha256").GetString() != DirectoryHash(account.ClaudeConfigDirectory))
                    return new("unknown", true);
                var status = result.GetProperty("status").GetString();
                var closed = result.GetProperty("process_closed").GetBoolean();
                if(!Guid.TryParse(result.GetProperty("attempt_id").GetString(),out var attempt)||attempt==Guid.Empty||
                    result.GetProperty("slot_id").GetString()!=account.SlotId.ToString("D")||result.GetProperty("automatic_retry").GetBoolean())
                    return new("unknown",closed);
                if(status=="completed_unverified"&&(!closed||!result.GetProperty("cli_started").GetBoolean()||
                    !result.GetProperty("root_exit_observed").GetBoolean()||!result.GetProperty("job_tree_exit_observed").GetBoolean()||
                    result.GetProperty("exit_code").GetInt32()!=0||!result.GetProperty("credential_change_verified").GetBoolean()||
                    !result.GetProperty("credential_changed").GetBoolean()))return new("unknown",closed);
                return status is "completed_unverified" or "blocked" or "failed" or "unknown"
                    ? new(status, closed) : new("unknown", closed);
            }
            catch { return new("unknown", true); }
        }
    }
}
