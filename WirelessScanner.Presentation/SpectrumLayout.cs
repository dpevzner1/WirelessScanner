using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

/// <summary>
/// Pure spectrum geometry for the RF view (DR-0010): where a network's energy actually sits, how wide it is,
/// how loaded each channel is, and the waterfall bins. No WPF types, so it is unit-tested directly.
/// </summary>
public static class SpectrumLayout
{
    public sealed record BandAxis(string Band, double LowMhz, double HighMhz, IReadOnlyList<int> TickChannels, IReadOnlyList<int> Channels,
        double? DfsLowMhz, double? DfsHighMhz, IReadOnlyList<int> PreferredChannels);

    public static readonly BandAxis Band24 = new("2.4 GHz", 2400, 2485, Enumerable.Range(1, 14).ToList(), Enumerable.Range(1, 13).ToList(),
        null, null, new[] { 1, 6, 11 });

    public static readonly BandAxis Band5 = new("5 GHz", 5150, 5850, new[] { 36, 48, 64, 100, 116, 132, 149, 165 },
        new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 },
        5250, 5730, new[] { 36, 40, 44, 48, 149, 153, 157, 161, 165 });

    public static readonly BandAxis Band6 = new("6 GHz", 5925, 7125, new[] { 1, 33, 65, 97, 129, 161, 193, 225 },
        Enumerable.Range(0, 59).Select(i => 1 + 4 * i).ToList(), null, null, Enumerable.Range(0, 15).Select(i => 5 + 16 * i).ToList());

    public static BandAxis? AxisFor(string band) => band switch
    {
        "2.4 GHz" => Band24,
        "5 GHz" => Band5,
        "6 GHz" => Band6,
        _ => null
    };

    // Geometry lives in WirelessScanner.Domain.ChannelMath (shared with the saturation analytics, DR-0012).
    public static double ChannelFrequencyMhz(string band, int channel) => ChannelMath.ChannelFrequencyMhz(band, channel);
    public static double BlockCenterMhz(string band, int primaryChannel, int? widthMhz) => ChannelMath.BlockCenterMhz(band, primaryChannel, widthMhz);
    public static (double Low, double High) Occupancy(AccessPoint ap) => ChannelMath.Occupancy(ap);

    /// <summary>
    /// Relative load per channel: sum over APs overlapping that channel's 20 MHz slot of a signal weight in 0..1
    /// ((RSSI + 95) / 45, clamped). Strong, wide neighbours load more channels.
    /// </summary>
    public static IReadOnlyDictionary<int, double> ChannelLoad(BandAxis axis, IEnumerable<AccessPoint> aps)
    {
        var list = aps.Where(a => a.Band == axis.Band && a.Channel > 0).ToList();
        var load = new Dictionary<int, double>();
        foreach (var ch in axis.Channels)
        {
            double f = ChannelFrequencyMhz(axis.Band, ch);
            double lo = f - 10, hi = f + 10;
            load[ch] = list.Where(a => { var (l, h) = Occupancy(a); return l < hi && h > lo; })
                           .Sum(a => Math.Clamp((a.RSSI + 95) / 45.0, 0, 1));
        }
        return load;
    }

    /// <summary>Least-loaded preferred channel (2.4 GHz: 1/6/11; 5 GHz: non-DFS first). Ties go to the lower channel.</summary>
    public static int LeastBusyChannel(BandAxis axis, IReadOnlyDictionary<int, double> load) =>
        axis.PreferredChannels.Where(load.ContainsKey).OrderBy(c => load[c]).ThenBy(c => c).DefaultIfEmpty(axis.Channels[0]).First();

    /// <summary>Strongest RSSI heard on each channel slot in one scan (null = nothing heard). Used by the waterfall.</summary>
    public static IReadOnlyDictionary<int, int?> StrongestPerChannel(BandAxis axis, IEnumerable<AccessPoint> aps)
    {
        var list = aps.Where(a => a.Band == axis.Band && a.Channel > 0).ToList();
        var result = new Dictionary<int, int?>();
        foreach (var ch in axis.Channels)
        {
            double f = ChannelFrequencyMhz(axis.Band, ch);
            var hits = list.Where(a => { var (l, h) = Occupancy(a); return l < f + 10 && h > f - 10; }).Select(a => a.RSSI).ToList();
            result[ch] = hits.Count == 0 ? null : hits.Max();
        }
        return result;
    }

    /// <summary>Waterfall intensity 0..1 for an RSSI (−90 dBm → 0, −45 dBm → 1).</summary>
    public static double Intensity(int rssi) => Math.Clamp((rssi + 90) / 45.0, 0, 1);

    public static bool IsDfs(BandAxis axis, double mhz) => axis.DfsLowMhz is double lo && axis.DfsHighMhz is double hi && mhz >= lo && mhz <= hi;
}
