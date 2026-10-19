using System.Collections.Generic;

namespace WirelessScanner.Domain;

public record TelemetryStats(
    int MinRssi,
    int MaxRssi,
    double MeanRssi,
    double StdDevRssi,
    double AvgJitter
);

public interface IStatisticsCalculator
{
    TelemetryStats CalculateStats(IEnumerable<int> rssiValues, IEnumerable<double> jitterValues);
}
