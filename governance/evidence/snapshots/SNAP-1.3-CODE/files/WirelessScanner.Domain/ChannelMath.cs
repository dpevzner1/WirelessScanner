using System;
using System.Linq;

namespace WirelessScanner.Domain;

/// <summary>
/// 802.11 channel geometry shared by the spectrum view (DR-0010) and the saturation analytics (DR-0012): where a
/// network's energy sits and how wide it is. Pure functions; unit-tested.
/// </summary>
public static class ChannelMath
{
    /// <summary>DFS range in 5 GHz (U-NII-2A/2C), MHz.</summary>
    public const double DfsLowMhz = 5250, DfsHighMhz = 5730;

    public static double ChannelFrequencyMhz(string band, int channel) => band switch
    {
        "2.4 GHz" => channel == 14 ? 2484 : 2407 + 5 * channel,
        "5 GHz" => 5000 + 5 * channel,
        "6 GHz" => 5950 + 5 * channel,
        _ => double.NaN
    };

    private static readonly int[] Centers5_40 = { 38, 46, 54, 62, 102, 110, 118, 126, 134, 142, 151, 159 };
    private static readonly int[] Centers5_80 = { 42, 58, 106, 122, 138, 155 };
    private static readonly int[] Centers5_160 = { 50, 114 };

    /// <summary>
    /// Centre frequency of the channel block the AP occupies. The WLAN API reports the primary channel; bonded
    /// 40/80/160 MHz blocks sit on standard centres (5/6 GHz). In 2.4 GHz a 40 MHz AP is assumed HT40+ below
    /// channel 8 and HT40- otherwise (the secondary-channel offset is not reported by the API).
    /// </summary>
    public static double BlockCenterMhz(string band, int primaryChannel, int? widthMhz)
    {
        int w = widthMhz ?? 20;
        if (band == "2.4 GHz")
        {
            double f = ChannelFrequencyMhz(band, primaryChannel);
            return w >= 40 ? f + (primaryChannel < 8 ? 10 : -10) : f;
        }
        if (band == "5 GHz")
        {
            int[]? centers = w switch { 40 => Centers5_40, 80 => Centers5_80, >= 160 => Centers5_160, _ => null };
            if (centers == null) return ChannelFrequencyMhz(band, primaryChannel);
            int halfSpan = w / 10; // in channel numbers (5 MHz each): w MHz / 5 / 2
            int center = centers.FirstOrDefault(c => Math.Abs(c - primaryChannel) <= halfSpan);
            return ChannelFrequencyMhz(band, center == 0 ? primaryChannel : center);
        }
        if (band == "6 GHz")
        {
            int n = Math.Max(1, w / 20);                    // 20 MHz channels in the block
            int index = Math.Max(0, (primaryChannel - 1) / 4);
            int block = index / n;
            int centerCh = 1 + block * n * 4 + (n * 4 - 4) / 2;
            return ChannelFrequencyMhz(band, centerCh);
        }
        return double.NaN;
    }

    /// <summary>Frequency span (MHz) occupied: block centre ± width/2. Unknown width is treated as 20 MHz.</summary>
    public static (double Low, double High) Occupancy(string band, int primaryChannel, int? widthMhz)
    {
        double c = BlockCenterMhz(band, primaryChannel, widthMhz);
        double half = (widthMhz ?? 20) / 2.0;
        return (c - half, c + half);
    }

    public static (double Low, double High) Occupancy(AccessPoint ap) => Occupancy(ap.Band, ap.Channel, ap.ChannelWidth);

    /// <summary>True when two occupied blocks share spectrum (same band). Touching edges do not count.</summary>
    public static bool Overlaps(AccessPoint a, AccessPoint b)
    {
        if (a.Band != b.Band || a.Channel <= 0 || b.Channel <= 0) return false;
        var (al, ah) = Occupancy(a);
        var (bl, bh) = Occupancy(b);
        return al < bh && bl < ah;
    }

    /// <summary>True when the occupied block touches the 5 GHz DFS range.</summary>
    public static bool IsDfs(string band, int primaryChannel, int? widthMhz)
    {
        if (band != "5 GHz" || primaryChannel <= 0) return false;
        var (lo, hi) = Occupancy(band, primaryChannel, widthMhz);
        return lo < DfsHighMhz && hi > DfsLowMhz;
    }
}

/// <summary>Shared measurement constants (DR-0004).</summary>
public static class WirelessScannerConstants
{
    /// <summary>Assumed noise floor used only to ESTIMATE SNR; Windows does not report a noise floor.</summary>
    public const int AssumedNoiseFloorDbm = -95;
}
