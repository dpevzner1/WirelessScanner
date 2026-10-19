using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

public class StatisticsCalculator : IStatisticsCalculator
{
    public TelemetryStats CalculateStats(IEnumerable<int> rssiValues, IEnumerable<double> jitterValues)
    {
        var rssiList = rssiValues as IList<int> ?? rssiValues.ToList();
        var jitterList = jitterValues as IList<double> ?? jitterValues.ToList();

        if (rssiList.Count == 0)
        {
            return new TelemetryStats(0, 0, 0.0, 0.0, 0.0);
        }

        int min = rssiList.Min();
        int max = rssiList.Max();
        double mean = rssiList.Average();
        
        // Std Dev calculation
        double varianceSum = rssiList.Sum(val => Math.Pow(val - mean, 2));
        double stdDev = Math.Sqrt(varianceSum / rssiList.Count);

        double avgJitter = jitterList.Count > 0 ? jitterList.Average() : 0.0;

        return new TelemetryStats(min, max, mean, stdDev, avgJitter);
    }
}
