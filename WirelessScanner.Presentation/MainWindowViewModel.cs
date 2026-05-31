using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private readonly DispatcherTimer _scanTimer;

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
        }
    }

    public bool IsSessionPaused
    {
        get => _isSessionPaused;
        set { _isSessionPaused = value; OnPropertyChanged(); }
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
        set { _apiEnabled = value; OnPropertyChanged(); }
    }

    public string ApiPort
    {
        get => _apiPort;
        set { _apiPort = value; OnPropertyChanged(); }
    }

    public string ApiKey
    {
        get => _apiKey;
        set { _apiKey = value; OnPropertyChanged(); }
    }

    public string ApiServerStatusText
    {
        get => _apiServerStatusText;
        set { _apiServerStatusText = value; OnPropertyChanged(); }
    }

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
                UpdateDisplayAccessPoints();
                OnPropertyChanged(nameof(FilteredActiveSessionLogs));
            }
        }
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
            if (_checkedKeys == null || !_checkedKeys.Any())
            {
                return _accessPoints;
            }

            var filtered = _accessPoints.Where(ap =>
            {
                if (IsSsidView)
                {
                    return _checkedKeys.Contains(ap.SSID);
                }
                else
                {
                    return _checkedKeys.Contains(ap.BSSID);
                }
            });

            return new ObservableCollection<AccessPoint>(filtered);
        }
    }

    public ObservableCollection<LiveCaptureLogItem> ActiveSessionLogs { get; } = new();

    public ObservableCollection<LiveCaptureLogItem> FilteredActiveSessionLogs
    {
        get
        {
            if (_checkedKeys == null || !_checkedKeys.Any())
            {
                return ActiveSessionLogs;
            }

            var filtered = ActiveSessionLogs.Where(log =>
            {
                if (IsSsidView)
                {
                    return _checkedKeys.Contains(log.SSID);
                }
                else
                {
                    return _checkedKeys.Contains(log.BSSID);
                }
            });

            return new ObservableCollection<LiveCaptureLogItem>(filtered);
        }
    }

    private void OnItemCheckedChanged(AccessPointDisplayItem item)
    {
        string key = IsSsidView ? item.SSID : item.BSSID;
        if (item.IsChecked)
        {
            _checkedKeys.Add(key);
        }
        else
        {
            _checkedKeys.Remove(key);
        }
        // Notify of change to trigger graph redraw and grid updates
        OnPropertyChanged(nameof(AccessPoints));
        OnPropertyChanged(nameof(FilteredAccessPoints));
        OnPropertyChanged(nameof(FilteredActiveSessionLogs));
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
                    bool isChecked = _checkedKeys.Contains(repAp.SSID);
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
                    bool isChecked = _checkedKeys.Contains(ap.BSSID);
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

                bool isChecked = IsSsidView ? _checkedKeys.Contains(target.SSID) : _checkedKeys.Contains(target.BSSID);
                if (existingItem.IsChecked != isChecked)
                {
                    existingItem.IsChecked = isChecked;
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
                ? "Stationary Saturation" 
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
        ISettingsRepository settingsRepository)
    {
        _adapterProvider = adapterProvider;
        _sessionManager = sessionManager;
        _sessionRepository = sessionRepository;
        _exportService = exportService;
        _apiServer = apiServer;
        _settingsRepository = settingsRepository;
        
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

        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _scanTimer.Tick += async (s, e) => await PerformScanAsync();

        // Load adapters and settings on initialization
        _ = LoadApiSettingsAsync();
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
        IsScanning = true;
        _scanTimer.Start();
        _ = PerformScanAsync();
    }

    private void StopScanTimer()
    {
        IsScanning = false;
        _scanTimer.Stop();
        AccessPoints.Clear();
        UpdateDisplayAccessPoints();
    }

    public async Task PerformScanAsync()
    {
        if (SelectedAdapter == null) return;

        try
        {
            StatusText = "Scanning RF spectrum...";
            
            if (SelectedAdapter.DeviceType == NetworkDeviceType.Wired_Ethernet)
            {
                var stats = await _adapterProvider.GetWiredStatsAsync(SelectedAdapter.Id);
                var tempAPs = new ObservableCollection<AccessPoint>();
                var apRepresentation = new AccessPoint(
                    "Wired Link",
                    $"Ethernet ({stats.LinkStatus})",
                    "Local Gateway",
                    "Wired",
                    0,
                    0,
                    stats.LinkStatus == "Up" ? 0 : -100,
                    null,
                    0,
                    stats.LinkStatus == "Up" ? 100 : 0,
                    "N/A",
                    "Wired",
                    null,
                    new[] { $"{stats.SpeedBitsPerSecond / 1000000} Mbps" },
                    DateTime.UtcNow
                );
                tempAPs.Add(apRepresentation);

                if (IsSessionActive && !IsSessionPaused)
                {
                    var sample = new TelemetrySample(
                        Timestamp: DateTime.UtcNow,
                        SurveyPoint: SurveyPoint,
                        BSSID: "Wired Link",
                        SSID: $"Ethernet ({stats.LinkStatus})",
                        Band: "Wired",
                        Channel: 0,
                        RSSI: stats.LinkStatus == "Up" ? 0 : -100,
                        SNR: 0,
                        Quality: stats.LinkStatus == "Up" ? 100 : 0,
                        Jitter: stats.AvgPingLatencyMs,
                        NoiseFloor: null
                    );
                    _sessionManager.SaveSample(sample);

                    string fluctText = "-";
                    string fluctColor = "#888888";
                    int currentRssi = stats.LinkStatus == "Up" ? 0 : -100;
                    if (_lastRssiByBssid.TryGetValue("Wired Link", out int lastRssi))
                    {
                        int diff = currentRssi - lastRssi;
                        if (diff > 0) { fluctText = $"▲ +{diff} dB"; fluctColor = "#00FF66"; }
                        else if (diff < 0) { fluctText = $"▼ {diff} dB"; fluctColor = "#FF3300"; }
                        else { fluctText = "• 0 dB"; fluctColor = "#888888"; }
                    }
                    _lastRssiByBssid["Wired Link"] = currentRssi;

                    var logItem = new LiveCaptureLogItem(sample, fluctText, fluctColor);
                    ActiveSessionLogs.Insert(0, logItem);
                    while (ActiveSessionLogs.Count > 200)
                    {
                        ActiveSessionLogs.RemoveAt(ActiveSessionLogs.Count - 1);
                    }
                }

                AccessPoints = tempAPs;
                StatusText = $"Wired interface active. Speed: {stats.SpeedBitsPerSecond / 1000000} Mbps | Latency: {stats.AvgPingLatencyMs:F1}ms";
            }
            else
            {
                var apList = await _adapterProvider.GetAccessPointsAsync(SelectedAdapter.Id);
                var tempAPs = new ObservableCollection<AccessPoint>();
                foreach (var ap in apList)
                {
                    tempAPs.Add(ap);

                    if (IsSessionActive && !IsSessionPaused)
                    {
                        bool shouldCapture = false;
                        if (_checkedKeys == null || !_checkedKeys.Any())
                        {
                            shouldCapture = true;
                        }
                        else
                        {
                            if (IsSsidView)
                            {
                                shouldCapture = _checkedKeys.Contains(ap.SSID);
                            }
                            else
                            {
                                shouldCapture = _checkedKeys.Contains(ap.BSSID);
                            }
                        }

                        if (shouldCapture)
                        {
                            double jitter = _sessionManager.CalculateJitterFor(ap.BSSID, ap.RSSI);
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
                                Jitter: jitter,
                                NoiseFloor: ap.NoiseFloor
                            );
                            _sessionManager.SaveSample(sample);

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

                            var logItem = new LiveCaptureLogItem(sample, fluctText, fluctColor);
                            ActiveSessionLogs.Insert(0, logItem);
                            while (ActiveSessionLogs.Count > 200)
                            {
                                ActiveSessionLogs.RemoveAt(ActiveSessionLogs.Count - 1);
                            }
                        }
                    }
                }
                AccessPoints = tempAPs;
                StatusText = $"Scan complete. Found {AccessPoints.Count} Access Points.";
            }
            UpdateDisplayAccessPoints();
            OnPropertyChanged(nameof(ActiveSessionLogs));
            OnPropertyChanged(nameof(FilteredActiveSessionLogs));
            _apiServer.UpdateLiveAccessPoints(AccessPoints);
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failure: {ex.Message}";
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
            SessionModeText = dialog.Mode == CaptureSessionMode.Stationary ? "Stationary Saturation" : "Roaming Survey";
            
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
            var fileInfo = new FileInfo("wireless_scanner.db");
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
        {
            ApiServerStatusText = $"REST API: Running on http://localhost:{_apiServer.Port}/";
        }
        else
        {
            ApiServerStatusText = "REST API: Stopped";
        }
    }

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
            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "API");
            if (!Directory.Exists(apiDirectory))
            {
                Directory.CreateDirectory(apiDirectory);
            }

            string filePath = Path.Combine(apiDirectory, "api_key.md");
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
            StatusText = $"API key exported to {Path.Combine("API", "api_key.md")}";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to export API key: {ex.Message}";
        }
    }

    private void DeleteApiKey()
    {
        ApiKey = "";
        StatusText = "API key cleared. Click 'Apply Changes' to save.";

        try
        {
            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "API");
            string filePath = Path.Combine(apiDirectory, "api_key.md");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"API key cleared. (Failed to delete markdown file: {ex.Message})";
        }
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
            
            if (ApiEnabled)
            {
                ExportApiKey();
                StatusText = "REST API settings saved and applied. Key saved to API/api_key.md.";
            }
            else
            {
                StatusText = "REST API settings saved and applied successfully.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to apply REST API settings: {ex.Message}";
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
