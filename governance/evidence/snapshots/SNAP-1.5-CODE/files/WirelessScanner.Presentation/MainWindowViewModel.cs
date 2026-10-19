using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Presentation;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly INetworkAdapterProvider _adapterProvider;
    private readonly ISessionManager _sessionManager;
    private readonly ISessionRepository _sessionRepository;
    private readonly IExportService _exportService;
    private readonly IRestApiServer _apiServer;
    private readonly ISettingsRepository _settingsRepository;
    // Acquisition is owned by the shared ScanScheduler (DR-0011, F18): one in-flight scan per adapter for every view.
    private readonly IScanScheduler _scheduler;
    private IDisposable? _liveCadence;
    private long _lastProcessedSeq;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly LinkedList<IReadOnlyList<AccessPoint>> _waterfall = new();
    public const int WaterfallDepth = 60;
    private string _lastCycleText = "No scan yet";

    private NetworkInterfaceInfo? _selectedAdapter;
    private ObservableCollection<NetworkInterfaceInfo> _adapters = new();
    private ObservableCollection<AccessPoint> _accessPoints = new();

    private string _statusText = "Ready";
    private string _databaseSizeText = "Database Size: 0.0 MB";
    private string _selectedInterfaceName = "No Interface Selected";
    private bool _isScanning;
    private bool _isSsidView;
    private bool _showSpectrumGraph = true;
    private ObservableCollection<AccessPointDisplayItem> _displayAccessPoints = new();
    private readonly HashSet<string> _checkedKeys = new();
    private readonly System.Collections.Generic.Dictionary<string, int> _lastRssiByBssid = new();

    // REST API fields
    private bool _apiEnabled;
    private string _apiPort = "5005";
    private string _apiKey = "";
    private string _apiServerStatusText = "REST API: Stopped";

    // Session fields
    private string _sessionName = "";
    private string _facilityName = "";
    private string _sessionScope = "All SSIDs";
    private string _surveyPoint = "Default Zone";
    private string _sessionNotes = "";
    private string _sessionModeText = "";
    private bool _isSessionActive;
    private bool _isSessionPaused;

    // Export metadata fields
    private string _exportClientName = "Default Client";
    private string _exportSurveyorName = "Field Engineer";
    private string _exportNotes = "";

    // Checked history keys (legend filter)
    private readonly System.Collections.Generic.HashSet<string> _uncheckedHistoryKeys = new();

    // History & Logs fields
    private ObservableCollection<CaptureSession> _historicalSessions = new();
    private CaptureSession? _selectedHistorySession;
    private ObservableCollection<TelemetrySample> _selectedSessionSamples = new();
    private int _selectedSessionSampleCount = 0;

    // History filter fields
    private DateTime? _historyFilterFromDate;
    private DateTime? _historyFilterToDate;
    private string _historyFilterName = "";
    private System.Collections.Generic.List<CaptureSession> _selectedHistorySessions = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    // Telemetry column selectors
    private bool _showColTime = true;
    private bool _showColSsid = true;
    private bool _showColBssid = true;
    private bool _showColChannel = true;
    private bool _showColBand = true;
    private bool _showColRssi = true;
    private bool _showColSnr = true;
    private bool _showColQuality = true;
    private bool _showColJitter = true;
    private bool _showColZone = true;

    public bool ShowColTime
    {
        get => _showColTime;
        set { _showColTime = value; OnPropertyChanged(); }
    }
    public bool ShowColSsid
    {
        get => _showColSsid;
        set { _showColSsid = value; OnPropertyChanged(); }
    }
    public bool ShowColBssid
    {
        get => _showColBssid;
        set { _showColBssid = value; OnPropertyChanged(); }
    }
    public bool ShowColChannel
    {
        get => _showColChannel;
        set { _showColChannel = value; OnPropertyChanged(); }
    }
    public bool ShowColBand
    {
        get => _showColBand;
        set { _showColBand = value; OnPropertyChanged(); }
    }
    public bool ShowColRssi
    {
        get => _showColRssi;
        set { _showColRssi = value; OnPropertyChanged(); }
    }
    public bool ShowColSnr
    {
        get => _showColSnr;
        set { _showColSnr = value; OnPropertyChanged(); }
    }
    public bool ShowColQuality
    {
        get => _showColQuality;
        set { _showColQuality = value; OnPropertyChanged(); }
    }
    public bool ShowColJitter
    {
        get => _showColJitter;
        set { _showColJitter = value; OnPropertyChanged(); }
    }
    public bool ShowColZone
    {
        get => _showColZone;
        set { _showColZone = value; OnPropertyChanged(); }
    }

    public ExportFields GetExportFields()
    {
        System.Collections.Generic.IEnumerable<string>? includedBssids = null;
        if (SelectedHistorySession != null && _selectedSessionSamples != null && _selectedSessionSamples.Any())
        {
            var allBssids = _selectedSessionSamples.Select(s => s.BSSID).Distinct();
            includedBssids = allBssids.Where(b => IsHistoryKeyChecked(b)).ToList();
        }

        return new ExportFields(
            Time: ShowColTime,
            SSID: ShowColSsid,
            BSSID: ShowColBssid,
            Channel: ShowColChannel,
            Band: ShowColBand,
            RSSI: ShowColRssi,
            SNR: ShowColSnr,
            Quality: ShowColQuality,
            Jitter: ShowColJitter,
            Zone: ShowColZone,
            IncludedBssids: includedBssids
        );
    }

    public ObservableCollection<NetworkInterfaceInfo> Adapters
    {
        get => _adapters;
        set { _adapters = value; OnPropertyChanged(); }
    }

    public NetworkInterfaceInfo? SelectedAdapter
    {
        get => _selectedAdapter;
        set
        {
            if (_selectedAdapter != value)
            {
                _selectedAdapter = value;
                OnPropertyChanged();
                SelectedInterfaceName = _selectedAdapter?.Name ?? "No Interface Selected";
                StatusText = _selectedAdapter != null ? $"Connected to {_selectedAdapter.Name}" : "Ready";

                if (_selectedAdapter != null)
                {
                    StartScanTimer();
                }
                else
                {
                    StopScanTimer();
                }
            }
        }
    }

    public ObservableCollection<AccessPoint> AccessPoints
    {
        get => _accessPoints;
        set
        {
            _accessPoints = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredAccessPoints));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public string DatabaseSizeText
    {
        get => _databaseSizeText;
        set { _databaseSizeText = value; OnPropertyChanged(); }
    }

    public string SelectedInterfaceName
    {
        get => _selectedInterfaceName;
        set { _selectedInterfaceName = value; OnPropertyChanged(); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); }
    }

    /// <summary>Interval between fresh-scan cycles on the live tab (Windows scans take ~2-4 s).</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time to wait for Windows to confirm a scan completed.</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>Status of the most recent scan cycle (fresh/stale/failed, duration, AP count).</summary>
    public string LastCycleText
    {
        get => _lastCycleText;
        set { _lastCycleText = value; OnPropertyChanged(); }
    }

    // Capture Session Properties
    public string SessionName
    {
        get => _sessionName;
        set { _sessionName = value; OnPropertyChanged(); }
    }

    public string FacilityName
    {
        get => _facilityName;
        set { _facilityName = value; OnPropertyChanged(); }
    }

    public string SessionScope
    {
        get => _sessionScope;
        set { _sessionScope = value; OnPropertyChanged(); }
    }

    public string SurveyPoint
    {
        get => _surveyPoint;
        set
        {
            _surveyPoint = value;
            OnPropertyChanged();
            _sessionManager.ActiveSurveyPoint = _surveyPoint;
        }
    }

    public string SessionNotes
    {
        get => _sessionNotes;
        set
        {
            _sessionNotes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSessionNotes));
        }
    }

    public bool HasSessionNotes => !string.IsNullOrWhiteSpace(SessionNotes);

    public string SessionModeText
    {
        get => _sessionModeText;
        set { _sessionModeText = value; OnPropertyChanged(); }
    }

    public bool IsSessionActive
    {
        get => _isSessionActive;
        set
        {
            _isSessionActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSessionIdle));
            OnPropertyChanged(nameof(ModeStatusText));
        }
    }

    public bool IsSessionPaused
    {
        get => _isSessionPaused;
        set { _isSessionPaused = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeStatusText)); }
    }

    public bool IsSessionIdle => !IsSessionActive;

    // Export metadata properties
    public string ExportClientName
    {
        get => _exportClientName;
        set { _exportClientName = value; OnPropertyChanged(); }
    }

    public string ExportSurveyorName
    {
        get => _exportSurveyorName;
        set { _exportSurveyorName = value; OnPropertyChanged(); }
    }

    public string ExportNotes
    {
        get => _exportNotes;
        set { _exportNotes = value; OnPropertyChanged(); }
    }

    // REST API properties
    public bool ApiEnabled
    {
        get => _apiEnabled;
        set { _apiEnabled = value; OnPropertyChanged(); UpdateApiServerStatusText(); }
    }

    public string ApiPort
    {
        get => _apiPort;
        set { _apiPort = value; OnPropertyChanged(); }
    }

    public string ApiKey
    {
        get => _apiKey;
        set { _apiKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayedApiKey)); }
    }

    public string ApiServerStatusText
    {
        get => _apiServerStatusText;
        set { _apiServerStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Legacy SSID/AP view switch, kept as an API bridge (the UI now uses the grouped explorer, DR-0010).
    /// Switching clears the selection, as it always did.
    /// </summary>
    public bool IsSsidView
    {
        get => _isSsidView;
        set
        {
            if (_isSsidView != value)
            {
                _isSsidView = value;
                OnPropertyChanged();
                DisplayAccessPoints.Clear();
                _checkedKeys.Clear();
                Selection.Clear();
                UpdateDisplayAccessPoints();
                OnPropertyChanged(nameof(FilteredActiveSessionLogs));
            }
        }
    }

    /// <summary>The single network selection shared by live view, capture and saturation test (DR-0011).</summary>
    public NetworkSelection Selection { get; } = new();

    /// <summary>Grouped network explorer (DR-0010).</summary>
    public NetworkExplorerViewModel Explorer { get; }
    /// <summary>Physical-unit inference (DR-0013), fed by every scan cycle.</summary>
    public PhysicalUnitGrouper UnitGrouper { get; } = new();

    /// <summary>Scan scheduler shared with the saturation test.</summary>
    public IScanScheduler Scheduler => _scheduler;

    // ---- Live Survey modes and centre views (DR-0011) ----
    public enum SurveyModes { Monitor, Capture, Saturation, Connection }
    private SurveyModes _surveyMode = SurveyModes.Monitor;
    public SurveyModes SurveyMode
    {
        get => _surveyMode;
        set
        {
            _surveyMode = value;
            OnPropertyChanged();
            foreach (var n in new[] { nameof(IsMonitorMode), nameof(IsCaptureMode), nameof(IsSaturationMode), nameof(IsConnectionMode), nameof(IsDrawerOpen), nameof(ModeDescription) }) OnPropertyChanged(n);
            if (value == SurveyModes.Connection && Connection != null)
            {
                Connection.PrefillWhere(Saturation);
                if (Connection.IsSetup) _ = Connection.RefreshContextAsync();
            }
        }
    }
    public bool IsConnectionMode { get => _surveyMode == SurveyModes.Connection; set { if (value) SurveyMode = SurveyModes.Connection; } }

    /// <summary>One line under the mode bar: what the current mode is and does (DR-0014 clarity between the two tests).</summary>
    public string ModeDescription => _surveyMode switch
    {
        SurveyModes.Capture => "Capture: record the selected networks to a session (walk-through or fixed spot). Listens only; nothing is sent.",
        SurveyModes.Saturation => "Saturation test: leave this PC in place and measure how stable the selected networks' signal is. Listens to the radio only; sends nothing.",
        SurveyModes.Connection => "Connection test: check the connection this PC is on, end to end (Wi-Fi → router → ISP → internet, DNS, web) with tiny probes, and see where problems happen. Never changes the connection.",
        _ => "Monitor: watch the networks around you. Nothing is recorded and nothing is sent."
    };
    public bool IsMonitorMode { get => _surveyMode == SurveyModes.Monitor; set { if (value) SurveyMode = SurveyModes.Monitor; } }
    public bool IsCaptureMode { get => _surveyMode == SurveyModes.Capture; set { if (value) SurveyMode = SurveyModes.Capture; } }
    public bool IsSaturationMode { get => _surveyMode == SurveyModes.Saturation; set { if (value) SurveyMode = SurveyModes.Saturation; } }
    public bool IsDrawerOpen => _surveyMode != SurveyModes.Monitor;

    public enum CenterViews { Spectrum, Waterfall, Grid, Test, Results, Connection }
    private CenterViews _centerView = CenterViews.Spectrum;
    public CenterViews CenterView
    {
        get => _centerView;
        set
        {
            _centerView = value;
            _showSpectrumGraph = value != CenterViews.Grid;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSpectrumGraph));
            OnPropertyChanged(nameof(ShowTelemetryGrid));
            foreach (var n in new[] { nameof(IsSpectrumView), nameof(IsWaterfallView), nameof(IsGridView), nameof(IsTestView), nameof(IsResultsView), nameof(IsSpectrumOrWaterfall), nameof(IsStageView), nameof(IsConnectionView) }) OnPropertyChanged(n);
            SpectrumChanged?.Invoke();
        }
    }
    public bool IsSpectrumView { get => _centerView == CenterViews.Spectrum; set { if (value) CenterView = CenterViews.Spectrum; } }
    public bool IsWaterfallView { get => _centerView == CenterViews.Waterfall; set { if (value) CenterView = CenterViews.Waterfall; } }
    public bool IsGridView { get => _centerView == CenterViews.Grid; set { if (value) CenterView = CenterViews.Grid; } }
    public bool IsTestView { get => _centerView == CenterViews.Test; set { if (value) CenterView = CenterViews.Test; } }
    public bool IsResultsView { get => _centerView == CenterViews.Results; set { if (value) CenterView = CenterViews.Results; } }
    /// <summary>Connection test stage (live path and timelines, or results).</summary>
    public bool IsConnectionView { get => _centerView == CenterViews.Connection; set { if (value) CenterView = CenterViews.Connection; } }
    public bool IsSpectrumOrWaterfall => _centerView is CenterViews.Spectrum or CenterViews.Waterfall;
    /// <summary>Saturation stage (live test or results). Selecting it shows results when the attached test has them.</summary>
    public bool IsStageView
    {
        get => _centerView is CenterViews.Test or CenterViews.Results;
        set { if (value) CenterView = Saturation?.IsResults == true ? CenterViews.Results : CenterViews.Test; }
    }

    /// <summary>"All" (stacked, the 1.1 default), "2.4 GHz", "5 GHz" or "6 GHz".</summary>
    private string _spectrumBand = "All";
    public string SpectrumBand { get => _spectrumBand; set { _spectrumBand = value; OnPropertyChanged(); foreach (var n in new[] { nameof(IsBandAll), nameof(IsBand24), nameof(IsBand5), nameof(IsBand6) }) OnPropertyChanged(n); SpectrumChanged?.Invoke(); } }
    public bool IsBandAll { get => _spectrumBand == "All"; set { if (value) SpectrumBand = "All"; } }
    public bool IsBand24 { get => _spectrumBand == "2.4 GHz"; set { if (value) SpectrumBand = "2.4 GHz"; } }
    public bool IsBand5 { get => _spectrumBand == "5 GHz"; set { if (value) SpectrumBand = "5 GHz"; } }
    public bool IsBand6 { get => _spectrumBand == "6 GHz"; set { if (value) SpectrumBand = "6 GHz"; } }

    private bool _labelAll;
    /// <summary>Label every AP on the spectrum (the 1.1 behaviour); default labels focused networks only.</summary>
    public bool LabelAll { get => _labelAll; set { _labelAll = value; OnPropertyChanged(); SpectrumChanged?.Invoke(); } }

    /// <summary>Raised (UI thread) when the spectrum/waterfall must be redrawn.</summary>
    public event Action? SpectrumChanged;

    /// <summary>The last fresh scans (newest first) for the waterfall view.</summary>
    public IReadOnlyList<IReadOnlyList<AccessPoint>> WaterfallHistory => _waterfall.ToList();

    /// <summary>Identity colour slot (0..2) of a network on the spectrum, or -1 for context (DR-0010).</summary>
    public int FocusSlot(string ssid) => Selection.FocusSlot(ssid, _accessPoints);

    public bool IsSelectionAutoFocus => Selection.IsEmpty;

    public void ToggleSpectrumAp(AccessPoint ap) => Selection.ToggleAp(ap.SSID, ap.BSSID, _accessPoints.Where(a => a.SSID == ap.SSID).Select(a => a.BSSID).ToList());

    /// <summary>Mode bar / explorer footer buttons: "Monitor", "Capture" or "Saturation".</summary>
    public ICommand SetModeCommand => _setModeCommand ??= new ParamCommand(p =>
    {
        if (Enum.TryParse<SurveyModes>(p as string, out var m)) SurveyMode = m;
    });
    private ICommand? _setModeCommand;

    /// <summary>The saturation test attached to Live Survey (null when it runs standalone).</summary>
    public SaturationTestViewModel? Saturation { get; private set; }

    /// <summary>
    /// Host the saturation test in Live Survey (DR-0011): it shares this selection and adapter, its running/results
    /// steps drive the centre view, and the mode bar shows that a test runs and the selection is locked.
    /// </summary>
    public void AttachSaturation(SaturationTestViewModel sat)
    {
        Saturation = sat;
        sat.AttachToLiveSurvey(Selection, ssid => _accessPoints.Count(a => a.SSID == ssid));
        sat.SelectedAdapter = SelectedAdapter;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedAdapter) && !IsTestRunning) sat.SelectedAdapter = SelectedAdapter; };
        sat.TestRunningChanged += running =>
        {
            IsTestRunning = running;
            if (running) { SurveyMode = SurveyModes.Saturation; CenterView = CenterViews.Test; }
        };
        sat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SaturationTestViewModel.CurrentStep)) return;
            if (sat.IsResults) CenterView = CenterViews.Results;
            else if (sat.IsSetup && _centerView is CenterViews.Test or CenterViews.Results) CenterView = CenterViews.Spectrum;
        };
        OnPropertyChanged(nameof(Saturation));
    }

    private bool _isTestRunning;
    /// <summary>Set by the saturation view model while a test runs (mode bar status).</summary>
    public bool IsTestRunning { get => _isTestRunning; set { _isTestRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeStatusText)); } }

    private bool _isConnectionTestRunning;
    /// <summary>Set by the connection test view model while it runs (mode bar status).</summary>
    public bool IsConnectionTestRunning { get => _isConnectionTestRunning; set { _isConnectionTestRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeStatusText)); } }

    public string ModeStatusText
    {
        get
        {
            var parts = new List<string>();
            if (IsSessionActive) parts.Add(IsSessionPaused ? "⏸ CAPTURE PAUSED" : "● RECORDING");
            if (IsTestRunning) parts.Add("● TESTING SIGNAL · selection locked");
            if (IsConnectionTestRunning) parts.Add("● TESTING CONNECTION");
            return parts.Count == 0 ? "Monitoring · nothing recorded" : string.Join("   ", parts);
        }
    }

    /// <summary>The connection test attached to Live Survey (DR-0014).</summary>
    public ConnectionTestViewModel? Connection { get; private set; }

    /// <summary>
    /// Host the connection test in Live Survey: it uses the Live Survey adapter, its running/results steps drive the CONNECTION centre
    /// view, and "Also run a saturation test" selects the connected access point (its whole unit when grouped) and starts that test.
    /// </summary>
    public void AttachConnection(ConnectionTestViewModel conn)
    {
        Connection = conn;
        conn.SelectedAdapter = SelectedAdapter;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedAdapter) && !IsConnectionTestRunning) conn.SelectedAdapter = SelectedAdapter; };
        conn.TestRunningChanged += running =>
        {
            IsConnectionTestRunning = running;
            if (running) { SurveyMode = SurveyModes.Connection; CenterView = CenterViews.Connection; }
        };
        conn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ConnectionTestViewModel.CurrentStep)) return;
            if (conn.IsResults) CenterView = CenterViews.Connection;
        };
        conn.StartLinkedSaturation = StartLinkedSaturationAsync;
        OnPropertyChanged(nameof(Connection));
    }

    private async Task<Guid?> StartLinkedSaturationAsync(ConnectionTestViewModel c)
    {
        if (Saturation == null || IsTestRunning || c.Context?.Bssid is not { } bssid) return null;
        if (!Selection.IsEmpty && !c.Confirm("Replace the current network selection with the connected access point for the linked saturation test?", "Linked saturation test")) return null;
        Selection.Clear();
        if (UnitGrouper.UnitOf(bssid) is { } unitId && UnitGrouper.Get(unitId) is { } unit) Selection.ToggleUnit(unit);
        else Selection.SetBssid(c.Context.Ssid ?? "", bssid.ToUpperInvariant(), true);
        Saturation.Location = c.Location;
        Saturation.Latitude = c.Latitude;
        Saturation.Longitude = c.Longitude;
        Saturation.Building = c.Building;
        Saturation.Floor = c.Floor;
        Saturation.Zone = c.Zone;
        Saturation.TestName = (string.IsNullOrWhiteSpace(c.TestName) ? "Connection test" : c.TestName.Trim()) + " (signal)";
        Saturation.Notes = "Started together with a connection test (one timeline).";
        await Saturation.StartAsync();
        return Saturation.IsRunningStep ? Saturation.ActiveTestId : null;
    }

    private DateTime _lastConnectedRead = DateTime.MinValue;

    /// <summary>Marks the access point this PC is connected to in the explorer (CONNECTED badge); at most every 5 s.</summary>
    private async Task UpdateConnectedAsync()
    {
        if (SelectedAdapter is not { } adapter || (DateTime.UtcNow - _lastConnectedRead).TotalSeconds < 5) return;
        _lastConnectedRead = DateTime.UtcNow;
        try
        {
            var link = await _adapterProvider.ReadLinkAsync(adapter.Id);
            string? bssid = link?.Connected == true ? link.Bssid : null;
            Explorer.ConnectedBssid = bssid;
            if (Connection != null)
                Connection.UnitName = bssid != null && UnitGrouper.UnitOf(bssid) is { } id ? UnitGrouper.Get(id)?.Name : null;
        }
        catch (Exception ex) { StatusText = $"Could not read the current connection: {ex.Message}"; }
    }

    public bool ShowSpectrumGraph
    {
        get => _showSpectrumGraph;
        set
        {
            if (_showSpectrumGraph != value)
            {
                _showSpectrumGraph = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowTelemetryGrid));
            }
        }
    }

    public bool ShowTelemetryGrid
    {
        get => !_showSpectrumGraph;
        set
        {
            if (_showSpectrumGraph == value)
            {
                _showSpectrumGraph = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowSpectrumGraph));
            }
        }
    }

    public ObservableCollection<AccessPointDisplayItem> DisplayAccessPoints
    {
        get => _displayAccessPoints;
        set { _displayAccessPoints = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AccessPoint> FilteredAccessPoints
    {
        get
        {
            if (Selection.IsEmpty)
            {
                return _accessPoints;
            }
            return new ObservableCollection<AccessPoint>(_accessPoints.Where(ap => Selection.Matches(ap.SSID, ap.BSSID)));
        }
    }

    public ObservableCollection<LiveCaptureLogItem> ActiveSessionLogs { get; } = new();

    public ObservableCollection<LiveCaptureLogItem> FilteredActiveSessionLogs
    {
        get
        {
            if (Selection.IsEmpty)
            {
                return ActiveSessionLogs;
            }
            return new ObservableCollection<LiveCaptureLogItem>(ActiveSessionLogs.Where(log => Selection.Matches(log.SSID, log.BSSID)));
        }
    }

    /// <summary>Legacy item-check bridge: SSID view selects whole SSIDs, AP view selects fixed BSSIDs.</summary>
    private void OnItemCheckedChanged(AccessPointDisplayItem item)
    {
        string key = IsSsidView ? item.SSID : item.BSSID;
        if (item.IsChecked) _checkedKeys.Add(key); else _checkedKeys.Remove(key);
        // A check that already matches the selection is a no-op (an AP inside a whole SSID is selected already).
        if (!IsSsidView && item.IsChecked && Selection.IsExplicitlySelected(item.SSID, item.BSSID)) return;
        if (IsSsidView) Selection.SetSsid(item.SSID, item.IsChecked);
        else Selection.SetBssid(item.SSID, item.BSSID, item.IsChecked);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(AccessPoints));
        OnPropertyChanged(nameof(FilteredAccessPoints));
        OnPropertyChanged(nameof(FilteredActiveSessionLogs));
        OnPropertyChanged(nameof(IsSelectionAutoFocus));
        SpectrumChanged?.Invoke();
    }

    public void UpdateDisplayAccessPoints()
    {
        if (AccessPoints == null)
        {
            DisplayAccessPoints.Clear();
            return;
        }

        System.Collections.Generic.List<AccessPointDisplayItem> targets;

        if (IsSsidView)
        {
            targets = AccessPoints
                .GroupBy(ap => ap.SSID)
                .Select(g =>
                {
                    var strongest = g.OrderByDescending(ap => ap.RSSI).First();
                    var repAp = new AccessPoint(
                        BSSID: g.Count() > 1 ? $"{g.Count()} APs" : strongest.BSSID,
                        SSID: string.IsNullOrEmpty(g.Key) ? "<Hidden SSID>" : g.Key,
                        Hostname: strongest.Hostname,
                        Band: strongest.Band,
                        Channel: strongest.Channel,
                        ChannelWidth: strongest.ChannelWidth,
                        RSSI: strongest.RSSI,
                        NoiseFloor: strongest.NoiseFloor,
                        SNR: strongest.SNR,
                        Quality: strongest.Quality,
                        SecurityType: strongest.SecurityType,
                        NetworkType: strongest.NetworkType,
                        VendorOUI: strongest.VendorOUI,
                        SupportedRates: strongest.SupportedRates,
                        LastSeen: strongest.LastSeen
                    );
                    bool isChecked = Selection.IsWholeSsid(repAp.SSID);
                    return new AccessPointDisplayItem(repAp, isChecked, true, OnItemCheckedChanged);
                })
                .OrderByDescending(x => x.RSSI)
                .ToList();
        }
        else
        {
            targets = AccessPoints
                .OrderByDescending(ap => ap.RSSI)
                .Select(ap =>
                {
                    bool isChecked = Selection.IsExplicitlySelected(ap.SSID, ap.BSSID);
                    return new AccessPointDisplayItem(ap, isChecked, false, OnItemCheckedChanged);
                })
                .ToList();
        }

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            int existingIndex = -1;

            for (int j = i; j < DisplayAccessPoints.Count; j++)
            {
                string keyExisting = IsSsidView ? DisplayAccessPoints[j].SSID : DisplayAccessPoints[j].BSSID;
                string keyTarget = IsSsidView ? target.SSID : target.BSSID;

                if (keyExisting == keyTarget)
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existingItem = DisplayAccessPoints[existingIndex];
                existingItem.Update(target.AccessPoint, IsSsidView);

                bool isChecked = IsSsidView ? Selection.IsWholeSsid(target.SSID) : Selection.IsExplicitlySelected(target.SSID, target.BSSID);
                if (existingItem.IsChecked != isChecked)
                {
                    // Mirror the selection without firing the user-check callback: echoing it back added every AP of a
                    // whole SSID as a fixed BSSID (live defect 2026-10-16, scope recorded as Mixed).
                    existingItem.SetCheckedSilently(isChecked);
                }

                if (existingIndex != i)
                {
                    DisplayAccessPoints.Move(existingIndex, i);
                }
            }
            else
            {
                DisplayAccessPoints.Insert(i, target);
            }
        }

        while (DisplayAccessPoints.Count > targets.Count)
        {
            DisplayAccessPoints.RemoveAt(DisplayAccessPoints.Count - 1);
        }
    }

    // History Properties
    public ObservableCollection<CaptureSession> HistoricalSessions
    {
        get => _historicalSessions;
        set { _historicalSessions = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredHistoricalSessions)); }
    }

    public CaptureSession? SelectedHistorySession
    {
        get => _selectedHistorySession;
        set
        {
            if (_selectedHistorySession != value)
            {
                _selectedHistorySession = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedHistorySessionScopeOnly));
                OnPropertyChanged(nameof(SelectedHistorySessionNotesOnly));
                OnPropertyChanged(nameof(HasSelectedHistorySessionNotes));
                OnPropertyChanged(nameof(SelectedHistorySessionModeText));
                _ = LoadSessionSamplesAsync();
            }
        }
    }

    public string SelectedHistorySessionScopeOnly
    {
        get
        {
            if (SelectedHistorySession == null) return "";
            string scope = SelectedHistorySession.Scope;
            if (scope.Contains(" | Notes: "))
            {
                return scope.Split(" | Notes: ", 2)[0];
            }
            return scope;
        }
    }

    public string SelectedHistorySessionNotesOnly
    {
        get
        {
            if (SelectedHistorySession == null) return "";
            string scope = SelectedHistorySession.Scope;
            if (scope.Contains(" | Notes: "))
            {
                return scope.Split(" | Notes: ", 2)[1];
            }
            return "";
        }
    }

    public bool HasSelectedHistorySessionNotes => !string.IsNullOrWhiteSpace(SelectedHistorySessionNotesOnly);

    public string SelectedHistorySessionModeText
    {
        get
        {
            if (SelectedHistorySession == null) return "";
            return SelectedHistorySession.Mode == CaptureSessionMode.Stationary 
                ? "Stationary Capture" 
                : "Roaming Survey";
        }
    }

    public ObservableCollection<TelemetrySample> SelectedSessionSamples
    {
        get => _selectedSessionSamples;
        set { _selectedSessionSamples = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilteredSelectedSessionSamples)); }
    }

    public ObservableCollection<TelemetrySample> FilteredSelectedSessionSamples
    {
        get
        {
            var filtered = _selectedSessionSamples.Where(s => IsHistoryKeyChecked(s.BSSID));
            return new ObservableCollection<TelemetrySample>(filtered);
        }
    }

    public bool IsHistoryKeyChecked(string bssid)
    {
        return !_uncheckedHistoryKeys.Contains(bssid);
    }

    public void SetHistoryKeyChecked(string bssid, bool isChecked)
    {
        if (isChecked)
        {
            _uncheckedHistoryKeys.Remove(bssid);
        }
        else
        {
            _uncheckedHistoryKeys.Add(bssid);
        }
        OnPropertyChanged(nameof(FilteredSelectedSessionSamples));
        OnPropertyChanged(nameof(SelectedSessionSampleCount));
    }

    public int SelectedSessionSampleCount
    {
        get => _selectedSessionSamples.Count(s => IsHistoryKeyChecked(s.BSSID));
    }

    // History Filter Properties
    public DateTime? HistoryFilterFromDate
    {
        get => _historyFilterFromDate;
        set { _historyFilterFromDate = value; OnPropertyChanged(); }
    }

    public DateTime? HistoryFilterToDate
    {
        get => _historyFilterToDate;
        set { _historyFilterToDate = value; OnPropertyChanged(); }
    }

    public string HistoryFilterName
    {
        get => _historyFilterName;
        set { _historyFilterName = value; OnPropertyChanged(); }
    }

    public System.Collections.Generic.List<CaptureSession> SelectedHistorySessions
    {
        get => _selectedHistorySessions;
        set { _selectedHistorySessions = value; OnPropertyChanged(); }
    }

    public ObservableCollection<CaptureSession> FilteredHistoricalSessions
    {
        get
        {
            var filtered = _historicalSessions.AsEnumerable();

            if (_historyFilterFromDate.HasValue)
            {
                var from = _historyFilterFromDate.Value.Date;
                filtered = filtered.Where(s => s.StartTime >= from);
            }

            if (_historyFilterToDate.HasValue)
            {
                var to = _historyFilterToDate.Value.Date.AddDays(1); // inclusive end of day
                filtered = filtered.Where(s => s.StartTime < to);
            }

            if (!string.IsNullOrWhiteSpace(_historyFilterName))
            {
                var nameFilter = _historyFilterName.Trim();
                filtered = filtered.Where(s => s.Name != null && s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
            }

            return new ObservableCollection<CaptureSession>(filtered);
        }
    }
    // Hook for unit testing dialogs without UI blocks
    public Func<string, string, string, string?>? ShowSaveFileDialogHook { get; set; }

    // Commands
    public ICommand ScanCommand { get; }
    public ICommand GenerateApiKeyCommand { get; }
    public ICommand ApplyApiSettingsCommand { get; }
    public ICommand StartSessionCommand { get; }
    public ICommand PauseSessionCommand { get; }
    public ICommand ResumeSessionCommand { get; }
    public ICommand StopSessionCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportJsonCommand { get; }
    public ICommand ExportPdfCommand { get; }
    public ICommand ExportApiKeyCommand { get; }
    public ICommand DeleteApiKeyCommand { get; }
    public ICommand RefreshSessionsCommand { get; }
    public ICommand DeleteSelectedSessionCommand { get; }
    public ICommand SearchHistoryCommand { get; }
    public ICommand ClearHistoryFilterCommand { get; }
    public ICommand ExportHistoryCsvCommand { get; }
    public ICommand ExportHistoryJsonCommand { get; }
    public ICommand ExportHistoryPdfCommand { get; }

    public MainWindowViewModel(
        INetworkAdapterProvider adapterProvider, 
        ISessionManager sessionManager, 
        ISessionRepository sessionRepository,
        IExportService exportService,
        IRestApiServer apiServer,
        ISettingsRepository settingsRepository,
        IScanScheduler? scanScheduler = null)
    {
        _adapterProvider = adapterProvider;
        // Without an injected scheduler (tests/headless) scans run on demand only; the app injects the shared looping one.
        _scheduler = scanScheduler ?? new ScanScheduler(adapterProvider) { AutoScan = false };
        _scheduler.CycleCompleted += OnSchedulerCycle;
        // physical units (DR-0013): one grouper feeds the explorer's hardware view and resolves unit selections
        Selection.UnitResolver = UnitGrouper;
        Explorer = new NetworkExplorerViewModel(Selection, UnitGrouper);
        Selection.Changed += OnSelectionChanged;
        _sessionManager = sessionManager;
        _sessionRepository = sessionRepository;
        _exportService = exportService;
        _apiServer = apiServer;
        _apiServer.StateChanged += OnApiServerStateChanged;
        _settingsRepository = settingsRepository;
        UnitGrouper.OverridesChanged += o => _ = SaveUnitOverridesAsync(o);

        ScanCommand = new RelayCommand(async () => await PerformScanAsync());
        GenerateApiKeyCommand = new RelayCommand(GenerateApiKey);
        ApplyApiSettingsCommand = new RelayCommand(async () => await ApplyApiSettingsAsync());
        ExportApiKeyCommand = new RelayCommand(ExportApiKey);
        DeleteApiKeyCommand = new RelayCommand(DeleteApiKey);

        StartSessionCommand = new RelayCommand(StartCaptureSession);
        PauseSessionCommand = new RelayCommand(PauseCaptureSession);
        ResumeSessionCommand = new RelayCommand(ResumeCaptureSession);
        StopSessionCommand = new RelayCommand(StopCaptureSession);

        ExportCsvCommand = new RelayCommand(async () => await ExportSessionData("csv"));
        ExportJsonCommand = new RelayCommand(async () => await ExportSessionData("json"));
        ExportPdfCommand = new RelayCommand(async () => await ExportSessionData("pdf"));

        RefreshSessionsCommand = new RelayCommand(async () => await LoadHistoricalSessionsAsync());
        DeleteSelectedSessionCommand = new RelayCommand(async () => await DeleteSelectedSessionAsync());
        SearchHistoryCommand = new RelayCommand(SearchHistory);
        ClearHistoryFilterCommand = new RelayCommand(ClearHistoryFilter);
        ExportHistoryCsvCommand = new RelayCommand(async () => await ExportHistorySessionsAsync("csv"));
        ExportHistoryJsonCommand = new RelayCommand(async () => await ExportHistorySessionsAsync("json"));
        ExportHistoryPdfCommand = new RelayCommand(async () => await ExportHistorySessionsAsync("pdf"));

        // Load adapters and settings on initialization
        _ = LoadApiSettingsAsync();
        _ = LoadUnitOverridesAsync();
        _ = LoadAdaptersAsync();
        _ = LoadHistoricalSessionsAsync();
        RefreshDatabaseSize();
    }

    public async Task LoadAdaptersAsync()
    {
        try
        {
            StatusText = "Enumerating adapters...";
            var devices = await _adapterProvider.GetInterfacesAsync();
            Adapters.Clear();
            foreach (var d in devices.Where(x => x.DeviceType == NetworkDeviceType.Wireless_WiFi))
            {
                Adapters.Add(d);
            }
            StatusText = "Adapters loaded. Choose an adapter to start.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading adapters: {ex.Message}";
        }
    }

    private void StartScanTimer()
    {
        var adapter = SelectedAdapter;
        IsScanning = true;
        if (adapter == null || adapter.DeviceType == NetworkDeviceType.Wired_Ethernet)
        {
            _scheduler.SetAdapter(null);
            _ = PerformScanAsync();
            return;
        }
        _scheduler.ScanTimeout = ScanTimeout;
        _scheduler.SetAdapter(adapter.Id);
        _liveCadence ??= _scheduler.RequestCadence(ScanInterval);
    }

    private void StopScanTimer()
    {
        _liveCadence?.Dispose();
        _liveCadence = null;
        _scheduler.SetAdapter(null);
        IsScanning = false;
        AccessPoints.Clear();
        UpdateDisplayAccessPoints();
        Explorer.Update(Array.Empty<AccessPoint>(), false);
    }

    /// <summary>Scheduler callback (background thread): process on the UI thread when there is one.</summary>
    private void OnSchedulerCycle(ScheduledCycle cycle)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(new Action(() => _ = ProcessCycleAsync(cycle)));
        else _ = ProcessCycleAsync(cycle);
    }

    /// <summary>
    /// Runs one fresh-scan cycle (DR-0003). The AP list is always refreshed for display, but telemetry
    /// samples are only captured for entries that are demonstrably fresh: the cycle was confirmed
    /// fresh by Windows, or the entry's beacon age is within the freshness limit. Cached repeats are
    /// never stored as new samples (finding F01).
    /// </summary>
    public async Task PerformScanAsync()
    {
        var adapter = SelectedAdapter;
        if (adapter == null) return;

        ScheduledCycle? scheduled = null;
        await _scanGate.WaitAsync();
        try
        {
            if (adapter.DeviceType == NetworkDeviceType.Wired_Ethernet)
            {
                // Wired adapters carry no RF telemetry; fake RSSI/quality sentinels were removed (finding F09).
                var stats = await _adapterProvider.GetWiredStatsAsync(adapter.Id);
                StatusText = $"Wired interface ({stats.LinkStatus}). Speed: {stats.SpeedBitsPerSecond / 1000000} Mbps | Gateway latency: {stats.AvgPingLatencyMs:F1} ms. RF telemetry is only available on Wi-Fi adapters.";
                return;
            }

            StatusText = "Scanning RF spectrum (requesting a fresh scan)...";
            _scheduler.ScanTimeout = ScanTimeout;
            if (_scheduler.AdapterId != adapter.Id) _scheduler.SetAdapter(adapter.Id);
            scheduled = await _scheduler.ScanNowAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failure: {ex.Message}";
        }
        finally
        {
            _scanGate.Release();
        }
        if (scheduled != null) await ProcessCycleAsync(scheduled);
    }

    /// <summary>
    /// Processes one scheduled cycle exactly once (by sequence), whoever triggered it: live loop, FORCE SCAN,
    /// saturation test or discovery. Display always refreshes; capture stores only demonstrably fresh entries.
    /// </summary>
    public async Task ProcessCycleAsync(ScheduledCycle scheduled)
    {
        var adapter = SelectedAdapter;
        if (adapter == null || scheduled.AdapterId != adapter.Id) return;
        await _scanGate.WaitAsync();
        try
        {
            if (scheduled.Sequence <= _lastProcessedSeq) return;
            _lastProcessedSeq = scheduled.Sequence;
            var cycle = scheduled.Result;

            if (cycle.Status == ScanCycleStatus.Failed)
            {
                LastCycleText = $"Scan FAILED at {DateTime.Now:HH:mm:ss}: {cycle.ErrorMessage}";
                StatusText = $"Scan failure: {cycle.ErrorMessage}";
                if (IsSessionActive && !IsSessionPaused)
                {
                    _sessionManager.HandleScanError(new InvalidOperationException($"Capture gap: scan cycle failed ({cycle.ErrorCode}) {cycle.ErrorMessage}"));
                }
                return;
            }

            var freshnessLimitMs = (ScanInterval + TimeSpan.FromSeconds(5)).TotalMilliseconds;
            bool IsFresh(AccessPoint ap) =>
                ap.BeaconAgeMs is double age
                    ? age <= freshnessLimitMs
                    : cycle.Status == ScanCycleStatus.Fresh;

            var tempAPs = new ObservableCollection<AccessPoint>(cycle.AccessPoints);
            var batch = new System.Collections.Generic.List<TelemetrySample>();
            int skippedStale = 0;

            if (IsSessionActive && !IsSessionPaused)
            {
                foreach (var ap in cycle.AccessPoints)
                {
                    bool inScope = Selection.Matches(ap.SSID, ap.BSSID);
                    if (!inScope) continue;
                    if (!IsFresh(ap))
                    {
                        skippedStale++;
                        continue;
                    }

                    double variation = _sessionManager.CalculateJitterFor(ap.BSSID, ap.RSSI);
                    var sample = new TelemetrySample(
                        Timestamp: DateTime.UtcNow,
                        SurveyPoint: SurveyPoint,
                        BSSID: ap.BSSID,
                        SSID: ap.SSID,
                        Band: ap.Band,
                        Channel: ap.Channel,
                        RSSI: ap.RSSI,
                        SNR: ap.SNR,
                        Quality: ap.Quality,
                        Jitter: variation,
                        NoiseFloor: ap.NoiseFloor
                    );
                    batch.Add(sample);

                    string fluctText = "-";
                    string fluctColor = "#888888";
                    if (_lastRssiByBssid.TryGetValue(ap.BSSID, out int lastRssi))
                    {
                        int diff = ap.RSSI - lastRssi;
                        if (diff > 0) { fluctText = $"▲ +{diff} dB"; fluctColor = "#00FF66"; }
                        else if (diff < 0) { fluctText = $"▼ {diff} dB"; fluctColor = "#FF3300"; }
                        else { fluctText = "• 0 dB"; fluctColor = "#888888"; }
                    }
                    _lastRssiByBssid[ap.BSSID] = ap.RSSI;

                    ActiveSessionLogs.Insert(0, new LiveCaptureLogItem(sample, fluctText, fluctColor));
                    while (ActiveSessionLogs.Count > 200)
                    {
                        ActiveSessionLogs.RemoveAt(ActiveSessionLogs.Count - 1);
                    }
                }

                // One ordered, batched write per cycle (findings F02/F03).
                _sessionManager.SaveSamples(batch);
            }

            AccessPoints = tempAPs;
            LastCycleText = $"Last scan {DateTime.Now:HH:mm:ss}: {cycle.Status.ToString().ToUpperInvariant()} in {cycle.DurationMs:F0} ms, {cycle.AccessPoints.Count} APs"
                            + (cycle.Status == ScanCycleStatus.Stale ? $" ({cycle.ErrorMessage})" : "");
            StatusText = $"Scan complete. Found {AccessPoints.Count} Access Points."
                         + (IsSessionActive && !IsSessionPaused ? $" Captured {batch.Count} fresh samples" + (skippedStale > 0 ? $", skipped {skippedStale} stale." : ".") : "");

            UpdateDisplayAccessPoints();
            UnitGrouper.Observe(cycle.AccessPoints, DateTime.UtcNow);
            Explorer.Update(cycle.AccessPoints, cycle.Status == ScanCycleStatus.Fresh);
            _ = UpdateConnectedAsync();
            if (cycle.Status == ScanCycleStatus.Fresh)
            {
                _waterfall.AddFirst(cycle.AccessPoints);
                while (_waterfall.Count > WaterfallDepth) _waterfall.RemoveLast();
            }
            OnPropertyChanged(nameof(ActiveSessionLogs));
            OnPropertyChanged(nameof(FilteredActiveSessionLogs));
            SpectrumChanged?.Invoke();
            _apiServer.UpdateLiveAccessPoints(AccessPoints);
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failure: {ex.Message}";
        }
        finally
        {
            _scanGate.Release();
        }
    }

    // Capture lifecycle methods
    private void StartCaptureSession()
    {
        if (SelectedAdapter == null)
        {
            StatusText = "Error: Choose a hardware adapter first.";
            return;
        }

        var dialog = new SessionCaptureDialog();
        // Pre-fill if we have existing values
        dialog.TxtSessionName.Text = SessionName ?? "";
        dialog.TxtFacility.Text = FacilityName ?? "";
        dialog.TxtScope.Text = SessionScope ?? "";
        dialog.TxtSurveyPoint.Text = SurveyPoint ?? "";

        if (dialog.ShowDialog() == true)
        {
            SessionName = dialog.SessionName;
            FacilityName = dialog.Facility;
            SessionScope = dialog.Scope;
            SurveyPoint = dialog.SurveyPoint;
            SessionNotes = dialog.Notes;
            SessionModeText = dialog.Mode == CaptureSessionMode.Stationary ? "Stationary Capture" : "Roaming Survey";

            // Need notes later for PDF generation perhaps, saving to scope for now if no notes field in manager. 
            // The ISessionManager currently takes (name, scope, facility, device, mode)
            var scopeWithNotes = string.IsNullOrWhiteSpace(dialog.Notes) ? SessionScope : $"{SessionScope} | Notes: {dialog.Notes}";

            try
            {
                _sessionManager.ActiveSurveyPoint = SurveyPoint;
                _sessionManager.StartSession(SessionName, scopeWithNotes, FacilityName, SelectedAdapter, dialog.Mode);
                IsSessionActive = true;
                IsSessionPaused = false;
                ActiveSessionLogs.Clear();
                _lastRssiByBssid.Clear();
                ShowSpectrumGraph = true;
                OnPropertyChanged(nameof(ActiveSessionLogs));
                OnPropertyChanged(nameof(FilteredActiveSessionLogs));
                StatusText = $"Session '{SessionName}' ({dialog.Mode}) started successfully.";
                RefreshDatabaseSize();
            }
            catch (Exception ex)
            {
                StatusText = $"Error starting session: {ex.Message}";
            }
        }
    }

    private void PauseCaptureSession()
    {
        _sessionManager.PauseSession();
        IsSessionPaused = true;
        StatusText = "Session paused.";
    }

    private void ResumeCaptureSession()
    {
        _sessionManager.ResumeSession();
        IsSessionPaused = false;
        StatusText = "Session resumed.";
    }

    private void StopCaptureSession()
    {
        var completedSession = _sessionManager.ActiveSession;
        _sessionManager.StopSession();
        IsSessionActive = false;
        IsSessionPaused = false;
        StatusText = "Session completed and saved to local storage.";
        RefreshDatabaseSize();

        // Show end-of-capture export dialog
        if (completedSession != null)
        {
            var dialog = new EndOfCaptureExportDialog();
            var duration = (completedSession.EndTime ?? DateTime.UtcNow) - completedSession.StartTime;
            dialog.SetSessionInfo(completedSession.Name, completedSession.FacilityName, duration);
            dialog.Owner = System.Windows.Application.Current.MainWindow;

            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.ChosenFormat))
            {
                _ = ExportSessionData(dialog.ChosenFormat, completedSession.SessionId);
            }
        }
    }

    private string? PromptForSaveFile(string filter, string defaultFileName, string defaultExt)
    {
        if (ShowSaveFileDialogHook != null)
        {
            return ShowSaveFileDialogHook(filter, defaultExt, defaultFileName);
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultFileName,
            DefaultExt = defaultExt
        };

        if (dialog.ShowDialog() == true)
        {
            return dialog.FileName;
        }

        return null;
    }

    private async Task ExportSessionData(string format)
    {
        var activeSession = _sessionManager.ActiveSession;
        Guid sessionId;

        if (activeSession != null)
        {
            sessionId = activeSession.SessionId;
        }
        else
        {
            // If no active session, look up the last saved session from database
            try
            {
                var factory = App.ServiceProvider.GetService(typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<WlanDbContext>)) 
                    as Microsoft.EntityFrameworkCore.IDbContextFactory<WlanDbContext>;
                if (factory == null)
                {
                    StatusText = "Error: Database service not available.";
                    return;
                }
                using var context = factory.CreateDbContext();
                var lastSession = context.Sessions.OrderByDescending(s => s.StartTime).FirstOrDefault();
                if (lastSession == null)
                {
                    StatusText = "Error: No sessions found in database to export.";
                    return;
                }
                sessionId = lastSession.SessionId;
            }
            catch (Exception ex)
            {
                StatusText = $"Error checking database: {ex.Message}";
                return;
            }
        }

        string filter;
        string defaultFileName;
        string defaultExt;
        if (format == "csv")
        {
            filter = "CSV File (*.csv)|*.csv";
            defaultFileName = $"Survey_Export_{DateTime.Now:yyyyMMdd}.csv";
            defaultExt = ".csv";
        }
        else if (format == "json")
        {
            filter = "JSON File (*.json)|*.json";
            defaultFileName = $"Survey_Export_{DateTime.Now:yyyyMMdd}.json";
            defaultExt = ".json";
        }
        else
        {
            filter = "PDF Report (*.pdf)|*.pdf";
            defaultFileName = $"Site_Survey_Report_{DateTime.Now:yyyyMMdd}.pdf";
            defaultExt = ".pdf";
        }

        string? fileName = PromptForSaveFile(filter, defaultFileName, defaultExt);
        if (!string.IsNullOrEmpty(fileName))
        {
            try
            {
                StatusText = $"Exporting session data as {format.ToUpper()}...";
                if (format == "csv")
                {
                    await _exportService.ExportToCsvAsync(fileName, sessionId, GetExportFields());
                }
                else if (format == "json")
                {
                    await _exportService.ExportToJsonAsync(fileName, sessionId, GetExportFields());
                }
                else
                {
                    var meta = new ExportMetadata(ExportClientName, ExportSurveyorName, ExportNotes);
                    await _exportService.ExportToPdfAsync(fileName, sessionId, meta, GetExportFields());
                }
                StatusText = $"Export complete: {Path.GetFileName(fileName)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }
        }
    }

    private async Task ExportSessionData(string format, Guid sessionId)
    {
        string filter;
        string defaultFileName;
        string defaultExt;
        if (format == "csv")
        {
            filter = "CSV File (*.csv)|*.csv";
            defaultFileName = $"Survey_Export_{DateTime.Now:yyyyMMdd}.csv";
            defaultExt = ".csv";
        }
        else if (format == "json")
        {
            filter = "JSON File (*.json)|*.json";
            defaultFileName = $"Survey_Export_{DateTime.Now:yyyyMMdd}.json";
            defaultExt = ".json";
        }
        else
        {
            filter = "PDF Report (*.pdf)|*.pdf";
            defaultFileName = $"Site_Survey_Report_{DateTime.Now:yyyyMMdd}.pdf";
            defaultExt = ".pdf";
        }

        string? fileName = PromptForSaveFile(filter, defaultFileName, defaultExt);
        if (!string.IsNullOrEmpty(fileName))
        {
            try
            {
                StatusText = $"Exporting session data as {format.ToUpper()}...";
                if (format == "csv")
                {
                    await _exportService.ExportToCsvAsync(fileName, sessionId, GetExportFields());
                }
                else if (format == "json")
                {
                    await _exportService.ExportToJsonAsync(fileName, sessionId, GetExportFields());
                }
                else
                {
                    var meta = new ExportMetadata(ExportClientName, ExportSurveyorName, ExportNotes);
                    await _exportService.ExportToPdfAsync(fileName, sessionId, meta, GetExportFields());
                }
                StatusText = $"Export complete: {Path.GetFileName(fileName)}";
            }
            catch (Exception ex)
            {
                StatusText = $"Export failed: {ex.Message}";
            }
        }
    }

    private void SearchHistory()
    {
        OnPropertyChanged(nameof(FilteredHistoricalSessions));
    }

    private void ClearHistoryFilter()
    {
        HistoryFilterFromDate = null;
        HistoryFilterToDate = null;
        HistoryFilterName = "";
        OnPropertyChanged(nameof(FilteredHistoricalSessions));
    }

    private async Task ExportHistorySessionsAsync(string format)
    {
        var sessions = _selectedHistorySessions;
        if (sessions == null || !sessions.Any())
        {
            if (SelectedHistorySession != null)
            {
                sessions = new List<CaptureSession> { SelectedHistorySession };
            }
            else
            {
                StatusText = "Error: Select one or more sessions to export.";
                return;
            }
        }

        var sessionIds = sessions.Select(s => s.SessionId).ToList();

        if (format == "pdf")
        {
            // PDF exports one file per session — prompt for each
            foreach (var session in sessions)
            {
                string filter = "PDF Report (*.pdf)|*.pdf";
                string defaultFileName = $"Survey_Report_{session.Name.Replace(" ", "_")}_{session.StartTime:yyyyMMdd}.pdf";
                string defaultExt = ".pdf";

                string? fileName = PromptForSaveFile(filter, defaultFileName, defaultExt);
                if (!string.IsNullOrEmpty(fileName))
                {
                    try
                    {
                        StatusText = $"Exporting PDF for '{session.Name}'...";
                        var meta = new ExportMetadata(ExportClientName, ExportSurveyorName, ExportNotes);
                        await _exportService.ExportToPdfAsync(fileName, session.SessionId, meta, GetExportFields());
                        StatusText = $"PDF exported: {Path.GetFileName(fileName)}";
                    }
                    catch (Exception ex)
                    {
                        StatusText = $"Export failed: {ex.Message}";
                    }
                }
            }
        }
        else
        {
            // CSV and JSON support multi-session batch export
            string filter;
            string defaultFileName;
            string defaultExt;
            if (format == "csv")
            {
                filter = "CSV File (*.csv)|*.csv";
                defaultFileName = $"Multi_Session_Export_{DateTime.Now:yyyyMMdd}.csv";
                defaultExt = ".csv";
            }
            else
            {
                filter = "JSON File (*.json)|*.json";
                defaultFileName = $"Multi_Session_Export_{DateTime.Now:yyyyMMdd}.json";
                defaultExt = ".json";
            }

            string? fileName = PromptForSaveFile(filter, defaultFileName, defaultExt);
            if (!string.IsNullOrEmpty(fileName))
            {
                try
                {
                    StatusText = $"Exporting {sessionIds.Count} sessions as {format.ToUpper()}...";
                    if (format == "csv")
                    {
                        await _exportService.ExportToCsvAsync(fileName, sessionIds, GetExportFields());
                    }
                    else
                    {
                        await _exportService.ExportToJsonAsync(fileName, sessionIds, GetExportFields());
                    }
                    StatusText = $"Export complete: {Path.GetFileName(fileName)} ({sessionIds.Count} sessions)";
                }
                catch (Exception ex)
                {
                    StatusText = $"Export failed: {ex.Message}";
                }
            }
        }
    }

    private void RefreshDatabaseSize()
    {
        try
        {
            var fileInfo = new FileInfo(AppPaths.DatabasePath);
            if (fileInfo.Exists)
            {
                double sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                DatabaseSizeText = $"Database Size: {sizeMB:F2} MB";
            }
            else
            {
                DatabaseSizeText = "Database Size: 0.0 MB";
            }
        }
        catch (Exception)
        {
            DatabaseSizeText = "Database Size: Unavailable";
        }
    }

    /// <summary>Settings key holding the user's physical-unit corrections (JSON, DR-0013).</summary>
    public const string UnitOverridesSettingKey = "PhysicalUnitOverrides";

    private async Task LoadUnitOverridesAsync()
    {
        try
        {
            var json = await _settingsRepository.GetSettingAsync(UnitOverridesSettingKey);
            if (string.IsNullOrWhiteSpace(json)) return;
            var o = System.Text.Json.JsonSerializer.Deserialize<UnitOverrides>(json);
            UnitGrouper.LoadOverrides(o == null ? null : new UnitOverrides
            {
                Assignments = new Dictionary<string, string>(o.Assignments, StringComparer.OrdinalIgnoreCase),
                Names = new Dictionary<string, string>(o.Names, StringComparer.Ordinal)
            });
        }
        catch (Exception ex)
        {
            // corrupt or unreadable corrections: automatic grouping still works; never block start-up
            StatusText = $"Access-point grouping corrections could not be loaded ({ex.Message}); automatic grouping is used.";
        }
    }

    private async Task SaveUnitOverridesAsync(UnitOverrides o)
    {
        try
        {
            await _settingsRepository.SaveSettingAsync(UnitOverridesSettingKey, System.Text.Json.JsonSerializer.Serialize(o));
        }
        catch (Exception ex)
        {
            StatusText = $"Access-point grouping correction not saved: {ex.Message}";
        }
    }

    private async Task LoadApiSettingsAsync()
    {
        try
        {
            var enabledStr = await _settingsRepository.GetSettingAsync("ApiEnabled");
            ApiEnabled = enabledStr == "true";

            var portStr = await _settingsRepository.GetSettingAsync("ApiPort");
            ApiPort = string.IsNullOrEmpty(portStr) ? "5005" : portStr;

            var key = await _settingsRepository.GetSettingAsync("ApiKey");
            ApiKey = key ?? "";

            UpdateApiServerStatusText();
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading API settings: {ex.Message}";
        }
    }

    private void UpdateApiServerStatusText()
    {
        if (_apiServer.IsRunning)
            ApiServerStatusText = $"● REST API: RUNNING on http://localhost:{_apiServer.Port}/";
        else if (!string.IsNullOrEmpty(_apiServer.LastError))
            ApiServerStatusText = $"✕ REST API: NOT RUNNING - {_apiServer.LastError}";
        else if (ApiEnabled)
            ApiServerStatusText = "○ REST API: enabled but not running - click APPLY & SAVE CHANGES to start it";
        else
            ApiServerStatusText = "○ REST API: disabled";
    }

    /// <summary>Keeps the label true to the server (it starts in the background at app start; 1.3 live finding).</summary>
    private void OnApiServerStateChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) UpdateApiServerStatusText();
        else d.BeginInvoke(new Action(UpdateApiServerStatusText));
    }

    // ---- API key display and export (1.3 live finding: export was silent and wrote to a shared folder) ----
    private bool _showApiKey;
    public bool ShowApiKey { get => _showApiKey; set { _showApiKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayedApiKey)); OnPropertyChanged(nameof(ShowApiKeyLabel)); } }
    public string ShowApiKeyLabel => _showApiKey ? "HIDE" : "SHOW";
    /// <summary>Masked by default so the key does not end up in screenshots.</summary>
    public string DisplayedApiKey => string.IsNullOrEmpty(ApiKey) ? "(no key - click GENERATE)"
        : _showApiKey ? ApiKey : ApiKey.Length > 12 ? ApiKey[..8] + new string('•', ApiKey.Length - 12) + ApiKey[^4..] : new string('•', ApiKey.Length);
    private string _apiExportStatus = "";
    public string ApiExportStatus { get => _apiExportStatus; set { _apiExportStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasApiExportStatus)); } }
    public bool HasApiExportStatus => !string.IsNullOrEmpty(_apiExportStatus);
    public string? LastApiExportPath { get; private set; }
    /// <summary>Save dialog hook (replaced in tests): suggested path in, chosen path or null out.</summary>
    public Func<string, string?> ApiKeySaveDialog { get; set; } = suggested =>
    {
        // Never open a modal dialog without a running WPF app (headless/tests): it would block forever.
        if (System.Windows.Application.Current == null) return null;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export WirelessScanner API key", Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = Path.GetFileName(suggested), InitialDirectory = Path.GetDirectoryName(suggested), AddExtension = true, DefaultExt = ".md"
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    };
    public Action<string> CopyToClipboard { get; set; } = text => System.Windows.Clipboard.SetText(text);
    public ICommand ToggleShowApiKeyCommand => _toggleShowApiKey ??= new RelayCommand(() => ShowApiKey = !ShowApiKey);
    private ICommand? _toggleShowApiKey;
    public ICommand CopyApiKeyCommand => _copyApiKey ??= new RelayCommand(() =>
    {
        if (string.IsNullOrEmpty(ApiKey)) { ApiExportStatus = "Generate an API key first."; return; }
        try { CopyToClipboard(ApiKey); ApiExportStatus = "API key copied to the clipboard."; }
        catch (Exception ex) { ApiExportStatus = $"Could not copy: {ex.Message}"; }
    });
    private ICommand? _copyApiKey;
    public ICommand OpenApiExportFolderCommand => _openApiExportFolder ??= new RelayCommand(() =>
    {
        if (LastApiExportPath == null || !File.Exists(LastApiExportPath)) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LastApiExportPath}\""); } catch { /* best effort */ }
    });
    private ICommand? _openApiExportFolder;

    private void GenerateApiKey()
    {
        byte[] keyBytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(keyBytes);
        ApiKey = "ws_live_" + Convert.ToHexString(keyBytes).ToLowerInvariant();
        StatusText = "New API key generated. Click 'Apply Changes' to activate.";
    }

    private void ExportApiKey()
    {
        if (string.IsNullOrEmpty(ApiKey))
        {
            StatusText = "Error: Generate an API key first.";
            return;
        }

        try
        {
            // Default to the user's own Documents (per-user permissions), not the machine-wide data folder.
            string suggested = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WirelessScanner", "API", "WirelessScanner-API-key.md");
            Directory.CreateDirectory(Path.GetDirectoryName(suggested)!);
            string? filePath = ApiKeySaveDialog(suggested);
            if (filePath == null)
            {
                ApiExportStatus = "Export cancelled.";
                return;
            }
            var content = $@"# Wireless Scanner REST API Credentials

This file contains the security credentials and access details for the Wireless Scanner REST API.

## API Key Details
- **Active Key**: `{ApiKey}`
- **Generated On**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
- **Server Port**: {ApiPort}
- **Status**: {(ApiEnabled && _apiServer.IsRunning ? "Active" : "Configuration Saved")}

## Usage Examples

### Shell / curl
```bash
curl -H ""X-API-Key: {ApiKey}"" http://localhost:{ApiPort}/api/live
```

### Python
```python
import requests
headers = {{""X-API-Key"": ""{ApiKey}""}}
response = requests.get(""http://localhost:{ApiPort}/api/live"", headers=headers)
print(response.json())
```
";
            File.WriteAllText(filePath, content, System.Text.Encoding.UTF8);
            LastApiExportPath = filePath;
            ApiExportStatus = $"API key exported to {filePath}. Keep this file private: anyone who can read it can query the API.";
            StatusText = ApiExportStatus;
        }
        catch (Exception ex)
        {
            ApiExportStatus = $"Failed to export API key: {ex.Message}";
            StatusText = ApiExportStatus;
        }
    }

    private void DeleteApiKey()
    {
        ApiKey = "";
        StatusText = "API key cleared. Click 'Apply Changes' to save.";

        // Remove copies of the old key this app wrote: the pre-1.3 shared file and this session's export.
        var removed = new List<string>();
        try
        {
            foreach (var path in new[] { Path.Combine(AppPaths.ApiDirectory, "api_key.md"), LastApiExportPath })
            {
                if (path != null && File.Exists(path)) { File.Delete(path); removed.Add(path); }
            }
            LastApiExportPath = null;
        }
        catch (Exception ex)
        {
            StatusText = $"API key cleared. (Failed to delete an exported key file: {ex.Message})";
        }
        ApiExportStatus = "API key cleared. Click APPLY & SAVE CHANGES to save." +
            (removed.Count > 0 ? $" Deleted exported key file(s): {string.Join(", ", removed)}." : "") +
            " Copies you saved elsewhere still contain the old key; it stops working once you apply a new one.";
    }

    private async Task ApplyApiSettingsAsync()
    {
        if (!int.TryParse(ApiPort, out int port) || port < 1024 || port > 65535)
        {
            StatusText = "Validation Error: Port must be a number between 1024 and 65535.";
            return;
        }

        if (ApiEnabled && string.IsNullOrEmpty(ApiKey))
        {
            StatusText = "Validation Error: API Key must be generated before enabling the REST API.";
            return;
        }

        try
        {
            await _settingsRepository.SaveSettingAsync("ApiEnabled", ApiEnabled ? "true" : "false");
            await _settingsRepository.SaveSettingAsync("ApiPort", ApiPort);
            await _settingsRepository.SaveSettingAsync("ApiKey", ApiKey);

            if (ApiEnabled)
            {
                StatusText = "Starting REST API Server...";
                await _apiServer.StartAsync(port, ApiKey);
            }
            else
            {
                StatusText = "Stopping REST API Server...";
                await _apiServer.StopAsync();
            }

            UpdateApiServerStatusText();
            StatusText = ApiEnabled
                ? $"REST API settings saved; server running on port {port}. Use EXPORT or COPY to hand the key to a script."
                : "REST API settings saved; server stopped.";
            ApiExportStatus = StatusText;
        }
        catch (Exception ex)
        {
            UpdateApiServerStatusText();
            StatusText = $"Failed to apply REST API settings: {_apiServer.LastError ?? ex.Message}";
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public async Task LoadHistoricalSessionsAsync()
    {
        try
        {
            var sessions = await _sessionRepository.GetAllSessionsAsync();
            Action updateAction = () =>
            {
                HistoricalSessions.Clear();
                foreach (var session in sessions)
                {
                    HistoricalSessions.Add(session);
                }
            };

            if (System.Windows.Application.Current == null)
            {
                updateAction();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(updateAction);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading history: {ex.Message}";
        }
    }

    private async Task LoadSessionSamplesAsync()
    {
        if (SelectedHistorySession == null)
        {
            Action clearAction = () =>
            {
                SelectedSessionSamples.Clear();
                OnPropertyChanged(nameof(SelectedSessionSampleCount));
                OnPropertyChanged(nameof(FilteredSelectedSessionSamples));
            };

            if (System.Windows.Application.Current == null)
            {
                clearAction();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(clearAction);
            }
            return;
        }

        try
        {
            var samples = await _sessionRepository.GetSamplesAsync(SelectedHistorySession.SessionId);
            Action updateAction = () =>
            {
                _uncheckedHistoryKeys.Clear();
                SelectedSessionSamples.Clear();
                foreach (var sample in samples)
                {
                    SelectedSessionSamples.Add(sample);
                }
                OnPropertyChanged(nameof(FilteredSelectedSessionSamples));
                OnPropertyChanged(nameof(SelectedSessionSampleCount));
            };

            if (System.Windows.Application.Current == null)
            {
                updateAction();
            }
            else
            {
                System.Windows.Application.Current.Dispatcher.Invoke(updateAction);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading session samples: {ex.Message}";
        }
    }

    private async Task DeleteSelectedSessionAsync()
    {
        if (SelectedHistorySession == null) return;

        try
        {
            await _sessionRepository.DeleteSessionAsync(SelectedHistorySession.SessionId);
            StatusText = $"Deleted session '{SelectedHistorySession.Name}'";
            SelectedHistorySession = null;
            await LoadHistoricalSessionsAsync();
            RefreshDatabaseSize();
        }
        catch (Exception ex)
        {
            StatusText = $"Error deleting session: {ex.Message}";
        }
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();

    public void Execute(object? parameter) => _execute();
}

public class AccessPointDisplayItem : INotifyPropertyChanged
{
    private readonly Action<AccessPointDisplayItem> _onCheckedChanged;
    private bool _isChecked;
    private AccessPoint _accessPoint;
    private bool _isSsidView;

    public AccessPoint AccessPoint => _accessPoint;
    public bool IsSsidView => _isSsidView;

    public AccessPointDisplayItem(AccessPoint ap, bool isChecked, bool isSsidView, Action<AccessPointDisplayItem> onCheckedChanged)
    {
        _accessPoint = ap;
        _isChecked = isChecked;
        _isSsidView = isSsidView;
        _onCheckedChanged = onCheckedChanged;
    }

    public void Update(AccessPoint ap, bool isSsidView)
    {
        _accessPoint = ap;
        _isSsidView = isSsidView;
        OnPropertyChanged(nameof(AccessPoint));
        OnPropertyChanged(nameof(IsSsidView));
        OnPropertyChanged(nameof(SSID));
        OnPropertyChanged(nameof(BSSID));
        OnPropertyChanged(nameof(Band));
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(RSSI));
        OnPropertyChanged(nameof(Quality));
        OnPropertyChanged(nameof(SecurityType));
        OnPropertyChanged(nameof(VendorOUI));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SubheaderText));
        OnPropertyChanged(nameof(DetailsText));
        OnPropertyChanged(nameof(TelemetryText));
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged();
                _onCheckedChanged(this);
            }
        }
    }

    /// <summary>Reflect the shared selection in this item without treating it as a user check.</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public string SSID => AccessPoint.SSID;
    public string BSSID => AccessPoint.BSSID;
    public string Band => AccessPoint.Band;
    public int Channel => AccessPoint.Channel;
    public int RSSI => AccessPoint.RSSI;
    public int Quality => AccessPoint.Quality;
    public string SecurityType => AccessPoint.SecurityType;
    public string? VendorOUI => AccessPoint.VendorOUI;

    public string HeaderText => _isSsidView ? SSID : BSSID;
    public string SubheaderText => _isSsidView ? "SSID Network Group" : $"SSID: {SSID}";
    public string DetailsText => _isSsidView ? BSSID : (VendorOUI ?? "Unknown Vendor");
    public string TelemetryText => _isSsidView 
        ? $"CH {Channel} | RSSI: {RSSI} dBm | Qual: {Quality}%"
        : $"CH {Channel} | RSSI: {RSSI} dBm | Qual: {Quality}% | SNR: {AccessPoint.SNR} dB | Host: {AccessPoint.Hostname ?? "N/A"}";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class LiveCaptureLogItem
{
    private static readonly System.Windows.Media.BrushConverter _brushConverter = new System.Windows.Media.BrushConverter();

    public DateTime Timestamp { get; }
    public string SSID { get; }
    public string BSSID { get; }
    public string Band { get; }
    public int Channel { get; }
    public int RSSI { get; }
    public int SNR { get; }
    public int Quality { get; }
    public double Jitter { get; }
    public string FluctuationText { get; }
    public System.Windows.Media.Brush FluctuationBrush { get; }

    public LiveCaptureLogItem(TelemetrySample sample, string fluctuationText, string fluctuationColor)
    {
        Timestamp = sample.Timestamp;
        SSID = sample.SSID;
        BSSID = sample.BSSID;
        Band = sample.Band;
        Channel = sample.Channel;
        RSSI = sample.RSSI;
        SNR = sample.SNR;
        Quality = sample.Quality;
        Jitter = sample.Jitter;
        FluctuationText = fluctuationText;

        System.Windows.Media.Brush? brush = null;
        try
        {
            brush = _brushConverter.ConvertFromString(fluctuationColor) as System.Windows.Media.Brush;
        }
        catch { }
        FluctuationBrush = brush ?? System.Windows.Media.Brushes.Gray;
    }
}
