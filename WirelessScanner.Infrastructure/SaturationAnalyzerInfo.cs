
namespace WirelessScanner.Infrastructure
{
    /// <summary>Analytics constants needed by the exporter (Infrastructure must not depend on Application internals).</summary>
    internal static class SaturationAnalyzerInfo
    {
        public const int MinTrendMinutes = 15;
    }
}
