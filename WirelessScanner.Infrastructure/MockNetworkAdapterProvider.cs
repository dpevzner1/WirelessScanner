using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class MockNetworkAdapterProvider : INetworkAdapterProvider
{
    private readonly List<NetworkInterfaceInfo> _mockDevices;
    private readonly List<AccessPoint> _mockAPs;
    private readonly Random _random = new();

    public event Action? HardwareChanged;

    public MockNetworkAdapterProvider()
    {
        _mockDevices = new List<NetworkInterfaceInfo>
        {
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Intel Wi-Fi 6E AX211", "Built-in Wireless NIC", "PCI\\VEN_8086&DEV_0090", NetworkDeviceType.Wireless_WiFi, true, "00:1A:11:22:33:44", "192.168.1.100"),
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Alfa AWUS036ACM USB", "External USB High-Gain Antenna", "USB\\VID_0E8D&PID_7612", NetworkDeviceType.Wireless_WiFi, true, "00:1A:11:55:66:77", "192.168.1.101"),
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Realtek PCIe GbE Controller", "Built-in Wired Ethernet Adapter", "PCI\\VEN_10EC&DEV_8168", NetworkDeviceType.Wired_Ethernet, true, "00:1A:11:88:99:AA", "192.168.1.102")
        };

        _mockAPs = new List<AccessPoint>
        {
            new("8C:5A:25:04:91:B0", "PlantOps", "ap-ops-01.local", "5 GHz", 36, 80, -52, -95, 43, 85, "WPA3-Enterprise", "Infrastructure", "Cisco Systems", new[] { "54", "150", "300", "867" }, DateTime.UtcNow),
            new("8C:5A:25:04:91:B1", "PlantOps", "ap-ops-02.local", "2.4 GHz", 6, 20, -68, -95, 27, 64, "WPA3-Enterprise", "Infrastructure", "Cisco Systems", new[] { "11", "54", "150" }, DateTime.UtcNow),
            new("A0:B1:C2:D3:E4:01", "Facilities-IoT", "iot-gateway-01", "2.4 GHz", 1, 20, -74, -92, 18, 52, "WPA2-PSK", "Infrastructure", "Hewlett Packard", new[] { "11", "54" }, DateTime.UtcNow),
            new("A0:B1:C2:D3:E4:02", "Facilities-IoT", "iot-gateway-02", "5 GHz", 149, 40, -82, -94, 12, 36, "WPA2-PSK", "Infrastructure", "Hewlett Packard", new[] { "54", "300" }, DateTime.UtcNow),
            new("00:17:88:A1:B2:C3", "Guest-Portal", null, "2.4 GHz", 11, 20, -58, -90, 32, 70, "Open", "Infrastructure", "Philips Lighting", new[] { "11", "54" }, DateTime.UtcNow)
        };
    }

    public Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync()
    {
        return Task.FromResult<IEnumerable<NetworkInterfaceInfo>>(_mockDevices);
    }

    public Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId)
    {
        // Simulate minor RSSI fluctuation
        var updatedAPs = _mockAPs.Select(ap =>
        {
            int offset = _random.Next(-3, 4);
            int newRSSI = Math.Clamp(ap.RSSI + offset, -100, -30);
            int newQuality = Math.Clamp(2 * (newRSSI + 100), 0, 100);
            int snr = newRSSI - (ap.NoiseFloor ?? -95);

            return ap with
            {
                RSSI = newRSSI,
                Quality = newQuality,
                SNR = snr,
                LastSeen = DateTime.UtcNow
            };
        }).ToList();

        return Task.FromResult<IEnumerable<AccessPoint>>(updatedAPs);
    }

    public Task TriggerHardwareScanAsync(Guid interfaceId)
    {
        // Mock hardware scan completes immediately
        return Task.CompletedTask;
    }

    public Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId)
    {
        // Simulate a wired ethernet connection running at 1Gbps, with a slight jitter in ping latency
        double latency = 1.0 + _random.NextDouble() * 3.0; // 1-4 ms latency
        return Task.FromResult(new WiredInterfaceStats(1000000000, "192.168.1.145", "Up", latency));
    }

    public void NotifyHardwareChange()
    {
        HardwareChanged?.Invoke();
    }
}
