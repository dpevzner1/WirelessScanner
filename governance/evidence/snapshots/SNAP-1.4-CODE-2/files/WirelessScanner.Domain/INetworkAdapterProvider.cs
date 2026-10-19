using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public enum NetworkDeviceType
{
    Wireless_WiFi,
    Wired_Ethernet,
    Other
}

public record NetworkInterfaceInfo(
    Guid Id, 
    string Name, 
    string Description, 
    string PnpDeviceId,
    NetworkDeviceType DeviceType, 
    bool IsConnected,
    string MacAddress,
    string IpAddress
)
{
    public string DisplayName => $"{Name} [{Description}] | MAC: {MacAddress} | IP: {IpAddress}";
    public string DetailsText => $"MAC: {MacAddress}  |  IP: {IpAddress}";
}

public record AccessPoint(
    string BSSID,
    string SSID,
    string? Hostname,
    string Band,
    int Channel,
    int? ChannelWidth,
    int RSSI,
    int? NoiseFloor,
    int SNR,
    int Quality,
    string SecurityType,
    string NetworkType,
    string? VendorOUI,
    string[]? SupportedRates,
    DateTime LastSeen,
    // Age of the most recent beacon/probe response when the list was read (null = unknown). DR-0003.
    double? BeaconAgeMs = null
)
{
    // Diagnostic detail added in 1.3 (DR-0012). Null = not reported by the AP / driver; never guessed.
    /// <summary>802.11 standard of the BSS as reported by Windows (e.g. "802.11ax"), from dot11BssPhyType.</summary>
    public string? PhyType { get; init; }
    /// <summary>Beacon interval in time units (1 TU = 1.024 ms).</summary>
    public int? BeaconIntervalTu { get; init; }
    /// <summary>Centre frequency of the primary 20 MHz channel in MHz.</summary>
    public double? CenterFrequencyMhz { get; init; }
    /// <summary>BSS Load element (802.11 element 11): associated stations, as advertised by the AP itself.</summary>
    public int? StationCount { get; init; }
    /// <summary>BSS Load element: share of time the AP sensed the channel busy, 0-100 % (raw value / 255).</summary>
    public double? ChannelUtilizationPct { get; init; }
    /// <summary>BSS Load element: available admission capacity in units of 32 microseconds per second.</summary>
    public int? AdmissionCapacity { get; init; }

    // Physical-unit evidence (1.4, DR-0013). Null = not reported; never guessed.
    /// <summary>
    /// The AP radio's TSF timer minus the host receive time, in milliseconds. BSSIDs served by the same radio share one
    /// TSF clock, so their offsets agree to within milliseconds; different radios/boxes differ by seconds or more.
    /// </summary>
    public double? TsfOffsetMs { get; init; }
    /// <summary>WPS element (vendor 00:50:F2 type 4): manufacturer, model name/number, device name, UUID-E.</summary>
    public string? WpsManufacturer { get; init; }
    public string? WpsModel { get; init; }
    public string? WpsModelNumber { get; init; }
    public string? WpsDeviceName { get; init; }
    /// <summary>WPS UUID-E: a per-device identifier shared by all BSSIDs of a unit that advertises it.</summary>
    public string? WpsUuid { get; init; }
}

/// <summary>The measuring PC's own Wi-Fi association (affects scan throttling and the receive view). DR-0012.</summary>
public record ClientConnectionInfo
{
    public bool Connected { get; init; }
    public string State { get; init; } = "";
    public string? Ssid { get; init; }
    public string? Bssid { get; init; }
    public string? PhyType { get; init; }
    public int? SignalQualityPct { get; init; }
    public double? RxRateMbps { get; init; }
    public double? TxRateMbps { get; init; }
    public int? Channel { get; init; }
    public string? Band { get; init; }
}

/// <summary>Adapter driver identity: RSSI calibration differs between drivers, so results are only comparable per driver. DR-0012.</summary>
public record AdapterDriverInfo(string? DriverVersion, string? DriverDate, string? Provider, string? HardwareDescription);

public record WiredInterfaceStats(
    long SpeedBitsPerSecond, 
    string IpAddress, 
    string LinkStatus, 
    double AvgPingLatencyMs
);

public interface INetworkAdapterProvider
{
    // Enumerates all system network interfaces (wired + wireless)
    Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync();

    // Scans and returns BSS list for selected wireless adapter
    Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId);

    // Triggers hardware scan cycle
    Task TriggerHardwareScanAsync(Guid interfaceId);

    // One acquisition cycle: request a fresh scan, wait for completion (or timeout), read the BSS list (DR-0003).
    // Default for providers without scan-completion support: read the current list and report it as Stale.
    async Task<ScanCycleResult> ScanAsync(Guid interfaceId, TimeSpan timeout, System.Threading.CancellationToken cancellationToken = default)
    {
        var requested = DateTime.UtcNow;
        try
        {
            var list = await GetAccessPointsAsync(interfaceId);
            return new ScanCycleResult(ScanCycleStatus.Stale, requested, DateTime.UtcNow,
                (DateTime.UtcNow - requested).TotalMilliseconds, System.Linq.Enumerable.ToList(list),
                "no_scan_confirmation", "Provider does not confirm fresh scans; list may be cached.");
        }
        catch (Exception ex)
        {
            return ScanCycleResult.Failure(requested, "provider_error", ex.Message);
        }
    }

    // Queries connection status, speed, and IP details for Ethernet adapter
    Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId);

    // The PC's own association on this adapter (null = not available from this provider).
    Task<ClientConnectionInfo?> GetConnectionInfoAsync(Guid interfaceId) => Task.FromResult<ClientConnectionInfo?>(null);

    // Driver version/date/provider of the adapter (null = not available).
    AdapterDriverInfo? GetDriverInfo(Guid interfaceId) => null;

    // Event raised when Windows notifies a PNP hardware change
    event Action HardwareChanged;

    // Internal callback invoked when WndProc captures WM_DEVICECHANGE
    void NotifyHardwareChange();
}
