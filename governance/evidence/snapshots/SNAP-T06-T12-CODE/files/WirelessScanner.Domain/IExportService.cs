using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public record ExportMetadata(
    string ClientName,
    string SurveyorName,
    string Notes
);

public record ExportFields(
    bool Time = true,
    bool SSID = true,
    bool BSSID = true,
    bool Channel = true,
    bool Band = true,
    bool RSSI = true,
    bool SNR = true,
    bool Quality = true,
    bool Jitter = true,
    bool Zone = true,
    System.Collections.Generic.IEnumerable<string>? IncludedBssids = null
);

public interface IExportService
{
    Task ExportToCsvAsync(string filePath, Guid sessionId, ExportFields? fields = null);
    Task ExportToJsonAsync(string filePath, Guid sessionId, ExportFields? fields = null);
    Task ExportToPdfAsync(string filePath, Guid sessionId, ExportMetadata metadata, ExportFields? fields = null);

    // Multi-session batch exports
    Task ExportToCsvAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null);
    Task ExportToJsonAsync(string filePath, IEnumerable<Guid> sessionIds, ExportFields? fields = null);
}
