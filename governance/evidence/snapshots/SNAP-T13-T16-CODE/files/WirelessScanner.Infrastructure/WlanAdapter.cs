using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class WlanAdapter : INetworkAdapterProvider, IDisposable
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

    // Scan-completion notifications (DR-0003). ACM source; codes 7 = scan_complete, 8 = scan_fail.
    private const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
    private const uint WLAN_NOTIFICATION_ACM_SCAN_COMPLETE = 7;
    private const uint WLAN_NOTIFICATION_ACM_SCAN_FAIL = 8;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WlanNotificationCallback(IntPtr pNotificationData, IntPtr pContext);

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanRegisterNotification(
        IntPtr hClientHandle,
        uint dwNotifSource,
        bool bIgnoreDuplicate,
        WlanNotificationCallback? funcCallback,
        IntPtr pCallbackContext,
        IntPtr pReserved,
        out uint pdwPrevNotifSource);

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_NOTIFICATION_DATA
    {
        public uint NotificationSource;
        public uint NotificationCode;
        public Guid InterfaceGuid;
        public uint dwDataSize;
        public IntPtr pData;
    }

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

    #region Fresh scan cycles (DR-0003, findings F01/F13)

    private readonly object _handleLock = new();
    private IntPtr _notifyHandle = IntPtr.Zero;
    private WlanNotificationCallback? _callback; // rooted for the lifetime of the registration
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<(bool Ok, uint Reason)>> _pendingScans = new();
    private bool _disposed;

    private IntPtr EnsureNotificationHandle()
    {
        lock (_handleLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WlanAdapter));
            if (_notifyHandle != IntPtr.Zero) return _notifyHandle;

            uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle);
            if (result != 0) throw new WlanApiException("WlanOpenHandle", result);

            _callback = OnNotification;
            result = WlanRegisterNotification(handle, WLAN_NOTIFICATION_SOURCE_ACM, true, _callback, IntPtr.Zero, IntPtr.Zero, out _);
            if (result != 0)
            {
                WlanCloseHandle(handle, IntPtr.Zero);
                _callback = null;
                throw new WlanApiException("WlanRegisterNotification", result);
            }
            _notifyHandle = handle;
            return handle;
        }
    }

    private void OnNotification(IntPtr pData, IntPtr pContext)
    {
        try
        {
            var data = Marshal.PtrToStructure<WLAN_NOTIFICATION_DATA>(pData);
            if (data.NotificationSource != WLAN_NOTIFICATION_SOURCE_ACM) return;
            if (data.NotificationCode != WLAN_NOTIFICATION_ACM_SCAN_COMPLETE && data.NotificationCode != WLAN_NOTIFICATION_ACM_SCAN_FAIL) return;
            if (!_pendingScans.TryGetValue(data.InterfaceGuid, out var tcs)) return;

            uint reason = 0;
            if (data.NotificationCode == WLAN_NOTIFICATION_ACM_SCAN_FAIL && data.pData != IntPtr.Zero && data.dwDataSize >= 4)
            {
                reason = (uint)Marshal.ReadInt32(data.pData);
            }
            tcs.TrySetResult((data.NotificationCode == WLAN_NOTIFICATION_ACM_SCAN_COMPLETE, reason));
        }
        catch (Exception ex)
        {
            _logger.Error($"WLAN notification callback error: {ex.Message}", ex);
        }
    }

    private readonly ConcurrentDictionary<Guid, Task<ScanCycleResult>> _inflightScans = new();

    /// <summary>
    /// Concurrent callers for the same interface join the scan already in flight (finding F18): previously a second
    /// call overwrote the first caller's pending-scan slot, so the first timed out as STALE. The shared scan runs
    /// with its own timeout; an individual caller's cancellation only stops that caller waiting.
    /// </summary>
    public Task<ScanCycleResult> ScanAsync(Guid interfaceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_inflightScans.TryGetValue(interfaceId, out var existing)) return existing.WaitAsync(cancellationToken);
            var tcs = new TaskCompletionSource<ScanCycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_inflightScans.TryAdd(interfaceId, tcs.Task)) continue;
            _ = RunSharedScanAsync(interfaceId, timeout, tcs);
            return tcs.Task.WaitAsync(cancellationToken);
        }
    }

    private async Task RunSharedScanAsync(Guid interfaceId, TimeSpan timeout, TaskCompletionSource<ScanCycleResult> tcs)
    {
        try
        {
            tcs.TrySetResult(await ScanCoreAsync(interfaceId, timeout, CancellationToken.None));
        }
        catch (Exception ex)
        {
            tcs.TrySetResult(ScanCycleResult.Failure(DateTime.UtcNow, "provider_exception", ex.Message));
        }
        finally
        {
            _inflightScans.TryRemove(new KeyValuePair<Guid, Task<ScanCycleResult>>(interfaceId, tcs.Task));
        }
    }

    private async Task<ScanCycleResult> ScanCoreAsync(Guid interfaceId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var requested = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr handle;
        try
        {
            handle = await Task.Run(EnsureNotificationHandle, cancellationToken);
        }
        catch (WlanApiException ex)
        {
            _logger.Error($"Scan cycle setup failed: {ex.Message}", ex);
            return ScanCycleResult.Failure(requested, ex.Code.ToString(), ex.Message);
        }

        var tcs = new TaskCompletionSource<(bool Ok, uint Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingScans[interfaceId] = tcs;
        try
        {
            uint scanResult = await Task.Run(() =>
            {
                Guid g = interfaceId;
                return WlanScan(handle, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }, cancellationToken);

            if (scanResult != 0)
            {
                _logger.Warn($"WlanScan failed for {interfaceId}: {WlanErrorText.Describe(scanResult)}");
                return ScanCycleResult.Failure(requested, scanResult.ToString(), WlanErrorText.Describe(scanResult));
            }

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            ScanCycleStatus status;
            string? code = null, message = null;
            if (completed == tcs.Task && tcs.Task.Result.Ok)
            {
                status = ScanCycleStatus.Fresh;
            }
            else if (completed == tcs.Task)
            {
                status = ScanCycleStatus.Stale;
                code = "scan_fail";
                message = $"Driver reported scan failure (WLAN reason code {tcs.Task.Result.Reason}); list may be cached.";
            }
            else
            {
                status = ScanCycleStatus.Stale;
                code = "scan_timeout";
                message = $"No scan-complete notification within {timeout.TotalSeconds:F0} s (scan throttled or busy); list may be cached.";
            }

            try
            {
                var list = await Task.Run(() => ReadBssList(handle, interfaceId), cancellationToken);
                return new ScanCycleResult(status, requested, DateTime.UtcNow, sw.Elapsed.TotalMilliseconds, list, code, message);
            }
            catch (WlanApiException ex)
            {
                return ScanCycleResult.Failure(requested, ex.Code.ToString(), ex.Message);
            }
        }
        finally
        {
            _pendingScans.TryRemove(new KeyValuePair<Guid, TaskCompletionSource<(bool Ok, uint Reason)>>(interfaceId, tcs));
        }
    }

    public void Dispose()
    {
        lock (_handleLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_notifyHandle != IntPtr.Zero)
            {
                WlanRegisterNotification(_notifyHandle, 0, true, null, IntPtr.Zero, IntPtr.Zero, out _);
                WlanCloseHandle(_notifyHandle, IntPtr.Zero);
                _notifyHandle = IntPtr.Zero;
            }
            _callback = null;
        }
        GC.SuppressFinalize(this);
    }

    #endregion

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
        IntPtr clientHandle = IntPtr.Zero;
        try
        {
            uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out clientHandle);
            if (result != 0) throw new WlanApiException("WlanOpenHandle", result);
            return Task.FromResult<IEnumerable<AccessPoint>>(ReadBssList(clientHandle, interfaceId));
        }
        catch (Exception ex)
        {
            _logger.Error($"Error scanning access points: {ex.Message}", ex);
            throw;
        }
        finally
        {
            if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    /// <summary>Reads the BSS list for an interface using an open client handle. Throws WlanApiException on API errors.</summary>
    private List<AccessPoint> ReadBssList(IntPtr clientHandle, Guid interfaceId)
    {
        var apList = new List<AccessPoint>();
        IntPtr ppBssList = IntPtr.Zero;
        try
        {
            Guid guid = interfaceId;
            // 3 = dot11_BSS_type_any
            uint result = WlanGetNetworkBssList(clientHandle, ref guid, IntPtr.Zero, 3, false, IntPtr.Zero, out ppBssList);
            if (result != 0) throw new WlanApiException("WlanGetNetworkBssList", result);
            if (ppBssList == IntPtr.Zero) return apList;

            var bssList = Marshal.PtrToStructure<WLAN_BSS_LIST>(ppBssList);
            int entrySize = Marshal.SizeOf<WLAN_BSS_ENTRY>();
            var nowUtc = DateTime.UtcNow;

            for (int i = 0; i < bssList.dwNumberOfItems; i++)
            {
                IntPtr entryPtr = IntPtr.Add(ppBssList, Marshal.SizeOf<WLAN_BSS_LIST>() + i * entrySize);
                var entry = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(entryPtr);

                string bssid = FormatMacAddress(entry.dot11Bssid.ucDot11MacAddress);
                string ssid = entry.dot11Ssid.uSSIDLength == 0
                    ? "<Hidden SSID>"
                    : Encoding.UTF8.GetString(entry.dot11Ssid.ucSSID, 0, (int)Math.Min(entry.dot11Ssid.uSSIDLength, 32));

                var (channel, band) = GetChannelAndBand(entry.ulChCenterFrequency);

                byte[] ieBytes = Array.Empty<byte>();
                if (entry.ulIeSize > 0 && entry.ulIeOffset > 0)
                {
                    ieBytes = new byte[entry.ulIeSize];
                    Marshal.Copy(IntPtr.Add(entryPtr, (int)entry.ulIeOffset), ieBytes, 0, (int)entry.ulIeSize);
                }

                int rssi = entry.lRssi;

                apList.Add(new AccessPoint(
                    BSSID: bssid,
                    SSID: ssid,
                    Hostname: null,
                    Band: band,
                    Channel: channel,
                    ChannelWidth: ParseChannelWidth(ieBytes, band),
                    RSSI: rssi,
                    // The WLAN API exposes no noise floor: it is UNAVAILABLE, and SNR is an ESTIMATE against
                    // the assumed floor below (DR-0004, finding F09). Never present either as a measurement.
                    NoiseFloor: null,
                    SNR: rssi - AssumedNoiseFloorDbm,
                    Quality: (int)entry.uLinkQuality,
                    SecurityType: ParseSecurity(ieBytes, entry.usCapabilityInformation),
                    NetworkType: "Infrastructure",
                    VendorOUI: ResolveVendor(bssid),
                    SupportedRates: ParseRates(ieBytes).ToArray(),
                    LastSeen: nowUtc,
                    BeaconAgeMs: BeaconAgeMs(entry.ullHostTimestamp, nowUtc)
                ));
            }
        }
        finally
        {
            if (ppBssList != IntPtr.Zero) WlanFreeMemory(ppBssList);
        }
        return apList;
    }

    /// <summary>Assumed noise floor used only to ESTIMATE SNR; never reported as a measurement.</summary>
    public const int AssumedNoiseFloorDbm = -95;

    /// <summary>
    /// ullHostTimestamp is documented as 100-ns intervals since 1601-01-01 (FILETIME, UTC).
    /// Returns null when the value is implausible so callers treat freshness as unknown.
    /// </summary>
    internal static double? BeaconAgeMs(ulong hostTimestamp, DateTime nowUtc)
    {
        if (hostTimestamp == 0 || hostTimestamp > (ulong)DateTime.MaxValue.ToFileTimeUtc()) return null;
        try
        {
            var seen = DateTime.FromFileTimeUtc((long)hostTimestamp);
            var age = (nowUtc - seen).TotalMilliseconds;
            return age is < -60_000 or > 86_400_000 ? null : Math.Max(0, age);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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

    internal static (int Channel, string Band) GetChannelAndBand(uint frequencyKHz)
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
            // 6 GHz: channel = (f - 5950) / 5, so 5955 MHz = channel 1 (finding F15; was 5940)
            int channel = (int)((freqMHz - 5950) / 5);
            return (channel, "6 GHz");
        }
        return (0, "Unknown");
    }

    /// <summary>
    /// Operating channel width in MHz from the beacon's OPERATION elements (finding F16):
    /// HT Operation (61), VHT Operation (192), HE Operation 6 GHz info (255/ext 36).
    /// Returns null when no operation element is present (unknown), never a guessed value.
    /// </summary>
    internal static int? ParseChannelWidth(byte[] ieBytes, string band)
    {
        int? width = null;
        int? vhtWidth = null;
        int? he6Width = null;
        int offset = 0;

        while (offset + 2 <= ieBytes.Length)
        {
            byte id = ieBytes[offset];
            byte len = ieBytes[offset + 1];
            int body = offset + 2;
            if (body + len > ieBytes.Length) break;

            if (id == 61 && len >= 2)
            {
                // HT Operation: byte0 primary channel; byte1 bits0-1 secondary channel offset, bit2 STA channel width
                byte info = ieBytes[body + 1];
                bool fortyMhz = (info & 0x03) != 0 && (info & 0x04) != 0;
                width = Math.Max(width ?? 0, fortyMhz ? 40 : 20);
            }
            else if (id == 192 && len >= 3)
            {
                // VHT Operation: byte0 channel width (0 = 20/40, 1 = 80/160/80+80, 2 = 160 deprecated, 3 = 80+80 deprecated)
                byte chWidth = ieBytes[body];
                byte ccfs0 = ieBytes[body + 1];
                byte ccfs1 = ieBytes[body + 2];
                vhtWidth = chWidth switch
                {
                    1 when ccfs1 != 0 && Math.Abs(ccfs1 - ccfs0) == 8 => 160,
                    1 when ccfs1 != 0 && Math.Abs(ccfs1 - ccfs0) > 16 => 160, // 80+80 reported as 160 MHz total
                    1 => 80,
                    2 or 3 => 160,
                    _ => null
                };
            }
            else if (id == 255 && len >= 7 && ieBytes[body] == 36)
            {
                // HE Operation (extension 36): params (3 bytes LE) after the ext id
                int p = body + 1;
                int parameters = ieBytes[p] | (ieBytes[p + 1] << 8) | (ieBytes[p + 2] << 16);
                int cursor = p + 3 + 1 + 2; // params + BSS color + basic HE-MCS/NSS
                if ((parameters & (1 << 14)) != 0) cursor += 3; // VHT Operation Information present
                if ((parameters & (1 << 15)) != 0) cursor += 1; // Co-Hosted BSS present
                if ((parameters & (1 << 17)) != 0 && cursor + 5 <= body + len)
                {
                    int control = ieBytes[cursor + 1] & 0x03;
                    he6Width = control switch { 0 => 20, 1 => 40, 2 => 80, _ => 160 };
                }
            }

            offset = body + len;
        }

        if (band == "6 GHz") return he6Width;
        return vhtWidth is int v && v > (width ?? 0) ? v : width;
    }

    private static readonly Dictionary<byte, string> AkmNames = new()
    {
        [1] = "802.1X", [2] = "PSK", [3] = "FT-802.1X", [4] = "FT-PSK", [5] = "802.1X-SHA256", [6] = "PSK-SHA256",
        [8] = "SAE", [9] = "FT-SAE", [11] = "802.1X-SuiteB", [12] = "802.1X-SuiteB-192", [13] = "FT-802.1X-SHA384",
        [18] = "OWE", [24] = "SAE-EXT-KEY", [25] = "FT-SAE-EXT-KEY"
    };

    /// <summary>
    /// Security label from the capability Privacy bit plus RSN (48) and WPA vendor (221) elements, using the
    /// advertised AKM suites (finding F17): Enterprise (802.1X) is no longer mislabelled as PSK.
    /// </summary>
    internal static string ParseSecurity(byte[] ieBytes, ushort capabilityInfo)
    {
        bool isPrivacy = (capabilityInfo & 0x0010) != 0;
        var akms = new HashSet<byte>();
        bool hasRsn = false;
        bool hasWpa = false;

        int offset = 0;
        while (offset + 2 <= ieBytes.Length)
        {
            byte id = ieBytes[offset];
            byte len = ieBytes[offset + 1];
            int body = offset + 2;
            if (body + len > ieBytes.Length) break;

            if (id == 48 && len >= 2)
            {
                hasRsn = true;
                // version(2) group(4) pairwise count(2) pairwise list(4n) AKM count(2) AKM list(4m)
                int c = body + 2 + 4;
                if (c + 2 <= body + len)
                {
                    int pairwise = ieBytes[c] | (ieBytes[c + 1] << 8);
                    c += 2 + 4 * pairwise;
                    if (c + 2 <= body + len)
                    {
                        int akmCount = ieBytes[c] | (ieBytes[c + 1] << 8);
                        c += 2;
                        for (int i = 0; i < akmCount && c + 4 <= body + len; i++, c += 4)
                        {
                            if (ieBytes[c] == 0x00 && ieBytes[c + 1] == 0x0F && ieBytes[c + 2] == 0xAC)
                            {
                                akms.Add(ieBytes[c + 3]);
                            }
                        }
                    }
                }
            }
            else if (id == 221 && len >= 4 && ieBytes[body] == 0x00 && ieBytes[body + 1] == 0x50 && ieBytes[body + 2] == 0xF2 && ieBytes[body + 3] == 0x01)
            {
                hasWpa = true;
            }

            offset = body + len;
        }

        bool sae = akms.Contains(8) || akms.Contains(9) || akms.Contains(24) || akms.Contains(25);
        bool psk = akms.Contains(2) || akms.Contains(4) || akms.Contains(6);
        bool dot1x = akms.Contains(1) || akms.Contains(3) || akms.Contains(5) || akms.Contains(13);

        if (akms.Contains(18)) return "OWE (Enhanced Open)";
        if (!isPrivacy && !hasRsn && !hasWpa) return "Open";
        if (akms.Contains(12) || akms.Contains(11)) return "WPA3-Enterprise-192";
        if (sae && psk) return "WPA2/WPA3-Personal";
        if (sae) return "WPA3-Personal";
        if (dot1x) return hasRsn ? "WPA2-Enterprise" : "WPA-Enterprise";
        if (psk) return "WPA2-Personal";
        if (hasRsn) return "WPA2 (unknown AKM)";
        if (hasWpa) return "WPA-Personal";
        return isPrivacy ? "WEP" : "Open";
    }

    internal static List<string> ParseRates(byte[] ieBytes)
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

/// <summary>A WLAN API call returned a non-zero Win32 error code.</summary>
public sealed class WlanApiException : Exception
{
    public uint Code { get; }
    public WlanApiException(string api, uint code) : base($"{api}: {WlanErrorText.Describe(code)}") => Code = code;
}
