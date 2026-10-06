using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GameDevUsageBar.App;
using GameDevUsageBar.App.Interop;
using GameDevUsageBar.App.Presentation;
using GameDevUsageBar.Core;
using GameDevUsageBar.Core.Presentation;
using GameDevUsageBar.Infrastructure;

// Observe actual visible HWND positions, including moves that occur synchronously
// inside one dispatcher callback. A final rectangle or screenshot alone would
// miss the brief monitor-origin flash this regression is intended to catch.
internal static class PopupPositionChecks
{
    private const int WindowPosChanged = 0x0047;
    private const uint ShowWindow = 0x0040;
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Handle, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    public static async Task Run(string qaRoot)
    {
        var root = Path.Combine(qaRoot, "popup-position-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var adapters = Enumerable.Range(0, 4).Select(i => new FixtureAdapter(i)).ToArray();
        var queries = new RejectQueryClient();
        await using var coordinator = new RefreshCoordinator(queries, new FixtureSnapshots());
        foreach (var adapter in adapters)
        {
            await coordinator.ConfigureAsync(adapter, new AccountConfig(adapter.Definition.Id, Guid.NewGuid(), "Isolated fixture", true));
            await coordinator.RefreshAsync(adapter.Definition.Id);
        }
        using var preferences = new PresentationPreferencesService(new PresentationStore(root), new(Language: "en-US"));
        using var hub = new ProviderStateHub(adapters, coordinator, preferences, Application.Current.Dispatcher);
        var panel = new TrayPopupWindow(hub) { ShowActivated = false };
        var handle = NativeWindows.Handle(panel);
        var source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("Popup HWND source was not created.");
        var monitors = NativeWindows.Monitors();
        var traces = new List<VisiblePosition>();
        var results = new List<object>();
        var stage = "initialization";
        var failures = 0;
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WindowPosChanged && lParam != IntPtr.Zero)
            {
                var position = Marshal.PtrToStructure<WindowPos>(lParam);
                if ((IsWindowVisible(hwnd) || (position.Flags & ShowWindow) != 0) && GetWindowRect(hwnd, out var rect))
                    traces.Add(new(stage, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, position.Flags));
            }
            return IntPtr.Zero;
        };
        source.AddHook(hook);

        async Task Check(string name, Func<Task> action)
        {
            stage = name;
            var start = traces.Count;
            try
            {
                await action();
                var positions = traces.Skip(start).ToArray();
                var staging = positions.FirstOrDefault(p => monitors.Any(m => Math.Abs(p.Left - (m.Work.Left + 8)) <= 1 && Math.Abs(p.Top - (m.Work.Top + 8)) <= 1));
                Assert(staging is null, "Visible popup visited monitor-origin staging position: " + JsonSerializer.Serialize(staging));
                results.Add(new { name, status = "PASS", visible_position_messages = positions.Length });
                Console.WriteLine("PASS " + name + " (" + positions.Length + " visible HWND position messages)");
            }
            catch (Exception error)
            {
                failures++;
                results.Add(new { name, status = "FAIL", error = error.Message });
                Console.WriteLine("FAIL " + name + " " + error.Message);
            }
        }

        async Task OpenAt(MonitorInfo monitor, NativeWindows.Point anchor, string? provider)
        {
            panel.SelectProvider(provider);
            panel.PositionAt(anchor);
            var firstVisibleStart = traces.Count;
            panel.Show();
            await Settle(panel);
            AssertFirstVisibleAnchored(traces, firstVisibleStart, monitor, anchor);
            AssertAnchored(panel, monitor, anchor);
        }

        try
        {
            var primary = monitors.FirstOrDefault(m => m.Primary) ?? monitors.First();
            var anchor = BottomRight(primary);
            await Check("Popup first show has no visible monitor-origin intermediate position", async () =>
            {
                await OpenAt(primary, anchor, adapters[0].Definition.Id);
                Assert(traces.Any(t => t.Stage == stage), "No actual visible HWND positioning messages were captured.");
            });
            await Check("Popup repeat visible placement goes directly to its new anchor", async () =>
            {
                // Keep it visible throughout. The previous implementation moves it
                // to work-area +8,+8 before returning to the anchor on every call.
                anchor = new() { X = (int)(primary.Work.Right - 64), Y = (int)(primary.Work.Bottom - 48) };
                panel.PositionAt(anchor);
                await Settle(panel);
                AssertAnchored(panel, primary, anchor);
            });
            await Check("Popup repeated hide and reopen retains anchored first visible bounds", async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    panel.Hide();
                    await Settle(panel);
                    await OpenAt(primary, anchor, adapters[0].Definition.Id);
                }
            });
            await Check("Popup queued content update cannot restore an obsolete anchor after reopen", async () =>
            {
                // This intentionally stays in one dispatcher turn until Show:
                // a content refit queued against the previous visible anchor must
                // not apply that captured point to the newly reopened popup.
                panel.SelectProvider(null);
                panel.Hide();
                anchor = new() { X = (int)(primary.Work.Right - 160), Y = (int)(primary.Work.Bottom - 120) };
                panel.SelectProvider(adapters[0].Definition.Id);
                panel.PositionAt(anchor);
                var firstVisibleStart = traces.Count;
                panel.Show();
                await Settle(panel);
                AssertFirstVisibleAnchored(traces, firstVisibleStart, primary, anchor);
                AssertAnchored(panel, primary, anchor);
            });
            await Check("Popup visible provider filtering and size changes never stage at monitor origin", async () =>
            {
                var before = panel.Height;
                panel.SelectProvider(null);
                await Settle(panel);
                Assert(panel.Height > before + 20, "Fixture did not exercise a real popup height change.");
                AssertAnchored(panel, primary, anchor);
                panel.SelectProvider("missing-fixture");
                await Settle(panel);
                Assert(panel.Height < before, "Empty selection did not shrink the actual popup.");
                AssertAnchored(panel, primary, anchor);
                panel.SelectProvider(adapters[0].Definition.Id);
                await Settle(panel);
                AssertAnchored(panel, primary, anchor);
            });
            await Check("Popup visible refresh and language changes preserve anchored bounds", async () =>
            {
                var before = panel.Height;
                adapters[0].MetricCount = 8;
                await coordinator.RefreshAsync(adapters[0].Definition.Id);
                await Settle(panel);
                Assert(hub.Models.Single(m => m.Id == adapters[0].Definition.Id).Metrics.Count() == 8, "Fixture provider update did not reach the rendered model.");
                Assert(panel.Height > before + 20, "Fixture refresh did not resize the actual popup.");
                AssertAnchored(panel, primary, anchor);
                foreach (var language in new[] { "zh-CN", "en-US" })
                {
                    Localizer.SetLanguage(language);
                    await Settle(panel);
                    AssertAnchored(panel, primary, anchor);
                }
            });
            foreach (var monitor in monitors)
            {
                await Check("Popup reopen clamps to real work area on " + monitor.Device, async () =>
                {
                    foreach (var edge in new[] {
                        BottomRight(monitor),
                        new NativeWindows.Point { X = (int)monitor.Work.Left + 1, Y = (int)monitor.Work.Bottom - 1 },
                        new NativeWindows.Point { X = (int)monitor.Work.Right - 1, Y = (int)monitor.Work.Top + 1 }
                    })
                    {
                        panel.Hide();
                        await Settle(panel);
                        await OpenAt(monitor, edge, adapters[1].Definition.Id);
                    }
                });
            }
            Assert(queries.Calls == 0, "Popup fixture unexpectedly issued a network/credential query.");
            Assert(adapters.Sum(a => a.Calls) == 5, "Showing or repositioning the popup caused an unsolicited provider refresh.");
        }
        finally
        {
            source.RemoveHook(hook);
            panel.AllowClose = true;
            panel.Close();
            Localizer.SetLanguage("en-US");
            await Settle(null);
            await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new {
                failures, results, monitors, visible_hwnd_positions = traces,
                scope = "Actual WPF/Win32 windows with synthetic in-memory provider state; no real credentials, CLI, provider network, installation or production process actions. Physical monitor DPI configurations are those present during this run."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("RESULT POPUP POSITION " + (results.Count - failures) + " passed, " + failures + " failed; " + root);
        if (failures != 0) throw new InvalidOperationException(failures + " popup positioning checks failed.");
    }

    private static NativeWindows.Point BottomRight(MonitorInfo monitor) => new() { X = (int)monitor.Work.Right - 32, Y = (int)monitor.Work.Bottom - 32 };
    private static async Task Settle(Window? panel)
    {
        // WPF queues layout/render/ContentRendered independently of native moves.
        // Four bounded turns let those actual callbacks run before final checks.
        for (var i = 0; i < 4; i++)
        {
            panel?.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(20);
        }
    }
    private static void AssertAnchored(TrayPopupWindow panel, MonitorInfo monitor, NativeWindows.Point anchor)
    {
        var bounds = NativeWindows.Bounds(panel);
        var expected = Placement.Anchor(anchor.X, anchor.Y, bounds.Width, bounds.Height, monitor.Work);
        Assert(IsWindowVisible(NativeWindows.Handle(panel)), "Popup HWND is not visible.");
        Assert(Math.Abs(bounds.Left - expected.Left) <= 2 && Math.Abs(bounds.Top - expected.Top) <= 2,
            "Popup final physical bounds do not match the anchor: " + bounds + "; expected " + expected);
        Assert(bounds.Left >= monitor.Work.Left - 1 && bounds.Top >= monitor.Work.Top - 1 && bounds.Right <= monitor.Work.Right + 1 && bounds.Bottom <= monitor.Work.Bottom + 1,
            "Popup extends beyond actual monitor work area: " + bounds + "; work " + monitor.Work);
    }
    private static void AssertFirstVisibleAnchored(List<VisiblePosition> traces, int start, MonitorInfo monitor, NativeWindows.Point anchor)
    {
        var first = traces.Skip(start).FirstOrDefault() ?? throw new InvalidOperationException("Show did not produce an actual visible HWND positioning message.");
        var expected = Placement.Anchor(anchor.X, anchor.Y, first.Width, first.Height, monitor.Work);
        // Check the first physical rectangle that is visible, before deferred
        // layout/refit callbacks can correct it. Subsequent resize notifications
        // may temporarily retain the old location while WPF applies new sizing.
        Assert(Math.Abs(first.Left - expected.Left) <= 2 && Math.Abs(first.Top - expected.Top) <= 2,
            "Popup first visible bounds do not match the requested anchor: " + JsonSerializer.Serialize(first) + "; expected " + expected);
    }
    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private sealed record VisiblePosition(string Stage, int Left, int Top, int Width, int Height, uint Flags);
    private sealed class FixtureAdapter(int index) : IProviderAdapter
    {
        public ProviderDefinition Definition { get; } = new("popup-fixture-" + index, "Popup fixture " + index, "Fixture", "Synthetic in-memory usage", "#6BD4A9", new("example.invalid", 443, "/unused"));
        public int Calls;
        public int MetricCount = 2;
        public Task<AdapterOutcome> RefreshAsync(AccountConfig config, IQueryClient client, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new AdapterOutcome(Enumerable.Range(0, MetricCount).Select(i => new Metric("fixture-" + i, "Available", 100 - i, "credits")).ToImmutableArray()));
        }
    }
    private sealed class RejectQueryClient : IQueryClient
    {
        public int Calls;
        public Task<byte[]> ReadAsync(ProviderDefinition definition, AccountConfig account, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("Network and credential queries are forbidden in popup fixtures."); }
    }
    private sealed class FixtureSnapshots : ISnapshotStore
    {
        private readonly Dictionary<string, UsageSnapshot> items = [];
        public Task<UsageSnapshot?> LoadAsync(string binding) => Task.FromResult(items.GetValueOrDefault(binding));
        public Task SaveAsync(UsageSnapshot snapshot) { items[snapshot.Binding] = snapshot; return Task.CompletedTask; }
        public Task RemoveAsync(string binding) { items.Remove(binding); return Task.CompletedTask; }
    }
}
