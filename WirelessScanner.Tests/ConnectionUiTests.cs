using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;
using WirelessScanner.Responder;

namespace WirelessScanner.Tests;

/// <summary>Provider that also reports the current association (for the CONNECTED badge).</summary>
internal sealed class ConnectedProvider : INetworkAdapterProvider
{
    public List<AccessPoint> Aps { get; } = LiveSurveyFixture.Environment();
    public string? ConnectedBssid { get; set; } = "8C:5A:25:04:91:B0";
    public Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync() => Task.FromResult<IEnumerable<NetworkInterfaceInfo>>(new[] { LiveSurveyFixture.Adapter });
    public Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId) => Task.FromResult<IEnumerable<AccessPoint>>(Aps);
    public Task TriggerHardwareScanAsync(Guid interfaceId) => Task.CompletedTask;
    public Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId) => Task.FromResult(new WiredInterfaceStats(0, "", "Down", 0));
    public event Action? HardwareChanged { add { } remove { } }
    public void NotifyHardwareChange() { }
    public Task<ScanCycleResult> ScanAsync(Guid interfaceId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ScanCycleResult(ScanCycleStatus.Fresh, DateTime.UtcNow, DateTime.UtcNow, 5, Aps.ToList()));
    public Task<WlanLinkReading?> ReadLinkAsync(Guid interfaceId) =>
        Task.FromResult<WlanLinkReading?>(ConnectedBssid == null ? new WlanLinkReading { Connected = false } : new WlanLinkReading { Connected = true, Ssid = "PlantOps", Bssid = ConnectedBssid, Rssi = -52, Channel = 36 });
}

internal static class ConnectionFixture
{
    public static (MainWindowViewModel Vm, SaturationTestViewModel Sat, ConnectionTestViewModel Conn, FakeProber Prober, MemoryConnectionRepository Repo, MockSettingsRepository Settings, ConnectedProvider Provider)
        Build()
    {
        var provider = new ConnectedProvider();
        var scheduler = new ScanScheduler(provider) { AutoScan = false };
        var repo = new MockSessionRepository();
        var settings = new MockSettingsRepository();
        var vm = new MainWindowViewModel(provider, new SessionManager(repo, new NullLogger()), repo, new MockExportService(), new MockRestApiServer(), settings, scheduler);
        var satRepo = new MemorySaturationRepository();
        var sat = new SaturationTestViewModel(provider, new SaturationTestRunner(scheduler, satRepo, new SaturationAnalyzer(), new NullLogger()), satRepo, new SaturationAnalyzer(), new SaturationExportService())
        { Ui = a => a(), Confirm = (_, _) => true, KeepAwake = _ => { }, IsOnBattery = () => false };
        vm.AttachSaturation(sat);
        var prober = new FakeProber { Ssid = "PlantOps", Bssid = "8C:5A:25:04:91:B0" };
        var connRepo = new MemoryConnectionRepository();
        var runner = new ConnectionTestRunner(prober, connRepo, new ConnectionAnalyzer(), new NullLogger());
        var conn = new ConnectionTestViewModel(prober, runner, connRepo, new ConnectionAnalyzer(), new ConnectionExportService(), settings, provider)
        { Ui = a => a(), Confirm = (_, _) => true, KeepAwake = _ => { }, IsOnBattery = () => false, TickOverrideMs = 50, DurationOverride = TimeSpan.FromSeconds(1.5) };
        vm.AttachConnection(conn);
        vm.Adapters.Add(LiveSurveyFixture.Adapter);
        vm.SelectedAdapter = LiveSurveyFixture.Adapter;
        return (vm, sat, conn, prober, connRepo, settings, provider);
    }

    public static async Task WaitFor(Func<bool> condition, int ms = 8000)
    {
        for (int i = 0; i < ms / 50 && !condition(); i++) await Task.Delay(50);
    }
}

[TestFixture]
public class ConnectionUiTests
{
    [Test]
    public async Task Validation_ExplainsWhatBlocksStarting()
    {
        var (vm, _, conn, prober, _, _, _) = ConnectionFixture.Build();
        await conn.RefreshContextAsync();
        Assert.That(conn.ValidationMessage, Does.Contain("location"));
        conn.Location = "Office";
        Assert.That(conn.CanStart, Is.True, conn.ValidationMessage);
        conn.InternetTargetsText = "1.1.1.1, not-an-ip";
        Assert.That(conn.ValidationMessage, Does.Contain("IPv4"));
        conn.InternetTargetsText = "1.1.1.1, 8.8.8.8";
        conn.SpeedAtStart = true;
        Assert.That(conn.ValidationMessage, Does.Contain("test server"));
        conn.SpeedAtStart = false;
        prober.Connected = false;
        await conn.RefreshContextAsync();
        Assert.Multiple(() =>
        {
            Assert.That(conn.CanStart, Is.False);
            Assert.That(conn.ValidationMessage, Does.Contain("never connects for you"));
            Assert.That(conn.SummaryLine, Does.Contain("kbit/s"));
            Assert.That(vm.Connection, Is.SameAs(conn));
        });
    }

    [Test]
    public async Task ModeBar_DescribesEachMode_AndConnectionModePrefillsWhereFromSaturation()
    {
        var (vm, sat, conn, _, _, _, _) = ConnectionFixture.Build();
        sat.Location = "Stock room B";
        sat.Building = "Plant 2";
        vm.SurveyMode = MainWindowViewModel.SurveyModes.Saturation;
        Assert.That(vm.ModeDescription, Does.Contain("Listens to the radio only"));
        vm.SurveyMode = MainWindowViewModel.SurveyModes.Connection;
        Assert.Multiple(() =>
        {
            Assert.That(vm.IsConnectionMode, Is.True);
            Assert.That(vm.IsDrawerOpen, Is.True);
            Assert.That(vm.ModeDescription, Does.Contain("Never changes the connection"));
            Assert.That(conn.Location, Is.EqualTo("Stock room B"));
            Assert.That(conn.Building, Is.EqualTo("Plant 2"));
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task FullRun_StartRunningResults_DrivesModeAndCentreView_AndHistory()
    {
        var (vm, _, conn, _, repo, _, _) = ConnectionFixture.Build();
        await conn.RefreshContextAsync();
        conn.Location = "Office";
        await conn.StartAsync();
        Assert.Multiple(() =>
        {
            Assert.That(conn.IsRunningStep, Is.True);
            Assert.That(vm.IsConnectionTestRunning, Is.True);
            Assert.That(vm.SurveyMode, Is.EqualTo(MainWindowViewModel.SurveyModes.Connection));
            Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Connection));
            Assert.That(vm.ModeStatusText, Does.Contain("TESTING CONNECTION"));
            Assert.That(conn.Tiles.Select(t => t.Segment), Is.EqualTo(new[] { ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web }));
        });
        var api = conn.ApiStatus();
        Assert.That(System.Text.Json.JsonSerializer.Serialize(api), Does.Contain("\"running\":true"));
        await ConnectionFixture.WaitFor(() => conn.IsResults);
        Assert.Multiple(() =>
        {
            Assert.That(conn.IsResults, Is.True);
            Assert.That(vm.IsConnectionTestRunning, Is.False);
            Assert.That(vm.CenterView, Is.EqualTo(MainWindowViewModel.CenterViews.Connection));
            Assert.That(conn.ResultVerdicts, Has.Count.EqualTo(4));
            Assert.That(conn.ResultSegments.Select(s => s.Segment), Does.Contain(ConnectionSegment.Gateway).And.Contain(ConnectionSegment.Internet));
            Assert.That(conn.ProblemsText, Does.StartWith("Where problems happened"));
            Assert.That(conn.Report!.Config.Environment, Is.Not.Null, "provenance captured");
            Assert.That(repo.Tests.Single().Status, Is.EqualTo(ConnectionTestStatus.Completed));
        });
        await ConnectionFixture.WaitFor(() => conn.History.Count == 1);
        conn.SelectedHistory = conn.History.Single();
        conn.NewTestCommand.Execute(null);
        await conn.OpenHistoryAsync();
        Assert.That(conn.IsResults, Is.True, "history re-analyses and shows results");
    }

    [Test]
    public async Task AlsoSaturation_StartsALinkedSaturationTestOfTheConnectedAccessPoint()
    {
        var (vm, sat, conn, _, _, _, _) = ConnectionFixture.Build();
        await vm.PerformScanAsync();
        await conn.RefreshContextAsync();
        conn.Location = "Office";
        conn.AlsoSaturation = true;
        await conn.StartAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sat.IsRunningStep, Is.True, "saturation test started");
            Assert.That(vm.Selection.Units, Has.Count.EqualTo(1), "the connected access point's whole unit is selected");
            Assert.That(sat.Location, Is.EqualTo("Office"));
            Assert.That(vm.ModeStatusText, Does.Contain("TESTING SIGNAL").And.Contain("TESTING CONNECTION"));
        });
        await ConnectionFixture.WaitFor(() => conn.IsResults);
        Assert.That(conn.Report!.Config.LinkedSaturationTestId, Is.EqualTo(sat.ActiveTestId));
        await sat.StopAsync();
    }

    [Test]
    public async Task TestServers_KeyIsStoredProtected_AndResponderCheckWorks()
    {
        var key = ResponderProtocol.NewKey();
        await using var server = new ResponderServer(key, 0, IPAddress.Loopback, "Bench Pi");
        server.Start();
        var (_, _, conn, _, _, settings, _) = ConnectionFixture.Build();
        conn.ResponderAddress = "127.0.0.1";
        conn.ResponderPort = server.Port.ToString();
        conn.ResponderKeyInput = "not base64!";
        await conn.SaveServersAsync();
        Assert.That(conn.ServerStatus, Does.Contain("not valid"));
        conn.ResponderKeyInput = ResponderProtocol.KeyToText(key);
        conn.SpeedKind = SpeedServerKind.Responder;
        await conn.SaveServersAsync();
        var stored = await settings.GetSettingAsync(ConnectionTestViewModel.ResponderKeySetting);
        Assert.Multiple(() =>
        {
            Assert.That(conn.ServerStatus, Is.EqualTo("Saved."));
            Assert.That(stored, Does.StartWith("dpapi:"), "key protected at rest");
            Assert.That(stored, Does.Not.Contain(ResponderProtocol.KeyToText(key)));
            Assert.That(conn.ResponderKeyInput, Is.Empty, "never shown again");
            Assert.That(conn.HasResponder && conn.HasSpeedServer, Is.True);
        });
        await conn.CheckResponderAsync();
        Assert.That(conn.ServerStatus, Does.StartWith("OK").And.Contain("Bench Pi"));

        // reload in a fresh view model from the same settings
        var (_, _, conn2, _, _, _, _) = ConnectionFixture.Build();
        typeof(ConnectionTestViewModel).GetField("_settings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(conn2, settings);
        await conn2.LoadServersAsync();
        Assert.That(conn2.ResponderSettingsValue().Key, Is.EqualTo(ResponderProtocol.KeyToText(key)));
    }

    [Test]
    public async Task Explorer_MarksTheConnectedAccessPoint()
    {
        var (vm, _, _, _, _, _, provider) = ConnectionFixture.Build();
        await vm.PerformScanAsync();
        await ConnectionFixture.WaitFor(() => vm.Explorer.ConnectedBssid != null, 2000);
        var plant = vm.Explorer.Groups.Single(g => g.Ssid == "PlantOps");
        vm.Explorer.ToggleExpandCommand.Execute(plant);
        Assert.Multiple(() =>
        {
            Assert.That(vm.Explorer.ConnectedBssid, Is.EqualTo("8C:5A:25:04:91:B0"));
            Assert.That(plant.IsConnected, Is.True);
            Assert.That(plant.Aps.Single(a => a.IsConnected).Bssid, Is.EqualTo("8C:5A:25:04:91:B0"));
            Assert.That(vm.Explorer.Groups.Where(g => g.Ssid != "PlantOps").All(g => !g.IsConnected), Is.True);
        });
        vm.Explorer.Grouping = ExplorerGrouping.AccessPoint;
        Assert.That(vm.Explorer.UnitGroups.Count(u => u.IsConnected), Is.EqualTo(1));
    }

    // ---------------- render (visual review with WS_UI_RENDER_OUT) ----------------

    [Test]
    public void ConnectionMode_SetupRunningResultsSettingsHistory_LoadAndRender()
    {
        string? outDir = System.Environment.GetEnvironmentVariable("WS_UI_RENDER_OUT");
        if (outDir != null) Directory.CreateDirectory(outDir);
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var (vm, sat, conn, prober, _, _, _) = ConnectionFixture.Build();
                for (int i = 0; i < 6; i++) vm.PerformScanAsync().GetAwaiter().GetResult();
                conn.RefreshContextAsync().GetAwaiter().GetResult();
                conn.Location = "Office 3F";
                var window = new MainWindow(vm, sat, conn) { Width = 1600, Height = 900, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                try
                {
                    vm.SurveyMode = MainWindowViewModel.SurveyModes.Connection;
                    vm.CenterView = MainWindowViewModel.CenterViews.Connection;
                    Render(window, outDir, "ui_conn_setup.png");

                    // running view from a synthetic live report (deterministic picture)
                    var report = Conn.Analyze(Conn.Healthy(120, (tick, s) => tick is >= 60 and <= 66 && s == ConnectionSegment.Internet ? double.NaN : tick is 90 or 91 && s == ConnectionSegment.Gateway ? 160 : null),
                        Enumerable.Range(0, 120).Select(x => Conn.L(x, rssi: -50 - x % 7, bssid: x < 80 ? "9A:3C:5E:26:42:70" : "9A:3C:5E:1F:42:70")).ToList());
                    conn.CurrentStep = ConnectionTestViewModel.Step.Running;
                    foreach (var seg in new[] { ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web })
                        conn.Tiles.Add(new ConnectionSegmentTile(seg));
                    conn.RunStatus = "RUNNING — Office check @ Office 3F";
                    conn.ApplyProgress(new ConnectionProgress(report.Config with { Duration = TimeSpan.FromMinutes(15) }, ConnectionTestStatus.Running, report.StartUtc, 120, 780, 120,
                        Array.Empty<ConnectionProbeResult>(), report.LinkSamples[^1], null), report);
                    Render(window, outDir, "ui_conn_running.png");

                    conn.ShowResults(report);
                    Render(window, outDir, "ui_conn_results.png");
                    Assert.That(vm.IsConnectionView, Is.True);

                    var tabs = FindAll<TabControl>(window).ToList();
                    tabs[0].SelectedIndex = 1;   // SETTINGS
                    Pump();
                    var settingsTabs = FindAll<TabControl>(window).First(tc => tc != tabs[0]);
                    settingsTabs.SelectedIndex = 1;   // TEST SERVERS
                    conn.ResponderAddress = "192.168.1.20";
                    conn.SpeedKind = SpeedServerKind.Responder;
                    Render(window, outDir, "ui_settings_test_servers.png");
                    Assert.That(settingsTabs.Items.Count, Is.EqualTo(2), "SETTINGS = REST API + TEST SERVERS");

                    tabs[0].SelectedIndex = 2;   // HISTORY
                    Pump();
                    var historyTabs = FindAll<TabControl>(window).First(tc => tc != tabs[0]);
                    historyTabs.SelectedIndex = 2;   // CONNECTION TESTS
                    conn.History.Add(new ConnectionTestRecord(report.Config with { Location = "Office 3F" }, report.StartUtc, report.EndUtc, report.Status, report.Totals.ProbesSent, report.ActiveSeconds));
                    Render(window, outDir, "ui_history_connection_tests.png");
                    Assert.That(historyTabs.Items.Count, Is.EqualTo(3));
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { error = ex; }
        });
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
}
