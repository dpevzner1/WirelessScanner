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
    public const string JsonSchema = "wirelessscanner.saturation_report.v1";
    public const int PdfAppendixRowLimit = 20000;

    public const string MeasurementBoundary =
        "Client-side receive view from the Windows Native Wi-Fi API on the test adapter. RSSI and link quality are driver-reported " +
        "(resolution and averaging vary by adapter/driver; not calibrated against an RF instrument). Channel/band are derived from centre " +
        "frequency; channel width from HT/VHT/HE operation elements (blank = unknown). Noise floor is UNAVAILABLE (not exposed by the API). " +
        "'RSSI variation' is the scan-to-scan change in RSSI (dB), not packet latency jitter. Throughput, latency and packet loss are not measured.";

    public const string ScoreFormula =
        "Stability score = 100 - 4*sigma - 0.5*(100 - availability%) - 3*drops/h - 5*dropouts/h - 5*channel changes/h, clamped 0-100; " +
        "rates use max(active hours, 0.25). Grades: A>=90, B>=75, C>=60, D>=40, F<40. Derived heuristic, not an industry standard.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
        samples.AppendLine(Row("TestId", "CycleSeq", "WallUtc", "ElapsedSec", "CycleStatus", "BSSID", "SSID", "ObservationStatus", "RSSI_dBm", "RSSI_Tier",
            "LinkQuality_pct", "Channel", "Band", "ChannelWidth_MHz", "BeaconAge_ms", "CoChannel_Count", "Overlap24_Count").TrimEnd() + "," + Row(geoHeads));
        foreach (var c in report.Cycles)
        {
            foreach (var o in c.Observations)
            {
                samples.AppendLine(Row(id, c.Sequence.ToString(Inv), Iso(c.WallUtc), F(c.ElapsedMs / 1000.0, "0.000"), c.Status.ToString(), o.Bssid, o.Ssid,
                    o.Status.ToString(), F(o.Rssi), o.Status == ObservationStatus.Observed ? tierByRssi(o.Rssi) : "", F(o.LinkQuality), F(o.Channel), o.Band,
                    F(o.ChannelWidth), F(o.BeaconAgeMs, "0"), F(o.CoChannelCount), F(o.OverlapCount)) + "," + Row(geoCells));
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
            "CoChannel_Mean", "CoChannel_Max", "Overlap_Mean", "Overlap_Max", "StabilityScore", "Grade", "Tier", "Findings") + "," + Row(geoHeads));
        foreach (var t in report.Targets)
        {
            summary.AppendLine(Row(t.Bssid, t.Ssid, t.Bands, t.Channels, F(t.PrimaryChannel), F(t.ChannelWidth), F(t.CyclesTracked), F(t.Observed), F(t.Missing),
                F(t.Stale), F(t.Unknown), F(t.AvailabilityPct), F(t.Min), F(t.Max), F(t.Mean), F(t.Median), F(t.StdDev), F(t.P5), F(t.P25), F(t.P75), F(t.P95),
                F(t.Iqr), F(t.Range), F(t.VariationMean), F(t.VariationP95), F(t.VariationMax), F(t.TrendDbPerHour), F(t.PctBelowGood), F(t.PctBelowFair),
                F(t.PctBelowUnusable), F(t.LongestBelowFairSeconds, "0.0"), F(t.LongestBelowUnusableSeconds, "0.0"), F(t.DropCount), F(t.PeakCount),
                F(t.DropoutCount), F(t.ChannelChangeCount), F(t.WorstDropDb), F(t.LongestDropoutSeconds, "0.0"), F(t.CoChannelMean), F(t.CoChannelMax),
                F(t.OverlapMean), F(t.OverlapMax), F(t.StabilityScore), t.Grade, t.Tier, string.Join(" | ", t.Findings)) + "," + Row(geoCells));
        }

        var cycles = new StringBuilder();
        cycles.AppendLine(Row("CycleSeq", "WallUtc", "ElapsedSec", "Status", "ScanDuration_ms", "BssCount", "ErrorCode", "ErrorMessage"));
        foreach (var c in report.Cycles)
        {
            cycles.AppendLine(Row(c.Sequence.ToString(Inv), Iso(c.WallUtc), F(c.ElapsedMs / 1000.0, "0.000"), c.Status.ToString(), F(c.DurationMs, "0"),
                F(c.BssCount), c.ErrorCode, c.ErrorMessage));
        }

        var markers = new StringBuilder();
        markers.AppendLine(Row("WallUtc", "ElapsedSec", "Text"));
        foreach (var m in report.Markers) markers.AppendLine(Row(Iso(m.WallUtc), F(m.ElapsedMs / 1000.0, "0.000"), m.Text));

        var files = new List<(string Path, StringBuilder Content)>
        {
            (P("samples"), samples), (P("events"), events), (P("summary"), summary), (P("cycles"), cycles), (P("markers"), markers)
        };
        foreach (var (path, content) in files)
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(content.ToString());
            await WriteAtomicAsync(path, s => s.WriteAsync(bytes).AsTask());
        }
        return files.Select(f => f.Path).ToList();
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
            generatedUtc = DateTime.UtcNow,
            measurementBoundary = MeasurementBoundary,
            scoreFormula = ScoreFormula,
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
                t.Span($"{_r.AnalyticsVersion} | Test {_r.Config.TestId} | Page ").FontSize(7).FontColor("#6B7280");
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
            col.Item().Text($"{_r.Config.Name}  |  {_r.Config.Location}  |  {_r.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm}").FontSize(9).FontColor("#4B5563");
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
            ("Started (local)", _r.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("Ended (local)", _r.EndUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "running"),
            ("Active duration", Dur(_r.ActiveSeconds) + (cfg.Duration.HasValue ? $" of planned {Dur(cfg.Duration.Value.TotalSeconds)}" : " (until stopped)")),
            ("Scan interval / timeout", $"{cfg.Interval.TotalSeconds:0.#} s / {cfg.ScanTimeout.TotalSeconds:0.#} s"),
            ("Final status", _r.Status.ToString()), ("Notes", cfg.Notes)
        });

        // 2. Executive summary
        H1(col, "2. Executive summary");
        Grid(col, new[] { "Target", "Grade", "Score", "Avail. %", "Mean dBm", "σ dB", "Min/Max", "Drops", "Dropouts", "Ch. chg" },
            _r.Targets.Select(t => new[] { TargetLabel(t), t.Grade, N(t.StabilityScore), N(t.AvailabilityPct), N(t.Mean), N(t.StdDev),
                $"{N(t.Min)} / {N(t.Max)}", N(t.DropCount), N(t.DropoutCount), N(t.ChannelChangeCount) }),
            new[] { 3.2f, 0.7f, 0.8f, 0.9f, 1f, 0.7f, 1.1f, 0.7f, 0.9f, 0.8f });
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
            ("Thresholds", $"Good ≥ {th.GoodDbm} dBm, Fair ≥ {th.FairDbm} dBm, Unusable < {th.UnusableDbm} dBm. Exposure is time-weighted by cycle spacing."),
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
            col.Item().PageBreak();
            if (firstTarget) H1(col, "5. Target detail");
            firstTarget = false;
            col.Item().Text(TargetLabel(t)).FontSize(11).Bold().FontColor(HeadBg);
            KeyValues(col, new[]
            {
                ("Grade / score / tier", $"{t.Grade} / {N(t.StabilityScore)} / {t.Tier}"),
                ("Band(s), channel(s), width", $"{t.Bands}; ch {t.Channels}; {N(t.ChannelWidth)} MHz"),
                ("Cycles tracked", $"{t.CyclesTracked}: {t.Observed} observed, {t.Missing} missing, {t.Stale} stale, {t.Unknown} unknown; availability {N(t.AvailabilityPct)} %"),
                ("RSSI (dBm)", $"min {N(t.Min)}, P5 {N(t.P5)}, P25 {N(t.P25)}, median {N(t.Median)}, mean {N(t.Mean)}, P75 {N(t.P75)}, P95 {N(t.P95)}, max {N(t.Max)}"),
                ("Spread", $"σ {N(t.StdDev)} dB, IQR {N(t.Iqr)} dB, range {N(t.Range)} dB, trend {N(t.TrendDbPerHour)} dB/h"),
                ("RSSI variation (dB)", $"mean {N(t.VariationMean)}, P95 {N(t.VariationP95)}, max {N(t.VariationMax)}"),
                ("Threshold exposure", $"below {th.GoodDbm}: {N(t.PctBelowGood)} %; below {th.FairDbm}: {N(t.PctBelowFair)} % (longest {t.LongestBelowFairSeconds:0} s); below {th.UnusableDbm}: {N(t.PctBelowUnusable)} % (longest {t.LongestBelowUnusableSeconds:0} s)"),
                ("Events", $"{t.DropCount} drops (worst {N(t.WorstDropDb, "0")} dB), {t.PeakCount} peaks, {t.DropoutCount} dropouts (longest {t.LongestDropoutSeconds:0} s), {t.ChannelChangeCount} channel changes"),
                ("Co-channel environment", $"same channel: mean {N(t.CoChannelMean)}, max {N(t.CoChannelMax)}; 2.4 GHz overlapping: mean {N(t.OverlapMean)}, max {N(t.OverlapMax)}")
            });

            col.Item().PaddingTop(6).Text("RSSI timeline (shaded bands: <-67 / <-70 / <-80 dBm; red ticks = missing; orange spans = drops; dashed = markers)").FontSize(7.5f).Italic();
            col.Item().Height(190).Image((ImageSize size) => Render(size, (canvas, w, hgt) => SaturationCharts.DrawTimeline(canvas, w, hgt, _r, t.Bssid)));
            col.Item().PaddingTop(4).Text("RSSI distribution (1 dB bins, observed samples)").FontSize(7.5f).Italic();
            col.Item().Height(110).Image((ImageSize size) => Render(size, (canvas, w, hgt) => SaturationCharts.DrawHistogram(canvas, w, hgt, _r, t.Bssid)));

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
        Grid(col, new[] { "#", "Time", "Cycle", "BSSID", "Status", "RSSI", "Ch", "Band", "MHz", "Age ms", "Co-ch" },
            rows.Take(SaturationExportService.PdfAppendixRowLimit).Select(x => new[]
            {
                x.c.Sequence.ToString(CultureInfo.InvariantCulture), x.c.WallUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), x.c.Status.ToString(),
                x.o.Bssid, x.o.Status.ToString(), N(x.o.Rssi), N(x.o.Channel), x.o.Band ?? "—", N(x.o.ChannelWidth), N(x.o.BeaconAgeMs, "0"), N(x.o.CoChannelCount)
            }), new[] { 0.5f, 0.8f, 0.7f, 1.6f, 0.8f, 0.6f, 0.4f, 0.7f, 0.5f, 0.7f, 0.5f });
    });

    private static byte[] Render(ImageSize size, Action<SKCanvas, float, float> draw)
    {
        const float scale = 2f;
        using var bitmap = new SKBitmap(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale)));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        draw(canvas, size.Width, size.Height);
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
        const float left = 34, right = 6, top = 6, bottom = 18;
        float pw = w - left - right, ph = h - top - bottom;
        var th = r.Config.Thresholds;
        var points = r.Cycles.SelectMany(c => c.Observations.Where(o => string.Equals(o.Bssid, bssid, StringComparison.OrdinalIgnoreCase)).Select(o => (c, o))).ToList();
        var observed = points.Where(p => p.o.Status == ObservationStatus.Observed && p.o.Rssi.HasValue).ToList();
        double maxT = Math.Max(1, r.Cycles.Count > 0 ? r.Cycles[^1].ElapsedMs / 1000.0 : 1);
        int yMax = Math.Min(-20, (observed.Count > 0 ? observed.Max(p => p.o.Rssi!.Value) : -40) + 5);
        int yMin = Math.Max(-100, (observed.Count > 0 ? observed.Min(p => p.o.Rssi!.Value) : -90) - 5);
        yMin = Math.Min(yMin, th.UnusableDbm - 5);
        float X(double s) => left + (float)(s / maxT) * pw;
        float Y(double dbm) => top + (float)((yMax - dbm) / (yMax - yMin)) * ph;

        using var text = new SKFont(SKTypeface.Default, 7);
        using var textPaint = Fill(new SKColor(0x4B, 0x55, 0x63));
        // threshold bands
        canvas.DrawRect(left, Y(th.GoodDbm), pw, Y(th.FairDbm) - Y(th.GoodDbm), Fill(new SKColor(0xF5, 0x9E, 0x0B, 0x18)));
        canvas.DrawRect(left, Y(th.FairDbm), pw, Y(th.UnusableDbm) - Y(th.FairDbm), Fill(new SKColor(0xF9, 0x73, 0x16, 0x22)));
        canvas.DrawRect(left, Y(th.UnusableDbm), pw, top + ph - Y(th.UnusableDbm), Fill(new SKColor(0xDC, 0x26, 0x26, 0x22)));
        // grid + labels
        using var grid = Stroke(new SKColor(0xE5, 0xE7, 0xEB), 0.5f);
        for (int db = (yMax / 10) * 10; db >= yMin; db -= 10)
        {
            canvas.DrawLine(left, Y(db), left + pw, Y(db), grid);
            canvas.DrawText($"{db}", 2, Math.Max(Y(db) + 2.5f, top + 7), SKTextAlign.Left, text, textPaint);
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
        const float left = 34, right = 6, top = 6, bottom = 16;
        float pw = w - left - right, ph = h - top - bottom;
        var values = r.Cycles.SelectMany(c => c.Observations).Where(o => string.Equals(o.Bssid, bssid, StringComparison.OrdinalIgnoreCase)
            && o.Status == ObservationStatus.Observed && o.Rssi.HasValue).Select(o => o.Rssi!.Value).ToList();
        using var text = new SKFont(SKTypeface.Default, 7);
        using var textPaint = Fill(new SKColor(0x4B, 0x55, 0x63));
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
        int step = Math.Max(1, bins.Count / 8);
        for (int i = 0; i < bins.Count; i += step)
            canvas.DrawText($"{bins[i].v}", left + i * bw + bw / 2, h - 4, SKTextAlign.Center, text, textPaint);
        canvas.DrawText($"n={values.Count}", 2, top + 8, SKTextAlign.Left, text, textPaint);
        canvas.DrawText($"max {maxN}", 2, top + 18, SKTextAlign.Left, text, textPaint);
    }
}
