using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;

namespace WirelessScanner.Tests;

internal static class LiveSurveyFixture
{
    public static readonly NetworkInterfaceInfo Adapter = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Intel Wi-Fi 6E AX211", "Built-in Wireless NIC",
        "PCI\\VEN_8086&DEV_0090", NetworkDeviceType.Wireless_WiFi, true, "00:1A:11:22:33:44", "192.168.1.100");

    private static AccessPoint Ap(string ssid, string bssid, string band, int ch, int? w, int rssi, string sec = "WPA2-Enterprise", string vendor = "Cisco Systems") =>
        new(bssid, ssid, null, band, ch, w, rssi, null, rssi + 95, Math.Clamp(2 * (rssi + 100), 0, 100), sec, "Infrastructure", vendor, Array.Empty<string>(), DateTime.UtcNow, 80);

    /// <summary>A realistic plant-floor environment: 3 bands, bonded 5/6 GHz channels, a hidden SSID, DFS use.</summary>
    public static List<AccessPoint> Environment() => new()
    {
        Ap("PlantOps", "8C:5A:25:04:91:B0", "5 GHz", 36, 80, -52),
        Ap("PlantOps", "8C:5A:25:04:91:B1", "2.4 GHz", 6, 20, -61),
        Ap("PlantOps", "8C:5A:25:04:91:B2", "5 GHz", 100, 80, -66),
        Ap("PlantOps", "8C:5A:25:04:91:B3", "6 GHz", 37, 160, -63),
        Ap("Guest-Portal", "00:17:88:A1:B2:C3", "2.4 GHz", 11, 20, -57, "Open", "Philips Lighting"),
        Ap("Guest-Portal", "00:17:88:A1:B2:C4", "5 GHz", 149, 40, -71, "Open", "Philips Lighting"),
        Ap("Facilities-IoT", "A0:B1:C2:D3:E4:01", "2.4 GHz", 1, 20, -74, "WPA2-Personal", "Hewlett Packard"),
        Ap("Facilities-IoT", "A0:B1:C2:D3:E4:02", "5 GHz", 157, 40, -82, "WPA2-Personal", "Hewlett Packard"),
        Ap("Scanner-Net", "3C:22:FB:10:00:01", "5 GHz", 44, 20, -69, "WPA3-Personal", "Zebra"),
        Ap("Scanner-Net", "3C:22:FB:10:00:02", "2.4 GHz", 6, 20, -77, "WPA3-Personal", "Zebra"),
        Ap("Office", "F0:9F:C2:00:11:22", "5 GHz", 52, 80, -73, "WPA2-Personal", "Ubiquiti"),
        Ap("Office", "F0:9F:C2:00:11:23", "6 GHz", 5, 80, -70, "WPA3-Personal", "Ubiquiti"),
        Ap("", "DE:AD:BE:EF:00:01", "2.4 GHz", 3, 40, -79, "WPA2-Personal", "Unknown Vendor"),
        Ap("Printer-DIRECT", "02:11:22:33:44:55", "2.4 GHz", 11, 20, -84, "WPA2-Personal", "HP"),
    };

    public static (MainWindowViewModel Vm, SaturationTestViewModel Sat, CountingProvider Provider, ScanScheduler Scheduler) Build(bool autoScan = false)
    {
        var provider = new CountingProvider();
        provider.Aps.Clear();
        provider.Aps.AddRange(Environment());
        var scheduler = new ScanScheduler(provider) { AutoScan = autoScan };
        var repo = new MockSessionRepository();
        var manager = new SessionManager(repo, new NullLogger());
        var vm = new MainWindowViewModel(provider, manager, repo, new MockExportService(), new MockRestApiServer(), new MockSettingsRepository(), scheduler);
        var satRepo = new MemorySaturationRepository();
        var runner = new SaturationTestRunner(scheduler, satRepo, new SaturationAnalyzer(), new NullLogger());
        var sat = new SaturationTestViewModel(provider, runner, satRepo, new SaturationAnalyzer(), new SaturationExportService())
        {
            Ui = a => a(), Confirm = (_, _) => true, KeepAwake = _ => { }, IsOnBattery = () => false
        };
        vm.AttachSaturation(sat);
        vm.Adapters.Add(Adapter);
        vm.SelectedAdapter = Adapter;
        return (vm, sat, provider, scheduler);
    }
}

[TestFixture]
public class LiveSurveyIntegrationTests
{
    [Test]
    public async Task AttachedTest_ScopeComesFromSharedSelection_IncludingMixed()
    {
        var (vm, sat, _, _) = LiveSurveyFixture.Build();
        await vm.PerformScanAsync();
        var plant = vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps");
        vm.Explorer.ToggleGroup(plant);
        var guest = vm.Explorer.Groups.Single(g => g.Ssid == "Guest-Portal");
        vm.Explorer.ToggleExpandCommand.Execute(guest);
        vm.Explorer.ToggleAp(guest.Aps.First());
        sat.Location = "Stock room B";
        var cfg = sat.BuildConfig();
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsAttached, Is.True);
            Assert.That(cfg.Scope, Is.EqualTo(SaturationScope.Mixed));
            Assert.That(cfg.TargetSsids, Is.EqualTo(new[] { "PlantOps" }));
            Assert.That(cfg.TargetBssids, Is.EqualTo(new[] { "00:17:88:A1:B2:C3" }));
            Assert.That(cfg.InterfaceId, Is.EqualTo(LiveSurveyFixture.Adapter.Id), "uses the Live Survey adapter");
            Assert.That(sat.SelectionText, Does.Contain("1 SSID (4 APs now, follows new APs) + 1 AP (fixed)"));
            Assert.That(sat.CanStart, Is.True, sat.ValidationMessage);
            Assert.That(sat.SummaryLine, Does.Contain("1 SSID (all BSSIDs) + 1 fixed BSSID"));
        });
    }

    [Test]
    public async Task EmptySelection_AsksBeforeTestingEverything_AndDeclineDoesNotStart()
    {
        var (vm, sat, _, _) = LiveSurveyFixture.Build();
        await vm.PerformScanAsync();
        sat.Location = "Dock 4";
        string? asked = null;
        sat.Confirm = (m, _) => { asked = m; return false; };
        await sat.StartAsync();
        Assert.Multiple(() =>
        {
            Assert.That(asked, Does.Contain("Test every visible network"));
            Assert.That(sat.IsSetup, Is.True);
            Assert.That(vm.Selection.IsLocked, Is.False);
            Assert.That(vm.IsTestRunning, Is.False);
        });
    }

    [Test]
    public async Task RunningTest_LocksSelection_DrivesModeAndCentreView_AndCaptureKeepsWorking()
    {
        var (vm, sat, provider, scheduler) = LiveSurveyFixture.Build(autoScan: true);
        await vm.PerformScanAsync();
        vm.Explorer.ToggleGroup(vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps"));
        sat.Location = "Line 3";
        await sat.StartAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsRunningStep, Is.True, sat.ValidationMessage);
            Assert.That(vm.Selection.IsLocked, Is.True, "scope cannot drift during a test");
            Assert.That(vm.Selection.ToggleGroup("Guest-Portal", new[] { "00:17:88:A1:B2:C3" }), Is.False, "locked selection refuses changes");
            Assert.That(vm.IsTestRunning, Is.True);
            Assert.That(vm.SurveyMode, Is.EqualTo(MainWindowViewModel.SurveyModes.Saturation));
            Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Test));
            Assert.That(vm.ModeStatusText, Does.Contain("TESTING"));
            Assert.That(vm.Explorer.ScopeText, Does.StartWith("LOCKED"));
        });

        // capture and live view run on the same scheduler at the same time (F18): no competing scans
        int before = provider.Calls;
        await vm.PerformScanAsync();
        await vm.PerformScanAsync();
        Assert.That(provider.Calls - before, Is.InRange(1, 3), "manual scans share the scheduler's in-flight scan");

        await Task.Delay(400);
        await sat.StopAsync();
        for (int i = 0; i < 50 && !sat.IsResults; i++) await Task.Delay(50);
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsResults, Is.True);
            Assert.That(vm.Selection.IsLocked, Is.False, "unlocked when the test ends");
            Assert.That(vm.IsTestRunning, Is.False);
            Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Results));
            Assert.That(sat.Report!.Config.Scope, Is.EqualTo(SaturationScope.SingleSsid));
            Assert.That(sat.Report.Targets.Select(t => t.Ssid).Distinct(), Is.EqualTo(new[] { "PlantOps" }));
        });
        sat.NewTestCommand.Execute(null);
        Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Spectrum), "NEW TEST returns to the spectrum");
        await scheduler.DisposeAsync();
    }

    [Test]
    public async Task LegacyBridges_StillFilterLiveViewAndCapture()
    {
        // Capacity/parity: the 1.1 SSID/AP check-list API still drives the same selection (L-rows of the parity checklist).
        var (vm, _, _, _) = LiveSurveyFixture.Build();
        await vm.PerformScanAsync();
        Assert.That(vm.FilteredAccessPoints.Count, Is.EqualTo(LiveSurveyFixture.Environment().Count), "empty selection = everything");
        vm.IsSsidView = true;
        vm.UpdateDisplayAccessPoints();
        var item = vm.DisplayAccessPoints.First(d => d.SSID == "Office");
        item.IsChecked = true;
        Assert.Multiple(() =>
        {
            Assert.That(vm.Selection.IsWholeSsid("Office"), Is.True);
            Assert.That(vm.FilteredAccessPoints.Select(a => a.SSID).Distinct(), Is.EqualTo(new[] { "Office" }));
        });
        vm.IsSsidView = false;
        Assert.That(vm.Selection.IsEmpty, Is.True, "switching view clears the selection, as in 1.1");
    }

    [Test]
    public async Task Waterfall_KeepsSixtyFreshScans_NewestFirst()
    {
        var (vm, _, provider, _) = LiveSurveyFixture.Build();
        for (int i = 0; i < 65; i++)
        {
            provider.Aps[0] = provider.Aps[0] with { RSSI = -40 - i % 20 };
            await vm.PerformScanAsync();
        }
        Assert.Multiple(() =>
        {
            Assert.That(vm.WaterfallHistory, Has.Count.EqualTo(MainWindowViewModel.WaterfallDepth));
            Assert.That(vm.WaterfallHistory[0][0].RSSI, Is.EqualTo(-40 - 64 % 20), "newest first");
        });
    }

    private static async Task RunShortTestAsync(SaturationTestViewModel sat, string location)
    {
        sat.Location = location;
        await sat.StartAsync();
        Assert.That(sat.IsRunningStep, Is.True, sat.ValidationMessage);
        await Task.Delay(300);
        await sat.StopAsync();
        for (int i = 0; i < 60 && !sat.IsResults; i++) await Task.Delay(50);
        Assert.That(sat.IsResults, Is.True);
    }

    [Test]
    public async Task SaturationHistory_OpenDeleteRefresh_AndOpenRefusedWhileATestRuns()
    {
        // Parity S16: the History → Saturation tests sub-tab drives these commands.
        var (vm, sat, _, scheduler) = LiveSurveyFixture.Build(autoScan: true);
        await vm.PerformScanAsync();
        vm.Explorer.ToggleGroup(vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps"));
        sat.TestName = "Dock soak";
        await RunShortTestAsync(sat, "Dock 4");
        await sat.LoadHistoryAsync();
        Assert.That(sat.History.Select(h => h.Config.Name), Is.EqualTo(new[] { "Dock soak" }));

        sat.NewTestCommand.Execute(null);
        sat.SelectedHistory = sat.History[0];
        await sat.OpenHistoryAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsResults, Is.True);
            Assert.That(sat.Report!.Config.Name, Is.EqualTo("Dock soak"));
            Assert.That(sat.Report.Config.Location, Is.EqualTo("Dock 4"));
        });

        // open is refused while a new test runs (the live stage stays on the running test)
        sat.NewTestCommand.Execute(null);
        sat.TestName = "Second";
        sat.Location = "Dock 5";
        await sat.StartAsync();
        sat.SelectedHistory = sat.History[0];
        await sat.OpenHistoryAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsRunningStep, Is.True);
            Assert.That(sat.HistoryStatus, Does.Contain("A test is running"));
        });
        await sat.StopAsync();
        for (int i = 0; i < 60 && !sat.IsResults; i++) await Task.Delay(50);
        await sat.LoadHistoryAsync();
        Assert.That(sat.History, Has.Count.EqualTo(2));

        // delete asks first; declining keeps the record
        string? asked = null;
        sat.Confirm = (m, _) => { asked = m; return false; };
        sat.SelectedHistory = sat.History.First(h => h.Config.Name == "Dock soak");
        await sat.DeleteHistoryAsync();
        Assert.Multiple(() =>
        {
            Assert.That(asked, Does.Contain("Dock soak").And.Contain("cannot be undone"));
            Assert.That(sat.History, Has.Count.EqualTo(2));
        });
        sat.Confirm = (_, _) => true;
        await sat.DeleteHistoryAsync();
        Assert.That(sat.History.Select(h => h.Config.Name), Is.EqualTo(new[] { "Second" }));
        await scheduler.DisposeAsync();
    }

    [Test]
    public async Task KeepAwakeAndBatteryWarning_WorkInAttachedMode()
    {
        // Parity S9.
        var (vm, sat, _, scheduler) = LiveSurveyFixture.Build(autoScan: true);
        var awake = new List<bool>();
        sat.KeepAwake = awake.Add;
        sat.IsOnBattery = () => true;
        await vm.PerformScanAsync();
        vm.Explorer.ToggleGroup(vm.Explorer.Groups.Single(g => g.Ssid == "Office"));
        sat.Location = "Mezzanine";
        await sat.StartAsync();
        Assert.That(sat.WarningText, Does.Contain("battery"));
        await Task.Delay(300);
        await sat.StopAsync();
        for (int i = 0; i < 60 && !sat.IsResults; i++) await Task.Delay(50);
        Assert.That(awake, Is.EqualTo(new[] { true, false }), "keep-awake on at start, off at the end");
        await scheduler.DisposeAsync();
    }

    [Test]
    public async Task RestLiveFeed_IsUpdatedOnEveryProcessedCycle()
    {
        // Parity L20: /api/live still receives every scan through the scheduler path.
        var provider = new CountingProvider();
        var repo = new MockSessionRepository();
        var api = new MockRestApiServer();
        var vm = new MainWindowViewModel(provider, new SessionManager(repo, new NullLogger()), repo, new MockExportService(), api, new MockSettingsRepository());
        vm.Adapters.Add(LiveSurveyFixture.Adapter);
        vm.SelectedAdapter = LiveSurveyFixture.Adapter;
        await vm.PerformScanAsync();
        await vm.PerformScanAsync();
        Assert.That(api.LiveUpdates, Is.EqualTo(2));
    }

    [Test]
    public void SpectrumBands_AllShows6GhzOnlyWhenHeard()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpectrumView.BandsFor("All", LiveSurveyFixture.Environment()).Select(b => b.Band), Is.EqualTo(new[] { "2.4 GHz", "5 GHz", "6 GHz" }));
            Assert.That(SpectrumView.BandsFor("All", LiveSurveyFixture.Environment().Where(a => a.Band != "6 GHz")).Select(b => b.Band), Is.EqualTo(new[] { "2.4 GHz", "5 GHz" }));
            Assert.That(SpectrumView.BandsFor("5 GHz", Array.Empty<AccessPoint>()).Single().Band, Is.EqualTo("5 GHz"));
        });
    }
}

/// <summary>Loads the real Live Survey XAML (MainWindow content) on an STA thread and renders each mode to PNG when WS_UI_RENDER_OUT is set.</summary>
[TestFixture]
public class LiveSurveyRenderTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new AggregateException(error);
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindAll<T>(child)) yield return d;
        }
    }

    private static void Render(Window w, string? outDir, string name)
    {
        for (int i = 0; i < 3; i++) { w.UpdateLayout(); Pump(); }
        if (outDir == null) return;
        var root = (FrameworkElement)w.Content;
        int width = (int)root.ActualWidth, height = (int)root.ActualHeight;
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new System.Windows.Shapes.Rectangle { Width = width, Height = height, Fill = Brushes.Black };
        bg.Measure(new Size(width, height)); bg.Arrange(new Rect(0, 0, width, height));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(outDir, name));
        enc.Save(fs);
    }

    [Test]
    public void LiveSurvey_AllModesAndViews_LoadAndRender()
    {
        string? outDir = System.Environment.GetEnvironmentVariable("WS_UI_RENDER_OUT");
        if (outDir != null) Directory.CreateDirectory(outDir);
        RunSta(() =>
        {
            var (vm, sat, provider, _) = LiveSurveyFixture.Build();
            for (int i = 0; i < 14; i++)
            {
                for (int k = 0; k < provider.Aps.Count; k++) provider.Aps[k] = provider.Aps[k] with { RSSI = provider.Aps[k].RSSI + (i * 7 + k) % 5 - 2 };
                vm.PerformScanAsync().GetAwaiter().GetResult();
            }
            var window = new MainWindow(vm, sat) { Width = 1600, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            try
            {
                Render(window, outDir, "ui_ls_monitor_auto.png");
                Assert.That(vm.Explorer.Groups, Has.Count.EqualTo(7));

                // selection: one whole SSID, one fixed AP, one expanded group
                var plant = vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps");
                vm.Explorer.ToggleGroup(plant);
                var guest = vm.Explorer.Groups.Single(g => g.Ssid == "Guest-Portal");
                vm.Explorer.ToggleExpandCommand.Execute(guest);
                vm.Explorer.ToggleAp(guest.Aps.First());
                vm.Explorer.ToggleGroup(vm.Explorer.Groups.Single(g => g.Ssid == "Scanner-Net"));
                Render(window, outDir, "ui_ls_monitor_selected.png");

                vm.LabelAll = true;
                vm.Explorer.ClearCommand.Execute(null);
                vm.SpectrumBand = "5 GHz";
                Render(window, outDir, "ui_ls_spectrum_5ghz_labelall.png");
                vm.LabelAll = false;
                vm.SpectrumBand = "All";
                vm.Explorer.ToggleGroup(vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps"));

                vm.CenterView = MainWindowViewModel.CenterViews.Waterfall;
                Render(window, outDir, "ui_ls_waterfall.png");
                vm.CenterView = MainWindowViewModel.CenterViews.Grid;
                Render(window, outDir, "ui_ls_grid.png");
                vm.CenterView = MainWindowViewModel.CenterViews.Spectrum;

                vm.SurveyMode = MainWindowViewModel.SurveyModes.Capture;
                Render(window, outDir, "ui_ls_capture_drawer.png");
                vm.SessionName = "Line 3 walk"; vm.FacilityName = "Plant 2"; vm.SessionScope = "PlantOps"; vm.SurveyPoint = "Zone A"; vm.SessionModeText = "Stationary";
                vm.IsSessionActive = true;
                Render(window, outDir, "ui_ls_capture_recording.png");
                vm.IsSessionPaused = true;
                Render(window, outDir, "ui_ls_capture_paused.png");
                vm.IsSessionPaused = false;
                vm.IsSessionActive = false;

                vm.SurveyMode = MainWindowViewModel.SurveyModes.Saturation;
                sat.Latitude = "41.878113"; sat.Longitude = "-87.629799"; sat.Building = "Plant 2";
                Render(window, outDir, "ui_ls_saturation_setup_invalid.png");
                sat.Location = "Stock room B";
                Render(window, outDir, "ui_ls_saturation_setup_ready.png");
                Assert.That(sat.CanStart, Is.True, sat.ValidationMessage);

                var report = ReportPreviewTests.Realistic();
                sat.CurrentStep = SaturationTestViewModel.Step.Running;
                vm.IsTestRunning = true;
                vm.Selection.Lock();
                vm.CenterView = MainWindowViewModel.CenterViews.Test;
                sat.RunStatus = "RUNNING — Stock room soak @ Stock room B";
                var progress = new SaturationProgress(report.Config, SaturationTestStatus.Running, report.StartUtc, report.ActiveSeconds, 600,
                    report.Cycles[^1], report.Cycles, report.Markers);
                sat.ApplyProgress(progress, report);
                Render(window, outDir, "ui_ls_saturation_running.png");

                vm.Selection.Unlock();
                vm.IsTestRunning = false;
                sat.ShowResults(report);
                Render(window, outDir, "ui_ls_saturation_results.png");
                Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Results));

                // HISTORY / LOGS → Saturation tests (list + inline results)
                var rec = new SaturationTestRecord(report.Config, report.StartUtc, report.EndUtc, report.Status, report.Cycles.Count);
                sat.History.Add(rec);
                sat.SelectedHistory = rec;
                var tabs = FindAll<System.Windows.Controls.TabControl>(window).ToList();
                tabs[0].SelectedIndex = 2;
                Render(window, outDir, "ui_history_capture_sessions.png");
                FindAll<System.Windows.Controls.TabControl>(window).Skip(1).First().SelectedIndex = 1;
                Render(window, outDir, "ui_history_saturation_tests.png");
                Assert.That(tabs[0].Items.Count, Is.EqualTo(3), "SATURATION TEST tab merged into Live Survey");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
