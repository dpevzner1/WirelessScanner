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
    DateTime LastSeen
);

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

    // Queries connection status, speed, and IP details for Ethernet adapter
    Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId);

    // Event raised when Windows notifies a PNP hardware change
    event Action HardwareChanged;
    
    // Internal callback invoked when WndProc captures WM_DEVICECHANGE
    void NotifyHardwareChange();
}
