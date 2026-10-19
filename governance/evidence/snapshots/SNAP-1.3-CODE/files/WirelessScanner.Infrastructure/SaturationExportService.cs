using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

/// <summary>
/// Saturation test exports (SPEC-SAT-001 §8): PDF report, CSV bundle (samples incl. missing/stale rows,
/// events, summary, cycles, markers) and JSON. All numbers use invariant culture; all files are written
/// atomically (temp file then move, NFR-05).
/// </summary>
public class SaturationExportService : ISaturationExportService
{
    public const string JsonSchema = "wirelessscanner.saturation_report.v2";
    public const int PdfAppendixRowLimit = 20000;

    public const string MeasurementBoundary =
        "Client-side receive view from the Windows Native Wi-Fi API on the test adapter. RSSI and link quality are driver-reported " +
        "(resolution and averaging vary by adapter/driver; not calibrated against an RF instrument). Channel/band are derived from centre " +
        "frequency; channel width from HT/VHT/HE operation elements (blank = unknown). Noise floor is UNAVAILABLE (not exposed by the API). " +
        "'RSSI variation' is the scan-to-scan change in RSSI (dB), not packet latency jitter. Throughput, latency and packet loss are not measured.";

    public const string ScoreFormula =
        "Stability score = 100 - 4*sigma - 0.5*(100 - availability%) - 3*drops/h - 5*dropouts/h - 5*channel changes/h, clamped 0-100; " +
        "rates use max(active hours, 0.25). Grades: A>=90, B>=75, C>=60, D>=40, F<40. Derived heuristic, not an industry standard.";

    /// <summary>How every number in the reports is defined and rounded (DR-0012). Printed in the PDF, carried in JSON.</summary>
    public const string PrecisionStatement =
        "RSSI is reported by the driver in whole dBm; min/max/percentiles are therefore whole dBm (nearest-rank percentiles). " +
        "Means, standard deviation (population), variation and slopes are computed in double precision and shown to 1 decimal in the PDF, " +
        "2 decimals in CSV, and unrounded in JSON. Time shares are weighted by the time each cycle represents (until the next cycle, " +
        "max 2 intervals; the last cycle's window ends at the end of the test). Durations are seconds of active (unpaused) time. " +
        "Times are UTC in data files and local (with UTC offset) in the PDF.";

    public const string NeighbourDefinition =
        "Neighbours are other BSSIDs heard FRESH in the same cycle (beacon within the freshness limit). Co-channel = same primary channel. " +
        "Overlapping = different primary channel whose occupied block (incl. 40/80/160 MHz bonding, all bands) shares spectrum. " +
        "Interference margin = target RSSI minus the strongest co-channel or overlapping neighbour (dB; higher is better, aim >= 20 dB). " +
        "BSS Load (stations, channel busy %) is what the AP itself advertises (802.11 element 11); blank when not advertised.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Local time with its UTC offset, e.g. 2026-10-16 11:31:26 (UTC-05:00).</summary>
    internal static string LocalWithOffset(DateTime utc, string format = "yyyy-MM-dd HH:mm:ss")
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = u.ToLocalTime();
        var off = TimeZoneInfo.Local.GetUtcOffset(u);
        return $"{local.ToString(format, Inv)} (UTC{(off < TimeSpan.Zero ? "-" : "+")}{off.Duration():hh\\:mm})";
    }

    internal static string LocalIso(DateTime? utc)
    {
        if (!utc.HasValue) return "";
        var u = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return new DateTimeOffset(u).ToOffset(TimeZoneInfo.Local.GetUtcOffset(u)).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", Inv);
    }

    /// <summary>Version of the app that generated the export.</summary>
    internal static string GeneratorVersion() =>
        typeof(SaturationExportService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion?.Split('+')[0]
        ?? typeof(SaturationExportService).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    internal static string ConnectionText(ClientConnectionInfo? c) => c == null ? "not recorded"
        : !c.Connected ? $"not connected ({c.State})"
        : $"connected to {c.Ssid ?? "<hidden>"} ({c.Bssid}), ch {c.Channel?.ToString(Inv) ?? "?"} {c.Band ?? ""}, {c.PhyType ?? "PHY ?"}, " +
          $"rx {c.RxRateMbps?.ToString("0.#", Inv) ?? "?"} / tx {c.TxRateMbps?.ToString("0.#", Inv) ?? "?"} Mbps, signal {c.SignalQualityPct?.ToString(Inv) ?? "?"} %";

    static SaturationExportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // ---------- helpers ----------

    internal static async Task WriteAtomicAsync(string path, Func<Stream, Task> write)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await write(fs);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string F(double? v, string fmt = "0.##") => v.HasValue ? v.Value.ToString(fmt, Inv) : "";
    private static string F(int? v) => v.HasValue ? v.Value.ToString(Inv) : "";
    private static string Iso(DateTime? t) => t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv) : "";

    internal static string Csv(string? field)
    {
        field ??= "";
        return field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }

    private static string Row(params string?[] cells) => string.Join(",", cells.Select(Csv));

    // ---------- CSV ----------

    public async Task<IReadOnlyList<string>> ExportCsvBundleAsync(string directory, string filePrefix, SaturationReport report)
    {
        Directory.CreateDirectory(directory);
        string P(string name) => Path.Combine(directory, $"{filePrefix}_{name}.csv");
        var tierByRssi = new Func<int?, string>(r => r.HasValue ? Tier(r.Value) : "");
        var id = report.Config.TestId.ToString();
        var geo = report.Config.Geo;
        // Geolocation repeated on every row so multiple tests can be merged and tracked by place (owner requirement).
        string[] geoCells = { F(geo.Latitude, "0.000000"), F(geo.Longitude, "0.000000"), F(geo.AccuracyMeters, "0"), geo.Source, geo.Building, geo.Floor, geo.Zone, report.Config.Location };
        string[] geoHeads = { "Latitude", "Longitude", "GeoAccuracy_m", "GeoSource", "Building", "Floor", "Zone", "Location" };

        var samples = new StringBuilder();
        samples.AppendLine($"# WirelessScanner saturation test samples | test {id} | analytics {report.AnalyticsVersion} | one row per cycle per target, including missing/stale/unknown rows");
        samples.AppendLine(Row("TestId", "CycleSeq", "WallUtc", "WallLocal", "ElapsedSec", "CycleStatus", "BSSID", "SSID", "ObservationStatus", "RSSI_dBm", "RSSI_Tier",
            "LinkQuality_pct", "Channel", "Band", "ChannelWidth_MHz", "CenterFreq_MHz", "PhyType", "Security", "Vendor", "BeaconInterval_TU", "BeaconAge_ms",
            "CoChannel_Count", "Overlap_Count", "StrongestNeighbour_dBm", "InterferenceMargin_dB", "BssLoad_Stations", "BssLoad_ChannelBusy_pct").TrimEnd() + "," + Row(geoHeads));
        foreach (var c in report.Cycles)
        {
            foreach (var o in c.Observations)
            {
                samples.AppendLine(Row(id, c.Sequence.ToString(Inv), Iso(c.WallUtc), LocalIso(c.WallUtc), F(c.ElapsedMs / 1000.0, "0.000"), c.Status.ToString(), o.Bssid, o.Ssid,
                    o.Status.ToString(), F(o.Rssi), o.Status == ObservationStatus.Observed ? tierByRssi(o.Rssi) : "", F(o.LinkQuality), F(o.Channel), o.Band,
                    F(o.ChannelWidth), F(o.CenterFrequencyMhz, "0.###"), o.PhyType, o.Security, o.Vendor, F(o.BeaconIntervalTu), F(o.BeaconAgeMs, "0"),
                    F(o.CoChannelCount), F(o.OverlapCount), F(o.StrongestOverlapRssi),
                    o.Rssi is int rr && o.StrongestOverlapRssi is int sn ? F(rr - sn) : "", F(o.StationCount), F(o.ChannelUtilizationPct, "0.0")) + "," + Row(geoCells));
            }
        }

        var events = new StringBuilder();
        events.AppendLine(Row("Type", "Subject", "SSID", "StartUtc", "EndUtc", "StartElapsedSec", "EndElapsedSec", "Duration_s", "Cycles",
            "Baseline_dBm", "Extreme_dBm", "Magnitude_dB", "Recovery_s", "Detail"));
        foreach (var e in report.Events)
        {
            events.AppendLine(Row(e.Type.ToString(), e.Subject, e.Ssid, Iso(e.StartUtc), Iso(e.EndUtc), F(e.StartElapsedMs / 1000.0, "0.000"),
                e.EndElapsedMs.HasValue ? F(e.EndElapsedMs.Value / 1000.0, "0.000") : "", F(e.DurationSeconds, "0.0"), F(e.Cycles),
                F(e.BaselineDbm, "0.0"), F(e.ExtremeRssi), F(e.MagnitudeDb, "0.0"), F(e.RecoverySeconds, "0.0"), e.Detail));
        }

        var summary = new StringBuilder();
        summary.AppendLine(Row("BSSID", "SSID", "Bands", "Channels", "PrimaryChannel", "ChannelWidth_MHz", "CyclesTracked", "Observed", "Missing", "Stale", "Unknown",
            "Availability_pct", "Min_dBm", "Max_dBm", "Mean_dBm", "Median_dBm", "StdDev_dB", "P5", "P25", "P75", "P95", "IQR_dB", "Range_dB",
            "Variation_Mean_dB", "Variation_P95_dB", "Variation_Max_dB", "Trend_dB_per_h", "PctBelowGood", "PctBelowFair", "PctBelowUnusable",
            "LongestBelowFair_s", "LongestBelowUnusable_s", "Drops", "Peaks", "Dropouts", "ChannelChanges", "WorstDrop_dB", "LongestDropout_s",
            "CoChannel_Mean", "CoChannel_Max", "Overlap_Mean", "Overlap_Max", "StabilityScore", "Grade", "Tier", "Findings",
            "Vendor", "Security", "PhyType", "BeaconInterval_TU", "CenterFreq_MHz", "DFS", "LinkQuality_Mean_pct", "LinkQuality_Min_pct", "SNR_Estimated_Mean_dB",
            "ObservedSpan_s", "BssLoad_Advertised_pct", "BssLoad_Stations_Mean", "BssLoad_Stations_Max", "BssLoad_ChannelBusy_Mean_pct", "BssLoad_ChannelBusy_Max_pct",
            "InterferenceMargin_Median_dB", "InterferenceMargin_Min_dB", "StrongestNeighbour_Max_dBm",
            "TestId", "TestStartUtc", "ActiveSec", "AnalyticsVersion", "AppVersion") + "," + Row(geoHeads));
        foreach (var t in report.Targets)
        {
            summary.AppendLine(Row(t.Bssid, t.Ssid, t.Bands, t.Channels, F(t.PrimaryChannel), F(t.ChannelWidth), F(t.CyclesTracked), F(t.Observed), F(t.Missing),
                F(t.Stale), F(t.Unknown), F(t.AvailabilityPct), F(t.Min), F(t.Max), F(t.Mean), F(t.Median), F(t.StdDev), F(t.P5), F(t.P25), F(t.P75), F(t.P95),
                F(t.Iqr), F(t.Range), F(t.VariationMean), F(t.VariationP95), F(t.VariationMax), F(t.TrendDbPerHour), F(t.PctBelowGood), F(t.PctBelowFair),
                F(t.PctBelowUnusable), F(t.LongestBelowFairSeconds, "0.0"), F(t.LongestBelowUnusableSeconds, "0.0"), F(t.DropCount), F(t.PeakCount),
                F(t.DropoutCount), F(t.ChannelChangeCount), F(t.WorstDropDb), F(t.LongestDropoutSeconds, "0.0"), F(t.CoChannelMean), F(t.CoChannelMax),
                F(t.OverlapMean), F(t.OverlapMax), F(t.StabilityScore), t.Grade, t.Tier, string.Join(" | ", t.Findings),
                t.Vendor, t.Security, t.PhyType, F(t.BeaconIntervalTu), F(t.CenterFrequencyMhz, "0.###"), t.IsDfs is bool dfs ? (dfs ? "yes" : "no") : "",
                F(t.LinkQualityMean), F(t.LinkQualityMin), F(t.SnrEstimatedMeanDb), F(t.ObservedSpanSeconds, "0.0"), F(t.LoadAdvertisedPct), F(t.StationCountMean),
                F(t.StationCountMax), F(t.ChannelUtilizationMeanPct), F(t.ChannelUtilizationMaxPct), F(t.InterferenceMarginMedianDb), F(t.InterferenceMarginMinDb),
                F(t.StrongestOverlapRssiMax), id, Iso(report.StartUtc), F(report.ActiveSeconds, "0.000"), report.AnalyticsVersion, GeneratorVersion()) + "," + Row(geoCells));
        }

        var cycles = new StringBuilder();
        cycles.AppendLine(Row("CycleSeq", "WallUtc", "WallLocal", "ElapsedSec", "Status", "ScanDuration_ms", "BssCount", "ErrorCode", "ErrorMessage"));
        foreach (var c in report.Cycles)
        {
            cycles.AppendLine(Row(c.Sequence.ToString(Inv), Iso(c.WallUtc), LocalIso(c.WallUtc), F(c.ElapsedMs / 1000.0, "0.000"), c.Status.ToString(), F(c.DurationMs, "0"),
                F(c.BssCount), c.ErrorCode, c.ErrorMessage));
        }

        var markers = new StringBuilder();
        markers.AppendLine(Row("WallUtc", "ElapsedSec", "Text"));
        foreach (var m in report.Markers) markers.AppendLine(Row(Iso(m.WallUtc), F(m.ElapsedMs / 1000.0, "0.000"), m.Text));

        // Test-level provenance and configuration: one key/value row each (DR-0012)
        var info = new StringBuilder();
        info.AppendLine(Row("Key", "Value"));
        foreach (var (k, v) in InfoRows(report)) info.AppendLine(Row(k, v));

        var files = new List<(string Path, StringBuilder Content)>
        {
            (P("info"), info), (P("samples"), samples), (P("events"), events), (P("summary"), summary), (P("cycles"), cycles), (P("markers"), markers)
        };
        foreach (var (path, content) in files)
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(content.ToString());
            await WriteAtomicAsync(path, s => s.WriteAsync(bytes).AsTask());
        }
        return files.Select(f => f.Path).ToList();
    }

    /// <summary>Provenance, configuration and definitions as key/value pairs (CSV _info file; mirrors the PDF sections 1-2).</summary>
    internal static IEnumerable<(string, string)> InfoRows(SaturationReport r)
    {
        var c = r.Config;
        var e = c.Environment;
        var th = c.Thresholds;
        yield return ("ReportSchema", JsonSchema);
        yield return ("AnalyticsVersion", r.AnalyticsVersion);
        yield return ("GeneratedBy", $"WirelessScanner {GeneratorVersion()}");
        yield return ("GeneratedUtc", Iso(DateTime.UtcNow));
        yield return ("TestId", c.TestId.ToString());
        yield return ("TestName", c.Name);
        yield return ("Status", r.Status.ToString());
        yield return ("Scope", c.Scope.ToString());
        yield return ("TargetSsids", string.Join(" | ", c.TargetSsids));
        yield return ("TargetBssids", string.Join(" | ", c.TargetBssids));
        yield return ("StartUtc", Iso(r.StartUtc));
        yield return ("StartLocal", LocalIso(r.StartUtc));
        yield return ("EndUtc", Iso(r.EndUtc));
        yield return ("EndLocal", LocalIso(r.EndUtc));
        yield return ("ActiveSeconds", F(r.ActiveSeconds, "0.000"));
        yield return ("PlannedSeconds", c.Duration.HasValue ? F(c.Duration.Value.TotalSeconds, "0") : "until stopped");
        yield return ("Interval_s", F(c.Interval.TotalSeconds, "0.###"));
        yield return ("ScanTimeout_s", F(c.ScanTimeout.TotalSeconds, "0.###"));
        yield return ("FreshnessLimit_s", F(c.FreshnessLimit.TotalSeconds, "0.###"));
        yield return ("Thresholds", $"drop {th.DropDb} dB, peak {th.PeakDb} dB, baseline window {th.BaselineWindow} (min {th.MinBaselineSamples}), dropout {th.DropoutMissedCycles} missed, strongest-change hysteresis {th.StrongestChangeHysteresisDb} dB, good {th.GoodDbm}, fair {th.FairDbm}, unusable {th.UnusableDbm} dBm");
        yield return ("Location", c.Location);
        yield return ("Facility", c.Facility);
        yield return ("Operator", c.Operator);
        yield return ("Client", c.Client);
        yield return ("Notes", c.Notes);
        yield return ("Latitude", F(c.Geo.Latitude, "0.000000"));
        yield return ("Longitude", F(c.Geo.Longitude, "0.000000"));
        yield return ("GeoAccuracy_m", F(c.Geo.AccuracyMeters, "0"));
        yield return ("GeoSource", c.Geo.Source);
        yield return ("Building/Floor/Zone", $"{c.Geo.Building} / {c.Geo.Floor} / {c.Geo.Zone}");
        yield return ("Adapter", e?.AdapterDescription ?? c.InterfaceName);
        yield return ("AdapterMac", e?.AdapterMac ?? "");
        yield return ("DriverVersion", e?.DriverVersion ?? "not recorded");
        yield return ("DriverDate", e?.DriverDate ?? "");
        yield return ("DriverProvider", e?.DriverProvider ?? "");
        yield return ("PcConnectionAtStart", ConnectionText(e?.ConnectionAtStart));
        yield return ("RecordedWithApp", e?.AppVersion ?? "before 1.3 (not recorded)");
        yield return ("OS", e?.OsDescription ?? "");
        yield return ("Machine", e?.MachineName ?? "");
        yield return ("TimeZone", e == null ? "" : $"{e.TimeZoneId} (UTC offset at start {e.UtcOffsetMinutes / 60.0:+0.##;-0.##;0} h)");
        yield return ("MeasurementBoundary", MeasurementBoundary);
        yield return ("Precision", PrecisionStatement);
        yield return ("Neighbours", NeighbourDefinition);
        yield return ("ScoreFormula", ScoreFormula);
    }

    // ---------- JSON ----------

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public Task ExportJsonAsync(string filePath, SaturationReport report)
    {
        var payload = new
        {
            schema = JsonSchema,
            analyticsVersion = report.AnalyticsVersion,
            generatedBy = $"WirelessScanner {GeneratorVersion()}",
            generatedUtc = DateTime.UtcNow,
            measurementBoundary = MeasurementBoundary,
            precision = PrecisionStatement,
            neighbours = NeighbourDefinition,
            scoreFormula = ScoreFormula,
            environment = report.Config.Environment,
            startLocal = LocalIso(report.StartUtc),
            endLocal = LocalIso(report.EndUtc),
            test = report.Config,
            geo = new { report.Config.Geo.Latitude, report.Config.Geo.Longitude, report.Config.Geo.AccuracyMeters, report.Config.Geo.Source,
                        report.Config.Geo.CapturedUtc, report.Config.Geo.Building, report.Config.Geo.Floor, report.Config.Geo.Zone,
                        location = report.Config.Location, mapUrl = report.Config.Geo.MapUrl },
            report.StartUtc,
            report.EndUtc,
            report.ActiveSeconds,
            report.Status,
            health = report.Health,
            targets = report.Targets,
            ssids = report.Ssids,
            events = report.Events,
            markers = report.Markers,
            cycles = report.Cycles
        };
        return WriteAtomicAsync(filePath, s => JsonSerializer.SerializeAsync(s, payload, JsonOptions));
    }

    // ---------- PDF ----------

    public Task ExportPdfAsync(string filePath, SaturationReport report)
    {
        var doc = new SaturationReportDocument(report);
        return WriteAtomicAsync(filePath, s =>
        {
            doc.GeneratePdf(s);
            return Task.CompletedTask;
        });
    }

    internal static string Tier(double rssi) => rssi switch
    {
        >= -55 => "Excellent",
        >= -67 => "Good",
        >= -70 => "Fair",
        >= -80 => "Weak",
        _ => "Unusable"
    };
}

internal sealed class SaturationReportDocument : IDocument
{
    private readonly SaturationReport _r;
    private const string Accent = "#B45309";   // amber-700 for print legibility
    private const string HeadBg = "#1F2937";

    public SaturationReportDocument(SaturationReport report) => _r = report;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Signal Saturation Test - {_r.Config.Name}",
        Author = string.IsNullOrWhiteSpace(_r.Config.Operator) ? "WirelessScanner" : _r.Config.Operator,
        Subject = "Wi-Fi signal saturation (stability) test report",
        Creator = "WirelessScanner"
    };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1.4f, Unit.Centimetre);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily("Arial"));
            page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().AlignCenter().Text(t =>
            {
                t.Span($"WirelessScanner {SaturationExportService.GeneratorVersion()} | {_r.AnalyticsVersion} | Test {_r.Config.TestId} | Page ").FontSize(7).FontColor("#6B7280");
                t.CurrentPageNumber().FontSize(7);
                t.Span(" of ").FontSize(7).FontColor("#6B7280");
                t.TotalPages().FontSize(7);
            });
        });
    }

    private void Header(IContainer c) => c.Row(row =>
    {
        row.RelativeItem().Column(col =>
        {
            col.Item().Text("SIGNAL SATURATION TEST REPORT").FontSize(16).Bold().FontColor(Accent);
            col.Item().Text($"{_r.Config.Name}  |  {_r.Config.Location}  |  {SaturationExportService.LocalWithOffset(_r.StartUtc, "yyyy-MM-dd HH:mm")}").FontSize(9).FontColor("#4B5563");
        });
        row.ConstantItem(110).AlignRight().Text("WirelessScanner").FontSize(11).Bold().FontColor(HeadBg);
    });

    private static void H1(ColumnDescriptor col, string text) =>
        col.Item().PaddingTop(12).PaddingBottom(4).Text(text).FontSize(12).Bold().FontColor(Accent);

    private static void KeyValues(ColumnDescriptor col, IEnumerable<(string K, string V)> rows) =>
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.ConstantColumn(140); cd.RelativeColumn(); });
            foreach (var (k, v) in rows)
            {
                t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(k).Bold();
                t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(string.IsNullOrWhiteSpace(v) ? "—" : v);
            }
        });

    private static void Grid(ColumnDescriptor col, string[] headers, IEnumerable<string[]> rows, float[]? widths = null) =>
        col.Item().Table(t =>
        {
            t.ColumnsDefinition(cd =>
            {
                for (int i = 0; i < headers.Length; i++) cd.RelativeColumn(widths?[i] ?? 1);
            });
            t.Header(h =>
            {
                foreach (var head in headers) h.Cell().Background(HeadBg).Padding(2).Text(head).FontColor(Colors.White).Bold().FontSize(7.5f);
            });
            foreach (var r in rows)
                foreach (var cell in r)
                    t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(cell ?? "").FontSize(7.5f);
        });

    private static string N(double? v, string fmt = "0.0") => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";
    private static string N(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "—";
    private static string Dur(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 86400 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");

    private string TargetLabel(SaturationTargetSummary t) => $"{(string.IsNullOrEmpty(t.Ssid) ? "<hidden>" : t.Ssid)} ({t.Bssid})";

    private void Content(IContainer container) => container.Column(col =>
    {
        var cfg = _r.Config;
        var th = cfg.Thresholds;

        // 1. Test metadata
        H1(col, "1. Test details");
        KeyValues(col, new[]
        {
            ("Test ID", cfg.TestId.ToString()), ("Test name", cfg.Name), ("Location / test area", cfg.Location), ("Facility", cfg.Facility),
            ("Geolocation (WGS84)", cfg.Geo.HasCoordinates
                ? $"{cfg.Geo.CoordinatesText} — source: {cfg.Geo.Source}{(cfg.Geo.CapturedUtc is DateTime gc ? $", captured {gc.ToLocalTime():yyyy-MM-dd HH:mm}" : "")}"
                : "not recorded"),
            ("Map link", cfg.Geo.MapUrl),
            ("Building / floor / zone", string.Join(" / ", new[] { cfg.Geo.Building, cfg.Geo.Floor, cfg.Geo.Zone }.Where(x => !string.IsNullOrWhiteSpace(x)))),
            ("Operator", cfg.Operator), ("Client", cfg.Client), ("Adapter", cfg.InterfaceName), ("Scope", cfg.Scope.ToString()),
            ("Target SSIDs", string.Join(", ", cfg.TargetSsids)), ("Target BSSIDs", string.Join(", ", cfg.TargetBssids)),
            ("Started", $"{SaturationExportService.LocalWithOffset(_r.StartUtc)}  =  {_r.StartUtc:yyyy-MM-dd HH:mm:ss} UTC"),
            ("Ended", _r.EndUtc is DateTime eu ? $"{SaturationExportService.LocalWithOffset(eu)}  =  {eu:yyyy-MM-dd HH:mm:ss} UTC" : "running"),
            ("Active duration", $"{Dur(_r.ActiveSeconds)} ({_r.ActiveSeconds:0.0} s, paused time excluded)" + (cfg.Duration.HasValue ? $" of planned {Dur(cfg.Duration.Value.TotalSeconds)}" : " (until stopped)")),
            ("Scan interval / timeout", $"{cfg.Interval.TotalSeconds:0.#} s / {cfg.ScanTimeout.TotalSeconds:0.#} s"),
            ("Final status", _r.Status.ToString()), ("Notes", cfg.Notes)
        });

        // 1b. Measurement environment (provenance, DR-0012)
        var env = cfg.Environment;
        H1(col, "Measurement environment");
        KeyValues(col, new[]
        {
            ("Recorded with", env == null ? "WirelessScanner before 1.3 (environment not recorded)" : $"WirelessScanner {env.AppVersion}; report generated by {SaturationExportService.GeneratorVersion()}; analytics {_r.AnalyticsVersion}"),
            ("Computer / OS", env == null ? "" : $"{env.MachineName}; {env.OsDescription}"),
            ("Time zone", env == null ? TimeZoneInfo.Local.Id : $"{env.TimeZoneId} (UTC offset at start {env.UtcOffsetMinutes / 60.0:+0.##;-0.##;0} h)"),
            ("Adapter", $"{env?.AdapterDescription ?? cfg.InterfaceName}{(string.IsNullOrEmpty(env?.AdapterMac) ? "" : $" (MAC {env!.AdapterMac})")}"),
            ("Adapter driver", env?.DriverVersion == null ? "not recorded" : $"{env.DriverVersion} ({env.DriverDate}, {env.DriverProvider}) — RSSI is comparable only between tests with the same adapter and driver"),
            ("This PC's connection at start", SaturationExportService.ConnectionText(env?.ConnectionAtStart) + (env?.ConnectionAtStart?.Connected == true ? " — Windows may throttle scans while connected (see stale cycles)" : ""))
        });

        // 2. Executive summary
        H1(col, "2. Executive summary");
        Grid(col, new[] { "Target", "Grade", "Score", "Tier", "Avail. %", "Mean dBm", "σ dB", "Min/Max", "Margin dB", "Drops", "Dropouts", "Ch. chg" },
            _r.Targets.Select(t => new[] { TargetLabel(t), t.Grade, N(t.StabilityScore), t.Tier, N(t.AvailabilityPct), N(t.Mean), N(t.StdDev),
                $"{N(t.Min)} / {N(t.Max)}", N(t.InterferenceMarginMinDb, "0"), N(t.DropCount), N(t.DropoutCount), N(t.ChannelChangeCount) }),
            new[] { 3.0f, 0.6f, 0.7f, 0.9f, 0.8f, 0.9f, 0.6f, 1.0f, 0.8f, 0.6f, 0.8f, 0.7f });
        col.Item().PaddingTop(2).Text("Grade = stability, not strength (read it with the tier). Margin = lowest interference margin observed (own RSSI − strongest overlapping neighbour).").FontSize(7).Italic().FontColor("#4B5563");
        foreach (var t in _r.Targets)
        {
            col.Item().PaddingTop(4).Text(TargetLabel(t)).Bold();
            foreach (var f in t.Findings) col.Item().PaddingLeft(10).Text("• " + f);
        }
        if (_r.Targets.Count == 0) col.Item().Text("No targets were observed during this test.");

        // 3. Methodology
        H1(col, "3. Methodology and measurement limits");
        KeyValues(col, new[]
        {
            ("Analytics version", _r.AnalyticsVersion),
            ("Sampling", $"Each cycle requests a fresh Windows scan and waits up to {cfg.ScanTimeout.TotalSeconds:0} s for completion. Entries older than {cfg.FreshnessLimit.TotalSeconds:0} s are 'stale' and excluded from statistics."),
            ("Drop / peak", $"RSSI ≤ baseline − {th.DropDb:0} dB (drop) or ≥ baseline + {th.PeakDb:0} dB (peak); baseline = rolling median of last {th.BaselineWindow} observed samples (min {th.MinBaselineSamples}); event ends within half the threshold (hysteresis)."),
            ("Dropout", $"Target missing from ≥ {th.DropoutMissedCycles} consecutive fresh scans."),
            ("Thresholds", $"Good ≥ {th.GoodDbm} dBm, Fair ≥ {th.FairDbm} dBm, Unusable < {th.UnusableDbm} dBm. Exposure is time-weighted by cycle spacing; the last cycle's window ends at the end of the test."),
            ("Trend", $"Least-squares slope of observed RSSI, reported only when the observed span is ≥ {SaturationAnalyzerInfo.MinTrendMinutes} min (shorter spans extrapolate noise)."),
            ("Neighbours / BSS Load", cfg.Environment == null ? "Recorded before 1.3: neighbour counts were captured with the 1.2 definition (every listed BSSID incl. cached entries; co-channel = same primary channel; overlap = 2.4 GHz only) and cannot be re-derived; BSS Load, PHY and interference margin were not recorded." : SaturationExportService.NeighbourDefinition),
            ("Precision", SaturationExportService.PrecisionStatement),
            ("Strongest-BSSID change", $"SSID scope: best BSSID changes when previous is not seen or new is ≥ {th.StrongestChangeHysteresisDb:0} dB stronger."),
            ("Stability score", SaturationExportService.ScoreFormula),
            ("Measurement boundary", SaturationExportService.MeasurementBoundary)
        });

        // 4. Scan health
        H1(col, "4. Scan health");
        var h = _r.Health;
        KeyValues(col, new[]
        {
            ("Cycles", $"{h.Total} total: {h.Fresh} fresh, {h.Stale} stale, {h.Failed} failed, {h.Skipped} skipped"),
            ("Scan duration", $"mean {N(h.MeanScanMs, "0")} ms, max {N(h.MaxScanMs, "0")} ms"),
            ("Largest gap between cycles", $"{h.MaxGapSeconds:0.0} s"),
            ("Errors", h.ErrorCounts.Count == 0 ? "none" : string.Join("; ", h.ErrorCounts.Select(kv => $"{kv.Key} ×{kv.Value}")))
        });

        // 5. Per-target detail
        bool firstTarget = true;
        foreach (var t in _r.Targets)
        {
            col.Item().EnsureSpace(520);
            if (firstTarget) H1(col, "5. Target detail");
            else col.Item().PaddingTop(10).LineHorizontal(0.6f).LineColor("#D1D5DB");
            firstTarget = false;
            col.Item().Text(TargetLabel(t)).FontSize(11).Bold().FontColor(HeadBg);
            bool legacy = cfg.Environment == null;      // recorded before 1.3: identity, BSS Load and neighbour RSSI were not captured
            const string NotRecorded = "not recorded (test recorded before 1.3)";
            KeyValues(col, new[]
            {
                ("Grade / score / tier", $"{t.Grade} / {N(t.StabilityScore)} / {t.Tier}"),
                ("Access point", legacy ? NotRecorded : $"{t.Vendor ?? "vendor not in table"}; {t.Security ?? "security not reported"}; {t.PhyType ?? "standard not reported"}; beacon {(t.BeaconIntervalTu is int bi ? $"{bi} TU ({bi * 1.024:0.#} ms)" : "not reported")}"),
                ("Band(s), channel(s), width", $"{t.Bands}; ch {t.Channels}; {(t.ChannelWidth is int cw ? $"{cw} MHz" : "width not advertised")}" +
                    (t.CenterFrequencyMhz is double cf ? $"; primary {cf:0} MHz" : "") + (t.IsDfs == true ? "; DFS (radar may force channel changes)" : "")),
                ("Cycles tracked", $"{t.CyclesTracked}: {t.Observed} observed, {t.Missing} missing, {t.Stale} stale, {t.Unknown} unknown; availability {N(t.AvailabilityPct)} %"),
                ("RSSI (dBm)", $"min {N(t.Min)}, P5 {N(t.P5)}, P25 {N(t.P25)}, median {N(t.Median)}, mean {N(t.Mean)}, P75 {N(t.P75)}, P95 {N(t.P95)}, max {N(t.Max)}"),
                ("Spread", $"σ {N(t.StdDev)} dB, IQR {N(t.Iqr)} dB, range {N(t.Range)} dB, trend " +
                    (t.TrendDbPerHour is double tr ? $"{tr:0.0} dB/h" : $"n/a (observed span {t.ObservedSpanSeconds / 60:0.#} min < {SaturationAnalyzerInfo.MinTrendMinutes} min)")),
                ("Link quality / SNR", $"link quality mean {N(t.LinkQualityMean)} %, min {N(t.LinkQualityMin)} %; SNR estimated {N(t.SnrEstimatedMeanDb)} dB (assumed −95 dBm floor; not measured)"),
                ("RSSI variation (dB)", $"mean {N(t.VariationMean)}, P95 {N(t.VariationP95)}, max {N(t.VariationMax)}"),
                ("Threshold exposure", $"below {th.GoodDbm}: {N(t.PctBelowGood)} %; below {th.FairDbm}: {N(t.PctBelowFair)} % (longest {t.LongestBelowFairSeconds:0} s); below {th.UnusableDbm}: {N(t.PctBelowUnusable)} % (longest {t.LongestBelowUnusableSeconds:0} s)"),
                ("Events", $"{t.DropCount} drops (worst {N(t.WorstDropDb, "0")} dB), {t.PeakCount} peaks, {t.DropoutCount} dropouts (longest {t.LongestDropoutSeconds:0} s), {t.ChannelChangeCount} channel changes"),
                (legacy ? "Neighbours (1.2 definition)" : "Neighbours (fresh)",
                    legacy ? $"co-channel: mean {N(t.CoChannelMean)}, max {N(t.CoChannelMax)}; 2.4 GHz overlapping: mean {N(t.OverlapMean)}, max {N(t.OverlapMax)} (see methodology)"
                           : $"co-channel: mean {N(t.CoChannelMean)}, max {N(t.CoChannelMax)}; overlapping: mean {N(t.OverlapMean)}, max {N(t.OverlapMax)}" +
                             (t.StrongestOverlapRssiMax is int sn ? $"; strongest neighbour up to {sn} dBm" : "")),
                ("Interference margin", legacy ? NotRecorded
                    : t.InterferenceMarginMedianDb.HasValue ? $"median {N(t.InterferenceMarginMedianDb, "0")} dB, minimum {N(t.InterferenceMarginMinDb, "0")} dB (aim ≥ 20 dB)"
                    : "no co-channel or overlapping neighbour heard fresh"),
                ("BSS Load (AP-reported)", legacy ? NotRecorded
                    : t.LoadAdvertisedPct is double la && la > 0
                        ? $"advertised in {la:0} % of samples; stations mean {N(t.StationCountMean)}, max {N(t.StationCountMax)}; channel busy mean {N(t.ChannelUtilizationMeanPct)} %, max {N(t.ChannelUtilizationMaxPct)} %"
                        : "not advertised by this AP")
            });

            var bssid = t.Bssid;
            col.Item().ShowEntire().Column(chart =>
            {
                chart.Item().PaddingTop(6).Text("RSSI timeline — y: RSSI (dBm), x: elapsed active time (hh:mm:ss). Shaded: below −67 / −70 / −80 dBm; red ticks = missing; orange spans = drops; dashed = markers").FontSize(7.5f).Italic();
                chart.Item().Height(190).Image((ImageSize size) => Render(size, (canvas, w, hgt) => SaturationCharts.DrawTimeline(canvas, w, hgt, _r, bssid)));
            });
            col.Item().ShowEntire().Column(chart =>
            {
                chart.Item().PaddingTop(4).Text("RSSI distribution — x: RSSI (dBm, 1 dB bins), y: number of observed samples").FontSize(7.5f).Italic();
                chart.Item().Height(110).Image((ImageSize size) => Render(size, (canvas, w, hgt) => SaturationCharts.DrawHistogram(canvas, w, hgt, _r, bssid)));
            });

            var ev = _r.Events.Where(e => string.Equals(e.Subject, t.Bssid, StringComparison.OrdinalIgnoreCase)).ToList();
            col.Item().PaddingTop(6).Text($"Events ({ev.Count})").Bold();
            if (ev.Count == 0) col.Item().Text($"No drops, peaks, dropouts or channel changes detected (drop/peak threshold {th.DropDb:0} dB, dropout ≥ {th.DropoutMissedCycles} missed scans).");
            else Grid(col, new[] { "Type", "Start", "End", "Dur. s", "Magn. dB", "Detail" },
                ev.Select(e => new[] { e.Type.ToString(), e.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    e.EndUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "open", N(e.DurationSeconds, "0"), N(e.MagnitudeDb, "0"), e.Detail }),
                new[] { 1f, 0.8f, 0.8f, 0.6f, 0.6f, 4f });
        }

        // 6. SSID aggregates
        if (_r.Ssids.Count > 0)
        {
            H1(col, "6. SSID aggregates (best BSSID per cycle)");
            Grid(col, new[] { "SSID", "BSSIDs", "Avail. %", "Best mean", "Best min", "Best max", "Strongest changes" },
                _r.Ssids.Select(s => new[] { s.Ssid, N(s.BssidCount), N(s.AvailabilityPct), N(s.BestMean), N(s.BestMin), N(s.BestMax), N(s.StrongestBssidChanges) }));
            var sc = _r.Events.Where(e => e.Type == SaturationEventType.StrongestBssidChange).ToList();
            if (sc.Count > 0)
                Grid(col, new[] { "Time", "SSID", "Detail" },
                    sc.Select(e => new[] { e.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Ssid ?? "", e.Detail }), new[] { 0.8f, 1.2f, 4f });
        }

        // 7. Markers
        H1(col, "7. Tester markers");
        if (_r.Markers.Count == 0) col.Item().Text("No markers were added during this test.");
        else Grid(col, new[] { "Time", "Elapsed", "Marker" },
            _r.Markers.Select(m => new[] { m.WallUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Dur(m.ElapsedMs / 1000.0), m.Text }), new[] { 0.8f, 0.8f, 4f });

        // 8. Appendix
        col.Item().PageBreak();
        H1(col, "Appendix A. Per-cycle observation log");
        var rows = _r.Cycles.SelectMany(c => c.Observations.Select(o => (c, o))).ToList();
        if (rows.Count > SaturationExportService.PdfAppendixRowLimit)
            col.Item().Text($"Showing the first {SaturationExportService.PdfAppendixRowLimit:N0} of {rows.Count:N0} rows. The complete log is in the CSV bundle (_samples.csv) and JSON export.").Bold();
        col.Item().Text($"Times are local ({SaturationExportService.LocalWithOffset(_r.StartUtc, "yyyy-MM-dd")}). Co/Ovl = fresh co-channel / overlapping neighbours; Nbr = strongest neighbour dBm; Sta/Busy = AP-advertised BSS Load.").FontSize(7).Italic();
        Grid(col, new[] { "#", "Time", "Cycle", "BSSID", "Status", "RSSI", "Ch", "Band", "MHz", "Age ms", "Co", "Ovl", "Nbr", "Sta", "Busy %" },
            rows.Take(SaturationExportService.PdfAppendixRowLimit).Select(x => new[]
            {
                x.c.Sequence.ToString(CultureInfo.InvariantCulture), x.c.WallUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), x.c.Status.ToString(),
                x.o.Bssid, x.o.Status.ToString(), N(x.o.Rssi), N(x.o.Channel), x.o.Band ?? "—", N(x.o.ChannelWidth), N(x.o.BeaconAgeMs, "0"), N(x.o.CoChannelCount),
                N(x.o.OverlapCount), N(x.o.StrongestOverlapRssi), N(x.o.StationCount), N(x.o.ChannelUtilizationPct, "0")
            }), new[] { 0.45f, 0.75f, 0.6f, 1.55f, 0.8f, 0.5f, 0.4f, 0.65f, 0.45f, 0.6f, 0.35f, 0.4f, 0.45f, 0.4f, 0.5f });
    });

    /// <summary>A4 content width in points (595.3 pt page − 2 × 1.4 cm margins).</summary>
    internal const float ContentWidthPt = 595.3f - 2 * 1.4f * 72f / 2.54f;

    /// <summary>
    /// QuestPDF hands the raster size in PIXELS (at its raster DPI). Charts are drawn in POINTS so a 7.5 pt label prints at
    /// 7.5 pt (the 1.2 charts drew 7 px text into a ~4x raster, i.e. ~2 pt on paper - unreadable).
    /// </summary>
    private static byte[] Render(ImageSize size, Action<SKCanvas, float, float> draw)
    {
        float scale = Math.Max(1f, size.Width / ContentWidthPt);
        using var bitmap = new SKBitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        draw(canvas, size.Width / scale, size.Height / scale);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>SkiaSharp charts for the saturation PDF (print palette; values are also tabulated, never colour-only).</summary>
internal static class SaturationCharts
{
    private static SKPaint Stroke(SKColor c, float w, bool dashed = false) => new()
    {
        Color = c, StrokeWidth = w, IsAntialias = true, Style = SKPaintStyle.Stroke,
        PathEffect = dashed ? SKPathEffect.CreateDash(new[] { 3f, 3f }, 0) : null
    };
    private static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static void DrawTimeline(SKCanvas canvas, float w, float h, SaturationReport r, string bssid)
    {
        const float left = 40, right = 6, top = 6, bottom = 20;
        float pw = w - left - right, ph = h - top - bottom;
        var th = r.Config.Thresholds;
        var points = r.Cycles.SelectMany(c => c.Observations.Where(o => string.Equals(o.Bssid, bssid, StringComparison.OrdinalIgnoreCase)).Select(o => (c, o))).ToList();
        var observed = points.Where(p => p.o.Status == ObservationStatus.Observed && p.o.Rssi.HasValue).ToList();
        // x axis spans the whole active test (not just up to the last scan)
        double maxT = Math.Max(1, Math.Max(r.ActiveSeconds, r.Cycles.Count > 0 ? r.Cycles[^1].ElapsedMs / 1000.0 : 1));
        int yMax = Math.Min(-20, (observed.Count > 0 ? observed.Max(p => p.o.Rssi!.Value) : -40) + 5);
        int yMin = Math.Max(-100, (observed.Count > 0 ? observed.Min(p => p.o.Rssi!.Value) : -90) - 5);
        yMin = Math.Min(yMin, th.UnusableDbm - 5);
        float X(double s) => left + (float)(s / maxT) * pw;
        float Y(double dbm) => top + (float)((yMax - dbm) / (yMax - yMin)) * ph;

        using var text = new SKFont(SKTypeface.Default, 7.5f);
        using var textPaint = Fill(new SKColor(0x37, 0x41, 0x51));
        // threshold bands
        canvas.DrawRect(left, Y(th.GoodDbm), pw, Y(th.FairDbm) - Y(th.GoodDbm), Fill(new SKColor(0xF5, 0x9E, 0x0B, 0x18)));
        canvas.DrawRect(left, Y(th.FairDbm), pw, Y(th.UnusableDbm) - Y(th.FairDbm), Fill(new SKColor(0xF9, 0x73, 0x16, 0x22)));
        canvas.DrawRect(left, Y(th.UnusableDbm), pw, top + ph - Y(th.UnusableDbm), Fill(new SKColor(0xDC, 0x26, 0x26, 0x22)));
        // grid + labels
        using var grid = Stroke(new SKColor(0xE5, 0xE7, 0xEB), 0.5f);
        for (int db = (yMax / 10) * 10; db >= yMin; db -= 10)
        {
            canvas.DrawLine(left, Y(db), left + pw, Y(db), grid);
            if (Y(db) < top + 3) continue; // label would be clipped/collide at the top edge
            canvas.DrawText($"{db} dBm", left - 3, Y(db) + 2.5f, SKTextAlign.Right, text, textPaint);
        }
        for (int i = 0; i <= 4; i++)
        {
            double s = maxT * i / 4;
            var align = i == 0 ? SKTextAlign.Left : i == 4 ? SKTextAlign.Right : SKTextAlign.Center;
            canvas.DrawText(TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss"), X(s), h - 4, align, text, textPaint);
        }
        // drop spans
        foreach (var e in r.Events.Where(e => e.Type == SaturationEventType.Drop && string.Equals(e.Subject, bssid, StringComparison.OrdinalIgnoreCase)))
        {
            double s0 = e.StartElapsedMs / 1000.0, s1 = (e.EndElapsedMs ?? e.StartElapsedMs) / 1000.0;
            canvas.DrawRect(X(s0), top, Math.Max(1.5f, X(s1) - X(s0)), ph, Fill(new SKColor(0xEA, 0x58, 0x0C, 0x30)));
        }
        // missing ticks
        using var miss = Stroke(new SKColor(0xDC, 0x26, 0x26), 1.2f);
        foreach (var p in points.Where(p => p.o.Status == ObservationStatus.Missing))
            canvas.DrawLine(X(p.c.ElapsedMs / 1000.0), top + ph - 6, X(p.c.ElapsedMs / 1000.0), top + ph, miss);
        // markers
        using var mk = Stroke(new SKColor(0x37, 0x41, 0x51), 0.8f, dashed: true);
        foreach (var m in r.Markers)
        {
            canvas.DrawLine(X(m.ElapsedMs / 1000.0), top, X(m.ElapsedMs / 1000.0), top + ph, mk);
            canvas.DrawText(m.Text.Length > 18 ? m.Text[..18] + "…" : m.Text, X(m.ElapsedMs / 1000.0) + 2, top + 8, SKTextAlign.Left, text, textPaint);
        }
        // series (break the line across non-observed cycles)
        using var line = Stroke(new SKColor(0x1D, 0x4E, 0xD8), 1.2f);
        SKPoint? prev = null;
        foreach (var p in points)
        {
            if (p.o.Status != ObservationStatus.Observed || !p.o.Rssi.HasValue) { prev = null; continue; }
            var pt = new SKPoint(X(p.c.ElapsedMs / 1000.0), Y(p.o.Rssi.Value));
            if (prev is SKPoint q) canvas.DrawLine(q, pt, line);
            else canvas.DrawCircle(pt, 1f, Fill(line.Color));
            prev = pt;
        }
        canvas.DrawRect(left, top, pw, ph, Stroke(new SKColor(0x9C, 0xA3, 0xAF), 0.6f));
    }

    public static void DrawHistogram(SKCanvas canvas, float w, float h, SaturationReport r, string bssid)
    {
        const float left = 40, right = 6, top = 6, bottom = 18;
        float pw = w - left - right, ph = h - top - bottom;
        var values = r.Cycles.SelectMany(c => c.Observations).Where(o => string.Equals(o.Bssid, bssid, StringComparison.OrdinalIgnoreCase)
            && o.Status == ObservationStatus.Observed && o.Rssi.HasValue).Select(o => o.Rssi!.Value).ToList();
        using var text = new SKFont(SKTypeface.Default, 7.5f);
        using var textPaint = Fill(new SKColor(0x37, 0x41, 0x51));
        if (values.Count == 0)
        {
            canvas.DrawText("No observed samples", w / 2, h / 2, SKTextAlign.Center, text, textPaint);
            return;
        }
        int lo = values.Min(), hi = values.Max();
        var bins = Enumerable.Range(lo, hi - lo + 1).Select(v => (v, n: values.Count(x => x == v))).ToList();
        int maxN = bins.Max(b => b.n);
        float bw = pw / bins.Count;
        foreach (var (v, n) in bins)
        {
            float bh = ph * n / maxN;
            var color = v >= -67 ? new SKColor(0x05, 0x96, 0x69) : v >= -70 ? new SKColor(0xD9, 0x77, 0x06) : v >= -80 ? new SKColor(0xEA, 0x58, 0x0C) : new SKColor(0xDC, 0x26, 0x26);
            canvas.DrawRect(left + (v - lo) * bw + 0.5f, top + ph - bh, Math.Max(0.8f, bw - 1), bh, Fill(color));
        }
        int step = Math.Max(1, (int)Math.Ceiling(bins.Count / 12.0));
        for (int i = 0; i < bins.Count; i += step)
            canvas.DrawText($"{bins[i].v}", left + i * bw + bw / 2, h - 5, SKTextAlign.Center, text, textPaint);
        canvas.DrawText($"{maxN}", left - 3, top + 7, SKTextAlign.Right, text, textPaint);
        canvas.DrawText("0", left - 3, top + ph, SKTextAlign.Right, text, textPaint);
        canvas.DrawText($"n = {values.Count}", left + pw, top + 8, SKTextAlign.Right, text, textPaint);
    }
}
