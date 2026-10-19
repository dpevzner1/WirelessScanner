using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// Read-only probe for the "select by hardware / mesh" proposal (2026-10-18): which information elements do the APs in
/// range actually broadcast? One ordinary scan, then an inventory of IEs per BSSID: Multiple BSSID (71), Reduced
/// Neighbor Report (201), Mesh ID (114), BSS Load (11), Wi-Fi Alliance Multi-AP/EasyMesh (vendor 50:6F:9A type 0x1B),
/// and vendor OUIs. SSIDs are printed as short hashes only. Run: dotnet test --filter Category=HardwareIeInventory
/// </summary>
[TestFixture, Explicit("Requires a real Wi-Fi adapter"), Category("HardwareIeInventory")]
public class HardwareIeInventoryProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct DOT11_SSID { public uint uSSIDLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] ucSSID; }
    [StructLayout(LayoutKind.Sequential)] private struct DOT11_MAC { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] b; }
    [StructLayout(LayoutKind.Sequential)] private struct WLAN_RATE_SET { public uint uRateSetLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] usRateSet; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid; public uint uPhyId; public DOT11_MAC dot11Bssid; public uint dot11BssType; public uint dot11BssPhyType;
        public int lRssi; public uint uLinkQuality; public bool bInRegDomain; public ushort usBeaconPeriod; public ulong ullTimestamp; public ulong ullHostTimestamp;
        public ushort usCapabilityInformation; public uint ulChCenterFrequency; public WLAN_RATE_SET wlanRateSet; public uint ulIeOffset; public uint ulIeSize;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WLAN_BSS_LIST { public uint dwTotalSize; public uint dwNumberOfItems; }

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint v, IntPtr r, out uint nv, out IntPtr h);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr h, IntPtr r);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr h, ref Guid g, IntPtr ssid, uint type, bool sec, IntPtr r, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    private static string H(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..6];

    [Test]
    public async Task InventoryInformationElements()
    {
        using var adapter = new WlanAdapter(new NullLogger());
        var wifi = (await adapter.GetInterfacesAsync()).First(i => i.DeviceType == NetworkDeviceType.Wireless_WiFi);
        await adapter.ScanAsync(wifi.Id, TimeSpan.FromSeconds(6));   // one ordinary fresh scan

        Assert.That(WlanOpenHandle(2, IntPtr.Zero, out _, out var h), Is.EqualTo(0));
        var rows = new List<string>();
        var counts = new Dictionary<string, int>();
        try
        {
            Guid g = wifi.Id;
            Assert.That(WlanGetNetworkBssList(h, ref g, IntPtr.Zero, 3, false, IntPtr.Zero, out var list), Is.EqualTo(0));
            var header = Marshal.PtrToStructure<WLAN_BSS_LIST>(list);
            int size = Marshal.SizeOf<WLAN_BSS_ENTRY>();
            for (int i = 0; i < header.dwNumberOfItems; i++)
            {
                var p = IntPtr.Add(list, Marshal.SizeOf<WLAN_BSS_LIST>() + i * size);
                var e = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(p);
                var ie = new byte[e.ulIeSize];
                if (e.ulIeSize > 0) Marshal.Copy(IntPtr.Add(p, (int)e.ulIeOffset), ie, 0, ie.Length);
                string mac = string.Join(":", e.dot11Bssid.b.Select(b => b.ToString("X2")));
                string ssid = e.dot11Ssid.uSSIDLength == 0 ? "" : Encoding.UTF8.GetString(e.dot11Ssid.ucSSID, 0, (int)e.dot11Ssid.uSSIDLength);
                var found = new List<string>();
                var vendors = new SortedSet<string>();
                for (int o = 0; o + 2 <= ie.Length;)
                {
                    int id = ie[o], len = ie[o + 1], body = o + 2;
                    if (body + len > ie.Length) break;
                    switch (id)
                    {
                        case 11: found.Add("BSSLoad"); break;
                        case 71: found.Add($"MultiBSSID(maxInd={(len > 0 ? ie[body] : 0)})"); break;
                        case 201: found.Add($"RNR({len}B)"); break;
                        case 114: found.Add("MeshID(802.11s)"); break;
                        case 113: found.Add("MeshConfig"); break;
                        case 133: found.Add("CiscoAPName?(133)"); break;
                        case 221 when len >= 4:
                            string oui = $"{ie[body]:X2}{ie[body + 1]:X2}{ie[body + 2]:X2}";
                            vendors.Add($"{oui}/{ie[body + 3]:X2}");
                            if (oui == "506F9A" && ie[body + 3] == 0x1B) found.Add("MultiAP/EasyMesh");
                            if (oui == "0050F2" && ie[body + 3] == 0x04)
                            {
                                // WPS TLVs (big-endian type/length): 0x1021 Manufacturer, 0x1023 Model Name, 0x1024 Model Number, 0x1011 Device Name
                                var wps = new List<string>();
                                for (int t = body + 4; t + 4 <= body + len;)
                                {
                                    int type = (ie[t] << 8) | ie[t + 1], l = (ie[t + 2] << 8) | ie[t + 3];
                                    if (t + 4 + l > body + len) break;
                                    string v = Encoding.UTF8.GetString(ie, t + 4, l).Trim('\0', ' ');
                                    if (type is 0x1021 or 0x1023 or 0x1024 or 0x1011 && v.Length > 0)
                                        wps.Add($"{(type == 0x1021 ? "mfr" : type == 0x1023 ? "model" : type == 0x1024 ? "modelNo" : "name")}={v}");
                                    t += 4 + l;
                                }
                                if (wps.Count > 0) found.Add("WPS{" + string.Join("|", wps) + "}");
                            }
                            break;
                    }
                    o = body + len;
                }
                foreach (var f in found.Select(x => x.Split('(')[0])) counts[f] = counts.GetValueOrDefault(f) + 1;
                double mhz = e.ulChCenterFrequency / 1000.0;
                // TSF (AP timer, microseconds since the radio started) minus host receive time: equal for BSSIDs sharing one radio clock
                double hostUs = e.ullHostTimestamp / 10.0;   // FILETIME 100 ns -> us
                found.Add($"tsfOffset={(e.ullTimestamp - hostUs) / 1e6:0.000}s tsfUptime={e.ullTimestamp / 3.6e9:0.0}h");
                rows.Add($"{mac}  ssid#{(ssid.Length == 0 ? "hidden" : H(ssid))}  {mhz,6:0} MHz  {e.lRssi,4} dBm  phy{e.dot11BssPhyType}  " +
                         $"[{string.Join(",", found)}]  vendorIEs:{string.Join(" ", vendors)}");
            }
            WlanFreeMemory(list);
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }

        foreach (var r in rows.OrderBy(r => r)) TestContext.Out.WriteLine("IE " + r);
        TestContext.Out.WriteLine("IE_SUMMARY " + string.Join("; ", counts.Select(kv => $"{kv.Key}={kv.Value}")) + $"; bssids={rows.Count}");
        Assert.That(rows, Is.Not.Empty);
    }
}
