using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Presentation;

/// <summary>
/// Connection test in Live Survey (DR-0014): Setup → Running → Results, history, and the SETTINGS → Test servers page.
/// Treated like the saturation test: same steps, hooks and drawer/stage layout; the target is the current connection (fixed).
/// </summary>
public class ConnectionTestViewModel : INotifyPropertyChanged
{
    public enum Step { Setup, Running, Results }

    public const string SpeedKindSetting = "SpeedServerKind", SpeedDownloadSetting = "SpeedHttpDownloadUrl", SpeedUploadSetting = "SpeedHttpUploadUrl",
        SpeedStreamsSetting = "SpeedStreams", SpeedSecondsSetting = "SpeedSeconds", ResponderAddressSetting = "ResponderAddress",
        ResponderPortSetting = "ResponderPort", ResponderKeySetting = "ResponderKey", TargetsSetting = "ConnectionInternetTargets";

    private readonly IConnectionProber _prober;
    private readonly IConnectionTestRunner _runner;
    private readonly IConnectionRepository _repository;
    private readonly IConnectionAnalyzer _analyzer;
    private readonly IConnectionExportService _export;
    private readonly ISettingsRepository _settings;
    private readonly INetworkAdapterProvider _adapters;

    // Hooks (replaced in tests), same pattern as the saturation test
    public Action<Action> Ui { get; set; } = a =>
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.Invoke(a);
    };
    public Func<string, string, bool> Confirm { get; set; } = (message, title) =>
        System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
    public Func<string, string, string, string?> SaveFileDialog { get; set; } = DefaultSaveDialog;
    public Action<bool> KeepAwake { get; set; } = NativePower.KeepAwake;
    public Func<bool?> IsOnBattery { get; set; } = NativePower.IsOnBattery;
    /// <summary>Starts a saturation test of the connected access point (set by Live Survey); returns its test id, or null when not started.</summary>
    public Func<ConnectionTestViewModel, Task<Guid?>>? StartLinkedSaturation { get; set; }

    public event Action? ChartDataChanged;
    public event Action<bool>? TestRunningChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionTestViewModel(IConnectionProber prober, IConnectionTestRunner runner, IConnectionRepository repository, IConnectionAnalyzer analyzer,
        IConnectionExportService export, ISettingsRepository settings, INetworkAdapterProvider adapters)
    {
        _prober = prober;
        _runner = runner;
        _repository = repository;
        _analyzer = analyzer;
        _export = export;
        _settings = settings;
        _adapters = adapters;

        DurationOptions = new List<DurationOption>
        {
            new("5 minutes", TimeSpan.FromMinutes(5)), new("15 minutes", TimeSpan.FromMinutes(15)), new("30 minutes", TimeSpan.FromMinutes(30)),
            new("1 hour", TimeSpan.FromHours(1)), new("4 hours", TimeSpan.FromHours(4)), new("8 hours", TimeSpan.FromHours(8)),
            new("24 hours", TimeSpan.FromHours(24)), new("Custom (minutes)", null, IsCustom: true), new("Until stopped", null)
        };
        _selectedDuration = DurationOptions[1];

        RefreshContextCommand = new RelayCommand(async () => await RefreshContextAsync());
        StartCommand = new RelayCommand(async () => await StartAsync());
        PauseCommand = new RelayCommand(() => { _runner.Pause(); IsPaused = true; RunStatus = "PAUSED — probing suspended; paused time is not counted."; });
        ResumeCommand = new RelayCommand(() => { _runner.Resume(); IsPaused = false; RunStatus = RunningTitle(); });
        StopCommand = new RelayCommand(async () => await StopAsync());
        AddMarkerCommand = new RelayCommand(() => { if (!string.IsNullOrWhiteSpace(MarkerText)) { _runner.AddMarker(MarkerText); EventFeed.Insert(0, $"{DateTime.Now:HH:mm:ss}  Marker: {MarkerText.Trim()}"); MarkerText = ""; } });
        RunSpeedNowCommand = new RelayCommand(async () => await RunSpeedNowAsync());
        RunTwoWayNowCommand = new RelayCommand(async () => await RunTwoWayNowAsync());
        ExportPdfCommand = new RelayCommand(async () => await ExportAsync("pdf"));
        ExportCsvCommand = new RelayCommand(async () => await ExportAsync("csv"));
        ExportJsonCommand = new RelayCommand(async () => await ExportAsync("json"));
        NewTestCommand = new RelayCommand(() => { CurrentStep = Step.Setup; _ = RefreshContextAsync(); });
        RefreshHistoryCommand = new RelayCommand(async () => await LoadHistoryAsync());
        OpenHistoryCommand = new RelayCommand(async () => await OpenHistoryAsync());
        DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
        SaveServersCommand = new RelayCommand(async () => await SaveServersAsync());
        CheckResponderCommand = new RelayCommand(async () => await CheckResponderAsync());

        _runner.Progress += OnProgress;
        _runner.Completed += OnCompleted;
        if (_runner is ConnectionTestRunner concrete)
        {
            concrete.SpeedClientFactory = () => SpeedClient() is { } c ? (c, SpeedSettings()) : null;
            concrete.TwoWayClientFactory = () => ResponderSettingsValue().IsConfigured ? new ResponderClient(ResponderSettingsValue()) : null;
        }
        Revalidate();
    }

    // ------------------------------------------------------------------ steps
    private Step _step = Step.Setup;
    public Step CurrentStep
    {
        get => _step;
        set { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSetup)); OnPropertyChanged(nameof(IsRunningStep)); OnPropertyChanged(nameof(IsResults)); }
    }
    public bool IsSetup => _step == Step.Setup;
    public bool IsRunningStep => _step == Step.Running;
    public bool IsResults => _step == Step.Results;
    public bool IsRunning => _runner.IsRunning;

    public const string WhatItDoes =
        "Checks the connection this PC is already on, end to end (Wi-Fi link → router → ISP → internet, plus DNS and a small web request), " +
        "with tiny probes, and shows where any problem happens. It never connects, disconnects or changes the connection.";

    // ------------------------------------------------------------------ 1 · what (the current connection)
    private NetworkInterfaceInfo? _adapter;
    public NetworkInterfaceInfo? SelectedAdapter { get => _adapter; set { _adapter = value; OnPropertyChanged(); _ = RefreshContextAsync(); } }

    private ConnectionContext? _context;
    public ConnectionContext? Context { get => _context; private set { _context = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContextText)); OnPropertyChanged(nameof(ContextWarning)); OnPropertyChanged(nameof(HasContextWarning)); Revalidate(); } }
    private bool _isReading;
    public bool IsReadingContext { get => _isReading; set { _isReading = value; OnPropertyChanged(); } }

    public string ContextText
    {
        get
        {
            var c = _context;
            if (c == null) return IsReadingContext ? "Reading your connection…" : "Choose a Wi-Fi adapter.";
            if (!c.WifiConnected) return c.Problem ?? "Not connected over Wi-Fi.";
            var radio = new List<string>();
            if (c.Band != null || c.Channel != null) radio.Add($"{c.Band} ch {c.Channel}".Trim());
            if (c.PhyType != null) radio.Add(c.PhyType);
            if (c.RxRateMbps is double rx) radio.Add($"{rx:0} Mbps");
            radio.Add(c.Rssi.HasValue ? $"{c.Rssi} dBm" : "signal n/a");
            return $"{c.Ssid} · access point {UnitName ?? c.Bssid}\n{string.Join(" · ", radio)}\n" +
                   $"This PC {c.LocalIPv4} · router {(c.Gateways.Count == 0 ? "none" : string.Join(", ", c.Gateways))} · DNS {(c.DnsServers.Count == 0 ? "none" : string.Join(", ", c.DnsServers))}";
        }
    }

    /// <summary>Name of the connected access point from the physical-unit grouping (1.4), when known.</summary>
    public string? UnitName { get; set; }

    public string ContextWarning => _context?.OtherActiveInterfaces.Count > 0
        ? $"Also connected: {string.Join(", ", _context.OtherActiveInterfaces)}. Probes are forced through Wi-Fi." : "";
    public bool HasContextWarning => !string.IsNullOrEmpty(ContextWarning);

    public async Task RefreshContextAsync()
    {
        if (_adapter == null || _runner.IsRunning) { if (_adapter == null) Context = null; return; }
        IsReadingContext = true;
        OnPropertyChanged(nameof(ContextText));
        try
        {
            var ctx = await _prober.GetContextAsync(_adapter.Id);
            Ui(() => Context = ctx);
        }
        catch (Exception ex)
        {
            Ui(() => Context = new ConnectionContext { Problem = $"Could not read the connection: {ex.Message}" });
        }
        finally { Ui(() => IsReadingContext = false); }
    }

    // segments
    private bool _pWifi = true, _pGw = true, _pIsp = true, _pInet = true, _pDns = true, _pWeb = true;
    public bool ProbeWifiLink { get => _pWifi; set { _pWifi = value; OnPropertyChanged(); Revalidate(); } }
    public bool ProbeGateway { get => _pGw; set { _pGw = value; OnPropertyChanged(); Revalidate(); } }
    public bool ProbeIsp { get => _pIsp; set { _pIsp = value; OnPropertyChanged(); Revalidate(); } }
    public bool ProbeInternet { get => _pInet; set { _pInet = value; OnPropertyChanged(); Revalidate(); } }
    public bool ProbeDns { get => _pDns; set { _pDns = value; OnPropertyChanged(); Revalidate(); } }
    public bool ProbeWeb { get => _pWeb; set { _pWeb = value; OnPropertyChanged(); Revalidate(); } }
    private string _targets = string.Join(", ", ConnectionTestConfig.DefaultInternetTargets);
    public string InternetTargetsText { get => _targets; set { _targets = value; OnPropertyChanged(); Revalidate(); } }

    // ------------------------------------------------------------------ 2 · how long
    public IReadOnlyList<DurationOption> DurationOptions { get; }
    private DurationOption _selectedDuration;
    public DurationOption SelectedDuration { get => _selectedDuration; set { _selectedDuration = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomDuration)); Revalidate(); } }
    public bool IsCustomDuration => _selectedDuration.IsCustom;
    private string _customMinutes = "60";
    public string CustomMinutes { get => _customMinutes; set { _customMinutes = value; OnPropertyChanged(); Revalidate(); } }
    public IReadOnlyList<string> IntensityOptions { get; } = new[] { "Light", "Detailed" };
    private string _intensity = "Light";
    public string Intensity { get => _intensity; set { _intensity = value; OnPropertyChanged(); Revalidate(); } }

    // ------------------------------------------------------------------ 3 · where (shared with the saturation test)
    private string _testName = "", _location = "", _facility = "", _operator = "", _client = "", _notes = "";
    public string TestName { get => _testName; set { _testName = value; OnPropertyChanged(); } }
    public string Location { get => _location; set { _location = value; OnPropertyChanged(); Revalidate(); } }
    public string Facility { get => _facility; set { _facility = value; OnPropertyChanged(); } }
    public string Operator { get => _operator; set { _operator = value; OnPropertyChanged(); } }
    public string Client { get => _client; set { _client = value; OnPropertyChanged(); } }
    public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }
    private string _lat = "", _lon = "", _building = "", _floor = "", _zone = "";
    public string Latitude { get => _lat; set { _lat = value; OnPropertyChanged(); Revalidate(); } }
    public string Longitude { get => _lon; set { _lon = value; OnPropertyChanged(); Revalidate(); } }
    public string Building { get => _building; set { _building = value; OnPropertyChanged(); } }
    public string Floor { get => _floor; set { _floor = value; OnPropertyChanged(); } }
    public string Zone { get => _zone; set { _zone = value; OnPropertyChanged(); } }

    /// <summary>Prefill "where" from the saturation test setup (only empty fields).</summary>
    public void PrefillWhere(SaturationTestViewModel? sat)
    {
        if (sat == null) return;
        if (string.IsNullOrWhiteSpace(Location)) Location = sat.Location;
        if (string.IsNullOrWhiteSpace(Latitude)) Latitude = sat.Latitude;
        if (string.IsNullOrWhiteSpace(Longitude)) Longitude = sat.Longitude;
        if (string.IsNullOrWhiteSpace(Building)) Building = sat.Building;
        if (string.IsNullOrWhiteSpace(Floor)) Floor = sat.Floor;
        if (string.IsNullOrWhiteSpace(Zone)) Zone = sat.Zone;
        if (string.IsNullOrWhiteSpace(Facility)) Facility = sat.Facility;
        if (string.IsNullOrWhiteSpace(Operator)) Operator = sat.Operator;
        if (string.IsNullOrWhiteSpace(Client)) Client = sat.Client;
    }

    // options
    private bool _alsoSaturation, _stopOnChange, _keepAwake = true, _speedAtStart, _twoWayAtStart;
    public bool AlsoSaturation { get => _alsoSaturation; set { _alsoSaturation = value; OnPropertyChanged(); } }
    public bool StopOnNetworkChange { get => _stopOnChange; set { _stopOnChange = value; OnPropertyChanged(); } }
    public bool KeepAwakeEnabled { get => _keepAwake; set { _keepAwake = value; OnPropertyChanged(); } }
    public bool SpeedAtStart { get => _speedAtStart; set { _speedAtStart = value; OnPropertyChanged(); Revalidate(); } }
    public bool TwoWayAtStart { get => _twoWayAtStart; set { _twoWayAtStart = value; OnPropertyChanged(); Revalidate(); } }
    public IReadOnlyList<int> ScheduleOptions { get; } = new[] { 0, 15, 30, 60 };
    private int _speedEvery, _twoWayEvery;
    public int SpeedEveryMinutes { get => _speedEvery; set { _speedEvery = value; OnPropertyChanged(); Revalidate(); } }
    public int TwoWayEveryMinutes { get => _twoWayEvery; set { _twoWayEvery = value; OnPropertyChanged(); Revalidate(); } }
    public bool HasSpeedServer => SpeedClient() != null;
    public bool HasResponder => ResponderSettingsValue().IsConfigured;
    public string SpeedHint => HasSpeedServer ? $"Server: {SpeedClient()!.Provider} {SpeedClient()!.Server}" : "Set up a test server first: SETTINGS → Test servers.";
    public string TwoWayHint => HasResponder ? $"Responder: {ResponderSettingsValue().Address}:{ResponderSettingsValue().Port}" : "Pair a LAN responder first: SETTINGS → Test servers.";

    // validation / summary
    private string _validation = "", _summary = "";
    public string ValidationMessage { get => _validation; private set { _validation = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasValidationMessage)); } }
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validation);
    public string SummaryLine { get => _summary; private set { _summary = value; OnPropertyChanged(); } }
    private bool _canStart;
    public bool CanStart { get => _canStart; private set { _canStart = value; OnPropertyChanged(); } }

    private static double? ParseDouble(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public IReadOnlyList<string> ParseTargets() => _targets.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(t => t.Trim()).Distinct().ToList();

    public void Revalidate()
    {
        var problems = new List<string>();
        if (_adapter == null) problems.Add("Choose a Wi-Fi adapter.");
        else if (_context == null) problems.Add("Reading your connection…");
        else if (!_context.WifiConnected) problems.Add((_context.Problem ?? "Not connected over Wi-Fi.") + " Connect to a network first; this test never connects for you.");
        if (!(_pWifi || _pGw || _pIsp || _pInet || _pDns || _pWeb)) problems.Add("Choose at least one part of the path to test.");
        var targets = ParseTargets();
        if (_pInet && targets.Count == 0) problems.Add("Enter at least one internet target (e.g. 1.1.1.1).");
        if (_pInet && targets.Any(t => !System.Net.IPAddress.TryParse(t, out var a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            problems.Add("Internet targets must be IPv4 addresses (e.g. 1.1.1.1, 8.8.8.8, 9.9.9.9).");
        TimeSpan? duration = _selectedDuration.Duration;
        if (_selectedDuration.IsCustom)
        {
            if (!int.TryParse(_customMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) || m < 1 || m > 1440) problems.Add("Custom duration must be 1–1440 minutes.");
            else duration = TimeSpan.FromMinutes(m);
        }
        if (string.IsNullOrWhiteSpace(_location)) problems.Add("Enter the test area / location (it is printed on the report).");
        bool latText = !string.IsNullOrWhiteSpace(_lat), lonText = !string.IsNullOrWhiteSpace(_lon);
        if ((latText && ParseDouble(_lat) == null) || (lonText && ParseDouble(_lon) == null)) problems.Add("Latitude/longitude must be decimal degrees.");
        else if (GeoTag.Validate(latText ? ParseDouble(_lat) : null, lonText ? ParseDouble(_lon) : null) is string geoError) problems.Add(geoError);
        if ((_speedAtStart || _speedEvery > 0) && !HasSpeedServer) problems.Add("Speed tests need a test server: SETTINGS → Test servers.");
        if ((_twoWayAtStart || _twoWayEvery > 0) && !HasResponder) problems.Add("Two-way tests need a paired LAN responder: SETTINGS → Test servers.");

        ValidationMessage = problems.FirstOrDefault() ?? "";
        CanStart = problems.Count == 0 && !_runner.IsRunning;
        var probe = BuildConfigCore(duration ?? TimeSpan.FromMinutes(15));
        string howLong = duration is TimeSpan d ? $"for {SaturationTestViewModel.FormatSpan(d)}" : "until you stop it";
        SummaryLine = $"Test your connection {howLong}: about {probe.EstimatedKbps():0.#} kbit/s of probes" +
                      (_speedAtStart || _speedEvery > 0 ? ", plus speed tests" : "") + (_twoWayAtStart || _twoWayEvery > 0 ? ", plus two-way tests" : "") + ".";
        OnPropertyChanged(nameof(HasSpeedServer));
        OnPropertyChanged(nameof(HasResponder));
        OnPropertyChanged(nameof(SpeedHint));
        OnPropertyChanged(nameof(TwoWayHint));
    }

    /// <summary>Tests only: shorter tick and duration so a full run takes a second.</summary>
    internal int? TickOverrideMs { get; set; }
    internal TimeSpan? DurationOverride { get; set; }

    private ConnectionTestConfig BuildConfigCore(TimeSpan? duration) => new()
    {
        Name = string.IsNullOrWhiteSpace(_testName) ? $"Connection {DateTime.Now:yyyy-MM-dd HH:mm}" : _testName.Trim(),
        Duration = duration,
        Intensity = _intensity == "Detailed" ? ConnectionIntensity.Detailed : ConnectionIntensity.Light,
        TickMilliseconds = TickOverrideMs ?? (_intensity == "Detailed" ? 500 : 1000),
        InterfaceId = _adapter?.Id ?? Guid.Empty,
        InterfaceName = _adapter?.Description ?? "",
        ProbeWifiLink = _pWifi, ProbeGateway = _pGw, ProbeIsp = _pIsp, ProbeInternet = _pInet, ProbeDns = _pDns, ProbeWeb = _pWeb,
        InternetTargets = ParseTargets(),
        Location = _location.Trim(), Facility = _facility.Trim(), Operator = _operator.Trim(), Client = _client.Trim(), Notes = _notes.Trim(),
        Geo = new GeoTag { Latitude = ParseDouble(_lat), Longitude = ParseDouble(_lon), Building = _building.Trim(), Floor = _floor.Trim(), Zone = _zone.Trim() },
        KeepAwake = _keepAwake,
        StopOnNetworkChange = _stopOnChange,
        SpeedTestAtStart = _speedAtStart, SpeedTestEveryMinutes = _speedEvery,
        TwoWayAtStart = _twoWayAtStart, TwoWayEveryMinutes = _twoWayEvery,
        ContextAtStart = _context
    };

    public ConnectionTestConfig BuildConfig()
    {
        TimeSpan? duration = _selectedDuration.IsCustom ? TimeSpan.FromMinutes(int.Parse(_customMinutes, CultureInfo.InvariantCulture)) : _selectedDuration.Duration;
        return BuildConfigCore(DurationOverride ?? duration);
    }

    // ------------------------------------------------------------------ running
    public ObservableCollection<ConnectionSegmentTile> Tiles { get; } = new();
    public ObservableCollection<string> EventFeed { get; } = new();
    private ConnectionReport? _live;
    public ConnectionReport? LiveReport { get => _live; private set { _live = value; OnPropertyChanged(); } }
    private double _progressPct;
    public double ProgressPct { get => _progressPct; set { _progressPct = value; OnPropertyChanged(); } }
    private bool _indeterminate;
    public bool IsIndeterminate { get => _indeterminate; set { _indeterminate = value; OnPropertyChanged(); } }
    private string _elapsed = "", _remaining = "", _runStatus = "", _activity = "", _warning = "", _markerText = "", _actionStatus = "";
    public string ElapsedText { get => _elapsed; set { _elapsed = value; OnPropertyChanged(); } }
    public string RemainingText { get => _remaining; set { _remaining = value; OnPropertyChanged(); } }
    public string RunStatus { get => _runStatus; set { _runStatus = value; OnPropertyChanged(); } }
    public string ActivityText { get => _activity; set { _activity = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasActivity)); } }
    public bool HasActivity => !string.IsNullOrEmpty(_activity);
    public string WarningText { get => _warning; set { _warning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(_warning);
    public string MarkerText { get => _markerText; set { _markerText = value; OnPropertyChanged(); } }
    public string ActionStatus { get => _actionStatus; set { _actionStatus = value; OnPropertyChanged(); } }
    private bool _isPaused;
    public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotPaused)); } }
    public bool IsNotPaused => !_isPaused;
    private ConnectionTestConfig? _activeConfig;
    public Guid? ActiveTestId => _activeConfig?.TestId;
    private int _lastEventCount;
    private DateTime _lastAnalysis = DateTime.MinValue;

    private string RunningTitle() => $"RUNNING — {_activeConfig?.Name} @ {_activeConfig?.Location}";

    public async Task StartAsync()
    {
        await RefreshContextAsync();
        Revalidate();
        if (!CanStart) return;
        Guid? linked = null;
        if (_alsoSaturation && StartLinkedSaturation != null) linked = await StartLinkedSaturation(this);
        var config = BuildConfig() with { LinkedSaturationTestId = linked };
        config = config with { Environment = await CaptureEnvironmentAsync(config) };
        _activeConfig = config;
        Tiles.Clear();
        foreach (var seg in new[] { ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns, ConnectionSegment.Web })
            Tiles.Add(new ConnectionSegmentTile(seg));
        EventFeed.Clear();
        _lastEventCount = 0;
        LiveReport = null;
        IsPaused = false;
        ProgressPct = 0;
        IsIndeterminate = config.Duration == null;
        RunStatus = RunningTitle();
        ElapsedText = "00:00:00";
        RemainingText = config.Duration is TimeSpan d ? SaturationTestViewModel.FormatSpan(d) : "until stopped";
        WarningText = IsOnBattery() == true ? "On battery power: connect the charger for long tests." : "";
        ActionStatus = "";
        CurrentStep = Step.Running;
        if (config.KeepAwake) KeepAwake(true);
        TestRunningChanged?.Invoke(true);
        try
        {
            await _runner.StartAsync(config);
        }
        catch (Exception ex)
        {
            if (config.KeepAwake) KeepAwake(false);
            TestRunningChanged?.Invoke(false);
            CurrentStep = Step.Setup;
            ValidationMessage = $"Could not start: {ex.Message}";
        }
        Revalidate();
    }

    private async Task<SaturationEnvironment> CaptureEnvironmentAsync(ConnectionTestConfig config)
    {
        ClientConnectionInfo? conn = null;
        try { conn = await _adapters.GetConnectionInfoAsync(config.InterfaceId); } catch { }
        AdapterDriverInfo? driver = null;
        try { driver = _adapters.GetDriverInfo(config.InterfaceId); } catch { }
        var now = DateTime.UtcNow;
        var asm = typeof(ConnectionTestViewModel).Assembly;
        string version = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "unknown";
        return new SaturationEnvironment
        {
            AppVersion = version, OsDescription = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})", MachineName = System.Environment.MachineName,
            TimeZoneId = TimeZoneInfo.Local.Id, UtcOffsetMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes),
            AdapterDescription = _adapter?.Description ?? config.InterfaceName, AdapterMac = _adapter?.MacAddress ?? "",
            DriverVersion = driver?.DriverVersion, DriverDate = driver?.DriverDate, DriverProvider = driver?.Provider, ConnectionAtStart = conn, CapturedUtc = now
        };
    }

    private void OnProgress(ConnectionProgress p)
    {
        // Analyse at most every 2 s (5 s for very long tests) off the UI thread.
        var snap = _runner.Snapshot();
        bool due = (DateTime.UtcNow - _lastAnalysis).TotalSeconds >= (snap.Probes.Count > 50_000 ? 5 : 2);
        ConnectionReport? report = null;
        if (due)
        {
            _lastAnalysis = DateTime.UtcNow;
            report = _analyzer.Analyze(p.Config, snap.Probes, snap.Link, snap.Speed, snap.TwoWay, snap.Markers, snap.StartUtc, null, p.Status, snap.ActiveSeconds);
        }
        Ui(() => ApplyProgress(p, report));
    }

    internal void ApplyProgress(ConnectionProgress p, ConnectionReport? report)
    {
        ElapsedText = SaturationTestViewModel.FormatSpan(TimeSpan.FromSeconds(p.ActiveSeconds));
        if (p.RemainingSeconds is double rem && p.Config.Duration is TimeSpan d)
        {
            RemainingText = SaturationTestViewModel.FormatSpan(TimeSpan.FromSeconds(rem));
            ProgressPct = Math.Clamp(100 * p.ActiveSeconds / d.TotalSeconds, 0, 100);
        }
        ActivityText = p.Activity ?? "";
        var link = Tiles.FirstOrDefault(t => t.Segment == ConnectionSegment.WifiLink);
        if (link != null && p.LastLink is { } l) link.UpdateLink(l, p.Config.ProbeWifiLink);
        if (report == null) return;
        LiveReport = report;
        foreach (var tile in Tiles.Where(t => t.Segment != ConnectionSegment.WifiLink))
            tile.Update(report, p.Config);
        foreach (var e in report.Events.Skip(_lastEventCount))
            EventFeed.Insert(0, $"{e.StartUtc.ToLocalTime():HH:mm:ss}  {e.Type,-13} {(e.AttributedTo.HasValue ? "[" + ConnectionAnalyzer.Name(e.AttributedTo.Value) + "] " : "")}{e.Detail}");
        _lastEventCount = report.Events.Count;
        ChartDataChanged?.Invoke();
    }

    private void OnCompleted(ConnectionReport report)
    {
        Ui(() =>
        {
            if (_activeConfig?.KeepAwake == true) KeepAwake(false);
            TestRunningChanged?.Invoke(false);
            ShowResults(report);
            _ = LoadHistoryAsync();
            Revalidate();
        });
    }

    public async Task StopAsync()
    {
        if (!Confirm("Stop the connection test now? Results collected so far will be analysed and saved.", "Stop test")) return;
        RunStatus = "Stopping…";
        await _runner.StopAsync();
    }

    public async Task RunSpeedNowAsync()
    {
        if (SpeedClient() is not { } client) { ActionStatus = "Set up a test server first: SETTINGS → Test servers."; return; }
        var s = SpeedSettings();
        if (!Confirm($"Run a speed test against {client.Provider} ({client.Server})?\n\nIt saturates the connection for about {2 * s.SecondsPerDirection + 4} s " +
                     "(other users of this network will notice) and transfers as much data as the link allows. Regular probes pause meanwhile.", "Speed test")) return;
        ActionStatus = "Speed test running…";
        var r = await _runner.RunSpeedTestAsync(client, s);
        ActionStatus = r == null ? "Speed test not run (no test running, or another capacity test is in progress)."
            : r.Error != null ? $"Speed test failed: {r.Error}"
            : $"Down {r.DownloadMbps:0.0} Mbps · up {r.UploadMbps:0.0} Mbps · latency +{r.LatencyIncreaseMs:0} ms under load (grade {r.BufferbloatGrade}).";
    }

    public async Task RunTwoWayNowAsync()
    {
        var rs = ResponderSettingsValue();
        if (!rs.IsConfigured) { ActionStatus = "Pair a LAN responder first: SETTINGS → Test servers."; return; }
        ActionStatus = "Two-way test running (about 20 s)…";
        var r = await _runner.RunTwoWayAsync(new ResponderClient(rs));
        ActionStatus = r == null ? "Two-way test not run (no test running, or another capacity test is in progress)."
            : r.Error != null ? $"Two-way test failed: {r.Error}"
            : $"Loss up {r.UpLossPct:0.##} % / down {r.DownLossPct:0.##} % · jitter up {r.UpJitterMs:0.0} / down {r.DownJitterMs:0.0} ms · MOS {r.MosUp:0.00} / {r.MosDown:0.00} (est.)";
    }

    // ------------------------------------------------------------------ results
    private ConnectionReport? _report;
    public ConnectionReport? Report { get => _report; private set { _report = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultTitle)); OnPropertyChanged(nameof(ProblemsText)); OnPropertyChanged(nameof(VoiceText)); } }
    public ObservableCollection<UseVerdict> ResultVerdicts { get; } = new();
    public ObservableCollection<ConnectionSegmentSummary> ResultSegments { get; } = new();
    public ObservableCollection<ConnectionEvent> ResultEvents { get; } = new();
    public ObservableCollection<SpeedTestResult> ResultSpeed { get; } = new();
    public ObservableCollection<TwoWayResult> ResultTwoWay { get; } = new();
    public string ResultTitle => _report == null ? "" :
        $"{_report.Config.Name} @ {_report.Config.Location} · {_report.Config.ContextAtStart?.Ssid} · {SaturationTestViewModel.FormatSpan(TimeSpan.FromSeconds(_report.ActiveSeconds))} · {_report.Status} · {_report.Totals.ProbesSent:N0} probes";
    public string ProblemsText => _report == null ? "" : _report.ProblemsBySegment.Count == 0
        ? "Where problems happened: nowhere — no outages or latency spikes were attributed to any part of the path."
        : "Where problems happened: " + string.Join(" · ", _report.ProblemsBySegment.OrderBy(kv => kv.Key).Select(kv => $"{ConnectionAnalyzer.Name(kv.Key)} {kv.Value}"));
    public string VoiceText => _report?.VoiceMos is double m ? $"Voice estimate: MOS {m:0.00} (R {_report.VoiceRFactor:0}) — simplified E-model, not a measured call." : "Voice estimate: not available.";
    private string _exportStatus = "";
    public string ExportStatus { get => _exportStatus; set { _exportStatus = value; OnPropertyChanged(); } }
    public string? LastExportFolder { get; private set; }

    public void ShowResults(ConnectionReport report)
    {
        Report = report;
        ResultVerdicts.Clear(); foreach (var v in report.Verdicts) ResultVerdicts.Add(v);
        ResultSegments.Clear(); foreach (var s in report.Segments) ResultSegments.Add(s);
        ResultEvents.Clear(); foreach (var e in report.Events) ResultEvents.Add(e);
        ResultSpeed.Clear(); foreach (var s in report.SpeedTests) ResultSpeed.Add(s);
        ResultTwoWay.Clear(); foreach (var t in report.TwoWayTests) ResultTwoWay.Add(t);
        ExportStatus = "";
        CurrentStep = Step.Results;
        ChartDataChanged?.Invoke();
    }

    public async Task ExportAsync(string format)
    {
        if (_report == null) return;
        string safe = string.Concat(_report.Config.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
        string baseName = $"Connection_{safe}_{_report.StartUtc.ToLocalTime():yyyyMMdd_HHmm}";
        try
        {
            switch (format)
            {
                case "pdf":
                {
                    var path = SaveFileDialog("PDF report (*.pdf)|*.pdf", baseName + ".pdf", ".pdf");
                    if (path == null) return;
                    ExportStatus = "Generating PDF…";
                    await _export.ExportPdfAsync(path, _report);
                    ExportStatus = $"Saved PDF report: {path}";
                    LastExportFolder = Path.GetDirectoryName(path);
                    break;
                }
                case "csv":
                {
                    var path = SaveFileDialog("CSV bundle prefix (*.csv)|*.csv", baseName + ".csv", ".csv");
                    if (path == null) return;
                    var dir = Path.GetDirectoryName(path)!;
                    var files = await _export.ExportCsvBundleAsync(dir, Path.GetFileNameWithoutExtension(path), _report);
                    ExportStatus = $"Saved {files.Count} CSV files in {dir}: {string.Join(", ", files.Select(Path.GetFileName))}";
                    LastExportFolder = dir;
                    break;
                }
                case "json":
                {
                    var path = SaveFileDialog("JSON (*.json)|*.json", baseName + ".json", ".json");
                    if (path == null) return;
                    await _export.ExportJsonAsync(path, _report);
                    ExportStatus = $"Saved JSON: {path}";
                    LastExportFolder = Path.GetDirectoryName(path);
                    break;
                }
            }
        }
        catch (IOException ex) { ExportStatus = $"Export failed (file may be open in another program): {ex.Message}. Close it and try again."; }
        catch (Exception ex) { ExportStatus = $"Export failed: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ history
    public ObservableCollection<ConnectionTestRecord> History { get; } = new();
    private ConnectionTestRecord? _selectedHistory;
    public ConnectionTestRecord? SelectedHistory { get => _selectedHistory; set { _selectedHistory = value; OnPropertyChanged(); } }
    private string _historyStatus = "";
    public string HistoryStatus { get => _historyStatus; set { _historyStatus = value; OnPropertyChanged(); } }

    public async Task LoadHistoryAsync()
    {
        var tests = await _repository.GetTestsAsync();
        Ui(() => { History.Clear(); foreach (var t in tests) History.Add(t); });
    }

    public async Task OpenHistoryAsync()
    {
        var rec = _selectedHistory;
        if (rec == null) return;
        if (_runner.IsRunning) { HistoryStatus = "A connection test is running: open past results after it finishes."; return; }
        HistoryStatus = "";
        var d = await _repository.GetDataAsync(rec.Config.TestId);
        var report = _analyzer.Analyze(rec.Config, d.Probes, d.Link, d.Speed, d.TwoWay, d.Markers, rec.StartUtc, rec.EndUtc, rec.Status,
            rec.ActiveSeconds ?? (d.Probes.Count == 0 ? 0 : d.Probes.Max(p => p.ElapsedMs) / 1000.0));
        Ui(() => ShowResults(report));
    }

    public async Task DeleteHistoryAsync()
    {
        var rec = _selectedHistory;
        if (rec == null) return;
        if (_runner.IsRunning && _activeConfig?.TestId == rec.Config.TestId) return;
        if (!Confirm($"Delete connection test '{rec.Config.Name}' ({rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}) and all of its {rec.ProbeCount:N0} probes? This cannot be undone.", "Delete test")) return;
        await _repository.DeleteTestAsync(rec.Config.TestId);
        await LoadHistoryAsync();
    }

    // ------------------------------------------------------------------ SETTINGS → Test servers
    public IReadOnlyList<SpeedServerKind> SpeedKindOptions { get; } = new[] { SpeedServerKind.None, SpeedServerKind.Responder, SpeedServerKind.Http };
    private SpeedServerKind _speedKind = SpeedServerKind.None;
    public SpeedServerKind SpeedKind { get => _speedKind; set { _speedKind = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsHttpSpeed)); } }
    public bool IsHttpSpeed => _speedKind == SpeedServerKind.Http;
    private string _dl = "", _ul = "", _streams = "4", _seconds = "10", _rAddr = "", _rPort = "5410", _rKeyInput = "", _serverStatus = "";
    private string _rKeyStored = "";
    public string HttpDownloadUrl { get => _dl; set { _dl = value; OnPropertyChanged(); } }
    public string HttpUploadUrl { get => _ul; set { _ul = value; OnPropertyChanged(); } }
    public string SpeedStreams { get => _streams; set { _streams = value; OnPropertyChanged(); } }
    public string SpeedSeconds { get => _seconds; set { _seconds = value; OnPropertyChanged(); } }
    public string ResponderAddress { get => _rAddr; set { _rAddr = value; OnPropertyChanged(); } }
    public string ResponderPort { get => _rPort; set { _rPort = value; OnPropertyChanged(); } }
    /// <summary>Key typed by the user; cleared after saving (the stored key is DPAPI-protected and never shown again).</summary>
    public string ResponderKeyInput { get => _rKeyInput; set { _rKeyInput = value; OnPropertyChanged(); } }
    public string ResponderKeyState => string.IsNullOrEmpty(_rKeyStored) ? "No key saved." : "Key saved (protected for this Windows user).";
    public string ServerStatus { get => _serverStatus; set { _serverStatus = value; OnPropertyChanged(); } }

    public SpeedServerSettings SpeedSettings() => new()
    {
        Kind = _speedKind, HttpDownloadUrl = _dl.Trim(), HttpUploadUrl = _ul.Trim(),
        Streams = int.TryParse(_streams, out var s) ? Math.Clamp(s, 1, 16) : 4,
        SecondsPerDirection = int.TryParse(_seconds, out var sec) ? Math.Clamp(sec, 3, 30) : 10
    };

    public ResponderSettings ResponderSettingsValue() => new()
    {
        Address = _rAddr.Trim(), Port = int.TryParse(_rPort, out var p) && p is > 0 and < 65536 ? p : 5410, Key = _rKeyStored
    };

    public ISpeedTestClient? SpeedClient()
    {
        var s = SpeedSettings();
        try
        {
            return s.Kind switch
            {
                SpeedServerKind.Responder when ResponderSettingsValue().IsConfigured => new ResponderClient(ResponderSettingsValue()),
                SpeedServerKind.Http when Uri.TryCreate(s.HttpDownloadUrl, UriKind.Absolute, out _) && Uri.TryCreate(s.HttpUploadUrl, UriKind.Absolute, out _) => new HttpSpeedClient(s),
                _ => null
            };
        }
        catch (FormatException) { return null; }
    }

    public async Task LoadServersAsync()
    {
        try
        {
            async Task<string> Get(string k) => await _settings.GetSettingAsync(k) ?? "";
            var kind = await Get(SpeedKindSetting);
            var dl = await Get(SpeedDownloadSetting);
            var ul = await Get(SpeedUploadSetting);
            var st = await Get(SpeedStreamsSetting);
            var sec = await Get(SpeedSecondsSetting);
            var addr = await Get(ResponderAddressSetting);
            var port = await Get(ResponderPortSetting);
            var key = SecretProtector.Unprotect(await Get(ResponderKeySetting));
            var targets = await Get(TargetsSetting);
            Ui(() =>
            {
                SpeedKind = Enum.TryParse<SpeedServerKind>(kind, out var k) ? k : SpeedServerKind.None;
                HttpDownloadUrl = dl; HttpUploadUrl = ul;
                if (st.Length > 0) SpeedStreams = st;
                if (sec.Length > 0) SpeedSeconds = sec;
                ResponderAddress = addr;
                if (port.Length > 0) ResponderPort = port;
                _rKeyStored = key;
                if (targets.Length > 0) InternetTargetsText = targets;
                OnPropertyChanged(nameof(ResponderKeyState));
                Revalidate();
            });
        }
        catch (Exception ex) { ServerStatus = $"Test-server settings could not be loaded: {ex.Message}"; }
    }

    public async Task SaveServersAsync()
    {
        if (_speedKind == SpeedServerKind.Http && (!Uri.TryCreate(_dl.Trim(), UriKind.Absolute, out _) || !Uri.TryCreate(_ul.Trim(), UriKind.Absolute, out _)))
        {
            ServerStatus = "Enter full download and upload URLs (https://…) for the HTTP server.";
            return;
        }
        if (!string.IsNullOrWhiteSpace(_rKeyInput))
        {
            try { _ = WirelessScanner.Responder.ResponderProtocol.KeyFromText(_rKeyInput); }
            catch (FormatException) { ServerStatus = "That responder key is not valid. Copy it exactly from the responder (--show-key)."; return; }
            _rKeyStored = _rKeyInput.Trim();
        }
        await _settings.SaveSettingAsync(SpeedKindSetting, _speedKind.ToString());
        await _settings.SaveSettingAsync(SpeedDownloadSetting, _dl.Trim());
        await _settings.SaveSettingAsync(SpeedUploadSetting, _ul.Trim());
        await _settings.SaveSettingAsync(SpeedStreamsSetting, SpeedSettings().Streams.ToString(CultureInfo.InvariantCulture));
        await _settings.SaveSettingAsync(SpeedSecondsSetting, SpeedSettings().SecondsPerDirection.ToString(CultureInfo.InvariantCulture));
        await _settings.SaveSettingAsync(ResponderAddressSetting, _rAddr.Trim());
        await _settings.SaveSettingAsync(ResponderPortSetting, ResponderSettingsValue().Port.ToString(CultureInfo.InvariantCulture));
        await _settings.SaveSettingAsync(ResponderKeySetting, SecretProtector.Protect(_rKeyStored));
        await _settings.SaveSettingAsync(TargetsSetting, string.Join(", ", ParseTargets()));
        ResponderKeyInput = "";
        OnPropertyChanged(nameof(ResponderKeyState));
        ServerStatus = "Saved.";
        Revalidate();
    }

    public async Task CheckResponderAsync()
    {
        if (!string.IsNullOrWhiteSpace(_rKeyInput)) await SaveServersAsync();
        var rs = ResponderSettingsValue();
        if (!rs.IsConfigured) { ServerStatus = "Enter the responder address and key, then save."; return; }
        ServerStatus = "Checking…";
        try
        {
            var (ok, message) = await new ResponderClient(rs).CheckAsync(default);
            ServerStatus = (ok ? "OK: " : "Failed: ") + message;
        }
        catch (FormatException) { ServerStatus = "The saved key is not valid."; }
    }

    // ------------------------------------------------------------------ commands
    public ICommand RefreshContextCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AddMarkerCommand { get; }
    public ICommand RunSpeedNowCommand { get; }
    public ICommand RunTwoWayNowCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand NewTestCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand SaveServersCommand { get; }
    public ICommand CheckResponderCommand { get; }

    /// <summary>Compact live state for GET /api/connection.</summary>
    public object? ApiStatus()
    {
        var r = _live;
        var cfg = _activeConfig;
        if (!_runner.IsRunning || cfg == null) return new { running = false, lastTest = _report == null ? null : new { _report.Config.TestId, _report.Config.Name, status = _report.Status.ToString() } };
        return new
        {
            running = true,
            testId = cfg.TestId,
            name = cfg.Name,
            ssid = cfg.ContextAtStart?.Ssid,
            bssid = cfg.ContextAtStart?.Bssid,
            elapsed = ElapsedText,
            analyticsVersion = r?.AnalyticsVersion,
            segments = r?.Segments.Select(s => new { segment = s.Segment.ToString(), s.Sent, s.LossPct, s.AvailabilityPct, s.RttP50, s.RttP95, s.JitterMs, s.Outages }),
            problemsBySegment = r?.ProblemsBySegment.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            verdicts = r?.Verdicts,
            events = r?.Events.Count,
            speedTests = r?.SpeedTests.Count,
            twoWayTests = r?.TwoWayTests.Count
        };
    }

    private static string? DefaultSaveDialog(string filter, string fileName, string ext)
    {
        if (System.Windows.Application.Current == null) return null;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName, DefaultExt = ext, AddExtension = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One part of the path on the live connection strip: status word (never colour alone), latency and loss.</summary>
public class ConnectionSegmentTile : INotifyPropertyChanged
{
    public ConnectionSegmentTile(ConnectionSegment segment) { Segment = segment; Title = ConnectionAnalyzer.Name(segment); }
    public ConnectionSegment Segment { get; }
    public string Title { get; }
    public string Status { get; private set; } = "WAITING";
    public string Brush { get; private set; } = "#7A6A55";
    public string Line1 { get; private set; } = "—";
    public string Line2 { get; private set; } = "";
    public string ToolTipText { get; private set; } = "";

    private void Set(string status, string brush, string l1, string l2, string tip)
    {
        Status = status; Brush = brush; Line1 = l1; Line2 = l2; ToolTipText = tip;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal void UpdateLink(ConnectionLinkSample l, bool enabled)
    {
        if (!enabled) { Set("NOT TESTED", "#7A6A55", "off", "", "Wi-Fi link sampling switched off."); return; }
        if (!l.Connected) { Set("DOWN", "#D03B3B", "disconnected", "", "The Wi-Fi association is down."); return; }
        string tier = l.Rssi is int r ? (r >= -67 ? "GOOD" : r >= -75 ? "DEGRADED" : "WEAK") : "GOOD";
        Set(tier, tier == "GOOD" ? "#0CA30C" : tier == "DEGRADED" ? "#FAB219" : "#EC835A", l.Rssi.HasValue ? $"{l.Rssi} dBm" : "signal n/a",
            $"{l.RxRateMbps:0} Mbps", $"{l.Ssid} · {l.Bssid} · ch {l.Channel}");
    }

    internal void Update(ConnectionReport r, ConnectionTestConfig cfg)
    {
        bool enabled = Segment switch
        {
            ConnectionSegment.Gateway => cfg.ProbeGateway, ConnectionSegment.IspHop => cfg.ProbeIsp && cfg.ContextAtStart?.IspHop != null,
            ConnectionSegment.Internet => cfg.ProbeInternet, ConnectionSegment.Dns => cfg.ProbeDns, ConnectionSegment.Web => cfg.ProbeWeb, _ => true
        };
        var s = r.Segments.FirstOrDefault(x => x.Segment == Segment);
        if (!enabled) { Set("NOT TESTED", "#7A6A55", Segment == ConnectionSegment.IspHop && cfg.ProbeIsp ? "hop not found" : "off", "", "This part of the path is not measured."); return; }
        if (s == null) { Set("WAITING", "#7A6A55", "—", "", "No probe has finished yet."); return; }
        var recent = r.Probes.Where(p => p.Segment == Segment && p.Method == s.Method).GroupBy(p => p.Sequence).OrderByDescending(g => g.Key).Take(3).ToList();
        bool down = recent.Count == 3 && recent.All(g => g.All(p => p.Outcome != ProbeOutcome.Ok));
        bool degraded = (s.LossPct ?? 0) > 2 || r.Events.Any(e => e.Type == ConnectionEventType.LatencySpike && e.Segment == Segment && e.StartElapsedMs > r.ActiveSeconds * 1000 - 30_000);
        string status = down ? "DOWN" : degraded ? "DEGRADED" : "GOOD";
        Set(status, down ? "#D03B3B" : degraded ? "#FAB219" : "#0CA30C", s.RttP50.HasValue ? $"{s.RttP50:0} ms" : "no reply",
            $"loss {s.LossPct:0.#} %", $"{Title}: {s.Target} ({s.Method}) · p95 {s.RttP95:0} ms · jitter {s.JitterMs:0.0} ms · {s.Sent} probes · outages {s.Outages}");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
