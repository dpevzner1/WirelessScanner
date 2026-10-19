using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using WirelessScanner.Application;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

/// <summary>Connection test reports (DR-0014): PDF, CSV bundle and JSON (schema wirelessscanner.connection_report.v1). Writes are atomic.</summary>
public sealed class ConnectionExportService : IConnectionExportService
{
    public const string JsonSchema = "wirelessscanner.connection_report.v1";
    public const int PdfAppendixRowLimit = 20_000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Method =
        "Probes are sent from this PC's Wi-Fi address only (bound to the Wi-Fi interface) to the router (ICMP echo), the first ISP hop " +
        "(ICMP echo, or a TTL-limited probe when the hop does not answer echo), public anycast targets (TCP connect to port 443 = primary, plus ICMP), " +
        "the configured DNS resolvers (one A query) and one HTTPS address (cold request: name resolution + TCP + TLS + first byte). Light intensity: " +
        "router every 1 s, ISP and internet every 2 s, DNS every 10 s, web every 30 s; Detailed halves the intervals. The Wi-Fi connection itself is " +
        "never changed. Round-trip times are measured with the PC's high-resolution clock around each probe.";

    public const string Definitions =
        "Loss % = failed ÷ sent. Availability % = share of probe rounds of a segment with at least one successful probe. Percentiles: nearest rank over " +
        "successful probes. Jitter = mean |RTT(n) − RTT(n−1)| over consecutive successful probes of one target; delay variation P95 = 95th percentile of " +
        "the same differences (round-trip; one-way per direction only with a LAN responder). Outage = ≥ 3 consecutive failed rounds, attributed to the first " +
        "failing segment upstream (Wi-Fi link → router → ISP hop → internet; DNS and web after those). Latency spike = RTT > max(3 × baseline, baseline + 100 ms), " +
        "baseline = median of the previous 30 successful RTTs of that target. A TCP reset (port closed) is recorded as Refused without a round trip (Windows retries the SYN first). ISP-hop failures while " +
        "the internet stays reachable are treated as reply rate-limiting, not a fault.";

    public const string Limits =
        "Round-trip measurements only (one-way delay needs synchronised clocks). Routers may deprioritise ICMP; TCP connect is therefore the primary internet " +
        "metric. The voice score is an ESTIMATE from a simplified ITU-T G.107 E-model (one-way delay ≈ mean RTT ÷ 2), not a measured call. Results describe " +
        "this PC at this place on this connection; other devices may differ. Speed tests depend on the server and its path, and saturate the link while they run; " +
        "regular probes pause during speed and two-way tests.";

    private static string F(double? v, string fmt = "0.###") => v.HasValue ? v.Value.ToString(fmt, Inv) : "";
    private static string F(int? v) => v.HasValue ? v.Value.ToString(Inv) : "";
    private static string Iso(DateTime? t) => t.HasValue ? DateTime.SpecifyKind(t.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv) : "";
    private static string Row(params string?[] cells) => string.Join(",", cells.Select(SaturationExportService.Csv));

    internal static IEnumerable<(string, string)> InfoRows(ConnectionReport r)
    {
        var c = r.Config;
        var x = c.ContextAtStart;
        var e = c.Environment;
        yield return ("ReportSchema", JsonSchema);
        yield return ("AnalyticsVersion", r.AnalyticsVersion);
        yield return ("GeneratedBy", $"WirelessScanner {SaturationExportService.GeneratorVersion()}");
        yield return ("TestId", c.TestId.ToString());
        yield return ("TestName", c.Name);
        yield return ("Status", r.Status.ToString());
        yield return ("StartUtc", Iso(r.StartUtc));
        yield return ("StartLocal", SaturationExportService.LocalIso(r.StartUtc));
        yield return ("EndUtc", Iso(r.EndUtc));
        yield return ("EndLocal", SaturationExportService.LocalIso(r.EndUtc));
        yield return ("ActiveSeconds", F(r.ActiveSeconds, "0.000"));
        yield return ("PlannedSeconds", c.Duration.HasValue ? F(c.Duration.Value.TotalSeconds, "0") : "until stopped");
        yield return ("Intensity", $"{c.Intensity} (tick {c.TickMilliseconds} ms, about {F(c.EstimatedKbps(), "0.0")} kbit/s)");
        yield return ("Segments", string.Join(" | ", new[] { c.ProbeWifiLink ? "Wi-Fi link" : null, c.ProbeGateway ? "router" : null, c.ProbeIsp ? "ISP hop" : null,
            c.ProbeInternet ? "internet" : null, c.ProbeDns ? "DNS" : null, c.ProbeWeb ? "web" : null }.Where(s => s != null)));
        yield return ("InternetTargets", string.Join(" | ", c.InternetTargets) + $" (TCP {c.InternetTcpPort} + ICMP)");
        yield return ("DnsQueryName", c.DnsQueryName);
        yield return ("WebUrl", c.WebUrl);
        yield return ("Ssid", x?.Ssid ?? "");
        yield return ("Bssid", x?.Bssid ?? "");
        yield return ("ChannelBand", $"{x?.Channel} {x?.Band}".Trim());
        yield return ("Phy", x?.PhyType ?? "");
        yield return ("RssiAtStart_dBm", F(x?.Rssi));
        yield return ("RxTxRate_Mbps", $"{F(x?.RxRateMbps, "0.#")} / {F(x?.TxRateMbps, "0.#")}");
        yield return ("LocalIPv4", x?.LocalIPv4 ?? "");
        yield return ("Gateways", string.Join(" | ", x?.Gateways ?? Array.Empty<string>()));
        yield return ("DnsServers", string.Join(" | ", x?.DnsServers ?? Array.Empty<string>()));
        yield return ("PathHops", string.Join(" → ", x?.PathHops ?? Array.Empty<string>()));
        yield return ("IspHop", x?.IspHop == null ? "not found" : $"{x.IspHop} (TTL {x.IspHopTtl}, {(x.IspHopAnswersEcho ? "echo" : "TTL-limited probe")})");
        yield return ("OtherActiveInterfaces", x?.OtherActiveInterfaces.Count > 0 ? string.Join(" | ", x.OtherActiveInterfaces) + " (probes bound to Wi-Fi)" : "none");
        yield return ("LinkedSaturationTest", c.LinkedSaturationTestId?.ToString() ?? "");
        yield return ("Location", c.Location);
        yield return ("Facility", c.Facility);
        yield return ("Operator", c.Operator);
        yield return ("Client", c.Client);
        yield return ("Notes", c.Notes);
        yield return ("Latitude", F(c.Geo.Latitude, "0.000000"));
        yield return ("Longitude", F(c.Geo.Longitude, "0.000000"));
        yield return ("Building/Floor/Zone", $"{c.Geo.Building} / {c.Geo.Floor} / {c.Geo.Zone}");
        yield return ("Adapter", e?.AdapterDescription ?? c.InterfaceName);
        yield return ("DriverVersion", e?.DriverVersion ?? "not recorded");
        yield return ("RecordedWithApp", e?.AppVersion ?? "");
        yield return ("OS", e?.OsDescription ?? "");
        yield return ("Machine", e?.MachineName ?? "");
        yield return ("TimeZone", e == null ? "" : $"{e.TimeZoneId} (UTC offset at start {e.UtcOffsetMinutes / 60.0:+0.##;-0.##;0} h)");
        yield return ("Totals", $"{r.Totals.ProbesSent} probes sent = {r.Totals.ProbesOk} ok + {r.Totals.ProbesFailed} failed; {r.Totals.LinkSamples} link samples; {r.Totals.SleepGaps} gap(s), {F(r.Totals.UnmeasuredSeconds, "0.0")} s unmeasured");
        yield return ("VoiceEstimate", r.VoiceMos.HasValue ? $"R {F(r.VoiceRFactor, "0.0")}, MOS {F(r.VoiceMos, "0.00")} (estimate)" : "not available");
        yield return ("Method", Method);
        yield return ("Definitions", Definitions);
        yield return ("Limits", Limits);
        yield return ("VerdictThresholds", string.Join(" | ", ConnectionAnalyzer.VerdictThresholds.Select(t =>
            FormattableString.Invariant($"{t.Use}: GOOD p95 ≤ {t.GoodP95} ms, jitter ≤ {t.GoodJitter} ms, loss ≤ {t.GoodLoss} %; FAIR p95 ≤ {t.FairP95}, jitter ≤ {t.FairJitter}, loss ≤ {t.FairLoss}"))));
    }

    public async Task<IReadOnlyList<string>> ExportCsvBundleAsync(string directory, string filePrefix, ConnectionReport r)
    {
        Directory.CreateDirectory(directory);
        string P(string name) => Path.Combine(directory, $"{filePrefix}_{name}.csv");
        string id = r.Config.TestId.ToString();
        var geo = r.Config.Geo;
        string[] geoCells = { F(geo.Latitude, "0.000000"), F(geo.Longitude, "0.000000"), geo.Building, geo.Floor, geo.Zone, r.Config.Location };
        string geoHead = Row("Latitude", "Longitude", "Building", "Floor", "Zone", "Location");

        var info = new StringBuilder(Row("Key", "Value") + "\n");
        foreach (var (k, v) in InfoRows(r)) info.AppendLine(Row(k, v));

        var probes = new StringBuilder();
        probes.AppendLine($"# WirelessScanner connection test probes | test {id} | {r.AnalyticsVersion} | one row per probe, failures included");
        probes.AppendLine(Row("TestId", "Round", "WallUtc", "WallLocal", "ElapsedSec", "Segment", "Target", "Method", "Outcome", "RTT_ms", "Detail") + "," + geoHead);
        foreach (var p in r.Probes)
            probes.AppendLine(Row(id, p.Sequence.ToString(Inv), Iso(p.WallUtc), SaturationExportService.LocalIso(p.WallUtc), F(p.ElapsedMs / 1000.0, "0.000"),
                p.Segment.ToString(), p.Target, p.Method.ToString(), p.Outcome.ToString(), F(p.RttMs, "0.000"), p.Detail) + "," + Row(geoCells));

        var link = new StringBuilder(Row("TestId", "Tick", "WallUtc", "WallLocal", "ElapsedSec", "Connected", "SSID", "BSSID", "Channel", "RSSI_dBm", "SignalQuality_pct",
            "RxRate_Mbps", "TxRate_Mbps", "LocalIp", "Gateway", "IfBytesIn", "IfBytesOut", "TxFrames", "TxRetries", "TxFailed", "AckFailures", "RxFrames", "FcsErrors", "GapMs") + "\n");
        foreach (var l in r.LinkSamples)
            link.AppendLine(Row(id, l.Sequence.ToString(Inv), Iso(l.WallUtc), SaturationExportService.LocalIso(l.WallUtc), F(l.ElapsedMs / 1000.0, "0.000"), l.Connected ? "yes" : "no",
                l.Ssid, l.Bssid, F(l.Channel), F(l.Rssi), F(l.SignalQualityPct), F(l.RxRateMbps, "0.#"), F(l.TxRateMbps, "0.#"), l.LocalIp, l.Gateway,
                l.IfBytesIn?.ToString(Inv), l.IfBytesOut?.ToString(Inv), l.Counters?.TxFrames.ToString(Inv), l.Counters?.TxRetries.ToString(Inv), l.Counters?.TxFailed.ToString(Inv),
                l.Counters?.AckFailures.ToString(Inv), l.Counters?.RxFrames.ToString(Inv), l.Counters?.FcsErrors.ToString(Inv), l.GapMs?.ToString(Inv)));

        var segs = new StringBuilder(Row("Level", "Segment", "Target", "Method", "Sent", "Ok", "Failed", "Loss_pct", "Availability_pct", "RTT_min_ms", "RTT_p50_ms",
            "RTT_p95_ms", "RTT_p99_ms", "RTT_max_ms", "RTT_mean_ms", "Jitter_ms", "DelayVariation_p95_ms", "Spikes", "Outages", "LongestOutage_s", "FailureReasons",
            "TestId", "AnalyticsVersion") + "," + geoHead + "\n");
        foreach (var (level, list) in new[] { ("segment", r.Segments), ("target", r.Targets) })
            foreach (var s in list)
                segs.AppendLine(Row(level, s.Segment.ToString(), s.Target, s.Method.ToString(), F(s.Sent), F(s.Ok), F(s.Failed), F(s.LossPct), F(s.AvailabilityPct),
                    F(s.RttMin), F(s.RttP50), F(s.RttP95), F(s.RttP99), F(s.RttMax), F(s.RttMean), F(s.JitterMs), F(s.DelayVariationP95Ms), F(s.Spikes), F(s.Outages),
                    F(s.LongestOutageSeconds, "0.0"), string.Join(" | ", s.FailureReasons.Select(kv => $"{kv.Key} {kv.Value}")), id, r.AnalyticsVersion) + "," + Row(geoCells));

        var events = new StringBuilder(Row("Type", "Segment", "AttributedTo", "StartUtc", "EndUtc", "StartElapsedSec", "EndElapsedSec", "Duration_s", "Probes", "PeakRTT_ms", "Detail") + "\n");
        foreach (var e in r.Events)
            events.AppendLine(Row(e.Type.ToString(), e.Segment?.ToString(), e.AttributedTo?.ToString(), Iso(e.StartUtc), Iso(e.EndUtc), F(e.StartElapsedMs / 1000.0, "0.000"),
                e.EndElapsedMs.HasValue ? F(e.EndElapsedMs.Value / 1000.0, "0.000") : "", F(e.DurationSeconds, "0.0"), F(e.Probes), F(e.PeakRttMs, "0.0"), e.Detail));

        var files = new List<(string, StringBuilder)> { (P("info"), info), (P("probes"), probes), (P("link"), link), (P("segments"), segs), (P("events"), events) };

        if (r.SpeedTests.Count > 0)
        {
            var sp = new StringBuilder(Row("StartUtc", "Provider", "Server", "Streams", "SecondsPerDirection", "Download_Mbps", "Upload_Mbps", "BytesDown", "BytesUp", "IdleLatency_ms",
                "LoadedLatencyDown_ms", "LoadedLatencyUp_ms", "LoadedLatency_p95_ms", "LatencyIncrease_ms", "BufferbloatGrade", "Responsiveness_RPM_approx", "LatencyTarget", "Error") + "\n");
            foreach (var s in r.SpeedTests)
                sp.AppendLine(Row(Iso(s.StartUtc), s.Provider, s.Server, F(s.Streams), F(s.SecondsPerDirection), F(s.DownloadMbps, "0.00"), F(s.UploadMbps, "0.00"),
                    s.BytesDown.ToString(Inv), s.BytesUp.ToString(Inv), F(s.IdleLatencyMs), F(s.LoadedLatencyDownMs), F(s.LoadedLatencyUpMs), F(s.LoadedLatencyP95Ms),
                    F(s.LatencyIncreaseMs), s.BufferbloatGrade, F(s.ResponsivenessRpm, "0"), s.LatencyTarget, s.Error));
            files.Add((P("speed"), sp));
        }
        if (r.TwoWayTests.Count > 0)
        {
            var tw = new StringBuilder(Row("StartUtc", "Responder", "Seconds", "PacketsPerSecond", "PayloadBytes", "Sent", "ReceivedByResponder", "RepliesSent", "ReceivedBack",
                "UpLoss_pct", "DownLoss_pct", "RTT_p50_ms", "RTT_p95_ms", "UpJitter_ms", "DownJitter_ms", "UpReordered", "DownReordered", "Duplicates", "MOS_up_est", "MOS_down_est",
                "LanDown_Mbps", "LanUp_Mbps", "Error") + "\n");
            foreach (var t in r.TwoWayTests)
                tw.AppendLine(Row(Iso(t.StartUtc), t.Responder, F(t.Seconds), F(t.PacketsPerSecond), F(t.PayloadBytes), F(t.Sent), F(t.ReceivedByResponder), F(t.RepliesSent),
                    F(t.ReceivedBack), F(t.UpLossPct), F(t.DownLossPct), F(t.RttP50Ms), F(t.RttP95Ms), F(t.UpJitterMs), F(t.DownJitterMs), F(t.UpReordered), F(t.DownReordered),
                    F(t.Duplicates), F(t.MosUp, "0.00"), F(t.MosDown, "0.00"), F(t.LanDownMbps, "0.00"), F(t.LanUpMbps, "0.00"), t.Error));
            files.Add((P("twoway"), tw));
        }
        var markers = new StringBuilder(Row("WallUtc", "ElapsedSec", "Text") + "\n");
        foreach (var m in r.Markers) markers.AppendLine(Row(Iso(m.WallUtc), F(m.ElapsedMs / 1000.0, "0.000"), m.Text));
        files.Add((P("markers"), markers));

        foreach (var (path, content) in files)
        {
            var bytes = new UTF8Encoding(true).GetBytes(content.ToString());
            await SaturationExportService.WriteAtomicAsync(path, s => s.WriteAsync(bytes).AsTask());
        }
        return files.Select(f => f.Item1).ToList();
    }

    public Task ExportJsonAsync(string filePath, ConnectionReport r)
    {
        var payload = new
        {
            schema = JsonSchema,
            analyticsVersion = r.AnalyticsVersion,
            generatedBy = $"WirelessScanner {SaturationExportService.GeneratorVersion()}",
            generatedUtc = DateTime.UtcNow,
            method = Method,
            definitions = Definitions,
            limits = Limits,
            verdictThresholds = ConnectionAnalyzer.VerdictThresholds.Select(t => new { use = t.Use, t.GoodP95, t.GoodJitter, t.GoodLoss, t.FairP95, t.FairJitter, t.FairLoss }),
            test = r.Config,
            environment = r.Config.Environment,
            connection = r.Config.ContextAtStart,
            startLocal = SaturationExportService.LocalIso(r.StartUtc),
            endLocal = SaturationExportService.LocalIso(r.EndUtc),
            r.StartUtc,
            r.EndUtc,
            r.ActiveSeconds,
            r.Status,
            totals = r.Totals,
            verdicts = r.Verdicts,
            voice = new { rFactor = r.VoiceRFactor, mos = r.VoiceMos, note = "estimate (simplified ITU-T G.107 E-model)" },
            problemsBySegment = r.ProblemsBySegment.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            segments = r.Segments,
            targets = r.Targets,
            link = r.Link,
            speedTests = r.SpeedTests,
            twoWayTests = r.TwoWayTests,
            events = r.Events,
            markers = r.Markers,
            probes = r.Probes,
            linkSamples = r.LinkSamples
        };
        return SaturationExportService.WriteAtomicAsync(filePath, s => JsonSerializer.SerializeAsync(s, payload, SaturationExportService.JsonOptions));
    }

    public Task ExportPdfAsync(string filePath, ConnectionReport r)
    {
        var doc = new ConnectionReportDocument(r);
        return SaturationExportService.WriteAtomicAsync(filePath, s => { doc.GeneratePdf(s); return Task.CompletedTask; });
    }
}

internal sealed class ConnectionReportDocument : IDocument
{
    private const string Accent = "#B45309";
    private const string HeadBg = "#1F2937";
    private readonly ConnectionReport _r;
    public ConnectionReportDocument(ConnectionReport r) => _r = r;

    public DocumentMetadata GetMetadata() => new()
    {
        Title = $"Connection Test - {_r.Config.Name}",
        Author = string.IsNullOrWhiteSpace(_r.Config.Operator) ? "WirelessScanner" : _r.Config.Operator,
        Subject = "Wi-Fi connection and internet quality report",
        Creator = "WirelessScanner"
    };

    public void Compose(IDocumentContainer container) => container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(1.4f, Unit.Centimetre);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(x => x.FontSize(8.5f).FontFamily("Arial"));
        page.Header().Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("CONNECTION TEST REPORT").FontSize(16).Bold().FontColor(Accent);
                col.Item().Text($"{_r.Config.Name}  |  {_r.Config.Location}  |  {SaturationExportService.LocalWithOffset(_r.StartUtc, "yyyy-MM-dd HH:mm")}").FontSize(9).FontColor("#4B5563");
            });
            row.ConstantItem(110).AlignRight().Text("WirelessScanner").FontSize(11).Bold().FontColor(HeadBg);
        });
        page.Content().Element(Content);
        page.Footer().AlignCenter().Text(t =>
        {
            t.Span($"WirelessScanner {SaturationExportService.GeneratorVersion()} | {_r.AnalyticsVersion} | Test {_r.Config.TestId} | Page ").FontSize(7).FontColor("#6B7280");
            t.CurrentPageNumber().FontSize(7);
            t.Span(" of ").FontSize(7).FontColor("#6B7280");
            t.TotalPages().FontSize(7);
        });
    });

    private static string N(double? v, string fmt = "0.0") => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";
    private static string N(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "—";
    private static void H1(ColumnDescriptor col, string text) => col.Item().PaddingTop(12).PaddingBottom(4).Text(text).FontSize(12).Bold().FontColor(Accent);

    private static void KeyValues(ColumnDescriptor col, IEnumerable<(string K, string V)> rows) => col.Item().Table(t =>
    {
        t.ColumnsDefinition(cd => { cd.ConstantColumn(150); cd.RelativeColumn(); });
        foreach (var (k, v) in rows)
        {
            t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(k).Bold();
            t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(string.IsNullOrWhiteSpace(v) ? "—" : v);
        }
    });

    private static void Grid(ColumnDescriptor col, string[] headers, IEnumerable<string[]> rows, float[]? widths = null) => col.Item().Table(t =>
    {
        t.ColumnsDefinition(cd => { for (int i = 0; i < headers.Length; i++) cd.RelativeColumn(widths?[i] ?? 1); });
        t.Header(h => { foreach (var head in headers) h.Cell().Background(HeadBg).Padding(2).Text(head).FontColor(Colors.White).Bold().FontSize(7.5f); });
        foreach (var r in rows) foreach (var cell in r) t.Cell().BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(2).Text(cell ?? "").FontSize(7.5f);
    });

    private void Content(IContainer c) => c.Column(col =>
    {
        var cfg = _r.Config;
        var x = cfg.ContextAtStart;
        H1(col, "1. Test details");
        KeyValues(col, new[]
        {
            ("Test", $"{cfg.Name} ({_r.Status})"), ("Location", $"{cfg.Location}  {cfg.Geo.CoordinatesText}"), ("Building / floor / zone", $"{cfg.Geo.Building} / {cfg.Geo.Floor} / {cfg.Geo.Zone}"),
            ("Start", $"{SaturationExportService.LocalWithOffset(_r.StartUtc)}  ({_r.StartUtc:yyyy-MM-dd HH:mm:ss} UTC)"),
            ("End", _r.EndUtc.HasValue ? $"{SaturationExportService.LocalWithOffset(_r.EndUtc.Value)}  ({_r.EndUtc:yyyy-MM-dd HH:mm:ss} UTC)" : "running"),
            ("Active / planned", $"{TimeSpan.FromSeconds(_r.ActiveSeconds):hh\\:mm\\:ss} / {(cfg.Duration.HasValue ? cfg.Duration.Value.ToString(@"hh\:mm\:ss") : "until stopped")}"),
            ("Intensity", $"{cfg.Intensity}: about {cfg.EstimatedKbps():0.0} kbit/s of probe traffic"),
            ("Operator / client / facility", $"{cfg.Operator} / {cfg.Client} / {cfg.Facility}"), ("Notes", cfg.Notes),
            ("Linked saturation test", cfg.LinkedSaturationTestId?.ToString() ?? "none")
        });

        H1(col, "2. What was tested (your connection)");
        KeyValues(col, new[]
        {
            ("Network / access point", $"{x?.Ssid} / {x?.Bssid}"), ("Channel / band / standard", $"{x?.Channel} / {x?.Band} / {x?.PhyType}"),
            ("Signal / PHY rate at start", $"{N(x?.Rssi)} dBm / rx {N(x?.RxRateMbps)} Mbps, tx {N(x?.TxRateMbps)} Mbps"),
            ("This PC", $"{x?.LocalIPv4} on {cfg.Environment?.AdapterDescription ?? cfg.InterfaceName} (driver {cfg.Environment?.DriverVersion ?? "not recorded"})"),
            ("Router / DNS", $"{string.Join(", ", x?.Gateways ?? Array.Empty<string>())} / {string.Join(", ", x?.DnsServers ?? Array.Empty<string>())}"),
            ("Path at start", string.Join(" → ", x?.PathHops ?? Array.Empty<string>())),
            ("ISP hop", x?.IspHop == null ? "not found (ISP segment not measured)" : $"{x.IspHop} at TTL {x.IspHopTtl} ({(x.IspHopAnswersEcho ? "echo" : "TTL-limited probe")})"),
            ("Internet targets", $"{string.Join(", ", cfg.InternetTargets)} (TCP {cfg.InternetTcpPort} primary + ICMP)"), ("DNS name / web address", $"{cfg.DnsQueryName} / {cfg.WebUrl}"),
            ("Other active interfaces", x?.OtherActiveInterfaces.Count > 0 ? string.Join(", ", x.OtherActiveInterfaces) + " — every probe was bound to Wi-Fi" : "none")
        });

        H1(col, "3. Summary");
        Grid(col, new[] { "Use", "Rating", "Why" }, _r.Verdicts.Select(v => new[] { v.Use, v.Rating, v.Reason }), new[] { 1.1f, 0.7f, 4f });
        col.Item().PaddingTop(4).Text($"Voice estimate: {(_r.VoiceMos.HasValue ? $"R {N(_r.VoiceRFactor)}, MOS {N(_r.VoiceMos, "0.00")} (simplified E-model; an estimate, not a measured call)" : "not available")}.");
        var problems = _r.ProblemsBySegment.OrderBy(kv => kv.Key).Select(kv => $"{ConnectionAnalyzer.Name(kv.Key)} {kv.Value}").ToList();
        col.Item().Text($"Where problems happened: {(problems.Count == 0 ? "no outages or latency spikes were attributed to any segment" : string.Join(" · ", problems))}.").Bold();
        col.Item().Text($"Totals: {_r.Totals.ProbesSent:N0} probes = {_r.Totals.ProbesOk:N0} ok + {_r.Totals.ProbesFailed:N0} failed; {_r.Totals.LinkSamples:N0} link samples; " +
                        $"{_r.Totals.SleepGaps} unmeasured gap(s), {_r.Totals.UnmeasuredSeconds:0.0} s.").FontSize(7.5f);

        H1(col, "4. Segments (pooled over targets of the primary method)");
        Grid(col, new[] { "Segment", "Method", "Sent", "Loss %", "Avail. %", "p50", "p95", "p99", "Max", "Jitter", "DV p95", "Spikes", "Outages", "Longest s" },
            _r.Segments.Select(s => new[] { ConnectionAnalyzer.Name(s.Segment), s.Method.ToString(), N(s.Sent), N(s.LossPct, "0.##"), N(s.AvailabilityPct, "0.##"), N(s.RttP50), N(s.RttP95),
                N(s.RttP99), N(s.RttMax), N(s.JitterMs), N(s.DelayVariationP95Ms), N(s.Spikes), N(s.Outages), N(s.LongestOutageSeconds) }),
            new[] { 1.3f, 0.6f, 0.5f, 0.5f, 0.55f, 0.45f, 0.45f, 0.45f, 0.5f, 0.5f, 0.5f, 0.5f, 0.55f, 0.6f });
        col.Item().Text("Round-trip times in ms. DV = delay variation. Failure reasons per target in section 5.").FontSize(7).Italic();

        H1(col, "5. Targets");
        Grid(col, new[] { "Segment", "Target", "Method", "Sent", "Loss %", "p50", "p95", "Max", "Jitter", "Failures" },
            _r.Targets.Select(s => new[] { ConnectionAnalyzer.Name(s.Segment), s.Target, s.Method.ToString(), N(s.Sent), N(s.LossPct, "0.##"), N(s.RttP50), N(s.RttP95), N(s.RttMax), N(s.JitterMs),
                s.FailureReasons.Count == 0 ? "—" : string.Join(", ", s.FailureReasons.Select(kv => $"{kv.Key} {kv.Value}")) }),
            new[] { 1.1f, 1.8f, 0.6f, 0.5f, 0.5f, 0.45f, 0.45f, 0.5f, 0.5f, 1.6f });

        H1(col, "6. Timelines");
        col.Item().Text("Round-trip time per segment (ms; red ticks = failed probes) and Wi-Fi signal (dBm), on one time axis (elapsed active time).").FontSize(7.5f).Italic();
        foreach (var seg in new[] { ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet, ConnectionSegment.Dns })
        {
            var pts = _r.Probes.Where(p => p.Segment == seg && p.Method == ConnectionAnalyzer.PrimaryMethod(seg, _r.Probes)).ToList();
            if (pts.Count == 0) continue;
            col.Item().ShowEntire().Column(ch =>
            {
                ch.Item().PaddingTop(4).Text(ConnectionAnalyzer.Name(seg)).Bold().FontSize(8);
                ch.Item().Height(80).Image((ImageSize size) => ConnectionCharts.Render(size, (canvas, w, h) => ConnectionCharts.DrawProbes(canvas, w, h, _r, pts)));
            });
        }
        if (_r.LinkSamples.Any(l => l.Rssi.HasValue))
            col.Item().ShowEntire().Column(ch =>
            {
                ch.Item().PaddingTop(4).Text("Wi-Fi signal (dBm) — vertical lines = roams").Bold().FontSize(8);
                ch.Item().Height(70).Image((ImageSize size) => ConnectionCharts.Render(size, (canvas, w, h) => ConnectionCharts.DrawRssi(canvas, w, h, _r)));
            });

        H1(col, "7. Wi-Fi link");
        var l = _r.Link;
        KeyValues(col, new[]
        {
            ("Samples / roams / disconnects", $"{l.Samples:N0} / {l.Roams} / {l.Disconnects}"), ("Access points used", string.Join(", ", l.Bssids)),
            ("Signal mean / minimum", $"{N(l.RssiMean)} / {N(l.RssiMin)} dBm"), ("PHY rate mean rx / tx", $"{N(l.RxRateMean)} / {N(l.TxRateMean)} Mbps"),
            ("Frame retries / failed / FCS errors", l.CountersAvailable ? $"{N(l.RetryPct, "0.##")} % / {N(l.TxFailedPct, "0.##")} % / {N(l.FcsErrorPct, "0.##")} %" : "not reported by this driver"),
            ("This PC's own traffic", $"mean {N(l.PcTrafficMeanKbps)} kbit/s, max {N(l.PcTrafficMaxKbps)} kbit/s (context for spikes)")
        });

        if (_r.SpeedTests.Count > 0)
        {
            H1(col, "8. Speed and responsiveness");
            Grid(col, new[] { "Time", "Server", "Down Mbps", "Up Mbps", "Idle ms", "Loaded ms (down/up)", "+ms", "Grade", "RPM≈", "Note" },
                _r.SpeedTests.Select(s => new[] { s.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), $"{s.Provider} {s.Server}", N(s.DownloadMbps, "0.0"), N(s.UploadMbps, "0.0"),
                    N(s.IdleLatencyMs), $"{N(s.LoadedLatencyDownMs)} / {N(s.LoadedLatencyUpMs)}", N(s.LatencyIncreaseMs), s.BufferbloatGrade, N(s.ResponsivenessRpm, "0"), s.Error ?? s.LatencyTarget }),
                new[] { 0.6f, 1.6f, 0.6f, 0.6f, 0.5f, 0.9f, 0.45f, 0.45f, 0.5f, 1.2f });
            col.Item().Text("Grade from the median latency increase under load: A+ < 5 ms, A < 30, B < 60, C < 200, D < 400, F ≥ 400. RPM≈ = 60 000 ÷ loaded median round trip (approximation, not the IETF algorithm).").FontSize(7).Italic();
        }
        if (_r.TwoWayTests.Count > 0)
        {
            H1(col, "9. Two-way Wi-Fi test (LAN responder)");
            Grid(col, new[] { "Time", "Responder", "Loss up/down %", "RTT p50/p95", "Jitter up/down", "Reorder up/down", "MOS up/down (est.)", "LAN Mbps down/up", "Note" },
                _r.TwoWayTests.Select(t => new[] { t.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), t.Responder, $"{N(t.UpLossPct, "0.##")} / {N(t.DownLossPct, "0.##")}",
                    $"{N(t.RttP50Ms)} / {N(t.RttP95Ms)}", $"{N(t.UpJitterMs, "0.00")} / {N(t.DownJitterMs, "0.00")}", $"{t.UpReordered} / {t.DownReordered}",
                    $"{N(t.MosUp, "0.00")} / {N(t.MosDown, "0.00")}", $"{N(t.LanDownMbps)} / {N(t.LanUpMbps)}", t.Error ?? $"{t.Sent} pkts, {t.PacketsPerSecond}/s, {t.PayloadBytes} B" }),
                new[] { 0.55f, 1.0f, 0.75f, 0.7f, 0.75f, 0.65f, 0.75f, 0.8f, 1.0f });
        }

        H1(col, "10. Events");
        if (_r.Events.Count == 0) col.Item().Text("No outages, spikes, roams, disconnects or gaps.");
        else Grid(col, new[] { "Time", "Type", "Where (attributed)", "Dur. s", "Detail" },
            _r.Events.Select(e => new[] { e.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Type.ToString(),
                e.AttributedTo.HasValue ? ConnectionAnalyzer.Name(e.AttributedTo.Value) : "—", N(e.DurationSeconds), e.Detail }), new[] { 0.55f, 0.8f, 1.0f, 0.45f, 4f });

        H1(col, "11. Method, definitions and limits");
        col.Item().Text(ConnectionExportService.Method).FontSize(7.5f);
        col.Item().PaddingTop(3).Text(ConnectionExportService.Definitions).FontSize(7.5f);
        col.Item().PaddingTop(3).Text(ConnectionExportService.Limits).FontSize(7.5f);
        col.Item().PaddingTop(3).Text("Verdict thresholds (internet round trip, pooled TCP): " + string.Join("; ", ConnectionAnalyzer.VerdictThresholds.Select(t =>
            FormattableString.Invariant($"{t.Use} GOOD p95 ≤ {t.GoodP95} ms, jitter ≤ {t.GoodJitter}, loss ≤ {t.GoodLoss} % / FAIR ≤ {t.FairP95}, {t.FairJitter}, {t.FairLoss} %")))).FontSize(7.5f);

        H1(col, "12. Tester markers");
        if (_r.Markers.Count == 0) col.Item().Text("No markers.");
        else Grid(col, new[] { "Time", "Marker" }, _r.Markers.Select(m => new[] { m.WallUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), m.Text }), new[] { 0.6f, 4f });

        col.Item().PageBreak();
        H1(col, "Appendix A. Probe log");
        if (_r.Probes.Count > ConnectionExportService.PdfAppendixRowLimit)
            col.Item().Text($"Showing the first {ConnectionExportService.PdfAppendixRowLimit:N0} of {_r.Probes.Count:N0} probes. The complete log is in the CSV bundle (_probes.csv) and the JSON export.").Bold();
        Grid(col, new[] { "Round", "Time", "Segment", "Target", "Method", "Outcome", "RTT ms", "Detail" },
            _r.Probes.Take(ConnectionExportService.PdfAppendixRowLimit).Select(p => new[] { p.Sequence.ToString(CultureInfo.InvariantCulture), p.WallUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                p.Segment.ToString(), p.Target, p.Method.ToString(), p.Outcome.ToString(), N(p.RttMs, "0.00"), p.Detail ?? "" }),
            new[] { 0.5f, 0.6f, 0.7f, 1.6f, 0.55f, 0.65f, 0.55f, 1.4f });
    });
}

/// <summary>Print charts for the connection report (SkiaSharp; drawn in points like the saturation charts).</summary>
internal static class ConnectionCharts
{
    internal static byte[] Render(ImageSize size, Action<SKCanvas, float, float> draw)
    {
        float scale = Math.Max(1f, size.Width / SaturationReportDocument.ContentWidthPt);
        using var bitmap = new SKBitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale(scale);
        draw(canvas, size.Width / scale, size.Height / scale);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKPaint Stroke(SKColor c, float w) => new() { Color = c, StrokeWidth = w, IsAntialias = true, Style = SKPaintStyle.Stroke };
    private static SKPaint Text(SKColor c) => new() { Color = c, IsAntialias = true };

    private static void Axes(SKCanvas canvas, float w, float h, double xMax, double yMin, double yMax, string unit, out Func<double, float> X, out Func<double, float> Y)
    {
        const float left = 36, right = 6, top = 4, bottom = 14;
        float pw = w - left - right, ph = h - top - bottom;
        X = s => left + (float)(xMax <= 0 ? 0 : s / xMax) * pw;
        double span = Math.Max(1e-6, yMax - yMin);
        Y = v => top + ph - (float)((v - yMin) / span) * ph;
        using var axis = Stroke(new SKColor(0x9C, 0xA3, 0xAF), 0.5f);
        using var font = new SKFont(SKTypeface.Default, 6.5f);
        using var ink = Text(new SKColor(0x4B, 0x55, 0x63));
        canvas.DrawLine(left, top, left, top + ph, axis);
        canvas.DrawLine(left, top + ph, left + pw, top + ph, axis);
        for (int i = 0; i <= 2; i++)
        {
            double v = yMin + span * i / 2;
            canvas.DrawText($"{v:0} {unit}", 2, Y(v) + 2, font, ink);
        }
        for (int i = 0; i <= 4; i++)
        {
            double s = xMax * i / 4;
            canvas.DrawText(TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss"), X(s) - 12, h - 3, font, ink);
        }
    }

    public static void DrawProbes(SKCanvas canvas, float w, float h, ConnectionReport r, IReadOnlyList<ConnectionProbeResult> pts)
    {
        double xMax = Math.Max(1, r.ActiveSeconds);
        var ok = pts.Where(p => p.Outcome == ProbeOutcome.Ok && p.RttMs.HasValue).ToList();
        double yMax = ok.Count == 0 ? 10 : Math.Max(10, ConnectionAnalyzer.Percentile(ok.Select(p => p.RttMs!.Value).OrderBy(v => v).ToList(), 99) ?? 10) * 1.15;
        Axes(canvas, w, h, xMax, 0, yMax, "ms", out var X, out var Y);
        using var line = Stroke(new SKColor(0x1D, 0x4E, 0xD8), 0.7f);
        using var fail = Stroke(new SKColor(0xDC, 0x26, 0x26), 0.8f);
        foreach (var g in ok.GroupBy(p => p.Target))
        {
            var path = new SKPath();
            bool first = true;
            foreach (var p in g)
            {
                float x = X(p.ElapsedMs / 1000.0), y = Y(Math.Min(p.RttMs!.Value, yMax));
                if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
            }
            canvas.DrawPath(path, line);
        }
        foreach (var p in pts.Where(p => p.Outcome != ProbeOutcome.Ok))
            canvas.DrawLine(X(p.ElapsedMs / 1000.0), Y(yMax), X(p.ElapsedMs / 1000.0), Y(yMax * 0.85), fail);
    }

    public static void DrawRssi(SKCanvas canvas, float w, float h, ConnectionReport r)
    {
        double xMax = Math.Max(1, r.ActiveSeconds);
        var pts = r.LinkSamples.Where(l => l.Rssi.HasValue).ToList();
        double yMin = Math.Min(-90, pts.Min(l => l.Rssi!.Value) - 3), yMax = Math.Max(-30, pts.Max(l => l.Rssi!.Value) + 3);
        Axes(canvas, w, h, xMax, yMin, yMax, "dBm", out var X, out var Y);
        using var line = Stroke(new SKColor(0x05, 0x96, 0x69), 0.8f);
        using var roam = Stroke(new SKColor(0x6B, 0x72, 0x80), 0.5f);
        var path = new SKPath();
        bool first = true;
        foreach (var l in pts)
        {
            float x = X(l.ElapsedMs / 1000.0), y = Y(l.Rssi!.Value);
            if (first) { path.MoveTo(x, y); first = false; } else path.LineTo(x, y);
        }
        canvas.DrawPath(path, line);
        foreach (var e in r.Events.Where(e => e.Type == ConnectionEventType.Roam))
            canvas.DrawLine(X(e.StartElapsedMs / 1000.0), Y(yMax), X(e.StartElapsedMs / 1000.0), Y(yMin), roam);
    }
}
