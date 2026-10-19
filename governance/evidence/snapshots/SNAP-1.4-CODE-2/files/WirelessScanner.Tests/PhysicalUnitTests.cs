using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;

namespace WirelessScanner.Tests;

/// <summary>
/// Physical-unit MVP (DR-0013): which SSIDs/BSSIDs come from the same access point. Fixtures are derived from the owner's
/// live inventory probe of 2026-10-18 (addresses replaced by synthetic ones with the same structure - LA bit, octet-4 radio, last-nibble SSID - SSIDs replaced, TSF offsets rounded; the raw probe output stays local-only).
/// </summary>
[TestFixture]
public class PhysicalUnitTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static AccessPoint Ap(string bssid, string ssid, string band, int channel, int rssi, double? tsf, string? uuid = null, string? model = null) =>
        new(bssid, ssid, null, band, channel, 20, rssi, null, 0, 0, "WPA2", "Infrastructure", "Unknown Vendor", null, T0)
        {
            TsfOffsetMs = tsf, WpsUuid = uuid, WpsModelNumber = model, WpsManufacturer = model != null ? "ARRIS" : null
        };

    /// <summary>The owner's environment, one scan. TSF offsets: one value per radio (ms), radios differ by seconds.</summary>
    private static List<AccessPoint> OwnerScan(double drift = 0) => new()
    {
        // the owner's router: 2 radios (octet 4 = radio), 4 SSIDs incl. hidden
        Ap("9A:3C:5E:1F:42:70", "Home", "2.4 GHz", 6, -25, 1_000_000.2 + drift),
        Ap("9A:3C:5E:1F:42:73", "Home-IoT", "2.4 GHz", 6, -27, 1_000_000.6 + drift),
        Ap("9A:3C:5E:26:42:70", "Home", "5 GHz", 149, -40, 1_000_900.1 + drift),
        Ap("9A:3C:5E:26:42:74", "Home-Guest", "5 GHz", 149, -42, 1_000_900.4 + drift),
        Ap("9A:3C:5E:26:42:76", PhysicalUnitGrouper.HiddenSsid, "5 GHz", 149, -51, 1_000_900.9 + drift),
        // neighbour: 3 BSSIDs on one radio, globally unique OUI
        Ap("3C:5D:7E:1B:61:43", "Mesh-Net", "5 GHz", 44, -70, 2_500_000.0 + drift),
        Ap("3C:5D:7E:1B:61:46", "Mesh-Net-Guest", "5 GHz", 44, -71, 2_500_000.7 + drift),
        Ap("3C:5D:7E:1B:61:49", "Mesh-Net-IoT", "5 GHz", 44, -72, 2_500_001.1 + drift),
        // another box broadcasting the same SSID (mesh node candidate)
        Ap("3C:5D:7E:19:61:43", "Mesh-Net", "5 GHz", 157, -80, 3_700_000.0 + drift),
        // Arris BGW210 families: locally-administered bit for virtual BSSIDs, radio clocks ~45 s apart
        Ap("80:AA:12:4C:7D:F0", "ATT-A", "2.4 GHz", 1, -75, 4_000_000.0 + drift, "aaaa-1", "BGW210"),
        Ap("80:AA:12:4C:7D:F3", "ATT-A", "5 GHz", 36, -78, 4_045_000.0 + drift, "aaaa-1", "BGW210"),
        Ap("82:AA:12:4C:7D:F3", PhysicalUnitGrouper.HiddenSsid, "5 GHz", 36, -79, 4_045_000.4 + drift),
        Ap("C4:55:66:31:5B:F0", "ATT-B", "2.4 GHz", 11, -82, 5_000_000.0 + drift, "bbbb-2", "BGW210"),
        Ap("C4:55:66:31:5B:F3", "ATT-B", "5 GHz", 100, -84, 5_052_000.0 + drift, "bbbb-2", "BGW210"),
        Ap("C6:55:66:31:5B:F3", PhysicalUnitGrouper.HiddenSsid, "5 GHz", 100, -85, 5_052_000.3 + drift),
        // D-Link DIR-820L: 2.4 + 5 GHz, same family
        Ap("D0:33:44:6E:2C:28", "dlink", "2.4 GHz", 6, -86, 6_000_000.0 + drift),
        Ap("D0:33:44:6E:2C:2A", "dlink-5GHz", "5 GHz", 157, -88, 6_000_010.0 + drift),
    };

    private static PhysicalUnitGrouper Observed(int scans = 2)
    {
        var g = new PhysicalUnitGrouper();
        for (int i = 0; i < scans; i++) g.Observe(OwnerScan(i * 3000.0), T0.AddSeconds(i * 3));
        return g;
    }

    private static PhysicalUnit UnitOf(PhysicalUnitGrouper g, string bssid) => g.Get(g.UnitOf(bssid)!)!;

    // ---------------- capture evidence ----------------

    [Test]
    public void TsfOffset_IsRadioTimerMinusHostTime_AndNullWhenMissing()
    {
        // host FILETIME 10_000 ticks = 1 ms; TSF 5_000 us = 5 ms -> +4 ms
        Assert.That(WlanAdapter.TsfOffsetMs(5_000, 10_000), Is.EqualTo(4.0).Within(1e-9));
        Assert.That(WlanAdapter.TsfOffsetMs(0, 10_000), Is.Null);
        Assert.That(WlanAdapter.TsfOffsetMs(5_000, 0), Is.Null);
    }

    [Test]
    public void Wps_ManufacturerModelAndUuid_ParsedFromVendorElement()
    {
        static byte[] Tlv(int type, byte[] v) => new byte[] { (byte)(type >> 8), (byte)type, (byte)(v.Length >> 8), (byte)v.Length }.Concat(v).ToArray();
        var uuid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var body = new byte[] { 0x00, 0x50, 0xF2, 0x04 }
            .Concat(Tlv(0x1021, "ARRIS"u8.ToArray()))
            .Concat(Tlv(0x1023, "Quantenna Topaz"u8.ToArray()))
            .Concat(Tlv(0x1024, "BGW210"u8.ToArray()))
            .Concat(Tlv(0x1011, "Gateway"u8.ToArray()))
            .Concat(Tlv(0x1047, uuid)).ToArray();
        var ies = new byte[] { 0, 1, (byte)'x', 221, (byte)body.Length }.Concat(body).ToArray();
        var wps = WlanAdapter.ParseWps(ies)!;
        Assert.Multiple(() =>
        {
            Assert.That(wps.Manufacturer, Is.EqualTo("ARRIS"));
            Assert.That(wps.Model, Is.EqualTo("Quantenna Topaz"));
            Assert.That(wps.ModelNumber, Is.EqualTo("BGW210"));
            Assert.That(wps.DeviceName, Is.EqualTo("Gateway"));
            Assert.That(wps.Uuid, Is.EqualTo("01020304-0506-0708-090a-0b0c0d0e0f10"));
            Assert.That(WlanAdapter.ParseWps(new byte[] { 0, 1, (byte)'x' }), Is.Null);
            Assert.That(WlanAdapter.ParseWps(new byte[] { 221, 40, 0x00, 0x50 }), Is.Null, "truncated element");
        });
    }

    // ---------------- rules ----------------

    [TestCase("9A:3C:5E:1F:42:70", "9A:3C:5E:26:42:74", true, TestName = "Family_LocallyAdministered_RadioInOctet4")]
    [TestCase("80:AA:12:4C:7D:F0", "82:AA:12:4C:7D:F3", true, TestName = "Family_LaBitVirtualBssid")]
    [TestCase("D0:33:44:6E:2C:28", "D0:33:44:6E:2C:2A", true, TestName = "Family_SsidIndexInLastNibble")]
    [TestCase("3C:5D:7E:1B:61:43", "3C:5D:7E:19:61:43", false, TestName = "Family_GlobalOui_Octet4DiffersIsAnotherBox")]
    [TestCase("80:AA:12:4C:7D:F0", "C4:55:66:31:5B:F0", false, TestName = "Family_UnrelatedOuis")]
    [TestCase("D0:33:44:6E:2C:28", "D0:33:44:6E:2C:38", false, TestName = "Family_HighNibbleDiffers")]
    [TestCase("bad", "D0:33:44:6E:2C:38", false, TestName = "Family_Malformed")]
    public void MacFamilyRule(string a, string b, bool same) => Assert.That(PhysicalUnitGrouper.SameFamily(a, b), Is.EqualTo(same));

    // ---------------- grouping on the owner's environment ----------------

    [Test]
    public void OwnersRouter_IsOneUnit_TwoRadios_FourSsids_High()
    {
        var u = UnitOf(Observed(), "9A:3C:5E:1F:42:70");
        Assert.Multiple(() =>
        {
            Assert.That(u.Members.Select(m => m.Bssid), Is.EquivalentTo(new[] { "9A:3C:5E:1F:42:70", "9A:3C:5E:1F:42:73", "9A:3C:5E:26:42:70", "9A:3C:5E:26:42:74", "9A:3C:5E:26:42:76" }));
            Assert.That(u.Radios, Has.Count.EqualTo(2));
            Assert.That(u.Members.Select(m => m.Ssid).Distinct().Count(), Is.EqualTo(4));
            Assert.That(u.Confidence, Is.EqualTo(UnitConfidence.High));
            Assert.That(u.Evidence, Has.Some.Contains("Same radio clock"));
            Assert.That(u.Evidence, Has.Some.Contains("Related MAC addresses"));
            Assert.That(u.IsMeshCandidate, Is.False);
        });
    }

    [Test]
    public void ThreeSsidsOnOneRadio_GroupByClock_AndOtherBoxIsMeshCandidate()
    {
        var g = Observed();
        var u = UnitOf(g, "3C:5D:7E:1B:61:43");
        var other = UnitOf(g, "3C:5D:7E:19:61:43");
        Assert.Multiple(() =>
        {
            Assert.That(u.Members.Count(), Is.EqualTo(3));
            Assert.That(u.Radios, Has.Count.EqualTo(1));
            Assert.That(u.Confidence, Is.EqualTo(UnitConfidence.High));
            Assert.That(other.Id, Is.Not.EqualTo(u.Id), "same SSID on another box is NOT merged");
            Assert.That(u.IsMeshCandidate && other.IsMeshCandidate, Is.True);
            Assert.That(u.SameSsidUnitIds, Does.Contain(other.Id));
        });
    }

    [Test]
    public void ArrisFamilies_GroupIncludingHiddenBackhaul_AndStaySeparate()
    {
        var g = Observed();
        var a = UnitOf(g, "80:AA:12:4C:7D:F0");
        var b = UnitOf(g, "C4:55:66:31:5B:F0");
        Assert.Multiple(() =>
        {
            Assert.That(a.Members.Select(m => m.Bssid), Is.EquivalentTo(new[] { "80:AA:12:4C:7D:F0", "80:AA:12:4C:7D:F3", "82:AA:12:4C:7D:F3" }));
            Assert.That(b.Members.Count(), Is.EqualTo(3));
            Assert.That(a.Id, Is.Not.EqualTo(b.Id), "different WPS UUIDs never merge");
            Assert.That(a.Confidence, Is.EqualTo(UnitConfidence.High));
            Assert.That(a.Model, Is.EqualTo("BGW210"));
            Assert.That(a.Name, Does.Contain("BGW210"));
        });
    }

    [Test]
    public void DLink_GroupsByFamilyOnly_Medium()
    {
        var u = UnitOf(Observed(), "D0:33:44:6E:2C:28");
        Assert.That(u.Members.Count(), Is.EqualTo(2));
        Assert.That(u.Confidence, Is.EqualTo(UnitConfidence.Medium));
    }

    [Test]
    public void TotalUnits_AndNoBssidInTwoUnits()
    {
        var g = Observed();
        Assert.That(g.Units, Has.Count.EqualTo(6));
        var all = g.Units.SelectMany(u => u.Members.Select(m => m.Bssid)).ToList();
        Assert.That(all, Is.Unique);
        Assert.That(all, Has.Count.EqualTo(17));
        Assert.That(g.Units[0].Members.Any(m => m.Bssid.StartsWith("9A:3C:5E")), Is.True, "strongest unit first");
    }

    [Test]
    public void ClockCoincidenceThatDoesNotPersist_DoesNotMerge()
    {
        var g = new PhysicalUnitGrouper();
        var a1 = Ap("10:00:00:00:00:01", "X", "5 GHz", 36, -60, 100.0);
        var b1 = Ap("20:00:00:00:00:01", "Y", "5 GHz", 36, -60, 101.0);   // coincidence
        g.Observe(new[] { a1, b1 }, T0);
        Assert.That(g.UnitOf(a1.BSSID), Is.EqualTo(g.UnitOf(b1.BSSID)), "one agreeing scan links them");
        g.Observe(new[] { a1 with { TsfOffsetMs = 200 }, b1 with { TsfOffsetMs = 900 } }, T0.AddSeconds(3));
        g.Observe(new[] { a1 with { TsfOffsetMs = 300 }, b1 with { TsfOffsetMs = 1900 } }, T0.AddSeconds(6));
        Assert.That(g.UnitOf(a1.BSSID), Is.Not.EqualTo(g.UnitOf(b1.BSSID)), "disagreement outweighs one coincidence");
    }

    [Test]
    public void DifferentBands_NeverLinkedByClock()
    {
        var g = new PhysicalUnitGrouper();
        g.Observe(new[] { Ap("10:00:00:00:00:01", "X", "2.4 GHz", 1, -60, 100.0), Ap("20:00:00:00:00:01", "Y", "5 GHz", 36, -60, 100.5) }, T0);
        Assert.That(g.Units, Has.Count.EqualTo(2));
        Assert.That(g.Units.All(u => u.Confidence == UnitConfidence.Single), Is.True);
    }

    [Test]
    public void EvidenceExpires_AfterRetention()
    {
        var g = Observed();
        g.Observe(new[] { Ap("10:00:00:00:00:01", "X", "5 GHz", 36, -60, 1.0) }, T0 + PhysicalUnitGrouper.Retention + TimeSpan.FromMinutes(1));
        Assert.That(g.Units, Has.Count.EqualTo(1));
    }

    // ---------------- user corrections ----------------

    [Test]
    public void SplitMergeRename_ArePersistedAndWin()
    {
        var g = Observed();
        UnitOverrides? saved = null;
        g.OverridesChanged += o => saved = o;
        string router = g.UnitOf("9A:3C:5E:1F:42:70")!;

        string split = g.MoveToNewUnit("9A:3C:5E:26:42:76");
        Assert.That(g.UnitOf("9A:3C:5E:26:42:76"), Is.EqualTo(split));
        Assert.That(g.Get(split)!.Confidence, Is.EqualTo(UnitConfidence.Manual));
        Assert.That(g.Get(router)!.Confidence, Is.EqualTo(UnitConfidence.Manual), "the source unit was edited too");

        g.MoveToUnit("D0:33:44:6E:2C:28", router);
        g.Rename(router, "  Living room router ");
        Assert.That(g.Get(router)!.Name, Is.EqualTo("Living room router"));
        Assert.That(g.Get(router)!.IsCustomName, Is.True);
        Assert.That(saved!.Assignments, Has.Count.EqualTo(2));

        // a new grouper (next app start) restores the corrections from settings
        var restarted = new PhysicalUnitGrouper();
        restarted.LoadOverrides(saved);
        restarted.Observe(OwnerScan(), T0);
        Assert.Multiple(() =>
        {
            Assert.That(restarted.UnitOf("9A:3C:5E:26:42:76"), Is.EqualTo(split));
            Assert.That(restarted.UnitOf("D0:33:44:6E:2C:28"), Is.EqualTo(router));
            Assert.That(restarted.Get(router)!.Name, Is.EqualTo("Living room router"));
        });

        restarted.Reset(router);
        Assert.That(restarted.UnitOf("D0:33:44:6E:2C:28"), Is.Not.EqualTo(router));
        Assert.That(restarted.Get(router)!.IsCustomName, Is.False);
    }
}
