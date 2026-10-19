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

namespace WirelessScanner.Presentation;

/// <summary>
/// Signal Saturation Test screen (SPEC-SAT-001 §7, DR-0005): Setup → Running → Results, plus history.
/// UI-thread marshalling, dialogs and power management are injectable hooks so the flow is unit-testable.
/// </summary>
public class SaturationTestViewModel : INotifyPropertyChanged
{
    public enum Step { Setup, Running, Results }

    private readonly INetworkAdapterProvider _provider;
    private readonly ISaturationTestRunner _runner;
    private readonly ISaturationRepository _repository;
    private readonly ISaturationAnalyzer _analyzer;
    private readonly ISaturationExportService _export;
    private readonly IGeoLocationProvider? _geo;

    // Hooks (replaced in tests)
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

    /// <summary>Raised (on the UI thread) when chart data changed; the view redraws its canvases.</summary>
    public event Action? ChartDataChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public SaturationTestViewModel(INetworkAdapterProvider provider, ISaturationTestRunner runner, ISaturationRepository repository,
        ISaturationAnalyzer analyzer, ISaturationExportService export, IGeoLocationProvider? geo = null)
    {
        _geo = geo;
        _provider = provider;
        _runner = runner;
        _repository = repository;
        _analyzer = analyzer;
        _export = export;

        DurationOptions = new List<DurationOption>
        {
            new("5 minutes", TimeSpan.FromMinutes(5)), new("15 minutes", TimeSpan.FromMinutes(15)), new("30 minutes", TimeSpan.FromMinutes(30)),
            new("1 hour", TimeSpan.FromHours(1)), new("4 hours", TimeSpan.FromHours(4)), new("8 hours", TimeSpan.FromHours(8)),
            new("24 hours", TimeSpan.FromHours(24)), new("Custom (minutes)", null, IsCustom: true), new("Until stopped", null)
        };
        _selectedDuration = DurationOptions[1];
        ScopeOptions = new List<ScopeOption>
        {
            new(SaturationScope.SingleAp, "Single AP (one BSSID)"), new(SaturationScope.MultipleAps, "Multiple APs (selected BSSIDs)"),
            new(SaturationScope.SingleSsid, "Single SSID (all its BSSIDs)"), new(SaturationScope.MultipleSsids, "Multiple SSIDs"),
            new(SaturationScope.AllVisible, "All visible networks (environment)")
        };
        _selectedScope = ScopeOptions[0];

        DiscoverCommand = new RelayCommand(async () => await DiscoverAsync());
        StartCommand = new RelayCommand(async () => await StartAsync());
        PauseCommand = new RelayCommand(Pause);
        ResumeCommand = new RelayCommand(Resume);
        StopCommand = new RelayCommand(async () => await StopAsync());
        AddMarkerCommand = new RelayCommand(AddMarker);
        ExportPdfCommand = new RelayCommand(async () => await ExportAsync("pdf"));
        ExportCsvCommand = new RelayCommand(async () => await ExportAsync("csv"));
        ExportJsonCommand = new RelayCommand(async () => await ExportAsync("json"));
        NewTestCommand = new RelayCommand(() => CurrentStep = Step.Setup);
        RefreshHistoryCommand = new RelayCommand(async () => await LoadHistoryAsync());
        OpenHistoryCommand = new RelayCommand(async () => await OpenHistoryAsync());
        DeleteHistoryCommand = new RelayCommand(async () => await DeleteHistoryAsync());
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        DetectLocationCommand = new RelayCommand(async () => await DetectLocationAsync());

        _runner.Progress += OnProgress;
        _runner.Completed += OnCompleted;
    }

    // ------------------------------------------------------------------ attached to Live Survey (DR-0011)
    private NetworkSelection? _selection;
    private Func<string, int> _apsInSsid = _ => 0;

    /// <summary>True when the test lives in Live Survey and takes its scope from the shared network selection.</summary>
    public bool IsAttached => _selection != null;
    public bool IsStandalone => _selection == null;

    /// <summary>Raised with true when a test starts (selection locked) and false when it ends (unlocked).</summary>
    public event Action<bool>? TestRunningChanged;

    /// <summary>Plain-language scope of the shared selection, e.g. "1 SSID (3 APs now, follows new APs) + 1 AP (fixed)".</summary>
    public string SelectionText => _selection == null ? "" : _selection.Describe(_apsInSsid);

    /// <summary>
    /// Use the Live Survey selection instead of the standalone discover list: scope is derived from it
    /// (SSIDs, fixed APs or mixed), it is locked while the test runs, and an empty selection means "all visible" after a confirmation.
    /// </summary>
    public void AttachToLiveSurvey(NetworkSelection selection, Func<string, int> apsInSsid)
    {
        if (_selection != null) _selection.Changed -= OnSelectionChanged;
        _selection = selection;
        _apsInSsid = apsInSsid;
        _selection.Changed += OnSelectionChanged;
        OnPropertyChanged(nameof(IsAttached));
        OnPropertyChanged(nameof(IsStandalone));
        OnPropertyChanged(nameof(SelectionText));
        Revalidate();
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        Revalidate();
    }

    // ------------------------------------------------------------------ common
    private Step _step = Step.Setup;
    public Step CurrentStep
    {
        get => _step;
        set { _step = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSetup)); OnPropertyChanged(nameof(IsRunningStep)); OnPropertyChanged(nameof(IsResults)); }
    }
    public bool IsSetup => _step == Step.Setup;
    public bool IsRunningStep => _step == Step.Running;
    public bool IsResults => _step == Step.Results;

    public ObservableCollection<NetworkInterfaceInfo> Adapters { get; } = new();
    private NetworkInterfaceInfo? _selectedAdapter;
    public NetworkInterfaceInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set { _selectedAdapter = value; OnPropertyChanged(); Revalidate(); }
    }

    public async Task LoadAdaptersAsync()
    {
        var list = (await _provider.GetInterfacesAsync()).Where(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi).ToList();
        Ui(() =>
        {
            Adapters.Clear();
            foreach (var a in list) Adapters.Add(a);
            SelectedAdapter ??= Adapters.FirstOrDefault();
            DiscoveryStatus = Adapters.Count == 0
                ? "No Wi-Fi adapter found. Connect one or check Device Manager, then reopen this tab."
                : "Press DISCOVER to scan for networks to test.";
        });
    }

    // ------------------------------------------------------------------ setup: targets
    public ObservableCollection<SaturationCandidate> Candidates { get; } = new();
    private bool _isDiscovering;
    public bool IsDiscovering { get => _isDiscovering; set { _isDiscovering = value; OnPropertyChanged(); } }
    private string _discoveryStatus = "Press DISCOVER to scan for networks to test.";
    public string DiscoveryStatus { get => _discoveryStatus; set { _discoveryStatus = value; OnPropertyChanged(); } }
    private string _search = "";
    public string SearchText { get => _search; set { _search = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredCandidates)); } }

    public IEnumerable<SaturationCandidate> FilteredCandidates => string.IsNullOrWhiteSpace(_search)
        ? Candidates
        : Candidates.Where(c => c.Ssid.Contains(_search, StringComparison.OrdinalIgnoreCase) || c.Bssid.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                || (c.Vendor ?? "").Contains(_search, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ScopeOption> ScopeOptions { get; }
    private ScopeOption _selectedScope;
    public ScopeOption SelectedScope
    {
        get => _selectedScope;
        set { _selectedScope = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectionHint)); Revalidate(); }
    }
    public string SelectionHint => _selectedScope.Scope switch
    {
        SaturationScope.SingleAp => "Tick exactly one network row (BSSID).",
        SaturationScope.MultipleAps => "Tick two or more network rows (BSSIDs).",
        SaturationScope.SingleSsid => "Tick any row of the SSID to test; all of its BSSIDs are followed.",
        SaturationScope.MultipleSsids => "Tick rows from two or more SSIDs; all their BSSIDs are followed.",
        _ => "Every visible network is followed; no selection needed."
    };

    public async Task DiscoverAsync()
    {
        if (SelectedAdapter == null)
        {
            DiscoveryStatus = "Choose a Wi-Fi adapter first.";
            return;
        }
        IsDiscovering = true;
        var seen = new Dictionary<string, AccessPoint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int i = 1; i <= 2; i++)
            {
                DiscoveryStatus = $"Scanning… (fresh scan {i}/2)";
                var cycle = await _provider.ScanAsync(SelectedAdapter.Id, TimeSpan.FromSeconds(6));
                if (cycle.Status == ScanCycleStatus.Failed)
                {
                    DiscoveryStatus = $"Scan failed: {cycle.ErrorMessage}";
                    return;
                }
                foreach (var ap in cycle.AccessPoints) seen[ap.BSSID] = ap;
            }
            var previouslySelected = new HashSet<string>(Candidates.Where(c => c.IsSelected).Select(c => c.Bssid), StringComparer.OrdinalIgnoreCase);
            Ui(() =>
            {
                Candidates.Clear();
                foreach (var ap in seen.Values.OrderBy(a => a.SSID).ThenByDescending(a => a.RSSI))
                {
                    var c = new SaturationCandidate(ap) { IsSelected = previouslySelected.Contains(ap.BSSID) };
                    c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SaturationCandidate.IsSelected)) Revalidate(); };
                    Candidates.Add(c);
                }
                OnPropertyChanged(nameof(FilteredCandidates));
                DiscoveryStatus = Candidates.Count == 0
                    ? "No networks found. Move closer to the access point or press DISCOVER again."
                    : $"{Candidates.Count} networks found ({Candidates.Select(c => c.Ssid).Distinct().Count()} SSIDs). Tick the ones to test.";
                Revalidate();
            });
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var c in FilteredCandidates) c.IsSelected = selected;
        Revalidate();
    }

    public int SelectedCount => Candidates.Count(c => c.IsSelected);
    public int SelectedSsidCount => Candidates.Where(c => c.IsSelected).Select(c => c.Ssid).Distinct().Count();

    // ------------------------------------------------------------------ setup: how long / where
    public IReadOnlyList<DurationOption> DurationOptions { get; }
    private DurationOption _selectedDuration;
    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set { _selectedDuration = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsCustomDuration)); Revalidate(); }
    }
    public bool IsCustomDuration => _selectedDuration.IsCustom;
    private string _customMinutes = "60";
    public string CustomMinutes { get => _customMinutes; set { _customMinutes = value; OnPropertyChanged(); Revalidate(); } }

    public IReadOnlyList<int> IntervalOptions { get; } = new[] { 5, 10, 15, 30, 60 };
    private int _intervalSeconds = 5;
    public int IntervalSeconds { get => _intervalSeconds; set { _intervalSeconds = value; OnPropertyChanged(); Revalidate(); } }

    private string _testName = "", _location = "", _facility = "", _operator = "", _client = "", _notes = "";
    public string TestName { get => _testName; set { _testName = value; OnPropertyChanged(); } }
    public string Location { get => _location; set { _location = value; OnPropertyChanged(); Revalidate(); } }
    public string Facility { get => _facility; set { _facility = value; OnPropertyChanged(); } }
    public string Operator { get => _operator; set { _operator = value; OnPropertyChanged(); } }
    public string Client { get => _client; set { _client = value; OnPropertyChanged(); } }
    public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

    // Geolocation (owner requirement 2026-10-15): stored with the test and printed on every export.
    private string _latitude = "", _longitude = "", _accuracy = "", _building = "", _floor = "", _zone = "";
    private string _geoSource = "manual";
    private DateTime? _geoCapturedUtc;
    private string _geoStatus = "Recommended: coordinates let results be tracked and compared by place.";
    public string Latitude { get => _latitude; set { _latitude = value; _geoSource = "manual"; _geoCapturedUtc = null; OnPropertyChanged(); Revalidate(); } }
    public string Longitude { get => _longitude; set { _longitude = value; _geoSource = "manual"; _geoCapturedUtc = null; OnPropertyChanged(); Revalidate(); } }
    public string AccuracyMeters { get => _accuracy; set { _accuracy = value; OnPropertyChanged(); } }
    public string Building { get => _building; set { _building = value; OnPropertyChanged(); } }
    public string Floor { get => _floor; set { _floor = value; OnPropertyChanged(); } }
    public string Zone { get => _zone; set { _zone = value; OnPropertyChanged(); } }
    public string GeoStatus { get => _geoStatus; set { _geoStatus = value; OnPropertyChanged(); } }
    private bool _isLocating;
    public bool IsLocating { get => _isLocating; set { _isLocating = value; OnPropertyChanged(); } }

    public async Task DetectLocationAsync()
    {
        if (_geo == null) { GeoStatus = "Location detection is not available; enter coordinates manually."; return; }
        IsLocating = true;
        GeoStatus = "Asking Windows for the current location...";
        try
        {
            var (tag, error) = await _geo.GetCurrentAsync(TimeSpan.FromSeconds(20));
            if (tag == null) { GeoStatus = error ?? "Location unavailable."; return; }
            _latitude = tag.Latitude!.Value.ToString("0.000000", CultureInfo.InvariantCulture);
            _longitude = tag.Longitude!.Value.ToString("0.000000", CultureInfo.InvariantCulture);
            _accuracy = tag.AccuracyMeters?.ToString("0", CultureInfo.InvariantCulture) ?? "";
            _geoSource = tag.Source;
            _geoCapturedUtc = tag.CapturedUtc;
            OnPropertyChanged(nameof(Latitude)); OnPropertyChanged(nameof(Longitude)); OnPropertyChanged(nameof(AccuracyMeters));
            GeoStatus = $"Detected {tag.CoordinatesText} via {tag.Source}. Check it and add building/floor/zone for indoor accuracy.";
            Revalidate();
        }
        finally
        {
            IsLocating = false;
        }
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public GeoTag BuildGeo() => new()
    {
        Latitude = ParseDouble(_latitude),
        Longitude = ParseDouble(_longitude),
        AccuracyMeters = ParseDouble(_accuracy),
        Source = _geoSource,
        CapturedUtc = _geoCapturedUtc ?? (ParseDouble(_latitude).HasValue ? DateTime.UtcNow : null),
        Building = _building.Trim(),
        Floor = _floor.Trim(),
        Zone = _zone.Trim()
    };

    public ICommand DetectLocationCommand { get; }

    private bool _keepAwakeEnabled = true;
    public bool KeepAwakeEnabled { get => _keepAwakeEnabled; set { _keepAwakeEnabled = value; OnPropertyChanged(); } }

    // Advanced thresholds (SPEC §6 defaults)
    private string _dropDb = "8", _peakDb = "8", _dropoutCycles = "2", _baselineWindow = "12";
    public string DropDb { get => _dropDb; set { _dropDb = value; OnPropertyChanged(); Revalidate(); } }
    public string PeakDb { get => _peakDb; set { _peakDb = value; OnPropertyChanged(); Revalidate(); } }
    public string DropoutCycles { get => _dropoutCycles; set { _dropoutCycles = value; OnPropertyChanged(); Revalidate(); } }
    public string BaselineWindow { get => _baselineWindow; set { _baselineWindow = value; OnPropertyChanged(); Revalidate(); } }

    // ------------------------------------------------------------------ validation (SAT-AC-07)
    private string _validation = "";
    public string ValidationMessage { get => _validation; private set { _validation = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasValidationMessage)); } }
    public bool HasValidationMessage => !string.IsNullOrEmpty(_validation);
    private bool _canStart;
    public bool CanStart { get => _canStart; private set { _canStart = value; OnPropertyChanged(); } }
    private string _summaryLine = "";
    public string SummaryLine { get => _summaryLine; private set { _summaryLine = value; OnPropertyChanged(); } }

    public void Revalidate()
    {
        OnPropertyChanged(nameof(SelectedCount));
        var problems = new List<string>();
        if (SelectedAdapter == null) problems.Add("Choose a Wi-Fi adapter.");
        else if (SelectedAdapter.DeviceType != NetworkDeviceType.Wireless_WiFi) problems.Add("A saturation test needs a Wi-Fi adapter.");
        var scope = _selection?.DeriveScope() ?? _selectedScope.Scope;
        int n = _selection?.Bssids.Count ?? SelectedCount, ssids = _selection?.Ssids.Count ?? SelectedSsidCount;
        int units = _selection?.Units.Count ?? 0, unitBssids = units == 0 ? 0 : _selection!.UnitBssids().Count;
        if (units > 0 && unitBssids == 0) problems.Add("The selected access point has no BSSIDs in range right now; wait for a scan or select it again.");
        if (_selection != null) { /* the shared selection is always a consistent scope; empty = all visible (confirmed at start) */ }
        else if (scope == SaturationScope.SingleAp && n != 1) problems.Add(n == 0 ? "Select one network to test." : "Single AP scope: select exactly one network (or switch to Multiple APs).");
        if (scope == SaturationScope.MultipleAps && n < 2) problems.Add("Multiple APs scope: select at least two networks.");
        if (scope == SaturationScope.SingleSsid && ssids != 1) problems.Add(ssids == 0 ? "Select a row of the SSID to test." : "Single SSID scope: selected rows belong to more than one SSID.");
        if (scope == SaturationScope.MultipleSsids && ssids < 2) problems.Add("Multiple SSIDs scope: select rows from at least two SSIDs.");
        if (_selection == null && scope is SaturationScope.SingleSsid or SaturationScope.MultipleSsids && Candidates.Any(c => c.IsSelected && c.Ssid == "<Hidden SSID>"))
            problems.Add("Hidden SSIDs can only be tested by BSSID (use an AP scope).");

        TimeSpan? duration = _selectedDuration.Duration;
        if (_selectedDuration.IsCustom)
        {
            if (!int.TryParse(_customMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) || m < 1 || m > 1440)
                problems.Add("Custom duration must be 1–1440 minutes.");
            else duration = TimeSpan.FromMinutes(m);
        }
        if (string.IsNullOrWhiteSpace(_location)) problems.Add("Enter the test area / location (it is printed on the report).");
        bool latText = !string.IsNullOrWhiteSpace(_latitude), lonText = !string.IsNullOrWhiteSpace(_longitude);
        if ((latText && ParseDouble(_latitude) == null) || (lonText && ParseDouble(_longitude) == null)) problems.Add("Latitude/longitude must be decimal degrees, e.g. 41.878113 and -87.629799.");
        else if (GeoTag.Validate(latText ? ParseDouble(_latitude) : null, lonText ? ParseDouble(_longitude) : null) is string geoError) problems.Add(geoError);
        if (!TryInt(_dropDb, 3, 30, out _) || !TryInt(_peakDb, 3, 30, out _)) problems.Add("Drop/peak thresholds must be 3–30 dB.");
        if (!TryInt(_dropoutCycles, 1, 20, out _)) problems.Add("Dropout cycles must be 1–20.");
        if (!TryInt(_baselineWindow, 3, 120, out _)) problems.Add("Baseline window must be 3–120 samples.");

        ValidationMessage = problems.FirstOrDefault() ?? "";
        CanStart = problems.Count == 0 && !_runner.IsRunning;

        string what = scope switch
        {
            SaturationScope.SingleAp or SaturationScope.MultipleAps => $"{n} BSSID{(n == 1 ? "" : "s")}",
            SaturationScope.SingleSsid or SaturationScope.MultipleSsids => $"{ssids} SSID{(ssids == 1 ? "" : "s")} (all BSSIDs)",
            SaturationScope.SingleUnit or SaturationScope.MultipleUnits => $"{units} access point{(units == 1 ? "" : "s")} ({unitBssids} BSSID{(unitBssids == 1 ? "" : "s")}, every SSID and radio)",
            SaturationScope.Mixed when units > 0 => $"{units} access point{(units == 1 ? "" : "s")} ({unitBssids} BSSIDs)" + (ssids > 0 ? $" + {ssids} SSID{(ssids == 1 ? "" : "s")}" : "") + (n > 0 ? $" + {n} fixed BSSID{(n == 1 ? "" : "s")}" : ""),
            SaturationScope.Mixed => $"{ssids} SSID{(ssids == 1 ? "" : "s")} (all BSSIDs) + {n} fixed BSSID{(n == 1 ? "" : "s")}",
            _ => "all visible networks"
        };
        string howLong = duration is TimeSpan d ? $"for {FormatSpan(d)} (≈{Math.Max(1, (int)(d.TotalSeconds / _intervalSeconds)):N0} cycles)" : "until you stop it";
        SummaryLine = $"Test {what} every {_intervalSeconds} s {howLong}.";
    }

    private static bool TryInt(string s, int min, int max, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    public SaturationTestConfig BuildConfig()
    {
        var selected = Candidates.Where(c => c.IsSelected).ToList();
        TimeSpan? duration = _selectedDuration.IsCustom ? TimeSpan.FromMinutes(int.Parse(_customMinutes, CultureInfo.InvariantCulture)) : _selectedDuration.Duration;
        var scope = _selection?.DeriveScope() ?? _selectedScope.Scope;
        IReadOnlyList<string> bssids, ssids;
        IReadOnlyList<SaturationUnitTarget> units = Array.Empty<SaturationUnitTarget>();
        if (_selection != null)
        {
            // units are resolved NOW (DR-0013): their members become fixed BSSIDs, so regrouping cannot change the test
            units = ResolveUnits(_selection);
            bssids = _selection.Bssids.Concat(units.SelectMany(u => u.Members.Select(m => m.Bssid)))
                .Select(b => b.ToUpperInvariant()).Distinct().OrderBy(b => b, StringComparer.Ordinal).ToList();
            ssids = _selection.Ssids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        else
        {
            bssids = scope is SaturationScope.SingleAp or SaturationScope.MultipleAps ? selected.Select(c => c.Bssid.ToUpperInvariant()).ToList() : Array.Empty<string>();
            ssids = scope is SaturationScope.SingleSsid or SaturationScope.MultipleSsids ? selected.Select(c => c.Ssid).Distinct().ToList() : Array.Empty<string>();
        }
        return new SaturationTestConfig
        {
            Name = string.IsNullOrWhiteSpace(_testName) ? $"Saturation {DateTime.Now:yyyy-MM-dd HH:mm}" : _testName.Trim(),
            Scope = scope,
            TargetBssids = bssids,
            TargetSsids = ssids,
            TargetUnits = units,
            Duration = duration,
            Interval = TimeSpan.FromSeconds(_intervalSeconds),
            Location = _location.Trim(),
            Facility = _facility.Trim(),
            Operator = _operator.Trim(),
            Client = _client.Trim(),
            Notes = _notes.Trim(),
            Geo = BuildGeo(),
            InterfaceId = SelectedAdapter!.Id,
            InterfaceName = SelectedAdapter.Description,
            KeepAwake = _keepAwakeEnabled,
            Thresholds = new SaturationThresholds
            {
                DropDb = int.Parse(_dropDb, CultureInfo.InvariantCulture),
                PeakDb = int.Parse(_peakDb, CultureInfo.InvariantCulture),
                DropoutMissedCycles = int.Parse(_dropoutCycles, CultureInfo.InvariantCulture),
                BaselineWindow = int.Parse(_baselineWindow, CultureInfo.InvariantCulture)
            }
        };
    }

    private static IReadOnlyList<SaturationUnitTarget> ResolveUnits(NetworkSelection selection)
    {
        if (selection.Units.Count == 0 || selection.UnitResolver is not { } resolver) return Array.Empty<SaturationUnitTarget>();
        var list = new List<SaturationUnitTarget>();
        foreach (var id in selection.Units.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (resolver.Get(id) is not { } unit) continue;
            list.Add(new SaturationUnitTarget
            {
                Id = unit.Id,
                Name = unit.Name,
                Confidence = unit.Confidence,
                Evidence = unit.Evidence,
                Members = unit.Radios.SelectMany(r => r.Members.Select(m => new SaturationUnitMember(m.Bssid.ToUpperInvariant(), m.Ssid, m.Band, r.Key))).ToList()
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ running
    public ObservableCollection<SaturationTile> Tiles { get; } = new();
    public ObservableCollection<string> EventFeed { get; } = new();
    private SaturationReport? _live;
    public SaturationReport? LiveReport { get => _live; private set { _live = value; OnPropertyChanged(); } }

    private double _progressPct;
    public double ProgressPct { get => _progressPct; set { _progressPct = value; OnPropertyChanged(); } }
    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; set { _isIndeterminate = value; OnPropertyChanged(); } }
    private string _elapsedText = "", _remainingText = "", _cycleText = "", _warningText = "", _runStatus = "";
    public string ElapsedText { get => _elapsedText; set { _elapsedText = value; OnPropertyChanged(); } }
    public string RemainingText { get => _remainingText; set { _remainingText = value; OnPropertyChanged(); } }
    public string CycleText { get => _cycleText; set { _cycleText = value; OnPropertyChanged(); } }
    public string WarningText { get => _warningText; set { _warningText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasWarning)); } }
    public bool HasWarning => !string.IsNullOrEmpty(_warningText);
    public string RunStatus { get => _runStatus; set { _runStatus = value; OnPropertyChanged(); } }
    private bool _isPaused;
    public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotPaused)); } }
    public bool IsNotPaused => !_isPaused;
    private string _markerText = "";
    public string MarkerText { get => _markerText; set { _markerText = value; OnPropertyChanged(); } }
    private SaturationTestConfig? _activeConfig;
    /// <summary>Id of the running (or last started) test; used to link a connection test to it (DR-0014).</summary>
    public Guid? ActiveTestId => _activeConfig?.TestId;

    public async Task StartAsync()
    {
        Revalidate();
        if (!CanStart) return;
        if (_selection is { IsEmpty: true } &&
            !Confirm("Nothing is selected in the network list. Test every visible network (environment scope)?\n\nTip: tick SSIDs or access points in the list to test only those.", "Test all networks"))
            return;
        var config = BuildConfig();
        config = config with { Environment = await CaptureEnvironmentAsync(config) };
        _activeConfig = config;
        Tiles.Clear();
        EventFeed.Clear();
        LiveReport = null;
        IsPaused = false;
        ProgressPct = 0;
        IsIndeterminate = config.Duration == null;
        RunStatus = $"RUNNING — {config.Name} @ {config.Location}";
        CycleText = "Waiting for first scan…";
        ElapsedText = "00:00:00";
        RemainingText = config.Duration is TimeSpan d ? FormatSpan(d) : "until stopped";
        WarningText = IsOnBattery() == true ? "On battery power: connect the charger for long tests." : "";
        CurrentStep = Step.Running;
        if (config.KeepAwake) KeepAwake(true);
        _selection?.Lock();
        TestRunningChanged?.Invoke(true);
        try
        {
            await _runner.StartAsync(config);
        }
        catch (Exception ex)
        {
            if (config.KeepAwake) KeepAwake(false);
            _selection?.Unlock();
            TestRunningChanged?.Invoke(false);
            CurrentStep = Step.Setup;
            ValidationMessage = $"Could not start: {ex.Message}";
        }
        Revalidate();
    }

    /// <summary>
    /// Provenance printed on every report (DR-0012): app/OS/time zone, adapter + driver (RSSI is only comparable per driver)
    /// and the PC's own association at the start (scan throttling and the receive view depend on it).
    /// </summary>
    public async Task<SaturationEnvironment> CaptureEnvironmentAsync(SaturationTestConfig config)
    {
        ClientConnectionInfo? conn = null;
        try { conn = await _provider.GetConnectionInfoAsync(config.InterfaceId); } catch { /* optional */ }
        AdapterDriverInfo? driver = null;
        try { driver = _provider.GetDriverInfo(config.InterfaceId); } catch { /* optional */ }
        var now = DateTime.UtcNow;
        var asm = typeof(SaturationTestViewModel).Assembly;
        string version = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0]
            ?? asm.GetName().Version?.ToString(3) ?? "unknown";
        return new SaturationEnvironment
        {
            AppVersion = version,
            OsDescription = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            MachineName = System.Environment.MachineName,
            TimeZoneId = TimeZoneInfo.Local.Id,
            UtcOffsetMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes),
            AdapterDescription = SelectedAdapter?.Description ?? config.InterfaceName,
            AdapterMac = SelectedAdapter?.MacAddress ?? "",
            DriverVersion = driver?.DriverVersion,
            DriverDate = driver?.DriverDate,
            DriverProvider = driver?.Provider,
            ConnectionAtStart = conn,
            CapturedUtc = now
        };
    }

    private int _lastEventCount;

    private void OnProgress(SaturationProgress p)
    {
        // Analyse off the UI thread, then publish.
        var report = _analyzer.Analyze(p.Config, p.Cycles, p.Markers, p.StartUtc, null, p.Status, p.ActiveSeconds);
        Ui(() => ApplyProgress(p, report));
    }

    internal void ApplyProgress(SaturationProgress p, SaturationReport report)
    {
        LiveReport = report;
        var h = report.Health;
        ElapsedText = FormatSpan(TimeSpan.FromSeconds(p.ActiveSeconds));
        if (p.RemainingSeconds is double rem && p.Config.Duration is TimeSpan d)
        {
            RemainingText = FormatSpan(TimeSpan.FromSeconds(rem));
            ProgressPct = Math.Clamp(100 * p.ActiveSeconds / d.TotalSeconds, 0, 100);
        }
        CycleText = $"Cycle {p.LastCycle.Sequence:N0}: {h.Fresh:N0} fresh · {h.Stale:N0} stale · {h.Failed:N0} failed";

        var warnings = new List<string>();
        if (p.LastCycle.Status == ScanCycleStatus.Failed) warnings.Add($"Adapter problem — retrying: {p.LastCycle.ErrorMessage}");
        if (h.Total >= 5 && h.Stale > h.Total / 3) warnings.Add("Many stale cycles: Windows may be throttling scans (often while connected). Results remain valid; stale data is excluded.");
        if (IsOnBattery() == true) warnings.Add("On battery power.");
        WarningText = string.Join("  ", warnings);

        // Tiles keyed by BSSID
        foreach (var t in report.Targets)
        {
            var tile = Tiles.FirstOrDefault(x => x.Bssid == t.Bssid);
            if (tile == null) Tiles.Add(tile = new SaturationTile(t.Bssid));
            var last = p.LastCycle.Observations.FirstOrDefault(o => string.Equals(o.Bssid, t.Bssid, StringComparison.OrdinalIgnoreCase));
            tile.Update(t, last);
        }

        foreach (var e in report.Events.Skip(_lastEventCount))
            EventFeed.Insert(0, $"{e.StartUtc.ToLocalTime():HH:mm:ss}  {e.Type,-21} {e.Subject}  {e.Detail}");
        _lastEventCount = report.Events.Count;
        ChartDataChanged?.Invoke();
    }

    private void OnCompleted(SaturationReport report)
    {
        Ui(() =>
        {
            if (_activeConfig?.KeepAwake == true) KeepAwake(false);
            _selection?.Unlock();
            TestRunningChanged?.Invoke(false);
            _lastEventCount = 0;
            ShowResults(report);
            _ = LoadHistoryAsync();
        });
    }

    private void Pause()
    {
        _runner.Pause();
        IsPaused = true;
        RunStatus = "PAUSED — scanning suspended; paused time is not counted.";
    }

    private void Resume()
    {
        _runner.Resume();
        IsPaused = false;
        RunStatus = $"RUNNING — {_activeConfig?.Name} @ {_activeConfig?.Location}";
    }

    public async Task StopAsync()
    {
        if (!Confirm("Stop the saturation test now? Results collected so far will be analysed and saved.", "Stop test")) return;
        RunStatus = "Stopping…";
        await _runner.StopAsync();
    }

    private void AddMarker()
    {
        if (string.IsNullOrWhiteSpace(_markerText)) return;
        _runner.AddMarker(_markerText);
        EventFeed.Insert(0, $"{DateTime.Now:HH:mm:ss}  Marker                {_markerText.Trim()}");
        MarkerText = "";
    }

    // ------------------------------------------------------------------ results
    private SaturationReport? _report;
    public SaturationReport? Report { get => _report; private set { _report = value; OnPropertyChanged(); OnPropertyChanged(nameof(ResultTitle)); } }
    public ObservableCollection<SaturationTargetSummary> ResultTargets { get; } = new();
    public ObservableCollection<SaturationEvent> ResultEvents { get; } = new();
    private SaturationTargetSummary? _selectedResultTarget;
    public SaturationTargetSummary? SelectedResultTarget
    {
        get => _selectedResultTarget;
        set { _selectedResultTarget = value; OnPropertyChanged(); ChartDataChanged?.Invoke(); }
    }
    public string ResultTitle => _report == null ? "" :
        $"{_report.Config.Name} — {_report.Config.Location} — {_report.Status} — {FormatSpan(TimeSpan.FromSeconds(_report.ActiveSeconds))} active, {_report.Cycles.Count:N0} cycles ({_report.Health.Fresh:N0} fresh / {_report.Health.Stale:N0} stale / {_report.Health.Failed:N0} failed)";
    private string _exportStatus = "";
    public string ExportStatus { get => _exportStatus; set { _exportStatus = value; OnPropertyChanged(); } }

    public void ShowResults(SaturationReport report)
    {
        Report = report;
        ResultTargets.Clear();
        foreach (var t in report.Targets.OrderBy(t => t.StabilityScore ?? -1)) ResultTargets.Add(t);
        ResultEvents.Clear();
        foreach (var e in report.Events) ResultEvents.Add(e);
        SelectedResultTarget = ResultTargets.FirstOrDefault();
        ExportStatus = report.Targets.Count == 0 ? "No targets were observed. Check the selection and adapter, then run again." : "";
        CurrentStep = Step.Results;
        Revalidate();
        ChartDataChanged?.Invoke();
    }

    public async Task ExportAsync(string format)
    {
        if (_report == null) return;
        string safe = string.Concat(_report.Config.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Replace(' ', '_');
        string baseName = $"Saturation_{safe}_{_report.StartUtc.ToLocalTime():yyyyMMdd_HHmm}";
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
        catch (IOException ex)
        {
            ExportStatus = $"Export failed (file may be open in another program): {ex.Message}. Close it and try again.";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
    }

    public string? LastExportFolder { get; private set; }

    // ------------------------------------------------------------------ history
    public ObservableCollection<SaturationTestRecord> History { get; } = new();
    private SaturationTestRecord? _selectedHistory;
    public SaturationTestRecord? SelectedHistory { get => _selectedHistory; set { _selectedHistory = value; OnPropertyChanged(); } }
    private string _historyStatus = "";
    public string HistoryStatus { get => _historyStatus; set { _historyStatus = value; OnPropertyChanged(); } }

    public async Task LoadHistoryAsync()
    {
        var tests = await _repository.GetTestsAsync();
        Ui(() =>
        {
            History.Clear();
            foreach (var t in tests) History.Add(t);
        });
    }

    public async Task OpenHistoryAsync()
    {
        var rec = _selectedHistory;
        if (rec == null) return;
        if (_runner.IsRunning)
        {
            HistoryStatus = "A test is running: open past results after it finishes (the live view stays on the running test).";
            return;
        }
        HistoryStatus = "";
        var cycles = await _repository.GetCyclesAsync(rec.Config.TestId);
        var markers = await _repository.GetMarkersAsync(rec.Config.TestId);
        var report = _analyzer.Analyze(rec.Config, cycles, markers, rec.StartUtc, rec.EndUtc, rec.Status, rec.ActiveSeconds);
        Ui(() => ShowResults(report));
    }

    public async Task DeleteHistoryAsync()
    {
        var rec = _selectedHistory;
        if (rec == null) return;
        if (_runner.IsRunning && _activeConfig?.TestId == rec.Config.TestId) return;
        if (!Confirm($"Delete saturation test '{rec.Config.Name}' ({rec.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}) and all of its {rec.CycleCount:N0} cycles? This cannot be undone.", "Delete test")) return;
        await _repository.DeleteTestAsync(rec.Config.TestId);
        await LoadHistoryAsync();
    }

    // ------------------------------------------------------------------ commands
    public ICommand DiscoverCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AddMarkerCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand NewTestCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }

    // ------------------------------------------------------------------ helpers
    public static string FormatSpan(TimeSpan t) => t.TotalHours >= 24 ? $"{(int)t.TotalDays}d {t:hh\\:mm\\:ss}" : t.ToString(@"hh\:mm\:ss");

    private static string? DefaultSaveDialog(string filter, string fileName, string ext)
    {
        var folder = WirelessScanner.Infrastructure.AppPaths.ReportsDirectory;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter, FileName = fileName, DefaultExt = ext, InitialDirectory = folder, AddExtension = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public record DurationOption(string Label, TimeSpan? Duration, bool IsCustom = false)
{
    public override string ToString() => Label;
}

public record ScopeOption(SaturationScope Scope, string Label)
{
    public override string ToString() => Label;
}

public class SaturationCandidate : INotifyPropertyChanged
{
    public SaturationCandidate(AccessPoint ap)
    {
        Bssid = ap.BSSID;
        Ssid = ap.SSID;
        Band = ap.Band;
        Channel = ap.Channel;
        Rssi = ap.RSSI;
        Security = ap.SecurityType;
        Vendor = ap.VendorOUI;
        Width = ap.ChannelWidth;
    }

    public string Bssid { get; }
    public string Ssid { get; }
    public string Band { get; }
    public int Channel { get; }
    public int Rssi { get; }
    public string Security { get; }
    public string? Vendor { get; }
    public int? Width { get; }
    public string Tier => SaturationAnalyzer.Tier(Rssi);
    public string RssiText => $"{Rssi} dBm ({Tier})";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Live per-target tile. Status always has a text label as well as colour (SAT-AC-09).</summary>
public class SaturationTile : INotifyPropertyChanged
{
    public SaturationTile(string bssid) => Bssid = bssid;
    public string Bssid { get; }
    public string Title { get; private set; } = "";
    public string Current { get; private set; } = "—";
    public string CurrentLabel { get; private set; } = "waiting";
    public string Brush { get; private set; } = "#AA4400";
    public string Stats { get; private set; } = "";
    public string Events { get; private set; } = "";

    public void Update(SaturationTargetSummary t, SaturationObservation? last)
    {
        Title = $"{(string.IsNullOrEmpty(t.Ssid) ? "<hidden>" : t.Ssid)}  ·  {t.Bssid}  ·  ch {t.PrimaryChannel?.ToString() ?? "?"}";
        if (last?.Status == ObservationStatus.Observed && last.Rssi is int r)
        {
            Current = $"{r} dBm";
            CurrentLabel = SaturationAnalyzer.Tier(r).ToUpperInvariant();
            Brush = r >= -67 ? "#00FF66" : r >= -70 ? "#FFBB33" : r >= -80 ? "#FF7700" : "#FF3300";
        }
        else
        {
            Current = "—";
            CurrentLabel = last?.Status switch
            {
                ObservationStatus.Missing => "NOT SEEN",
                ObservationStatus.Stale => "STALE",
                ObservationStatus.Unknown => "SCAN FAILED",
                _ => "WAITING"
            };
            Brush = last?.Status == ObservationStatus.Missing ? "#FF3300" : "#AA4400";
        }
        Stats = $"min {t.Min?.ToString() ?? "—"} / avg {(t.Mean.HasValue ? t.Mean.Value.ToString("F1", CultureInfo.InvariantCulture) : "—")} / max {t.Max?.ToString() ?? "—"} dBm  ·  σ {(t.StdDev.HasValue ? t.StdDev.Value.ToString("F1", CultureInfo.InvariantCulture) : "—")} dB  ·  avail {(t.AvailabilityPct.HasValue ? t.AvailabilityPct.Value.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—")}";
        Events = $"drops {t.DropCount} · peaks {t.PeakCount} · dropouts {t.DropoutCount} · ch changes {t.ChannelChangeCount} · grade {t.Grade}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Keep-awake and battery status (SPEC §4.7).</summary>
public static class NativePower
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>Must be called on a long-lived thread (the UI thread); prevents system sleep while true.</summary>
    public static void KeepAwake(bool on) => SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    public static bool? IsOnBattery()
    {
        try { return GetSystemPowerStatus(out var s) ? s.ACLineStatus == 0 : null; }
        catch { return null; }
    }
}
