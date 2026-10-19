using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Pure, deterministic analytics for the connection test, version CONN-ANALYTICS-1 (DR-0014, docs/CONNECTION_TEST_SPEC.md).
/// Same inputs always give the same report. Definitions:
///  - Loss % = failed ÷ sent (Skipped probes are not sent). Availability % (per segment) = rounds with ≥ 1 successful probe ÷ rounds,
///    where a round is one tick's probes of that segment (all internet targets together).
///  - RTT percentiles: nearest rank over successful probes. Jitter = mean |RTT(n) − RTT(n−1)| over consecutive successful probes of one
///    target; delay variation P95 = 95th percentile of the same differences (round-trip delay variation; one-way per direction only in stage C).
///  - Outage: ≥ 3 consecutive failed rounds of a segment. Attributed to the first failing segment upstream (Wi-Fi link → router → ISP → segment).
///  - Latency spike: RTT &gt; max(3 × baseline, baseline + 100 ms), baseline = median of the previous 30 successful RTTs of that target (≥ 10 needed).
///  - Voice estimate: simplified ITU-T G.107 E-model on the internet segment (always labelled an estimate).
/// </summary>
public sealed class ConnectionAnalyzer : IConnectionAnalyzer
{
    public const string AnalyticsVersion = "CONN-ANALYTICS-1";
    public string Version => AnalyticsVersion;

    public const int OutageRounds = 3;
    public const int BaselineWindow = 30;
    public const int MinBaseline = 10;
    public const double SpikeFactor = 3.0;
    public const double SpikeMinIncreaseMs = 100;

    private static readonly ConnectionSegment[] Upstream = { ConnectionSegment.WifiLink, ConnectionSegment.Gateway, ConnectionSegment.IspHop, ConnectionSegment.Internet };

    public ConnectionReport Analyze(ConnectionTestConfig config, IReadOnlyList<ConnectionProbeResult> probes, IReadOnlyList<ConnectionLinkSample> link,
        IReadOnlyList<SpeedTestResult> speed, IReadOnlyList<TwoWayResult> twoWay, IReadOnlyList<ConnectionMarker> markers,
        DateTime startUtc, DateTime? endUtc, ConnectionTestStatus status, double activeSeconds)
    {
        var ordered = probes.OrderBy(p => p.ElapsedMs).ThenBy(p => p.Sequence).ToList();
        var linkOrdered = link.OrderBy(l => l.ElapsedMs).ThenBy(l => l.Sequence).ToList();
        var sent = ordered.Where(p => p.Outcome != ProbeOutcome.Skipped).ToList();

        // per target
        var events = new List<ConnectionEvent>();
        var targets = new List<ConnectionSegmentSummary>();
        foreach (var g in sent.GroupBy(p => (p.Segment, p.Target, p.Method)).OrderBy(g => g.Key.Segment).ThenBy(g => g.Key.Target, StringComparer.Ordinal).ThenBy(g => g.Key.Method))
        {
            var list = g.ToList();
            var spikes = Spikes(list);
            targets.Add(Stats(g.Key.Segment, g.Key.Target, g.Key.Method, list, list.Select(p => new[] { p }).ToList()) with { Spikes = spikes.Count });
            foreach (var s in spikes) events.Add(s);
        }

        // per segment (pooled over targets of the primary method), with rounds
        var segments = new List<ConnectionSegmentSummary>();
        var failedRounds = new Dictionary<ConnectionSegment, List<(long Seq, long Ms, DateTime Wall, bool Ok)>>();
        foreach (var seg in Enum.GetValues<ConnectionSegment>())
        {
            if (seg == ConnectionSegment.WifiLink || seg == ConnectionSegment.LanTwoWay) continue;
            var segProbes = sent.Where(p => p.Segment == seg && p.Method == PrimaryMethod(seg, sent)).ToList();
            if (segProbes.Count == 0) continue;
            var rounds = segProbes.GroupBy(p => p.Sequence).OrderBy(r => r.Key).Select(r => r.ToArray()).ToList();
            failedRounds[seg] = rounds.Select(r => (r[0].Sequence, r[0].ElapsedMs, r[0].WallUtc, r.Any(p => p.Outcome == ProbeOutcome.Ok))).ToList();
            segments.Add(Stats(seg, seg == ConnectionSegment.Internet ? string.Join(" | ", segProbes.Select(p => p.Target).Distinct()) : segProbes[0].Target,
                segProbes[0].Method, segProbes, rounds) with { Spikes = targets.Where(t => t.Segment == seg).Sum(t => t.Spikes) });
        }

        // link events (roams, disconnects, IP / network changes, sleep gaps)
        events.AddRange(LinkEvents(config, linkOrdered));

        // outages with attribution
        foreach (var (seg, rounds) in failedRounds)
        {
            foreach (var (startIdx, endIdx) in Runs(rounds.Select(r => !r.Ok).ToList(), OutageRounds))
            {
                var first = rounds[startIdx];
                var after = endIdx + 1 < rounds.Count ? rounds[endIdx + 1] : ((long Seq, long Ms, DateTime Wall, bool Ok)?)null;
                long endMs = after?.Ms ?? rounds[endIdx].Ms;
                var (attr, why) = Attribute(seg, first.Ms, endMs, failedRounds, linkOrdered);
                string detail = $"{Name(seg)} unreachable for {endIdx - startIdx + 1} consecutive probes" + (after == null ? " (still failing at the end)" : "") + $". {why}";
                events.Add(new ConnectionEvent(ConnectionEventType.Outage, seg, attr, first.Wall, after?.Wall, first.Ms, after?.Ms,
                    (endMs - first.Ms) / 1000.0, endIdx - startIdx + 1, null, detail));
            }
        }
        segments = segments.Select(s =>
        {
            var o = events.Where(e => e.Type == ConnectionEventType.Outage && e.Segment == s.Segment).ToList();
            return s with { Outages = o.Count, LongestOutageSeconds = o.Count == 0 ? null : o.Max(e => e.DurationSeconds) };
        }).ToList();

        // spike attribution (local vs upstream)
        events = events.Select(e => e.Type == ConnectionEventType.LatencySpike ? AttributeSpike(e, events, linkOrdered) : e).ToList();

        foreach (var s in speed)
            events.Add(new ConnectionEvent(ConnectionEventType.SpeedTest, null, null, s.StartUtc, null, s.ElapsedMs, null, null, null, null,
                s.Error != null ? $"Speed test failed: {s.Error}" : $"Speed test ({s.Provider}): down {F1(s.DownloadMbps)} Mbps, up {F1(s.UploadMbps)} Mbps, latency +{F1(s.LatencyIncreaseMs)} ms under load (grade {s.BufferbloatGrade}). Regular probes paused meanwhile."));
        foreach (var t in twoWay)
            events.Add(new ConnectionEvent(ConnectionEventType.TwoWayTest, ConnectionSegment.LanTwoWay, null, t.StartUtc, null, t.ElapsedMs, null, t.Seconds, t.Sent, t.RttP95Ms,
                t.Error != null ? $"Two-way test failed: {t.Error}" : $"Two-way test ({t.Responder}): loss up {F1(t.UpLossPct)} % / down {F1(t.DownLossPct)} %, jitter up {F1(t.UpJitterMs)} / down {F1(t.DownJitterMs)} ms, MOS up {F2(t.MosUp)} / down {F2(t.MosDown)}."));

        events = events.OrderBy(e => e.StartElapsedMs).ThenBy(e => e.Type).ToList();
        var problems = events.Where(e => e.AttributedTo.HasValue).GroupBy(e => e.AttributedTo!.Value).ToDictionary(g => g.Key, g => g.Count());

        var internet = segments.FirstOrDefault(s => s.Segment == ConnectionSegment.Internet);
        var dns = segments.FirstOrDefault(s => s.Segment == ConnectionSegment.Dns);
        var (r, mos) = Voice(internet);
        var verdicts = Verdicts(internet, dns);
        var linkSummary = LinkSummary(linkOrdered, events);
        var gaps = linkOrdered.Where(l => l.GapMs.HasValue).ToList();
        var totals = new ConnectionTotals(sent.Count, sent.Count(p => p.Outcome == ProbeOutcome.Ok), sent.Count(p => p.Outcome != ProbeOutcome.Ok),
            linkOrdered.Count, gaps.Count, Math.Round(gaps.Sum(g => g.GapMs!.Value) / 1000.0, 3));

        return new ConnectionReport(config, AnalyticsVersion, startUtc, endUtc, activeSeconds, status, segments, targets, events, problems,
            verdicts, r, mos, linkSummary, speed.OrderBy(s => s.ElapsedMs).ToList(), twoWay.OrderBy(t => t.ElapsedMs).ToList(),
            markers.OrderBy(m => m.ElapsedMs).ToList(), totals, ordered, linkOrdered);
    }

    // ------------------------------------------------------------------ statistics

    /// <summary>Primary method per segment: TCP for the internet when present (ICMP is often deprioritised), else the most used method.</summary>
    public static ProbeMethod PrimaryMethod(ConnectionSegment seg, IReadOnlyList<ConnectionProbeResult> sent)
    {
        var methods = sent.Where(p => p.Segment == seg).Select(p => p.Method).Distinct().ToList();
        if (seg == ConnectionSegment.Internet && methods.Contains(ProbeMethod.Tcp)) return ProbeMethod.Tcp;
        return sent.Where(p => p.Segment == seg).GroupBy(p => p.Method).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault();
    }

    internal static ConnectionSegmentSummary Stats(ConnectionSegment seg, string target, ProbeMethod method, List<ConnectionProbeResult> list, List<ConnectionProbeResult[]> rounds)
    {
        var ok = list.Where(p => p.Outcome == ProbeOutcome.Ok && p.RttMs.HasValue).ToList();
        var rtts = ok.Select(p => p.RttMs!.Value).ToList();
        var sorted = rtts.OrderBy(v => v).ToList();
        // consecutive differences per target (pooled lists keep per-target order)
        var deltas = new List<double>();
        foreach (var t in ok.GroupBy(p => p.Target))
        {
            var series = t.OrderBy(p => p.ElapsedMs).Select(p => p.RttMs!.Value).ToList();
            for (int i = 1; i < series.Count; i++) deltas.Add(Math.Abs(series[i] - series[i - 1]));
        }
        int sent = list.Count;
        int failed = sent - ok.Count;
        return new ConnectionSegmentSummary
        {
            Segment = seg,
            Target = target,
            Method = method,
            Sent = sent,
            Ok = ok.Count,
            Failed = failed,
            FailureReasons = list.Where(p => p.Outcome != ProbeOutcome.Ok).GroupBy(p => p.Outcome.ToString()).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            LossPct = sent == 0 ? null : Round(100.0 * failed / sent),
            AvailabilityPct = rounds.Count == 0 ? null : Round(100.0 * rounds.Count(r => r.Any(p => p.Outcome == ProbeOutcome.Ok)) / rounds.Count),
            RttMin = sorted.Count == 0 ? null : Round(sorted[0]),
            RttP50 = Percentile(sorted, 50),
            RttP95 = Percentile(sorted, 95),
            RttP99 = Percentile(sorted, 99),
            RttMax = sorted.Count == 0 ? null : Round(sorted[^1]),
            RttMean = sorted.Count == 0 ? null : Round(sorted.Average()),
            JitterMs = deltas.Count == 0 ? null : Round(deltas.Average()),
            DelayVariationP95Ms = Percentile(deltas.OrderBy(v => v).ToList(), 95)
        };
    }

    /// <summary>Nearest-rank percentile (same method as the saturation analytics).</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        int rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return Round(sorted[Math.Clamp(rank, 1, sorted.Count) - 1]);
    }

    private static double Round(double v) => Math.Round(v, 3);
    private static string F1(double? v) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";
    private static string F2(double? v) => v.HasValue ? v.Value.ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

    public static string Name(ConnectionSegment s) => s switch
    {
        ConnectionSegment.WifiLink => "Wi-Fi link",
        ConnectionSegment.Gateway => "Router (gateway)",
        ConnectionSegment.IspHop => "ISP hop",
        ConnectionSegment.Internet => "Internet",
        ConnectionSegment.Dns => "DNS",
        ConnectionSegment.Web => "Web (HTTPS)",
        _ => "Wi-Fi two-way (LAN responder)"
    };

    /// <summary>Runs of true values at least minLength long, as (start, end) indices.</summary>
    internal static IEnumerable<(int Start, int End)> Runs(IReadOnlyList<bool> values, int minLength)
    {
        int i = 0;
        while (i < values.Count)
        {
            if (!values[i]) { i++; continue; }
            int j = i;
            while (j + 1 < values.Count && values[j + 1]) j++;
            if (j - i + 1 >= minLength) yield return (i, j);
            i = j + 1;
        }
    }

    // ------------------------------------------------------------------ events

    private static List<ConnectionEvent> Spikes(List<ConnectionProbeResult> list)
    {
        var result = new List<ConnectionEvent>();
        var ok = list.Where(p => p.Outcome == ProbeOutcome.Ok && p.RttMs.HasValue).OrderBy(p => p.ElapsedMs).ToList();
        var window = new List<double>();
        ConnectionProbeResult? start = null, last = null;
        double peak = 0, baseAtStart = 0;
        int count = 0;
        void Close()
        {
            if (start == null) return;
            result.Add(new ConnectionEvent(ConnectionEventType.LatencySpike, start.Segment, start.Segment, start.WallUtc, last!.WallUtc, start.ElapsedMs, last.ElapsedMs,
                (last.ElapsedMs - start.ElapsedMs) / 1000.0, count, Round(peak),
                $"{Name(start.Segment)} {start.Target}: round trip up to {peak:0} ms (normal {baseAtStart:0} ms) over {count} probe(s)."));
            start = null;
        }
        foreach (var p in ok)
        {
            double rtt = p.RttMs!.Value;
            bool spike = false;
            double baseline = 0;
            if (window.Count >= MinBaseline)
            {
                var s = window.OrderBy(v => v).ToList();
                baseline = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
                spike = rtt > Math.Max(SpikeFactor * baseline, baseline + SpikeMinIncreaseMs);
            }
            if (spike)
            {
                if (start == null) { start = p; peak = rtt; baseAtStart = baseline; count = 0; }
                count++;
                peak = Math.Max(peak, rtt);
                last = p;
            }
            else
            {
                Close();
                window.Add(rtt);                     // spikes never raise the baseline
                if (window.Count > BaselineWindow) window.RemoveAt(0);
            }
        }
        Close();
        return result;
    }

    private static IEnumerable<ConnectionEvent> LinkEvents(ConnectionTestConfig config, List<ConnectionLinkSample> link)
    {
        int tick = Math.Max(1, config.TickMilliseconds);
        ConnectionLinkSample? prev = null;
        ConnectionLinkSample? downSince = null;
        foreach (var l in link)
        {
            if (l.GapMs is long gap && gap >= 3L * tick)
                yield return new ConnectionEvent(ConnectionEventType.SleepGap, null, null, l.WallUtc.AddMilliseconds(-gap), l.WallUtc, l.ElapsedMs - gap, l.ElapsedMs,
                    gap / 1000.0, null, null, $"Nothing was measured for {gap / 1000.0:0.0} s (PC asleep, suspended or stalled). This time is not counted as an outage.");
            if (prev != null)
            {
                if (prev.Connected && !l.Connected) downSince = l;
                if (!prev.Connected && l.Connected && downSince != null)
                {
                    yield return new ConnectionEvent(ConnectionEventType.Disconnect, ConnectionSegment.WifiLink, ConnectionSegment.WifiLink, downSince.WallUtc, l.WallUtc,
                        downSince.ElapsedMs, l.ElapsedMs, (l.ElapsedMs - downSince.ElapsedMs) / 1000.0, null, null,
                        $"Wi-Fi disconnected for {(l.ElapsedMs - downSince.ElapsedMs) / 1000.0:0.0} s" + (l.Ssid != null ? $", reconnected to {l.Ssid}" : "") + ".");
                    downSince = null;
                }
                if (prev.Connected && l.Connected && prev.Ssid != null && l.Ssid != null && !string.Equals(prev.Ssid, l.Ssid, StringComparison.Ordinal))
                    yield return new ConnectionEvent(ConnectionEventType.NetworkChange, ConnectionSegment.WifiLink, null, l.WallUtc, null, l.ElapsedMs, null, null, null, null,
                        $"The PC moved to another network: {prev.Ssid} → {l.Ssid}. Results after this point describe the new network.");
                else if (prev.Connected && l.Connected && prev.Bssid != null && l.Bssid != null && !string.Equals(prev.Bssid, l.Bssid, StringComparison.OrdinalIgnoreCase))
                    yield return new ConnectionEvent(ConnectionEventType.Roam, ConnectionSegment.WifiLink, null, l.WallUtc, null, l.ElapsedMs, null, null, null, null,
                        $"Roamed {prev.Bssid} → {l.Bssid}" + (prev.Rssi.HasValue && l.Rssi.HasValue ? $" (signal {prev.Rssi} → {l.Rssi} dBm)" : "") + (prev.Channel != l.Channel ? $", channel {prev.Channel} → {l.Channel}" : "") + ".");
                if (l.Connected && prev.Connected && ((prev.LocalIp != null && l.LocalIp != null && prev.LocalIp != l.LocalIp) || (prev.Gateway != null && l.Gateway != null && prev.Gateway != l.Gateway)))
                    yield return new ConnectionEvent(ConnectionEventType.IpChange, ConnectionSegment.WifiLink, null, l.WallUtc, null, l.ElapsedMs, null, null, null, null,
                        $"Address changed: {prev.LocalIp} via {prev.Gateway} → {l.LocalIp} via {l.Gateway}.");
            }
            prev = l;
        }
        if (downSince != null && prev != null)
            yield return new ConnectionEvent(ConnectionEventType.Disconnect, ConnectionSegment.WifiLink, ConnectionSegment.WifiLink, downSince.WallUtc, null, downSince.ElapsedMs, null,
                (prev.ElapsedMs - downSince.ElapsedMs) / 1000.0, null, null, "Wi-Fi disconnected and still disconnected at the end of the test.");
    }

    /// <summary>First failing segment upstream during the window; ISP-hop-only failures with a healthy internet are not a problem (ICMP rate limiting).</summary>
    private static (ConnectionSegment? Segment, string Why) Attribute(ConnectionSegment seg, long fromMs, long toMs,
        Dictionary<ConnectionSegment, List<(long Seq, long Ms, DateTime Wall, bool Ok)>> rounds, List<ConnectionLinkSample> link)
    {
        bool FailingDuring(ConnectionSegment s)
        {
            if (!rounds.TryGetValue(s, out var r)) return false;
            var inWin = r.Where(x => x.Ms >= fromMs - 1000 && x.Ms <= toMs).ToList();
            return inWin.Count > 0 && inWin.Count(x => !x.Ok) * 2 >= inWin.Count;
        }
        bool linkDown = link.Any(l => l.ElapsedMs >= fromMs - 1000 && l.ElapsedMs <= toMs && !l.Connected);
        if (linkDown) return (ConnectionSegment.WifiLink, "Cause: the Wi-Fi link itself was down.");

        if (seg == ConnectionSegment.IspHop && rounds.ContainsKey(ConnectionSegment.Internet) && !FailingDuring(ConnectionSegment.Internet))
            return (null, "The internet stayed reachable, so this is most likely the ISP router rate-limiting probe replies, not a fault.");
        if (seg is ConnectionSegment.Dns)
            return FailingDuring(ConnectionSegment.Gateway) ? (ConnectionSegment.Gateway, "Cause: the router was unreachable at the same time.")
                : FailingDuring(ConnectionSegment.Internet) ? (ConnectionSegment.Internet, "Cause: the internet was unreachable at the same time.")
                : (ConnectionSegment.Dns, "The internet was reachable, so name resolution itself failed.");
        if (seg is ConnectionSegment.Web)
            return FailingDuring(ConnectionSegment.Gateway) ? (ConnectionSegment.Gateway, "Cause: the router was unreachable at the same time.")
                : FailingDuring(ConnectionSegment.Internet) ? (ConnectionSegment.Internet, "Cause: the internet was unreachable at the same time.")
                : FailingDuring(ConnectionSegment.Dns) ? (ConnectionSegment.Dns, "Cause: name resolution was failing at the same time.")
                : (ConnectionSegment.Web, "Router, internet and DNS were fine, so the web service itself failed.");

        foreach (var up in Upstream)
        {
            if (up == ConnectionSegment.WifiLink) continue;
            if ((int)up >= (int)seg) break;
            if (up == ConnectionSegment.IspHop && rounds.TryGetValue(ConnectionSegment.IspHop, out _) && FailingDuring(ConnectionSegment.IspHop))
                return (ConnectionSegment.IspHop, "Cause: the ISP hop stopped answering at the same time (the router was fine).");
            if (up == ConnectionSegment.Gateway && FailingDuring(ConnectionSegment.Gateway))
                return (ConnectionSegment.Gateway, "Cause: the router was unreachable at the same time (Wi-Fi association was up).");
        }
        return (seg, seg == ConnectionSegment.Gateway ? "The Wi-Fi association was up, so the router or the local network failed."
            : seg == ConnectionSegment.Internet ? "Router (and ISP hop, when measured) were fine, so the problem is beyond them (ISP or upstream)."
            : "");
    }

    private static ConnectionEvent AttributeSpike(ConnectionEvent e, List<ConnectionEvent> all, List<ConnectionLinkSample> link)
    {
        long from = e.StartElapsedMs - 2000, to = (e.EndElapsedMs ?? e.StartElapsedMs) + 2000;
        bool Overlaps(ConnectionSegment s) => all.Any(x => x.Type == ConnectionEventType.LatencySpike && x.Segment == s && x.StartElapsedMs <= to && (x.EndElapsedMs ?? x.StartElapsedMs) >= from);
        bool roamNear = all.Any(x => x.Type is ConnectionEventType.Roam or ConnectionEventType.Disconnect && x.StartElapsedMs >= e.StartElapsedMs - 10000 && x.StartElapsedMs <= to);
        (ConnectionSegment? a, string why) = e.Segment switch
        {
            ConnectionSegment.Gateway => roamNear ? (ConnectionSegment.WifiLink, " Local: right after a roam/reconnect.") : (ConnectionSegment.Gateway, " Local: Wi-Fi or router (the router itself answered slowly)."),
            ConnectionSegment.Internet or ConnectionSegment.Dns or ConnectionSegment.Web or ConnectionSegment.IspHop =>
                Overlaps(ConnectionSegment.Gateway) ? (roamNear ? ConnectionSegment.WifiLink : ConnectionSegment.Gateway, " Local: the router was slow at the same time.")
                : e.Segment != ConnectionSegment.IspHop && Overlaps(ConnectionSegment.IspHop) ? (ConnectionSegment.IspHop, " ISP: the ISP hop was slow at the same time.")
                : (e.Segment, e.Segment == ConnectionSegment.Internet ? " Upstream: router and ISP hop were normal." : ""),
            _ => (e.Segment, "")
        };
        return e with { AttributedTo = a, Detail = e.Detail + why };
    }

    // ------------------------------------------------------------------ verdicts and voice

    /// <summary>Simplified E-model (ITU-T G.107 derived): one-way delay ≈ mean RTT / 2. An estimate, never a measurement of call quality.</summary>
    internal static (double? R, double? Mos) Voice(ConnectionSegmentSummary? internet)
    {
        if (internet?.RttMean is not double rtt || internet.JitterMs is not double jitter || internet.LossPct is not double loss) return (null, null);
        double eff = rtt / 2 + 2 * jitter + 10;
        double r = eff < 160 ? 93.2 - eff / 40 : 93.2 - (eff - 120) / 10;
        r = Math.Clamp(r - 2.5 * loss, 0, 100);
        double mos = Math.Clamp(1 + 0.035 * r + 0.000007 * r * (r - 60) * (100 - r), 1, 4.5);
        return (Math.Round(r, 1), Math.Round(mos, 2));
    }

    /// <summary>Thresholds are printed in every report. Internet = pooled TCP connect round trips.</summary>
    public static readonly (string Use, double GoodP95, double GoodJitter, double GoodLoss, double FairP95, double FairJitter, double FairLoss)[] VerdictThresholds =
    {
        ("Browsing", 200, 100, 2, 500, 200, 5),
        ("Streaming video", 300, 100, 1, 800, 200, 3),
        ("Video calls", 150, 30, 1, 300, 50, 3),
        ("Online gaming", 60, 15, 0.5, 100, 30, 1.5)
    };

    internal static IReadOnlyList<UseVerdict> Verdicts(ConnectionSegmentSummary? internet, ConnectionSegmentSummary? dns)
    {
        var list = new List<UseVerdict>();
        foreach (var t in VerdictThresholds)
        {
            if (internet?.RttP95 is not double p95 || internet.JitterMs is not double j || internet.LossPct is not double loss)
            {
                list.Add(new UseVerdict(t.Use, "NOT TESTED", "Internet segment not measured."));
                continue;
            }
            string rating = p95 <= t.GoodP95 && j <= t.GoodJitter && loss <= t.GoodLoss ? "GOOD"
                : p95 <= t.FairP95 && j <= t.FairJitter && loss <= t.FairLoss ? "FAIR" : "POOR";
            if (t.Use == "Browsing" && dns?.RttP95 is double d && d > 300 && rating == "GOOD") rating = "FAIR";
            string reason = FormattableString.Invariant($"round trip p95 {p95:0} ms (good ≤ {t.GoodP95:0}), jitter {j:0.0} ms (≤ {t.GoodJitter:0}), loss {loss:0.##} % (≤ {t.GoodLoss:0.##})")
                            + (t.Use == "Browsing" && dns?.RttP95 is double d2 ? FormattableString.Invariant($", DNS p95 {d2:0} ms") : "");
            list.Add(new UseVerdict(t.Use, rating, reason));
        }
        return list;
    }

    private static ConnectionLinkSummary LinkSummary(List<ConnectionLinkSample> link, List<ConnectionEvent> events)
    {
        var connected = link.Where(l => l.Connected).ToList();
        var rssi = connected.Where(l => l.Rssi.HasValue).Select(l => l.Rssi!.Value).ToList();
        var rx = connected.Where(l => l.RxRateMbps.HasValue).Select(l => l.RxRateMbps!.Value).ToList();
        var tx = connected.Where(l => l.TxRateMbps.HasValue).Select(l => l.TxRateMbps!.Value).ToList();

        // counters: sum of positive deltas (a driver reset makes a counter go down; that step is skipped)
        ulong dTx = 0, dRetry = 0, dFail = 0, dRx = 0, dFcs = 0;
        WlanFrameCounters? prev = null;
        foreach (var c in link.Select(l => l.Counters).Where(c => c != null))
        {
            if (prev != null)
            {
                static ulong D(ulong a, ulong b) => b >= a ? b - a : 0;
                dTx += D(prev.TxFrames, c!.TxFrames); dRetry += D(prev.TxRetries, c.TxRetries); dFail += D(prev.TxFailed, c.TxFailed);
                dRx += D(prev.RxFrames, c.RxFrames); dFcs += D(prev.FcsErrors, c.FcsErrors);
            }
            prev = c;
        }
        bool counters = prev != null && (dTx > 0 || dRx > 0);

        var rates = new List<double>();
        for (int i = 1; i < link.Count; i++)
        {
            var a = link[i - 1];
            var b = link[i];
            if (a.IfBytesIn is long ai && a.IfBytesOut is long ao && b.IfBytesIn is long bi && b.IfBytesOut is long bo && b.ElapsedMs > a.ElapsedMs && bi >= ai && bo >= ao)
                rates.Add((bi - ai + bo - ao) * 8.0 / (b.ElapsedMs - a.ElapsedMs)); // bits per ms = kbit/s
        }
        return new ConnectionLinkSummary
        {
            Samples = link.Count,
            Roams = events.Count(e => e.Type == ConnectionEventType.Roam),
            Disconnects = events.Count(e => e.Type == ConnectionEventType.Disconnect),
            Bssids = connected.Where(l => l.Bssid != null).Select(l => l.Bssid!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            RssiMean = rssi.Count == 0 ? null : Round(rssi.Average()),
            RssiMin = rssi.Count == 0 ? null : rssi.Min(),
            RxRateMean = rx.Count == 0 ? null : Round(rx.Average()),
            TxRateMean = tx.Count == 0 ? null : Round(tx.Average()),
            CountersAvailable = counters,
            RetryPct = counters && dTx > 0 ? Round(100.0 * dRetry / dTx) : null,
            TxFailedPct = counters && dTx > 0 ? Round(100.0 * dFail / dTx) : null,
            FcsErrorPct = counters && dRx + dFcs > 0 ? Round(100.0 * dFcs / (dRx + dFcs)) : null,
            PcTrafficMeanKbps = rates.Count == 0 ? null : Round(rates.Average()),
            PcTrafficMaxKbps = rates.Count == 0 ? null : Round(rates.Max())
        };
    }
}
