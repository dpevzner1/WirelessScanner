using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

// Signal Saturation Test domain model — docs/SATURATION_TEST_SPEC.md (SPEC-SAT-001), DR-0004, DR-0005.

public enum SaturationScope
{
    SingleAp,
    MultipleAps,
    SingleSsid,
    MultipleSsids,
    AllVisible,
    /// <summary>Whole SSIDs plus individually selected BSSIDs in one test (DR-0011).</summary>
    Mixed
}

/// <summary>Per target per cycle (SPEC §4.3). Only Observed samples feed RSSI statistics.</summary>
public enum ObservationStatus
{
    Observed,
    Stale,
    Missing,
    Unknown
}

public enum SaturationEventType
{
    Drop,
    Peak,
    Dropout,
    ChannelChange,
    StrongestBssidChange
}

public enum SaturationTestStatus
{
    Running,
    Paused,
    Completed,
    Stopped,
    Failed
}

/// <summary>Analysis thresholds (SPEC §6). Defaults are SAT-ANALYTICS-1 defaults.</summary>
public record SaturationThresholds
{
    public double DropDb { get; init; } = 8;
    public double PeakDb { get; init; } = 8;
    public int BaselineWindow { get; init; } = 12;
    public int MinBaselineSamples { get; init; } = 3;
    public int DropoutMissedCycles { get; init; } = 2;
    public double StrongestChangeHysteresisDb { get; init; } = 3;
    public int GoodDbm { get; init; } = -67;
    public int FairDbm { get; init; } = -70;
    public int UnusableDbm { get; init; } = -80;
    public double FreshnessToleranceSeconds { get; init; } = 5;
}

/// <summary>
/// Where the test was performed (owner requirement 2026-10-15: geolocation is required for metric tracking).
/// Coordinates are WGS84 decimal degrees. Source: "manual" (typed/pasted) or "windows-location" (Windows Location service).
/// Building/Floor/Zone allow indoor comparison where GPS accuracy is insufficient.
/// </summary>
public record GeoTag
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AccuracyMeters { get; init; }
    public string Source { get; init; } = "manual";
    public DateTime? CapturedUtc { get; init; }
    public string Building { get; init; } = "";
    public string Floor { get; init; } = "";
    public string Zone { get; init; } = "";

    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;

    public string CoordinatesText => HasCoordinates
        ? FormattableString.Invariant($"{Latitude:0.000000}, {Longitude:0.000000}") + (AccuracyMeters is double a ? FormattableString.Invariant($" (±{a:0} m)") : "")
        : "not recorded";

    public string MapUrl => HasCoordinates
        ? FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={Latitude:0.000000}&mlon={Longitude:0.000000}#map=19/{Latitude:0.000000}/{Longitude:0.000000}")
        : "";

    /// <summary>Validates WGS84 ranges; returns an error message or null.</summary>
    public static string? Validate(double? lat, double? lon)
    {
        if (lat.HasValue != lon.HasValue) return "Enter both latitude and longitude, or neither.";
        if (lat is double la && (la < -90 || la > 90)) return "Latitude must be between -90 and 90.";
        if (lon is double lo && (lo < -180 || lo > 180)) return "Longitude must be between -180 and 180.";
        return null;
    }
}

public interface IGeoLocationProvider
{
    /// <summary>Returns the current position, or a failure reason (location service off, permission denied, timeout).</summary>
    System.Threading.Tasks.Task<(GeoTag? Tag, string? Error)> GetCurrentAsync(TimeSpan timeout);
}

public record SaturationTestConfig
{
    public Guid TestId { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public SaturationScope Scope { get; init; } = SaturationScope.SingleAp;
    public IReadOnlyList<string> TargetBssids { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TargetSsids { get; init; } = Array.Empty<string>();
    /// <summary>Null = run until stopped.</summary>
    public TimeSpan? Duration { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public string Location { get; init; } = "";
    public string Facility { get; init; } = "";
    public string Operator { get; init; } = "";
    public string Client { get; init; } = "";
    public string Notes { get; init; } = "";
    public GeoTag Geo { get; init; } = new();
    public Guid InterfaceId { get; init; }
    public string InterfaceName { get; init; } = "";
    public SaturationThresholds Thresholds { get; init; } = new();
    public bool KeepAwake { get; init; } = true;
    /// <summary>Who/what/where measured: captured at start, stored with the test, printed on every report (DR-0012).</summary>
    public SaturationEnvironment? Environment { get; init; }

    public TimeSpan FreshnessLimit => Interval + TimeSpan.FromSeconds(Thresholds.FreshnessToleranceSeconds);
}

/// <summary>
/// Measurement provenance (DR-0012): makes every report reproducible and comparable. RSSI is only comparable for the
/// same adapter + driver, and scan behaviour depends on whether the PC itself was associated.
/// </summary>
public record SaturationEnvironment
{
    public string AppVersion { get; init; } = "";
    public string OsDescription { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string TimeZoneId { get; init; } = "";
    /// <summary>Local offset from UTC at the start of the test, in minutes.</summary>
    public int UtcOffsetMinutes { get; init; }
    public string AdapterDescription { get; init; } = "";
    public string AdapterMac { get; init; } = "";
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    public string? DriverProvider { get; init; }
    public ClientConnectionInfo? ConnectionAtStart { get; init; }
    public DateTime CapturedUtc { get; init; }
}

public record SaturationObservation(
    long CycleSequence,
    string Bssid,
    string Ssid,
    ObservationStatus Status,
    int? Rssi,
    int? LinkQuality,
    int? Channel,
    string? Band,
    int? ChannelWidth,
    double? BeaconAgeMs,
    int? CoChannelCount,
    int? OverlapCount,
    // SAT-ANALYTICS-2 additions (DR-0012); null = not reported / not applicable
    string? Security = null,
    string? Vendor = null,
    string? PhyType = null,
    int? BeaconIntervalTu = null,
    double? CenterFrequencyMhz = null,
    int? StationCount = null,
    double? ChannelUtilizationPct = null,
    // Strongest fresh neighbour (other BSSID) whose occupied block overlaps this AP block, dBm
    int? StrongestOverlapRssi = null);

public record SaturationCycle(
    long Sequence,
    DateTime WallUtc,
    long ElapsedMs,
    ScanCycleStatus Status,
    double DurationMs,
    int BssCount,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<SaturationObservation> Observations);

public record SaturationMarker(DateTime WallUtc, long ElapsedMs, string Text);

public record SaturationEvent(
    SaturationEventType Type,
    string Subject,
    string? Ssid,
    DateTime StartUtc,
    DateTime? EndUtc,
    long StartElapsedMs,
    long? EndElapsedMs,
    double? DurationSeconds,
    int? Cycles,
    double? BaselineDbm,
    int? ExtremeRssi,
    double? MagnitudeDb,
    double? RecoverySeconds,
    string Detail);

public record SaturationTargetSummary
{
    public string Bssid { get; init; } = "";
    public string Ssid { get; init; } = "";
    public string Bands { get; init; } = "";
    public string Channels { get; init; } = "";
    public int? PrimaryChannel { get; init; }
    public int? ChannelWidth { get; init; }
    public int CyclesTracked { get; init; }
    public int Observed { get; init; }
    public int Missing { get; init; }
    public int Stale { get; init; }
    public int Unknown { get; init; }
    public double? AvailabilityPct { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public double? Mean { get; init; }
    public double? Median { get; init; }
    public double? StdDev { get; init; }
    public double? P5 { get; init; }
    public double? P25 { get; init; }
    public double? P75 { get; init; }
    public double? P95 { get; init; }
    public double? Iqr { get; init; }
    public int? Range { get; init; }
    public double? VariationMean { get; init; }
    public double? VariationP95 { get; init; }
    public double? VariationMax { get; init; }
    /// <summary>Least-squares slope; null when the observed span is shorter than the minimum trend span (15 min).</summary>
    public double? TrendDbPerHour { get; init; }
    /// <summary>Time between the first and last observed sample, seconds.</summary>
    public double ObservedSpanSeconds { get; init; }
    public double? PctBelowGood { get; init; }
    public double? PctBelowFair { get; init; }
    public double? PctBelowUnusable { get; init; }
    public double LongestBelowFairSeconds { get; init; }
    public double LongestBelowUnusableSeconds { get; init; }
    public int DropCount { get; init; }
    public int PeakCount { get; init; }
    public int DropoutCount { get; init; }
    public int ChannelChangeCount { get; init; }
    public double? WorstDropDb { get; init; }
    public double LongestDropoutSeconds { get; init; }
    public double? CoChannelMean { get; init; }
    public int? CoChannelMax { get; init; }
    public double? OverlapMean { get; init; }
    public int? OverlapMax { get; init; }
    // AP identity and capability (most recent observed value)
    public string? Vendor { get; init; }
    public string? Security { get; init; }
    public string? PhyType { get; init; }
    public int? BeaconIntervalTu { get; init; }
    public double? CenterFrequencyMhz { get; init; }
    /// <summary>Occupied block (MHz) touches 5250-5730 MHz: radar detection may force channel changes.</summary>
    public bool? IsDfs { get; init; }
    // Link quality (driver %) and estimated SNR (RSSI - assumed -95 dBm floor; NOT measured)
    public double? LinkQualityMean { get; init; }
    public int? LinkQualityMin { get; init; }
    public double? SnrEstimatedMeanDb { get; init; }
    // BSS Load advertised by the AP (null when the AP does not advertise it)
    public double? LoadAdvertisedPct { get; init; }
    public double? StationCountMean { get; init; }
    public int? StationCountMax { get; init; }
    public double? ChannelUtilizationMeanPct { get; init; }
    public double? ChannelUtilizationMaxPct { get; init; }
    // Interference margin: own RSSI minus the strongest fresh overlapping neighbour (dB); higher is better
    public double? InterferenceMarginMedianDb { get; init; }
    public double? InterferenceMarginMinDb { get; init; }
    public int? StrongestOverlapRssiMax { get; init; }
    public double? StabilityScore { get; init; }
    public string Grade { get; init; } = "n/a";
    public string Tier { get; init; } = "n/a";
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}

public record SaturationSsidSummary(
    string Ssid,
    int BssidCount,
    double? AvailabilityPct,
    double? BestMean,
    int? BestMin,
    int? BestMax,
    int StrongestBssidChanges);

public record SaturationCycleHealth(
    int Total,
    int Fresh,
    int Stale,
    int Failed,
    int Skipped,
    double? MeanScanMs,
    double? MaxScanMs,
    double MaxGapSeconds,
    IReadOnlyDictionary<string, int> ErrorCounts);

public record SaturationReport(
    SaturationTestConfig Config,
    string AnalyticsVersion,
    DateTime StartUtc,
    DateTime? EndUtc,
    double ActiveSeconds,
    SaturationTestStatus Status,
    SaturationCycleHealth Health,
    IReadOnlyList<SaturationTargetSummary> Targets,
    IReadOnlyList<SaturationSsidSummary> Ssids,
    IReadOnlyList<SaturationEvent> Events,
    IReadOnlyList<SaturationMarker> Markers,
    IReadOnlyList<SaturationCycle> Cycles);

/// <summary>Stored test header (history list).</summary>
public record SaturationTestRecord(
    SaturationTestConfig Config,
    DateTime StartUtc,
    DateTime? EndUtc,
    SaturationTestStatus Status,
    int CycleCount,
    /// <summary>Measured active (unpaused) duration; null for tests recorded before 1.3.</summary>
    double? ActiveSeconds = null);

public interface ISaturationAnalyzer
{
    string Version { get; }
    /// <param name="activeSeconds">Measured active duration from the runner; when null it is reconstructed from the cycles.</param>
    SaturationReport Analyze(SaturationTestConfig config, IReadOnlyList<SaturationCycle> cycles, IReadOnlyList<SaturationMarker> markers,
        DateTime startUtc, DateTime? endUtc, SaturationTestStatus status, double? activeSeconds = null);
}

public interface ISaturationRepository
{
    Task SaveTestAsync(SaturationTestConfig config, DateTime startUtc, SaturationTestStatus status);
    Task UpdateTestStatusAsync(Guid testId, SaturationTestStatus status, DateTime? endUtc, double? activeSeconds = null);
    Task SaveCycleAsync(Guid testId, SaturationCycle cycle);
    Task SaveMarkerAsync(Guid testId, SaturationMarker marker);
    Task<IReadOnlyList<SaturationTestRecord>> GetTestsAsync();
    Task<SaturationTestRecord?> GetTestAsync(Guid testId);
    Task<IReadOnlyList<SaturationCycle>> GetCyclesAsync(Guid testId);
    Task<IReadOnlyList<SaturationMarker>> GetMarkersAsync(Guid testId);
    Task DeleteTestAsync(Guid testId);
}

public interface ISaturationExportService
{
    Task ExportPdfAsync(string filePath, SaturationReport report);
    /// <summary>Writes samples/events/summary/cycles CSV files; returns their paths.</summary>
    Task<IReadOnlyList<string>> ExportCsvBundleAsync(string directory, string filePrefix, SaturationReport report);
    Task ExportJsonAsync(string filePath, SaturationReport report);
}

/// <summary>Live progress published by the runner after every cycle.</summary>
public record SaturationProgress(
    SaturationTestConfig Config,
    SaturationTestStatus Status,
    DateTime StartUtc,
    double ActiveSeconds,
    double? RemainingSeconds,
    SaturationCycle LastCycle,
    IReadOnlyList<SaturationCycle> Cycles,
    IReadOnlyList<SaturationMarker> Markers);

public interface ISaturationTestRunner
{
    bool IsRunning { get; }
    SaturationTestStatus? Status { get; }
    event Action<SaturationProgress>? Progress;
    event Action<SaturationReport>? Completed;
    Task StartAsync(SaturationTestConfig config, CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    Task<SaturationReport?> StopAsync();
    void AddMarker(string text);
}
