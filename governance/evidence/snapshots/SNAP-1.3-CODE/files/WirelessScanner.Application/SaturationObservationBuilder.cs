using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Tracks which BSSIDs a saturation test follows (SPEC-SAT-001 §5).
/// Explicit BSSID scopes are tracked from the first cycle (absence counts as Missing from the start);
/// SSID and All-visible scopes register a BSSID when it is first seen. Mixed (DR-0011) does both.
/// </summary>
public sealed class SaturationTargetTracker
{
    private readonly SaturationTestConfig _config;
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _ssidByBssid = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _targetSsids;

    public SaturationTargetTracker(SaturationTestConfig config)
    {
        _config = config;
        _targetSsids = new HashSet<string>(config.TargetSsids, StringComparer.Ordinal);
        if (config.Scope is SaturationScope.SingleAp or SaturationScope.MultipleAps or SaturationScope.Mixed)
        {
            foreach (var b in config.TargetBssids) Register(b.ToUpperInvariant(), "");
        }
    }

    public IReadOnlyList<string> Bssids => _order;

    public string SsidOf(string bssid) => _ssidByBssid.TryGetValue(bssid, out var s) ? s : "";

    public bool InScope(AccessPoint ap) => _config.Scope switch
    {
        SaturationScope.SingleAp or SaturationScope.MultipleAps => _ssidByBssid.ContainsKey(ap.BSSID),
        SaturationScope.SingleSsid or SaturationScope.MultipleSsids => _targetSsids.Contains(ap.SSID),
        SaturationScope.Mixed => _ssidByBssid.ContainsKey(ap.BSSID) || _targetSsids.Contains(ap.SSID),
        _ => true
    };

    public void Register(string bssid, string ssid)
    {
        if (_ssidByBssid.TryGetValue(bssid, out var existing))
        {
            if (string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(ssid)) _ssidByBssid[bssid] = ssid;
            return;
        }
        _ssidByBssid[bssid] = ssid;
        _order.Add(bssid);
    }
}

/// <summary>Turns one ScanCycleResult into per-target observations (SPEC §4). Pure apart from tracker registration.</summary>
public static class SaturationObservationBuilder
{
    public static IReadOnlyList<SaturationObservation> Build(SaturationTestConfig config, SaturationTargetTracker tracker, ScanCycleResult result, long sequence)
    {
        if (result.Status is ScanCycleStatus.Failed or ScanCycleStatus.Skipped)
        {
            return tracker.Bssids.Select(b => Unknown(sequence, b, tracker.SsidOf(b))).ToList();
        }

        var byBssid = new Dictionary<string, AccessPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in result.AccessPoints)
        {
            byBssid[ap.BSSID] = ap;
            if (tracker.InScope(ap)) tracker.Register(ap.BSSID.ToUpperInvariant(), ap.SSID);
        }

        double limitMs = config.FreshnessLimit.TotalMilliseconds;
        var observations = new List<SaturationObservation>(tracker.Bssids.Count);
        foreach (var bssid in tracker.Bssids)
        {
            if (byBssid.TryGetValue(bssid, out var ap))
            {
                bool fresh = IsFresh(ap, result.Status, limitMs);
                var (co, overlap, strongest) = CoChannel(ap, result.AccessPoints, n => IsFresh(n, result.Status, limitMs));
                observations.Add(new SaturationObservation(sequence, bssid, ap.SSID,
                    fresh ? ObservationStatus.Observed : ObservationStatus.Stale,
                    ap.RSSI, ap.Quality, ap.Channel, ap.Band, ap.ChannelWidth, ap.BeaconAgeMs, co, overlap,
                    Security: ap.SecurityType, Vendor: ap.VendorOUI, PhyType: ap.PhyType, BeaconIntervalTu: ap.BeaconIntervalTu,
                    CenterFrequencyMhz: ap.CenterFrequencyMhz, StationCount: ap.StationCount, ChannelUtilizationPct: ap.ChannelUtilizationPct,
                    StrongestOverlapRssi: strongest));
            }
            else
            {
                // Absence is only evidence of a dropout when Windows confirmed a fresh scan.
                observations.Add(result.Status == ScanCycleStatus.Fresh
                    ? new SaturationObservation(sequence, bssid, tracker.SsidOf(bssid), ObservationStatus.Missing, null, null, null, null, null, null, null, null)
                    : Unknown(sequence, bssid, tracker.SsidOf(bssid)));
            }
        }
        return observations;
    }

    private static SaturationObservation Unknown(long seq, string bssid, string ssid) =>
        new(seq, bssid, ssid, ObservationStatus.Unknown, null, null, null, null, null, null, null, null);

    private static bool IsFresh(AccessPoint ap, ScanCycleStatus cycle, double limitMs) =>
        ap.BeaconAgeMs is double age ? age <= limitMs : cycle == ScanCycleStatus.Fresh;

    /// <summary>
    /// SAT-ANALYTICS-2 (DR-0012), fresh neighbours only (cached entries are not current contention):
    /// co-channel = other BSSIDs on the same primary channel; overlap = other BSSIDs on a different primary channel whose
    /// occupied block (incl. 40/80/160 MHz bonding, every band) shares spectrum with this AP's block;
    /// strongest = highest RSSI among all of them (for the interference margin).
    /// </summary>
    internal static (int? CoChannel, int? Overlap, int? Strongest) CoChannel(AccessPoint ap, IReadOnlyList<AccessPoint> all, Func<AccessPoint, bool>? isFresh = null)
    {
        if (ap.Channel <= 0) return (null, null, null);
        var others = all.Where(o => !string.Equals(o.BSSID, ap.BSSID, StringComparison.OrdinalIgnoreCase) && o.Band == ap.Band && o.Channel > 0
                                    && (isFresh == null || isFresh(o))).ToList();
        var co = others.Where(o => o.Channel == ap.Channel).ToList();
        var overlap = others.Where(o => o.Channel != ap.Channel && ChannelMath.Overlaps(ap, o)).ToList();
        int? strongest = co.Concat(overlap).Select(o => (int?)o.RSSI).Max();
        return (co.Count, overlap.Count, strongest);
    }
}
