using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;
using GameDevUsageBar.Providers;
using Forms = System.Windows.Forms;

// These checks run the real WPF App in fresh copies of this test process. Every
// copy has a unique mutex, a fixture data home, a Demo-only catalog and no quota
// API listener. No production process, authentication file or service is used.
internal static class RuntimeLifecycleChecks
{
    private const string SecretCanary = "nonfunctional-runtime-secret-canary-A7c92";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(45);
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    {
        "tray-exit", "overview-exit", "handled-dispatcher", "startup-failure", "fatal-background", "late-fatal", "shutdown-failure", "hold"
    };

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    public static async Task Run(string root)
    {
        var runRoot = Path.Combine(Path.GetFullPath(root), "runtime-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var checks = new List<object>();
        var failed = 0;
        async Task Check(string name, Func<Task> check)
        {
            try
            {
                await check();
                checks.Add(new { name, status = "PASS" });
                Console.WriteLine("PASS " + name);
            }
            catch (Exception error)
            {
                failed++;
                checks.Add(new { name, status = "FAIL", error = error.ToString() });
                Console.WriteLine("FAIL " + name + " " + error.Message);
            }
        }

        foreach (var mode in new[] { "tray-exit", "overview-exit" })
        {
            var captured = mode;
            await Check("Real App records the actual " + captured + " menu and completes a clean session", async () =>
            {
                var folder = Path.Combine(runRoot, captured);
                var child = await RunCompletedChild(captured, folder, runRoot);
                Assert(child.ExitCode == 0, "The clean fixture did not exit successfully");
                var entries = ReadEntries(folder);
                Assert(Has(entries, "session_begin") && Has(entries, "app_ready"), "Startup or ready evidence is missing");
                var reason = captured == "tray-exit" ? "tray_exit" : "overview_exit";
                Assert(Has(entries, "exit_requested", reason), "The actual menu did not retain its exit source");
                Assert(Has(entries, "session_end", reason, 0), "The normal exit did not complete its session marker");
                Assert(!entries.Any(HasException), "A normal exit logged an unexpected exception");
                AssertSafeLogs(folder);
            });
        }

        await Check("Real dispatcher exception logs safe stack evidence and the App continues before clean exit", async () =>
        {
            var folder = Path.Combine(runRoot, "handled-dispatcher");
            var child = await RunCompletedChild("handled-dispatcher", folder, runRoot);
            Assert(child.ExitCode == 0 && File.Exists(Path.Combine(folder, "continued-after-error.json")), "The dispatcher did not continue after the handled error");
            var entries = ReadEntries(folder);
            AssertException(entries, "dispatcher_unhandled", nameof(ThrowHandledDispatcherFixture));
            Assert(Has(entries, "session_end", "explicit_exit", 0), "The handled error prevented a later clean shutdown");
            Assert(!Has(entries, "session_end", "fatal_exception"), "A handled UI error was classified as fatal");
            AssertSafeLogs(folder);
        });

        await Check("Real startup failure is classified separately and exits with failure evidence", async () =>
        {
            var folder = Path.Combine(runRoot, "startup-failure");
            var child = await RunCompletedChild("startup-failure", folder, runRoot);
            Assert(child.ExitCode != 0, "Startup failure incorrectly returned success");
            var entries = ReadEntries(folder);
            AssertException(entries, "startup_exception", nameof(ThrowStartupFixture));
            Assert(Has(entries, "session_end", "startup_failure", 1), "Startup failure did not close the marker with a nonzero code");
            AssertSafeLogs(folder);
        });

        await Check("Real unhandled background exception preserves its fatal stack before process termination", async () =>
        {
            var folder = Path.Combine(runRoot, "fatal-background");
            var child = await RunCompletedChild("fatal-background", folder, runRoot);
            Assert(child.ExitCode != 0, "The deliberately fatal fixture survived or reported success");
            var entries = ReadEntries(folder);
            AssertException(entries, "appdomain_unhandled", nameof(ThrowUnhandledBackgroundFixture));
            Assert(Has(entries, "session_end", "fatal_exception", 1), "The fatal callback did not durably close its marker");
            AssertSafeLogs(folder);
        });

        await Check("A real teardown exception preserves the requested menu source and records a failed shutdown", async () =>
        {
            var folder = Path.Combine(runRoot, "shutdown-failure");
            var child = await RunCompletedChild("shutdown-failure", folder, runRoot);
            Assert(child.ExitCode != 0, "A failed teardown returned success");
            var entries = ReadEntries(folder);
            Assert(Has(entries, "exit_requested", "tray_exit"), "The teardown error erased the actual requested exit source");
            Assert(entries.Any(entry => entry.GetProperty("event").GetString() == "shutdown_cleanup_exception"
                && HasException(entry) && entry.GetProperty("exception").GetProperty("type").GetString() == typeof(ObjectDisposedException).FullName),
                "The real Host cleanup exception was not captured");
            Assert(Has(entries, "session_end", "shutdown_failure", 1), "The failed teardown was falsely completed as a clean user exit");
            Assert(!Has(entries, "session_end", "tray_exit", 0), "A cleanup failure was classified as graceful");
            AssertSafeLogs(folder);
        });

        await Check("A fatal exception after WPF exit is still recorded before the process closes its session", async () =>
        {
            var folder = Path.Combine(runRoot, "late-fatal");
            var child = await RunCompletedChild("late-fatal", folder, runRoot);
            Assert(child.ExitCode != 0, "The deliberately fatal post-WPF fixture reported success");
            var entries = ReadEntries(folder);
            Assert(Has(entries, "app_exit", "explicit_exit"), "The late-fatal fixture did not first finish WPF exit");
            AssertException(entries, "appdomain_unhandled", nameof(ThrowAfterWpfExitFixture));
            Assert(Has(entries, "session_end", "fatal_exception", 1), "Late fatal evidence was lost after the WPF exit callback");
            Assert(!Has(entries, "session_end", "explicit_exit", 0), "WPF exit prematurely claimed a completed clean process session");
            AssertSafeLogs(folder);
        });

        await Check("Abruptly killing only the owned fixture yields an incomplete session with an unknown cause on restart", async () =>
        {
            var folder = Path.Combine(runRoot, "abrupt-termination");
            using (var child = await StartChild("hold", folder, runRoot))
            {
                try
                {
                    await WaitReady(child, folder);
                    var entries = ReadEntries(folder);
                    Assert(Has(entries, "app_ready") && !Has(entries, "session_end"), "The hold fixture was not alive in its active session");
                    KillOwnedChild(child, requireReady: true);
                    await WaitForExit(child);
                }
                finally
                {
                    if (!child.Process.HasExited)
                    {
                        KillOwnedChild(child, requireReady: false);
                        await WaitForExit(child);
                    }
                    await SaveChildOutput(child, runRoot);
                }
            }
            var before = ReadEntries(folder);
            Assert(!Has(before, "session_end"), "An abrupt kill fabricated a completed session");
            var restart = await RunCompletedChild("overview-exit", folder, runRoot);
            Assert(restart.ExitCode == 0, "The restart could not recover from the incomplete session");
            var after = ReadEntries(folder);
            Assert(Has(after, "previous_session_incomplete", "unclean_termination_unknown"), "The interrupted marker was not detected on the next launch");
            Assert(!Has(after, "previous_session_fatal"), "An externally killed process was falsely called an exception crash");
            Assert(Has(after, "session_end", "overview_exit", 0), "The recovered session could not complete a normal exit");
            AssertSafeLogs(folder);
        });

        File.WriteAllText(Path.Combine(runRoot, "results.json"), JsonSerializer.Serialize(new
        {
            checks,
            failed,
            fixtureOnly = true,
            productionProcessLaunched = false,
            liveProviderQueries = false,
            childQuotaApiEnabled = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (failed != 0) throw new Exception(failed + " runtime lifecycle integration checks failed");
    }

    // Program dispatches this before constructing its parent WPF Application.
    public static int RunChild(string[] args)
    {
        if (args.Length != 4 || args[0] != "--runtime-child" || !Modes.Contains(args[1])
            || !Guid.TryParseExact(args[3], "N", out _)) return 64;
        var qaRoot = Environment.GetEnvironmentVariable("GAMEDEVUSAGEBAR_QA_ROOT");
        if (string.IsNullOrWhiteSpace(qaRoot)) return 64;
        var folder = Path.GetFullPath(args[2]);
        var allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(qaRoot)) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) return 64;
        Directory.CreateDirectory(folder);
        // Expected fatal child tests must not open a Windows crash dialog.
        SetErrorMode(0x0001 | 0x0002);
        new PresentationStore(folder).SaveAsync(new PresentationPreferences(StartInTray: true,
            Widget: new WidgetPreferences(Visible: false, ShowNetwork: false), Language: "en-US")).GetAwaiter().GetResult();
        var mode = args[1];
        var token = args[3];
        var app = new GameDevUsageBar.App.App(folder, ProviderCatalog.Create().Where(a => a.Definition.IsDemo).ToArray(),
            "Local\\GameDevUsageBar.RuntimeFixture." + token, showStartupErrors: false, startQuotaApi: false);
        app.InitializeComponent();
        app.RuntimeReady += () =>
        {
            Assert(app.Host is not null && app.Host.Adapters.All(adapter => adapter.Definition.IsDemo), "An external provider reached the lifecycle fixture");
            Assert(app.Host!.Queries.Events.Count == 0 && app.Host.QuotaApi is null && app.Host.QuotaApiStatus == "NotStarted", "The fixture queried a service or attempted to open the production API port");
            if (mode == "startup-failure") ThrowStartupFixture();
            File.WriteAllText(ReadyPath(folder, token), JsonSerializer.Serialize(new
            {
                token,
                processId = Environment.ProcessId,
                fixtureRoot = folder,
                assemblyPath = typeof(RuntimeLifecycleChecks).Assembly.Location,
                providers = app.Host.Adapters.Select(adapter => adapter.Definition.Id).ToArray(),
                queryEventCount = app.Host.Queries.Events.Count,
                quotaApiStatus = app.Host.QuotaApiStatus
            }));
            switch (mode)
            {
                case "tray-exit":
                    app.Dispatcher.BeginInvoke(() => InvokeTrayExit(app), DispatcherPriority.Background);
                    break;
                case "overview-exit":
                    app.Dispatcher.BeginInvoke(() => InvokeOverviewExit(app), DispatcherPriority.Background);
                    break;
                case "handled-dispatcher":
                    app.Dispatcher.BeginInvoke(new Action(ThrowHandledDispatcherFixture), DispatcherPriority.Normal);
                    app.Dispatcher.BeginInvoke(async () =>
                    {
                        File.WriteAllText(Path.Combine(folder, "continued-after-error.json"), "{\"continued\":true}");
                        await app.ExitAsync("explicit_exit");
                    }, DispatcherPriority.Background);
                    break;
                case "fatal-background":
                    app.Dispatcher.BeginInvoke(() => new Thread(ThrowUnhandledBackgroundFixture)
                    {
                        IsBackground = true,
                        Name = "GameDevUsageBar fatal fixture"
                    }.Start(), DispatcherPriority.Background);
                    break;
                case "shutdown-failure":
                    app.Dispatcher.BeginInvoke(async () =>
                    {
                        // The isolated Host coordinator owns no enabled accounts or
                        // requests. Its deliberate early disposal makes the real
                        // App's later Host cleanup fail; no production object is used.
                        await app.Host.Coordinator.DisposeAsync();
                        InvokeTrayExit(app);
                    }, DispatcherPriority.Background);
                    break;
                case "late-fatal":
                    app.Dispatcher.BeginInvoke(async () => await app.ExitAsync("explicit_exit"), DispatcherPriority.Background);
                    break;
                // The parent controls the single deliberate abrupt termination.
                case "hold": break;
            }
        };
        var exitCode = app.Run();
        if (mode == "late-fatal") ThrowAfterWpfExitFixture();
        return exitCode;
    }

    private static void InvokeOverviewExit(GameDevUsageBar.App.App app)
    {
        var menu = ((Button)app.MainWindow.FindName("OverviewMore")).ContextMenu;
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void InvokeTrayExit(GameDevUsageBar.App.App app)
    {
        var tray = typeof(GameDevUsageBar.App.App).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app)!;
        var icon = (Forms.NotifyIcon)typeof(TrayIconHost).GetField("icon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
        icon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Last().PerformClick();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowHandledDispatcherFixture() => throw new InvalidOperationException("Authorization: Bearer " + SecretCanary);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowStartupFixture() => throw new InvalidOperationException("Cookie=" + SecretCanary);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUnhandledBackgroundFixture() => throw new InvalidOperationException("api_key=" + SecretCanary);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAfterWpfExitFixture() => throw new InvalidOperationException("post_shutdown_token=" + SecretCanary);

    private sealed class Child(Process process, string executable, string fixtureRoot, string token,
        Task<string> output, Task<string> error) : IDisposable
    {
        public Process Process { get; } = process;
        public string Executable { get; } = executable;
        public string FixtureRoot { get; } = fixtureRoot;
        public string Token { get; } = token;
        public Task<string> Output { get; } = output;
        public Task<string> Error { get; } = error;
        public void Dispose() => Process.Dispose();
    }

    private sealed record ChildResult(int ExitCode, int ProcessId, string Token);

    private static Task<Child> StartChild(string mode, string fixtureRoot, string qaRoot)
    {
        fixtureRoot = Path.GetFullPath(fixtureRoot);
        Directory.CreateDirectory(fixtureRoot);
        var executable = Environment.ProcessPath ?? throw new Exception("The test executable path is unavailable");
        var token = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = fixtureRoot
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(RuntimeLifecycleChecks).Assembly.Location);
        start.ArgumentList.Add("--runtime-child");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(fixtureRoot);
        start.ArgumentList.Add(token);
        start.Environment["GAMEDEVUSAGEBAR_QA_ROOT"] = qaRoot;
        var process = Process.Start(start) ?? throw new Exception("The owned fixture could not be launched");
        return Task.FromResult(new Child(process, executable, fixtureRoot, token,
            process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()));
    }

    private static async Task<ChildResult> RunCompletedChild(string mode, string folder, string qaRoot)
    {
        using var child = await StartChild(mode, folder, qaRoot);
        try
        {
            await WaitForExit(child);
            await SaveChildOutput(child, qaRoot);
            if (mode != "startup-failure") AssertReadyIsolation(child);
            return new(child.Process.ExitCode, child.Process.Id, child.Token);
        }
        catch
        {
            if (!child.Process.HasExited)
            {
                KillOwnedChild(child, requireReady: false);
                await WaitForExit(child);
                await SaveChildOutput(child, qaRoot);
            }
            throw;
        }
    }

    private static async Task WaitForExit(Child child)
    {
        var wait = child.Process.WaitForExitAsync();
        if (await Task.WhenAny(wait, Task.Delay(ChildTimeout)) != wait)
            throw new TimeoutException("The owned lifecycle fixture did not exit within 45 seconds");
        await wait;
    }

    private static async Task WaitReady(Child child, string folder)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < ChildTimeout)
        {
            if (child.Process.HasExited) throw new Exception("The hold fixture exited before it was ready");
            if (File.Exists(ReadyPath(folder, child.Token)))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(ReadyPath(folder, child.Token)));
                    var ready = document.RootElement;
                    if (ready.GetProperty("token").GetString() == child.Token
                        && ready.GetProperty("processId").GetInt32() == child.Process.Id
                        && SamePath(ready.GetProperty("fixtureRoot").GetString()!, child.FixtureRoot)
                        && SamePath(ready.GetProperty("assemblyPath").GetString()!, typeof(RuntimeLifecycleChecks).Assembly.Location)
                        && Has(ReadEntries(folder), "app_ready"))
                    {
                        AssertReadyIsolation(child);
                        return;
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(75);
        }
        // StartChild owns this exact PID and its launch arguments, even if startup
        // never reached the ready callback. Stop the fixture; never retry it.
        KillOwnedChild(child, requireReady: false);
        await WaitForExit(child);
        throw new TimeoutException("The owned lifecycle fixture did not become ready within 45 seconds");
    }

    private static void KillOwnedChild(Child child, bool requireReady)
    {
        if (child.Process.HasExited) return;
        Assert(child.Process.StartInfo.ArgumentList.Contains(child.FixtureRoot)
            && child.Process.StartInfo.ArgumentList.Contains(child.Token)
            && child.Process.StartInfo.ArgumentList.Contains("--runtime-child"), "Refusing to stop a process without the owned fixture arguments");
        Assert(SamePath(child.Process.MainModule!.FileName, child.Executable), "Refusing to stop a process with a different executable");
        if (requireReady)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(ReadyPath(child.FixtureRoot, child.Token)));
            Assert(document.RootElement.GetProperty("processId").GetInt32() == child.Process.Id
                && document.RootElement.GetProperty("token").GetString() == child.Token,
                "Refusing to stop a process whose ready receipt does not match");
        }
        child.Process.Kill(entireProcessTree: false);
    }

    private static async Task SaveChildOutput(Child child, string root)
    {
        var output = (await child.Output).Replace(SecretCanary, "[fixture-secret-redacted]", StringComparison.Ordinal);
        var error = (await child.Error).Replace(SecretCanary, "[fixture-secret-redacted]", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(root, "child-" + child.Token + ".log"),
            "Fixture PID " + child.Process.Id + "; exit code " + child.Process.ExitCode + Environment.NewLine + output + error);
    }

    private static void AssertReadyIsolation(Child child)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ReadyPath(child.FixtureRoot, child.Token)));
        var ready = document.RootElement;
        Assert(ready.GetProperty("processId").GetInt32() == child.Process.Id
            && ready.GetProperty("token").GetString() == child.Token
            && SamePath(ready.GetProperty("fixtureRoot").GetString()!, child.FixtureRoot), "The child fixture's identity receipt does not match");
        Assert(ready.GetProperty("providers").EnumerateArray().All(provider => provider.GetString() == "demo")
            && ready.GetProperty("queryEventCount").GetInt32() == 0
            && ready.GetProperty("quotaApiStatus").GetString() == "NotStarted", "The running fixture was not isolated from providers and the production API port");
    }

    private static List<JsonElement> ReadEntries(string root)
    {
        var logs = Path.Combine(root, "logs");
        Assert(Directory.Exists(logs), "The App did not create its log directory");
        var entries = new List<JsonElement>();
        foreach (var path in Directory.GetFiles(logs, "runtime*.jsonl").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            foreach (var line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                entries.Add(document.RootElement.Clone());
            }
        Assert(entries.Count > 0, "The runtime evidence log is empty");
        return entries;
    }

    private static bool Has(IEnumerable<JsonElement> entries, string eventName, string? reason = null, int? exitCode = null)
        => entries.Any(entry => entry.GetProperty("event").GetString() == eventName
            && (reason is null || entry.GetProperty("reason").GetString() == reason)
            && (exitCode is null || entry.GetProperty("exitCode").GetInt32() == exitCode));

    private static bool HasException(JsonElement entry)
        => entry.TryGetProperty("exception", out var error) && error.ValueKind == JsonValueKind.Object;

    private static void AssertException(IEnumerable<JsonElement> entries, string eventName, string method)
    {
        var entry = entries.Single(e => e.GetProperty("event").GetString() == eventName && HasException(e));
        var error = entry.GetProperty("exception");
        Assert(error.GetProperty("type").GetString() == typeof(InvalidOperationException).FullName, "The exception type was not preserved");
        Assert(error.GetProperty("methods").EnumerateArray().Any(m => m.GetString()?.Contains(method, StringComparison.Ordinal) == true), "Safe method stack evidence is missing");
        Assert(!error.TryGetProperty("message", out _) && !error.TryGetProperty("data", out _), "Arbitrary exception values were added to the runtime log");
    }

    private static void AssertSafeLogs(string root)
    {
        foreach (var path in Directory.GetFiles(Path.Combine(root, "logs"), "*"))
        {
            if (Path.GetExtension(path) is not (".json" or ".jsonl")) continue;
            var text = File.ReadAllText(path);
            Assert(!text.Contains(SecretCanary, StringComparison.Ordinal), "A fake secret from an exception message reached persistent runtime evidence");
        }
    }

    private static string ReadyPath(string folder, string token) => Path.Combine(folder, "lifecycle-ready-" + token + ".json");
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
}
