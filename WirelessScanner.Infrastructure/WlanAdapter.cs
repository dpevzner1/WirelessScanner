using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class WlanAdapter : INetworkAdapterProvider
{
    private readonly IAppLogger _logger;

    public WlanAdapter(IAppLogger logger)
    {
        _logger = logger;
    }

    public event Action? HardwareChanged;

    public void NotifyHardwareChange()
    {
        HardwareChanged?.Invoke();
    }

    #region P/Invoke Declarations

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanCloseHandle(
        IntPtr hClientHandle,
        IntPtr pReserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanScan(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        IntPtr pWlanBssList,
        IntPtr pReserved);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanGetNetworkBssList(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        uint dot11BssType,
        bool bSecurityEnabled,
        IntPtr pReserved,
        out IntPtr ppWlanBssList);

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public uint isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_LIST
    {
        public uint dwTotalSize;
        public uint dwNumberOfItems;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_MAC_ADDRESS
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] ucDot11MacAddress;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_RATE_SET
    {
        public uint uRateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] usRateSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid;
        public uint uPhyId;
        public DOT11_MAC_ADDRESS dot11Bssid;
        public uint dot11BssType;
        public uint dot11BssPhyType;
        public int lRssi;
        public uint uLinkQuality;
        public bool bInRegDomain;
        public ushort usBeaconPeriod;
        public ulong ullTimestamp;
        public ulong ullHostTimestamp;
        public ushort usCapabilityInformation;
        public uint ulChCenterFrequency;
        public WLAN_RATE_SET wlanRateSet;
        public uint ulIeOffset;
        public uint ulIeSize;
    }

    #endregion

    private static readonly Dictionary<string, string> VendorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "00:00:0C", "Cisco Systems" },
        { "00:03:7F", "Atheros" },
        { "00:0D:97", "Intel" },
        { "00:14:22", "Dell" },
        { "00:15:6D", "Ubiquiti Networks" },
        { "00:16:6C", "Intel" },
        { "00:17:88", "Philips Lighting" },
        { "00:1A:11", "Google" },
        { "00:1C:C2", "Hewlett Packard" },
        { "00:24:B2", "Apple" },
        { "00:26:86", "Intel" },
        { "00:27:0D", "Cisco Systems" },
        { "00:50:F2", "Microsoft" },
        { "04:18:D6", "Ubiquiti Networks" },
        { "18:E8:29", "Netgear" },
        { "24:5A:4C", "TP-Link" },
        { "3C:A8:2A", "HP" },
        { "50:D4:F7", "TP-Link" },
        { "70:8B:CD", "ASUSTek" },
        { "74:83:C2", "Apple" },
        { "8C:5A:25", "Cisco Systems" },
        { "A0:04:60", "ASUSTek" },
        { "A0:B1:C2", "HPE" },
        { "C0:25:E9", "TP-Link" },
        { "C8:D7:19", "Cisco Systems" },
        { "D8:EC:5E", "Cisco Systems" },
        { "E8:9F:80", "TP-Link" }
    };

    public Task<IEnumerable<NetworkInterfaceInfo>> GetInterfacesAsync()
    {
        var resultList = new List<NetworkInterfaceInfo>();

        try
        {
            var netInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in netInterfaces)
            {
                if (IsVirtualOrFilter(ni))
                {
                    continue;
                }

                NetworkDeviceType deviceType = NetworkDeviceType.Other;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    deviceType = NetworkDeviceType.Wireless_WiFi;
                }
                else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    deviceType = NetworkDeviceType.Wired_Ethernet;
                }
                else
                {
                    continue;
                }

                // Match with physical/known interface IDs
                if (Guid.TryParse(ni.Id, out Guid interfaceGuid))
                {
                    var physicalAddress = ni.GetPhysicalAddress();
                    string macAddress = string.Join(":", physicalAddress.GetAddressBytes().Select(b => b.ToString("X2")));
                    if (string.IsNullOrEmpty(macAddress)) macAddress = "N/A";

                    string ipAddress = "N/A";
                    try
                    {
                        var ipProps = ni.GetIPProperties();
                        var unicast = ipProps.UnicastAddresses.FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (unicast != null)
                        {
                            ipAddress = unicast.Address.ToString();
                        }
                    }
                    catch { }

                    var info = new NetworkInterfaceInfo(
                        Id: interfaceGuid,
                        Name: ni.Name,
                        Description: ni.Description,
                        PnpDeviceId: ni.Id,
                        DeviceType: deviceType,
                        IsConnected: ni.OperationalStatus == OperationalStatus.Up,
                        MacAddress: macAddress,
                        IpAddress: ipAddress
                    );
                    resultList.Add(info);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error enumerating interfaces: {ex.Message}", ex);
        }

        return Task.FromResult<IEnumerable<NetworkInterfaceInfo>>(resultList);
    }

    public async Task TriggerHardwareScanAsync(Guid interfaceId)
    {
        await Task.Run(() =>
        {
            uint negotiatedVersion;
            IntPtr clientHandle = IntPtr.Zero;
            try
            {
                uint result = WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle);
                if (result != 0)
                {
                    throw new InvalidOperationException($"WlanOpenHandle failed with error code {result}");
                }

                Guid guid = interfaceId;
                uint scanResult = WlanScan(clientHandle, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (scanResult != 0)
                {
                    throw new InvalidOperationException($"WlanScan failed with error code {scanResult}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"WlanScan failed for interface {interfaceId}: {ex.Message}", ex);
                throw;
            }
            finally
            {
                if (clientHandle != IntPtr.Zero)
                {
                    WlanCloseHandle(clientHandle, IntPtr.Zero);
                }
            }
        });
    }

    public Task<IEnumerable<AccessPoint>> GetAccessPointsAsync(Guid interfaceId)
    {
        var apList = new List<AccessPoint>();

        uint negotiatedVersion;
        IntPtr clientHandle = IntPtr.Zero;
        IntPtr ppBssList = IntPtr.Zero;

        try
        {
            uint result = WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle);
            if (result != 0)
            {
                throw new InvalidOperationException($"WlanOpenHandle failed with error code {result}");
            }

            Guid guid = interfaceId;
            // 3 = dot11_BSS_type_any
            result = WlanGetNetworkBssList(clientHandle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out ppBssList);
            if (result != 0)
            {
                throw new InvalidOperationException($"WlanGetNetworkBssList failed with error code {result}");
            }

            if (ppBssList == IntPtr.Zero)
            {
                return Task.FromResult<IEnumerable<AccessPoint>>(apList);
            }

            var bssList = Marshal.PtrToStructure<WLAN_BSS_LIST>(ppBssList);
            int entrySize = Marshal.SizeOf<WLAN_BSS_ENTRY>();

            for (int i = 0; i < bssList.dwNumberOfItems; i++)
            {
                IntPtr entryPtr = IntPtr.Add(ppBssList, Marshal.SizeOf<WLAN_BSS_LIST>() + i * entrySize);
                var entry = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(entryPtr);

                string bssid = FormatMacAddress(entry.dot11Bssid.ucDot11MacAddress);
                string ssid = entry.dot11Ssid.uSSIDLength == 0
                    ? "<Hidden SSID>"
                    : Encoding.UTF8.GetString(entry.dot11Ssid.ucSSID, 0, (int)entry.dot11Ssid.uSSIDLength);

                var (channel, band) = GetChannelAndBand(entry.ulChCenterFrequency);

                // Copy IE bytes
                byte[] ieBytes = Array.Empty<byte>();
                if (entry.ulIeSize > 0 && entry.ulIeOffset > 0)
                {
                    ieBytes = new byte[entry.ulIeSize];
                    Marshal.Copy(IntPtr.Add(entryPtr, (int)entry.ulIeOffset), ieBytes, 0, (int)entry.ulIeSize);
                }

                int? channelWidth = ParseChannelWidth(ieBytes, band);
                string security = ParseSecurity(ieBytes, entry.usCapabilityInformation);
                string vendor = ResolveVendor(bssid);
                string[] rates = ParseRates(ieBytes).ToArray();

                int rssi = entry.lRssi;
                int noiseFloor = -95;
                int snr = rssi - noiseFloor;

                apList.Add(new AccessPoint(
                    BSSID: bssid,
                    SSID: ssid,
                    Hostname: null,
                    Band: band,
                    Channel: channel,
                    ChannelWidth: channelWidth,
                    RSSI: rssi,
                    NoiseFloor: noiseFloor,
                    SNR: snr,
                    Quality: (int)entry.uLinkQuality,
                    SecurityType: security,
                    NetworkType: "Infrastructure",
                    VendorOUI: vendor,
                    SupportedRates: rates,
                    LastSeen: DateTime.UtcNow
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error scanning access points: {ex.Message}", ex);
            throw;
        }
        finally
        {
            if (ppBssList != IntPtr.Zero)
            {
                WlanFreeMemory(ppBssList);
            }
            if (clientHandle != IntPtr.Zero)
            {
                WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }

        return Task.FromResult<IEnumerable<AccessPoint>>(apList);
    }

    public async Task<WiredInterfaceStats> GetWiredStatsAsync(Guid interfaceId)
    {
        long speed = 0;
        string ipAddress = "N/A";
        string linkStatus = "Down";
        double pingLatency = 0.0;

        try
        {
            var netInterfaces = NetworkInterface.GetAllNetworkInterfaces();
            var ni = netInterfaces.FirstOrDefault(i => i.Id == interfaceId.ToString("B").ToUpper() || i.Id == interfaceId.ToString());
            if (ni != null)
            {
                speed = ni.Speed;
                linkStatus = ni.OperationalStatus == OperationalStatus.Up ? "Up" : "Down";

                var ipProps = ni.GetIPProperties();
                var unicast = ipProps.UnicastAddresses.FirstOrDefault(addr => addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (unicast != null)
                {
                    ipAddress = unicast.Address.ToString();
                }

                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    var gateway = ipProps.GatewayAddresses
                        .Select(g => g.Address)
                        .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

                    using var ping = new Ping();
                    PingReply reply;
                    if (gateway != null)
                    {
                        reply = await ping.SendPingAsync(gateway, 1000);
                    }
                    else
                    {
                        reply = await ping.SendPingAsync("8.8.8.8", 1000);
                    }

                    if (reply.Status == IPStatus.Success)
                    {
                        pingLatency = reply.RoundtripTime;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error querying wired stats: {ex.Message}", ex);
            throw;
        }

        return new WiredInterfaceStats(speed, ipAddress, linkStatus, pingLatency);
    }

    #region Helper Methods
 
    private static bool IsVirtualOrFilter(NetworkInterface ni)
    {
        string[] filterKeywords = {
            "Virtual", "VPN", "Loopback", "WAN Miniport", "Pseudo-Interface", "Bluetooth",
            "Filter", "Npcap", "Scheduler", "WFP", "LightWeight", "Miniport", "Virtualization"
        };

        foreach (var keyword in filterKeywords)
        {
            if (ni.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                ni.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
               ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
    }

    private static string FormatMacAddress(byte[] macBytes)
    {
        return string.Join(":", macBytes.Select(b => b.ToString("X2")));
    }

    private static (int Channel, string Band) GetChannelAndBand(uint frequencyKHz)
    {
        double freqMHz = frequencyKHz / 1000.0;
        if (freqMHz >= 2412 && freqMHz <= 2484)
        {
            int channel = freqMHz == 2484 ? 14 : (int)((freqMHz - 2407) / 5);
            return (channel, "2.4 GHz");
        }
        else if (freqMHz >= 5160 && freqMHz <= 5885)
        {
            int channel = (int)((freqMHz - 5000) / 5);
            return (channel, "5 GHz");
        }
        else if (freqMHz >= 5955 && freqMHz <= 7115)
        {
            int channel = (int)((freqMHz - 5940) / 5);
            return (channel, "6 GHz");
        }
        return (0, "Unknown");
    }

    private static int? ParseChannelWidth(byte[] ieBytes, string band)
    {
        int? width = 20;
        int offset = 0;

        while (offset < ieBytes.Length - 1)
        {
            byte id = ieBytes[offset];
            byte len = ieBytes[offset + 1];
            if (offset + 2 + len > ieBytes.Length) break;

            if (id == 45 && len >= 2)
            {
                byte htCapInfo = ieBytes[offset + 2];
                if ((htCapInfo & 0x02) != 0)
                {
                    width = 40;
                }
            }
            else if (id == 192 && len >= 2)
            {
                byte vhtOperationInfo = ieBytes[offset + 2];
                if (vhtOperationInfo == 1) width = 80;
                else if (vhtOperationInfo == 2) width = 160;
            }

            offset += 2 + len;
        }

        if (band == "6 GHz")
        {
            return width == 20 ? 80 : width;
        }

        return width;
    }

    private static string ParseSecurity(byte[] ieBytes, ushort capabilityInfo)
    {
        bool isPrivacy = (capabilityInfo & 0x0010) != 0;
        if (!isPrivacy) return "Open";

        bool hasRsn = false;
        bool hasWpa = false;
        bool isWpa3 = false;

        int offset = 0;
        while (offset < ieBytes.Length - 1)
        {
            byte id = ieBytes[offset];
            byte len = ieBytes[offset + 1];
            if (offset + 2 + len > ieBytes.Length) break;

            if (id == 48)
            {
                hasRsn = true;
                for (int i = 0; i < len - 3; i++)
                {
                    if (ieBytes[offset + 2 + i] == 0x00 &&
                        ieBytes[offset + 2 + i + 1] == 0x0F &&
                        ieBytes[offset + 2 + i + 2] == 0xAC &&
                        ieBytes[offset + 2 + i + 3] == 0x08)
                    {
                        isWpa3 = true;
                        break;
                    }
                }
            }
            else if (id == 221 && len >= 4)
            {
                if (ieBytes[offset + 2] == 0x00 &&
                    ieBytes[offset + 2 + 1] == 0x50 &&
                    ieBytes[offset + 2 + 2] == 0xF2 &&
                    ieBytes[offset + 2 + 3] == 0x01)
                {
                    hasWpa = true;
                }
            }

            offset += 2 + len;
        }

        if (isWpa3) return "WPA3-PSK";
        if (hasRsn) return "WPA2-PSK";
        if (hasWpa) return "WPA-PSK";
        return "WEP";
    }

    private static List<string> ParseRates(byte[] ieBytes)
    {
        var rates = new List<string>();
        int offset = 0;
        while (offset < ieBytes.Length - 1)
        {
            byte id = ieBytes[offset];
            byte len = ieBytes[offset + 1];
            if (offset + 2 + len > ieBytes.Length) break;

            if (id == 1 || id == 50)
            {
                for (int i = 0; i < len; i++)
                {
                    byte rateByte = ieBytes[offset + 2 + i];
                    double rateMbps = (rateByte & 0x7F) * 0.5;
                    rates.Add(rateMbps.ToString("F1"));
                }
            }
            offset += 2 + len;
        }
        return rates;
    }

    private static string ResolveVendor(string bssid)
    {
        if (bssid.Length >= 8)
        {
            string prefix = bssid.Substring(0, 8).Replace("-", ":");
            if (VendorMap.TryGetValue(prefix, out string? vendor))
            {
                return vendor;
            }
        }
        return "Unknown Vendor";
    }

    #endregion
}
