using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Pure, deterministic analytics for the Signal Saturation Test — SAT-ANALYTICS-1
/// (docs/SATURATION_TEST_SPEC.md §6, DR-0004). Same inputs always give the same report;
/// any change to a definition, default or formula requires a new version and decision record.
/// </summary>
public sealed class SaturationAnalyzer : ISaturationAnalyzer
{
    public const string AnalyticsVersion = "SAT-ANALYTICS-1";
    public string Version => AnalyticsVersion;

    /// <summary>Minimum duration (hours) used as the denominator of per-hour event rates in the stability score.</summary>
    public const double MinRateHours = 0.25;

    /// <summary>Minimum observed span before a trend finding is reported in plain language.</summary>
    public const double MinTrendSpanSeconds = 15 * 60;

    public SaturationReport Analyze(SaturationTestConfig config, IReadOnlyList<SaturationCycle> cycles, IReadOnlyList<SaturationMarker> markers,
        DateTime startUtc, DateTime? endUtc, SaturationTestStatus status)
    {
        var ordered = cycles.OrderBy(c => c.Sequence).ToList();
        var th = config.Thresholds;
        double intervalMs = config.Interval.TotalMilliseconds;
        double activeSeconds = ordered.Count == 0 ? 0 : ordered[^1].ElapsedMs / 1000.0;

        // Weight (ms) each cycle represents: time until the next cycle, capped at 2 intervals.
        var weightBySeq = new Dictionary<long, double>();
        for (int i = 0; i < ordered.Count; i++)
        {
            double w = i + 1 < ordered.Count ? ordered[i + 1].ElapsedMs - ordered[i].ElapsedMs : intervalMs;
            weightBySeq[ordered[i].Sequence] = Math.Clamp(w, 0, 2 * intervalMs);
        }

        // Series per BSSID in first-seen order.
        var keys = new List<string>();
        var series = new Dictionary<string, List<(SaturationCycle C, SaturationObservation O)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ordered)
        {
            foreach (var o in c.Observations)
            {
                if (!series.TryGetValue(o.Bssid, out var list))
                {
                    series[o.Bssid] = list = new();
                    keys.Add(o.Bssid);
                }
                list.Add((c, o));
            }
        }

        var events = new List<SaturationEvent>();
        var targets = new List<SaturationTargetSummary>();
        foreach (var bssid in keys)
        {
            var (summary, targetEvents) = AnalyzeTarget(bssid, series[bssid], th, weightBySeq, activeSeconds);
            targets.Add(summary);
            events.AddRange(targetEvents);
        }

        var ssids = new List<SaturationSsidSummary>();
        if (config.Scope is SaturationScope.SingleSsid or SaturationScope.MultipleSsids)
        {
            foreach (var group in targets.Where(t => !string.IsNullOrEmpty(t.Ssid)).GroupBy(t => t.Ssid))
            {
                var (s, ev) = AnalyzeSsid(group.Key, group.Select(t => t.Bssid).ToList(), ordered, th);
                ssids.Add(s);
                events.AddRange(ev);
            }
        }

        return new SaturationReport(config, AnalyticsVersion, startUtc, endUtc, activeSeconds, status,
            Health(ordered), targets, ssids,
            events.OrderBy(e => e.StartElapsedMs).ThenBy(e => e.Type).ToList(),
            markers.OrderBy(m => m.ElapsedMs).ToList(), ordered);
    }

    private static SaturationCycleHealth Health(List<SaturationCycle> cycles)
    {
        var scanned = cycles.Where(c => c.Status != ScanCycleStatus.Skipped).ToList();
        double maxGap = 0;
        for (int i = 1; i < cycles.Count; i++) maxGap = Math.Max(maxGap, (cycles[i].ElapsedMs - cycles[i - 1].ElapsedMs) / 1000.0);
        return new SaturationCycleHealth(
            cycles.Count,
            cycles.Count(c => c.Status == ScanCycleStatus.Fresh),
            cycles.Count(c => c.Status == ScanCycleStatus.Stale),
            cycles.Count(c => c.Status == ScanCycleStatus.Failed),
            cycles.Count(c => c.Status == ScanCycleStatus.Skipped),
            scanned.Count > 0 ? scanned.Average(c => c.DurationMs) : null,
            scanned.Count > 0 ? scanned.Max(c => c.DurationMs) : null,
            maxGap,
            cycles.Where(c => !string.IsNullOrEmpty(c.ErrorCode)).GroupBy(c => c.ErrorCode!).ToDictionary(g => g.Key, g => g.Count()));
    }

    private static (SaturationTargetSummary, List<SaturationEvent>) AnalyzeTarget(string bssid, List<(SaturationCycle C, SaturationObservation O)> s,
        SaturationThresholds th, Dictionary<long, double> weightBySeq, double activeSeconds)
    {
        var events = new List<SaturationEvent>();
        var observed = s.Where(x => x.O.Status == ObservationStatus.Observed && x.O.Rssi.HasValue).ToList();
        string ssid = s.Select(x => x.O.Ssid).LastOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";
        int nObserved = observed.Count;
        int nMissing = s.Count(x => x.O.Status == ObservationStatus.Missing);
        int nStale = s.Count(x => x.O.Status == ObservationStatus.Stale);
        int nUnknown = s.Count(x => x.O.Status == ObservationStatus.Unknown);
        double? availability = nObserved + nMissing > 0 ? 100.0 * nObserved / (nObserved + nMissing) : null;

        var rssi = observed.Select(x => x.O.Rssi!.Value).ToList();
        var sorted = rssi.OrderBy(v => v).ToList();
        double? mean = rssi.Count > 0 ? rssi.Average() : null;
        double? sd = mean is double m ? Math.Sqrt(rssi.Sum(v => (v - m) * (v - m)) / rssi.Count) : null;

        // Scan-to-scan RSSI variation over consecutive observed samples.
        var variation = new List<double>();
        for (int i = 1; i < observed.Count; i++) variation.Add(Math.Abs(observed[i].O.Rssi!.Value - observed[i - 1].O.Rssi!.Value));
        var varSorted = variation.OrderBy(v => v).ToList();

        // Threshold exposure, time-weighted.
        double totalW = observed.Sum(x => weightBySeq.GetValueOrDefault(x.C.Sequence));
        double Pct(int limit) => totalW > 0 ? 100.0 * observed.Where(x => x.O.Rssi < limit).Sum(x => weightBySeq.GetValueOrDefault(x.C.Sequence)) / totalW : 0;
        double Longest(int limit)
        {
            double best = 0, run = 0;
            foreach (var x in observed)
            {
                if (x.O.Rssi < limit) { run += weightBySeq.GetValueOrDefault(x.C.Sequence); best = Math.Max(best, run); }
                else run = 0;
            }
            return best / 1000.0;
        }

        events.AddRange(DetectExcursions(bssid, ssid, observed, th));
        events.AddRange(DetectDropouts(bssid, ssid, s, th));
        events.AddRange(DetectChannelChanges(bssid, ssid, s));

        int drops = events.Count(e => e.Type == SaturationEventType.Drop);
        int peaks = events.Count(e => e.Type == SaturationEventType.Peak);
        int dropouts = events.Count(e => e.Type == SaturationEventType.Dropout);
        int chg = events.Count(e => e.Type == SaturationEventType.ChannelChange);
        double? worstDrop = events.Where(e => e.Type == SaturationEventType.Drop).Select(e => e.MagnitudeDb).Max();
        double longestDropout = events.Where(e => e.Type == SaturationEventType.Dropout).Select(e => e.DurationSeconds ?? 0).DefaultIfEmpty(0).Max();

        double? score = null;
        if (nObserved > 0 && sd is double sdv)
        {
            double hours = Math.Max(activeSeconds / 3600.0, MinRateHours);
            double raw = 100 - 4 * sdv - 0.5 * (100 - (availability ?? 100)) - 3 * drops / hours - 5 * dropouts / hours - 5 * chg / hours;
            score = Math.Round(Math.Clamp(raw, 0, 100), 1);
        }

        var coList = observed.Where(x => x.O.CoChannelCount.HasValue).Select(x => x.O.CoChannelCount!.Value).ToList();
        var ovList = observed.Where(x => x.O.OverlapCount.HasValue).Select(x => x.O.OverlapCount!.Value).ToList();
        var channels = s.Where(x => x.O.Channel.HasValue).Select(x => (x.O.Channel!.Value, x.O.Band)).Distinct().ToList();
        var primary = s.Where(x => x.O.Channel.HasValue).GroupBy(x => x.O.Channel!.Value).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        var width = s.Where(x => x.O.ChannelWidth.HasValue).GroupBy(x => x.O.ChannelWidth!.Value).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();

        var summary = new SaturationTargetSummary
        {
            Bssid = bssid,
            Ssid = ssid,
            Bands = string.Join(", ", s.Select(x => x.O.Band).Where(b => b != null).Distinct()),
            Channels = string.Join(", ", channels.Select(c => $"{c.Item1}")),
            PrimaryChannel = primary,
            ChannelWidth = width,
            CyclesTracked = s.Count,
            Observed = nObserved,
            Missing = nMissing,
            Stale = nStale,
            Unknown = nUnknown,
            AvailabilityPct = availability,
            Min = sorted.Count > 0 ? sorted[0] : null,
            Max = sorted.Count > 0 ? sorted[^1] : null,
            Mean = mean,
            Median = Percentile(sorted, 50),
            StdDev = sd,
            P5 = Percentile(sorted, 5),
            P25 = Percentile(sorted, 25),
            P75 = Percentile(sorted, 75),
            P95 = Percentile(sorted, 95),
            Iqr = sorted.Count > 0 ? Percentile(sorted, 75) - Percentile(sorted, 25) : null,
            Range = sorted.Count > 0 ? sorted[^1] - sorted[0] : null,
            VariationMean = variation.Count > 0 ? variation.Average() : null,
            VariationP95 = Percentile(varSorted, 95),
            VariationMax = variation.Count > 0 ? variation.Max() : null,
            TrendDbPerHour = Slope(observed),
            PctBelowGood = nObserved > 0 ? Pct(th.GoodDbm) : null,
            PctBelowFair = nObserved > 0 ? Pct(th.FairDbm) : null,
            PctBelowUnusable = nObserved > 0 ? Pct(th.UnusableDbm) : null,
            LongestBelowFairSeconds = Longest(th.FairDbm),
            LongestBelowUnusableSeconds = Longest(th.UnusableDbm),
            DropCount = drops,
            PeakCount = peaks,
            DropoutCount = dropouts,
            ChannelChangeCount = chg,
            WorstDropDb = worstDrop,
            LongestDropoutSeconds = longestDropout,
            CoChannelMean = coList.Count > 0 ? coList.Average() : null,
            CoChannelMax = coList.Count > 0 ? coList.Max() : null,
            OverlapMean = ovList.Count > 0 ? ovList.Average() : null,
            OverlapMax = ovList.Count > 0 ? ovList.Max() : null,
            StabilityScore = score,
            Grade = Grade(score),
            Tier = Tier(mean),
        };
        double span = observed.Count >= 2 ? (observed[^1].C.ElapsedMs - observed[0].C.ElapsedMs) / 1000.0 : 0;
        return (summary with { Findings = Findings(summary, th, span) }, events);
    }

    /// <summary>Nearest-rank percentile of an ascending list.</summary>
    public static double? Percentile<T>(IReadOnlyList<T> ascending, double p) where T : IConvertible
    {
        if (ascending.Count == 0) return null;
        int rank = (int)Math.Ceiling(p / 100.0 * ascending.Count);
        rank = Math.Clamp(rank, 1, ascending.Count);
        return Convert.ToDouble(ascending[rank - 1]);
    }

    private static double? Slope(List<(SaturationCycle C, SaturationObservation O)> observed)
    {
        if (observed.Count < 2) return null;
        var xs = observed.Select(x => x.C.ElapsedMs / 3_600_000.0).ToList();
        var ys = observed.Select(x => (double)x.O.Rssi!.Value).ToList();
        double mx = xs.Average(), my = ys.Average();
        double sxx = xs.Sum(x => (x - mx) * (x - mx));
        if (sxx <= 0) return null;
        double sxy = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum();
        return sxy / sxx;
    }

    private static double Median(IEnumerable<int> values)
    {
        var v = values.OrderBy(x => x).ToList();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2.0;
    }

    /// <summary>Drops and peaks against a rolling-median baseline that is frozen while an event is open (hysteresis D/2).</summary>
    internal static List<SaturationEvent> DetectExcursions(string bssid, string ssid, List<(SaturationCycle C, SaturationObservation O)> observed, SaturationThresholds th)
    {
        var events = new List<SaturationEvent>();
        var window = new Queue<int>();
        SaturationEventType? open = null;
        double baselineAtStart = 0;
        SaturationCycle? startCycle = null;
        int extreme = 0;
        SaturationCycle? extremeCycle = null;
        int cyclesInEvent = 0;
        SaturationCycle? lastCycle = null;

        void Close(SaturationCycle? endCycle)
        {
            if (open is not SaturationEventType type || startCycle == null) return;
            double? duration = endCycle != null ? (endCycle.ElapsedMs - startCycle.ElapsedMs) / 1000.0 : (lastCycle!.ElapsedMs - startCycle.ElapsedMs) / 1000.0;
            double magnitude = type == SaturationEventType.Drop ? baselineAtStart - extreme : extreme - baselineAtStart;
            events.Add(new SaturationEvent(type, bssid, ssid, startCycle.WallUtc, endCycle?.WallUtc, startCycle.ElapsedMs, endCycle?.ElapsedMs,
                duration, cyclesInEvent, baselineAtStart, extreme, magnitude,
                endCycle != null && extremeCycle != null ? (endCycle.ElapsedMs - extremeCycle.ElapsedMs) / 1000.0 : null,
                endCycle == null
                    ? $"{(type == SaturationEventType.Drop ? "Drop" : "Peak")} of {magnitude:F0} dB vs baseline {baselineAtStart:F1} dBm; not recovered by end of test"
                    : $"{(type == SaturationEventType.Drop ? "Drop" : "Peak")} of {magnitude:F0} dB vs baseline {baselineAtStart:F1} dBm ({extreme} dBm), recovered after {duration:F0} s"));
            open = null;
        }

        foreach (var (c, o) in observed)
        {
            int r = o.Rssi!.Value;
            lastCycle = c;
            if (open == SaturationEventType.Drop)
            {
                cyclesInEvent++;
                if (r < extreme) { extreme = r; extremeCycle = c; }
                if (r >= baselineAtStart - th.DropDb / 2) { Close(c); window.Enqueue(r); Trim(window, th.BaselineWindow); }
                continue;
            }
            if (open == SaturationEventType.Peak)
            {
                cyclesInEvent++;
                if (r > extreme) { extreme = r; extremeCycle = c; }
                if (r <= baselineAtStart + th.PeakDb / 2) { Close(c); window.Enqueue(r); Trim(window, th.BaselineWindow); }
                continue;
            }

            if (window.Count >= th.MinBaselineSamples)
            {
                double baseline = Median(window);
                if (r <= baseline - th.DropDb || r >= baseline + th.PeakDb)
                {
                    open = r <= baseline - th.DropDb ? SaturationEventType.Drop : SaturationEventType.Peak;
                    baselineAtStart = baseline;
                    startCycle = c;
                    extreme = r;
                    extremeCycle = c;
                    cyclesInEvent = 1;
                    continue;
                }
            }
            window.Enqueue(r);
            Trim(window, th.BaselineWindow);
        }
        Close(null);
        return events;
    }

    private static void Trim(Queue<int> q, int max)
    {
        while (q.Count > max) q.Dequeue();
    }

    /// <summary>N or more consecutive Missing cycles. Stale/Unknown neither extend nor break a run.</summary>
    internal static List<SaturationEvent> DetectDropouts(string bssid, string ssid, List<(SaturationCycle C, SaturationObservation O)> s, SaturationThresholds th)
    {
        var events = new List<SaturationEvent>();
        SaturationCycle? first = null;
        int run = 0;
        foreach (var (c, o) in s)
        {
            if (o.Status == ObservationStatus.Missing)
            {
                first ??= c;
                run++;
            }
            else if (o.Status == ObservationStatus.Observed)
            {
                if (run >= th.DropoutMissedCycles && first != null)
                {
                    double d = (c.ElapsedMs - first.ElapsedMs) / 1000.0;
                    events.Add(new SaturationEvent(SaturationEventType.Dropout, bssid, ssid, first.WallUtc, c.WallUtc, first.ElapsedMs, c.ElapsedMs, d, run,
                        null, null, null, null, $"Not seen in {run} consecutive fresh scans ({d:F0} s)"));
                }
                first = null;
                run = 0;
            }
        }
        if (run >= th.DropoutMissedCycles && first != null)
        {
            var last = s[^1].C;
            double d = (last.ElapsedMs - first.ElapsedMs) / 1000.0;
            events.Add(new SaturationEvent(SaturationEventType.Dropout, bssid, ssid, first.WallUtc, null, first.ElapsedMs, null, d, run,
                null, null, null, null, $"Not seen in the last {run} consecutive fresh scans; not recovered by end of test"));
        }
        return events;
    }

    internal static List<SaturationEvent> DetectChannelChanges(string bssid, string ssid, List<(SaturationCycle C, SaturationObservation O)> s)
    {
        var events = new List<SaturationEvent>();
        (int Ch, string? Band, int? Width)? prev = null;
        foreach (var (c, o) in s)
        {
            if (o.Status is not (ObservationStatus.Observed or ObservationStatus.Stale) || o.Channel is not int ch) continue;
            if (prev is { } p && (p.Ch != ch || p.Band != o.Band))
            {
                string widthNote = p.Width != o.ChannelWidth ? $", width {p.Width?.ToString() ?? "?"}→{o.ChannelWidth?.ToString() ?? "?"} MHz" : "";
                events.Add(new SaturationEvent(SaturationEventType.ChannelChange, bssid, ssid, c.WallUtc, c.WallUtc, c.ElapsedMs, c.ElapsedMs, 0, 1,
                    null, o.Rssi, null, null, $"Channel {p.Ch} ({p.Band}) → {ch} ({o.Band}){widthNote}"));
            }
            prev = (ch, o.Band, o.ChannelWidth);
        }
        return events;
    }

    private static (SaturationSsidSummary, List<SaturationEvent>) AnalyzeSsid(string ssid, List<string> bssids, List<SaturationCycle> cycles, SaturationThresholds th)
    {
        var set = new HashSet<string>(bssids, StringComparer.OrdinalIgnoreCase);
        var events = new List<SaturationEvent>();
        var best = new List<int>();
        int eligible = 0, available = 0;
        string? current = null;
        int currentRssi = 0;

        foreach (var c in cycles)
        {
            var obs = c.Observations.Where(o => set.Contains(o.Bssid)).ToList();
            var seen = obs.Where(o => o.Status == ObservationStatus.Observed && o.Rssi.HasValue).ToList();
            if (seen.Count > 0 || (c.Status == ScanCycleStatus.Fresh && obs.Any(o => o.Status == ObservationStatus.Missing))) eligible++;
            if (seen.Count == 0) continue;
            available++;

            var top = seen.OrderByDescending(o => o.Rssi).First();
            best.Add(top.Rssi!.Value);
            var currentObs = current == null ? null : seen.FirstOrDefault(o => string.Equals(o.Bssid, current, StringComparison.OrdinalIgnoreCase));

            if (current == null)
            {
                current = top.Bssid;
                currentRssi = top.Rssi.Value;
                continue;
            }
            if (string.Equals(top.Bssid, current, StringComparison.OrdinalIgnoreCase))
            {
                currentRssi = top.Rssi.Value;
                continue;
            }
            bool previousGone = currentObs == null;
            bool stronger = currentObs != null && top.Rssi.Value >= currentObs.Rssi!.Value + th.StrongestChangeHysteresisDb;
            if (previousGone || stronger)
            {
                int fromRssi = currentObs?.Rssi ?? currentRssi;
                events.Add(new SaturationEvent(SaturationEventType.StrongestBssidChange, ssid, ssid, c.WallUtc, c.WallUtc, c.ElapsedMs, c.ElapsedMs, 0, 1,
                    null, top.Rssi, top.Rssi.Value - fromRssi, null,
                    previousGone
                        ? $"Strongest BSSID {current} not observed; now {top.Bssid} ({top.Rssi} dBm)"
                        : $"Strongest BSSID {current} ({fromRssi} dBm) → {top.Bssid} ({top.Rssi} dBm)"));
                current = top.Bssid;
                currentRssi = top.Rssi.Value;
            }
            else if (currentObs != null)
            {
                currentRssi = currentObs.Rssi!.Value;
            }
        }

        return (new SaturationSsidSummary(ssid, bssids.Count,
            eligible > 0 ? 100.0 * available / eligible : null,
            best.Count > 0 ? best.Average() : null,
            best.Count > 0 ? best.Min() : null,
            best.Count > 0 ? best.Max() : null,
            events.Count), events);
    }

    public static string Grade(double? score) => score switch
    {
        null => "n/a",
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F"
    };

    /// <summary>Text tier for an RSSI value (colour is never the only signal — SPEC §7.3).</summary>
    public static string Tier(double? rssi) => rssi switch
    {
        null => "n/a",
        >= -55 => "Excellent",
        >= -67 => "Good",
        >= -70 => "Fair",
        >= -80 => "Weak",
        _ => "Unusable"
    };

    private static IReadOnlyList<string> Findings(SaturationTargetSummary t, SaturationThresholds th, double observedSpanSeconds)
    {
        var f = new List<string>();
        if (t.Observed == 0)
        {
            f.Add("Never observed fresh during the test.");
            return f;
        }
        if (t.DropoutCount > 0) f.Add($"Disappeared {t.DropoutCount} time(s); longest outage {t.LongestDropoutSeconds:F0} s.");
        if (t.DropCount > 0) f.Add($"{t.DropCount} drop(s) of ≥ {th.DropDb:F0} dB below baseline; worst {t.WorstDropDb:F0} dB.");
        if (t.PeakCount > 0) f.Add($"{t.PeakCount} peak(s) of ≥ {th.PeakDb:F0} dB above baseline.");
        if (t.PctBelowUnusable > 0) f.Add($"Below {th.UnusableDbm} dBm (unusable) for {t.PctBelowUnusable:F1}% of observed time; longest {t.LongestBelowUnusableSeconds:F0} s.");
        else if (t.PctBelowFair > 0) f.Add($"Below {th.FairDbm} dBm (fair) for {t.PctBelowFair:F1}% of observed time; longest {t.LongestBelowFairSeconds:F0} s.");
        if (t.ChannelChangeCount > 0) f.Add($"Changed channel {t.ChannelChangeCount} time(s) (channels {t.Channels}).");
        // A per-hour trend extrapolated from a short window is noise; only report it over >= 15 minutes (lesson IT-T06-01).
        if (t.TrendDbPerHour is double slope && Math.Abs(slope) >= 2 && t.Observed >= 10 && observedSpanSeconds >= MinTrendSpanSeconds)
            f.Add($"Trending {(slope < 0 ? "down" : "up")} {Math.Abs(slope):F1} dB/hour.");
        if (t.AvailabilityPct is double a && a < 99) f.Add($"Availability {a:F1}% of fresh scans.");
        if (f.Count == 0) f.Add($"Stable: mean {t.Mean:F1} dBm ({t.Tier}), σ {t.StdDev:F1} dB, no events at current thresholds.");
        return f;
    }
}
