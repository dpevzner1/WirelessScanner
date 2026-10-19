using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WirelessScanner.Application;
using WirelessScanner.Domain;
using WirelessScanner.Infrastructure;
using WirelessScanner.Presentation;
using WirelessScanner.Responder;

namespace WirelessScanner.Tests;

/// <summary>
/// Android parity fixtures (DR-0015, A-LRN-012/041): the C# implementation computes reference answers for deterministic inputs and
/// writes them to AndroidApp/core/src/test/resources/parity. The Kotlin core tests feed the same inputs to the ported code and
/// require identical results. Inputs use synthetic addresses only (privacy rule). Run:
///   dotnet test WirelessScanner.Tests --filter "Category=AndroidParity"
/// </summary>
[TestFixture, Explicit("Writes Android parity fixtures"), Category("AndroidParity")]
public class AndroidParityFixtures
{
    private static readonly DateTime T0 = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Opt = SaturationExportService.JsonOptions;

    private static string OutDir()
    {
        var root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "WirelessScanner.slnx"))) root = Path.GetDirectoryName(root)!;
        var dir = Path.Combine(root!, "AndroidApp", "core", "src", "test", "resources", "parity");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string name, object payload) =>
        File.WriteAllText(Path.Combine(OutDir(), name), JsonSerializer.Serialize(payload, Opt));

    /// <summary>Deterministic pseudo-random sequence (same values on every run, independent of .NET Random).</summary>
    private sealed class Lcg(uint seed)
    {
        private uint _s = seed;
        public double Next() { _s = _s * 1664525u + 1013904223u; return (_s >> 8) / 16777216.0; }
        public int Noise(int amplitude) => (int)Math.Round((Next() * 2 - 1) * amplitude);
    }

    private static AccessPoint Ap(string bssid, string ssid, string band, int ch, int? w, int rssi, double? age, string sec = "WPA2-Personal") =>
        new(bssid, ssid, null, band, ch, w, rssi, null, rssi + 95, Math.Clamp(2 * (rssi + 100), 0, 100), sec, "Infrastructure", "Synthetic Vendor", null, T0, age)
        {
            PhyType = band == "2.4 GHz" ? "802.11n" : "802.11ax",
            BeaconIntervalTu = 100,
            CenterFrequencyMhz = ChannelMath.ChannelFrequencyMhz(band, ch),
            StationCount = ch == 36 ? 4 + (rssi & 1) : null,
            ChannelUtilizationPct = ch == 36 ? 30 + (-rssi % 7) * 4.5 : null
        };

    // ------------------------------------------------------------------ channel math and spectrum

    [Test]
    public void ChannelMathAndSpectrum()
    {
        var cases = new List<object>();
        foreach (var (band, chans) in new[] { ("2.4 GHz", Enumerable.Range(1, 14)), ("5 GHz", new[] { 36, 40, 44, 48, 52, 64, 100, 116, 132, 140, 144, 149, 153, 165 }.AsEnumerable()),
                     ("6 GHz", new[] { 1, 5, 33, 37, 93, 97, 225, 229, 233 }.AsEnumerable()) })
            foreach (var ch in chans)
                foreach (int? w in new int?[] { null, 20, 40, 80, 160 })
                {
                    var (lo, hi) = ChannelMath.Occupancy(band, ch, w);
                    cases.Add(new { band, channel = ch, width = w, frequency = ChannelMath.ChannelFrequencyMhz(band, ch), center = ChannelMath.BlockCenterMhz(band, ch, w), low = lo, high = hi, dfs = ChannelMath.IsDfs(band, ch, w) });
                }
        var aps = new List<AccessPoint>
        {
            Ap("02:00:00:00:01:01", "Alpha", "2.4 GHz", 1, 20, -48, 100), Ap("02:00:00:00:01:02", "Beta", "2.4 GHz", 6, 40, -63, 100),
            Ap("02:00:00:00:01:03", "Gamma", "2.4 GHz", 11, 20, -77, 100), Ap("02:00:00:00:01:04", "Alpha", "5 GHz", 36, 80, -52, 100),
            Ap("02:00:00:00:01:05", "Delta", "5 GHz", 44, 40, -70, 100), Ap("02:00:00:00:01:06", "Eps", "5 GHz", 100, 160, -81, 100),
            Ap("02:00:00:00:01:07", "Zeta", "5 GHz", 149, 80, -60, 100), Ap("02:00:00:00:01:08", "Eta", "6 GHz", 37, 160, -66, 100)
        };
        var spectrum = new List<object>();
        foreach (var axis in new[] { SpectrumLayout.Band24, SpectrumLayout.Band5, SpectrumLayout.Band6 })
        {
            var load = SpectrumLayout.ChannelLoad(axis, aps);
            spectrum.Add(new { band = axis.Band, load = load.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), leastBusy = SpectrumLayout.LeastBusyChannel(axis, load),
                strongest = SpectrumLayout.StrongestPerChannel(axis, aps).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });
        }
        var overlaps = new List<object>();
        for (int i = 0; i < aps.Count; i++) for (int j = i + 1; j < aps.Count; j++) overlaps.Add(new { a = aps[i].BSSID, b = aps[j].BSSID, overlaps = ChannelMath.Overlaps(aps[i], aps[j]) });
        Write("channel_math.json", new { cases, aps = aps.Select(ApDto), spectrum, overlaps, intensity = new[] { -95, -90, -70, -45, -30 }.Select(r => new { rssi = r, value = SpectrumLayout.Intensity(r) }) });
    }

    private static object ApDto(AccessPoint a) => new
    {
        bssid = a.BSSID, ssid = a.SSID, band = a.Band, channel = a.Channel, channelWidth = a.ChannelWidth, rssi = a.RSSI, snr = a.SNR, quality = a.Quality,
        securityType = a.SecurityType, vendorOui = a.VendorOUI, beaconAgeMs = a.BeaconAgeMs, phyType = a.PhyType, beaconIntervalTu = a.BeaconIntervalTu,
        centerFrequencyMhz = a.CenterFrequencyMhz, stationCount = a.StationCount, channelUtilizationPct = a.ChannelUtilizationPct, tsfOffsetMs = a.TsfOffsetMs,
        wpsManufacturer = a.WpsManufacturer, wpsModel = a.WpsModel, wpsModelNumber = a.WpsModelNumber, wpsDeviceName = a.WpsDeviceName, wpsUuid = a.WpsUuid
    };

    // ------------------------------------------------------------------ saturation (builder + analyzer), two scopes

    private static List<ScanCycleResult> SaturationScans()
    {
        var rnd = new Lcg(20261019);
        var scans = new List<ScanCycleResult>();
        for (int i = 0; i < 260; i++)                                  // 21+ minutes at 5 s: the trend rule applies
        {
            var at = T0.AddSeconds(i * 5);
            if (i == 40) { scans.Add(ScanCycleResult.Failure(at, "provider_error", "Synthetic failure")); continue; }
            var status = i is 70 or 71 ? ScanCycleStatus.Stale : ScanCycleStatus.Fresh;
            var list = new List<AccessPoint>();
            int a = -52 + rnd.Noise(2) - (i is >= 100 and < 106 ? 14 : 0) + (i is 150 ? 12 : 0) - i / 40;  // drop at 100..105, peak at 150, slow downward trend
            list.Add(Ap("02:11:22:33:44:01", "PlantOps", "5 GHz", i < 180 ? 36 : 44, 80, a, 150 + rnd.Noise(100) + 100));
            if (i is not (>= 120 and < 125)) list.Add(Ap("02:11:22:33:44:02", "PlantOps", "5 GHz", 36, 80, -61 + rnd.Noise(3), 300));   // dropout 120..124
            list.Add(Ap("02:11:22:33:55:01", "PlantOps", "2.4 GHz", 6, 20, -66 + rnd.Noise(4), i % 9 == 0 ? 12000 : 200));        // sometimes stale entry
            list.Add(Ap("06:11:22:33:44:01", "PlantOps-Guest", "5 GHz", 36, 80, -53 + rnd.Noise(2), 160));                      // same radio, other SSID
            if (i % 3 != 0) list.Add(Ap("02:99:88:77:66:01", "Neighbour", "5 GHz", 40, 20, -74 + rnd.Noise(5), 250));            // overlapping neighbour
            list.Add(Ap("02:99:88:77:66:02", "Neighbour", "2.4 GHz", 6, 20, -79 + rnd.Noise(3), 250));                           // co-channel neighbour
            scans.Add(new ScanCycleResult(status, at, at.AddMilliseconds(1200), 1200 + rnd.Noise(300), list, status == ScanCycleStatus.Stale ? "scan_timeout" : null, null));
        }
        return scans;
    }

    private static SaturationReport RunSaturation(SaturationTestConfig cfg, List<ScanCycleResult> scans, List<SaturationMarker> markers, out List<SaturationCycle> cycles)
    {
        var tracker = new SaturationTargetTracker(cfg);
        cycles = new List<SaturationCycle>();
        long seq = 0;
        foreach (var s in scans)
        {
            seq++;
            var obs = SaturationObservationBuilder.Build(cfg, tracker, s, seq);
            cycles.Add(new SaturationCycle(seq, s.CompletedUtc, (seq - 1) * 5000 + (seq % 4) * 37, s.Status, s.DurationMs, s.AccessPoints.Count, s.ErrorCode, s.ErrorMessage, obs));
        }
        return new SaturationAnalyzer().Analyze(cfg, cycles, markers, T0, T0.AddSeconds(scans.Count * 5), SaturationTestStatus.Completed, scans.Count * 5.0 - 2.5);
    }

    [Test]
    public void Saturation()
    {
        var scans = SaturationScans();
        var markers = new List<SaturationMarker> { new(T0.AddSeconds(300), 300_000, "Door closed"), new(T0.AddSeconds(900), 900_000, "Microwave on") };
        var unit = new SaturationUnitTarget
        {
            Id = "U-021122334401", Name = "Synthetic AP (44:01)", Confidence = UnitConfidence.Medium,
            Evidence = new[] { "Related MAC addresses (same address family across radios/SSIDs)." },
            Members = new[]
            {
                new SaturationUnitMember("02:11:22:33:44:01", "PlantOps", "5 GHz", "5 GHz@02:11:22:33:44:01"),
                new SaturationUnitMember("06:11:22:33:44:01", "PlantOps-Guest", "5 GHz", "5 GHz@02:11:22:33:44:01"),
                new SaturationUnitMember("02:11:22:33:55:01", "PlantOps", "2.4 GHz", "2.4 GHz@02:11:22:33:55:01")
            }
        };
        var configs = new (string Name, SaturationTestConfig Cfg)[]
        {
            ("mixed", new SaturationTestConfig { TestId = Guid.Parse("11111111-2222-3333-4444-555555555555"), Name = "Parity mixed", Scope = SaturationScope.Mixed,
                TargetSsids = new[] { "PlantOps" }, TargetBssids = new[] { "02:99:88:77:66:01" }, Interval = TimeSpan.FromSeconds(5), Duration = TimeSpan.FromMinutes(25), Location = "Lab" }),
            ("ssid", new SaturationTestConfig { TestId = Guid.Parse("11111111-2222-3333-4444-555555555556"), Name = "Parity SSID", Scope = SaturationScope.SingleSsid,
                TargetSsids = new[] { "PlantOps" }, Interval = TimeSpan.FromSeconds(5), Duration = TimeSpan.FromMinutes(25), Location = "Lab",
                Thresholds = new SaturationThresholds { DropDb = 6, PeakDb = 6, DropoutMissedCycles = 3 } }),
            ("unit", new SaturationTestConfig { TestId = Guid.Parse("11111111-2222-3333-4444-555555555557"), Name = "Parity unit", Scope = SaturationScope.SingleUnit,
                TargetBssids = unit.Members.Select(m => m.Bssid).ToArray(), TargetUnits = new[] { unit }, Interval = TimeSpan.FromSeconds(5), Duration = null, Location = "Lab" }),
            ("all", new SaturationTestConfig { TestId = Guid.Parse("11111111-2222-3333-4444-555555555558"), Name = "Parity all", Scope = SaturationScope.AllVisible,
                Interval = TimeSpan.FromSeconds(5), Duration = TimeSpan.FromMinutes(25), Location = "Lab" })
        };
        var outputs = new List<object>();
        foreach (var (name, cfg) in configs)
        {
            var r = RunSaturation(cfg, scans, markers, out var cycles);
            outputs.Add(new
            {
                name,
                config = new { scope = cfg.Scope, targetSsids = cfg.TargetSsids, targetBssids = cfg.TargetBssids, targetUnits = cfg.TargetUnits, intervalMs = cfg.Interval.TotalMilliseconds,
                    durationMs = cfg.Duration?.TotalMilliseconds, thresholds = cfg.Thresholds, testId = cfg.TestId },
                elapsedMs = cycles.Select(c => c.ElapsedMs), measuredActive = scans.Count * 5.0 - 2.5,
                expected = new { r.ActiveSeconds, r.Health, r.Targets, r.Ssids, r.Events, units = r.UnitSummaries, observations = r.Cycles.Select(c => c.Observations) }
            });
        }
        Write("saturation.json", new
        {
            t0 = T0, endSeconds = scans.Count * 5,
            scans = scans.Select(s => new { status = s.Status, requestedUtc = s.RequestedUtc, completedUtc = s.CompletedUtc, durationMs = s.DurationMs, errorCode = s.ErrorCode, errorMessage = s.ErrorMessage, aps = s.AccessPoints.Select(ApDto) }),
            markers, runs = outputs
        });
    }

    // ------------------------------------------------------------------ physical units

    [Test]
    public void PhysicalUnits()
    {
        AccessPoint U(string bssid, string ssid, string band, int ch, int rssi, double? tsf, string? uuid = null, string? model = null, string vendor = "Unknown Vendor") =>
            new(bssid, ssid, null, band, ch, 20, rssi, null, 0, 0, "WPA2", "Infrastructure", vendor, null, T0)
            { TsfOffsetMs = tsf, WpsUuid = uuid, WpsModelNumber = model, WpsManufacturer = model != null ? "Synthetic Networks" : null };
        List<AccessPoint> Scan(double drift) => new()
        {
            U("9A:00:00:1F:42:70", "Home", "2.4 GHz", 6, -25, 1_000_000.2 + drift), U("9A:00:00:1F:42:73", "Home-IoT", "2.4 GHz", 6, -27, 1_000_000.6 + drift),
            U("9A:00:00:26:42:70", "Home", "5 GHz", 149, -40, 1_000_900.1 + drift), U("9A:00:00:26:42:74", "Home-Guest", "5 GHz", 149, -42, 1_000_900.4 + drift),
            U("9A:00:00:26:42:76", PhysicalUnitGrouper.HiddenSsid, "5 GHz", 149, -51, 1_000_900.9 + drift),
            U("3C:00:00:1B:61:43", "Mesh-Net", "5 GHz", 44, -70, null, "uuid-mesh-a", "MX-1"), U("3C:00:00:1B:61:46", "Mesh-Net-Guest", "5 GHz", 44, -71, null, "uuid-mesh-a", "MX-1"),
            U("3C:00:00:19:61:43", "Mesh-Net", "5 GHz", 157, -80, null, "uuid-mesh-b", "MX-1"),
            U("80:00:00:4C:7D:F0", "Office", "2.4 GHz", 1, -60, null, vendor: "Synthetic Vendor"), U("80:00:00:4C:7D:F3", "Office-IoT", "2.4 GHz", 1, -61, null),
            U("82:00:00:4C:7D:F3", PhysicalUnitGrouper.HiddenSsid, "5 GHz", 52, -66, null),
            U("C4:00:00:31:5B:F0", "Lone", "5 GHz", 36, -75, null)
        };
        var steps = new List<object>();
        var g = new PhysicalUnitGrouper();
        void Snap(string what) => steps.Add(new { what, units = g.Units.Select(u => new { u.Id, u.Name, u.IsCustomName, u.Confidence, u.Evidence, u.Radios, u.Vendor, u.Model, u.SameSsidUnitIds, u.IsMeshCandidate }).ToList() });
        var scans = new List<List<AccessPoint>> { Scan(0), Scan(3.0), Scan(6.1) };
        for (int i = 0; i < scans.Count; i++) { g.Observe(scans[i], T0.AddSeconds(5 * i)); Snap($"scan{i}"); }
        g.MoveToNewUnit("80:00:00:4C:7D:F3"); Snap("split");
        g.Rename("U-9A00001F4270", "Owner router"); Snap("rename");
        g.MoveToUnit("C4:00:00:31:5B:F0", "U-3C0000194361"); Snap("merge");
        g.Reset("U-3C0000194361"); Snap("reset");
        var families = new[] { ("9A:00:00:1F:42:70", "9A:00:00:26:42:74"), ("80:00:00:4C:7D:F0", "82:00:00:4C:7D:F3"), ("3C:00:00:1B:61:43", "3C:00:00:19:61:43"), ("80:00:00:4C:7D:F0", "80:00:00:4D:7D:F0"), ("bad", "9A:00:00:1F:42:70") }
            .Select(p => new { a = p.Item1, b = p.Item2, same = PhysicalUnitGrouper.SameFamily(p.Item1, p.Item2) });
        Write("units.json", new { t0 = T0, scans = scans.Select(s => s.Select(ApDto)), operations = new[] { "split:80:00:00:4C:7D:F3", "rename:U-9A00001F4270:Owner router", "merge:C4:00:00:31:5B:F0:U-3C0000194361", "reset:U-3C0000194361" }, steps, families });
    }

    // ------------------------------------------------------------------ connection analytics

    [Test]
    public void Connection()
    {
        var rnd = new Lcg(7);
        var probes = new List<ConnectionProbeResult>();
        var link = new List<ConnectionLinkSample>();
        string gw = "192.0.2.1", isp = "198.51.100.1";
        string[] inet = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };
        for (long t = 0; t < 900; t++)
        {
            var wall = T0.AddMilliseconds(t * 1000 + 3);
            long ms = t * 1000 + (t % 5);
            bool down = t is >= 400 and < 406;                                       // Wi-Fi disconnect
            bool inetOut = t is >= 600 and < 612;                                   // internet outage, router fine
            bool gwSpike = t is >= 300 and < 304;
            long? gap = t == 700 ? 5400 : null;
            string bssid = t < 500 ? "02:AB:CD:00:00:01" : "02:AB:CD:00:00:02";       // roam at 500
            link.Add(new ConnectionLinkSample(t, wall, ms, !down, down ? null : "PlantOps", down ? null : bssid, t < 500 ? 36 : 44, down ? null : -55 - (int)(t % 7) + (t >= 500 ? -6 : 0),
                down ? null : 80, down ? null : 866.7, down ? null : 780, "192.0.2.50", gw, 1_000_000 + t * 12_000 + rnd.Noise(500), 400_000 + t * 3_000, null, gap));
            ConnectionProbeResult P(ConnectionSegment seg, string target, ProbeMethod m, bool fail, double baseMs, ProbeOutcome failOutcome = ProbeOutcome.Timeout) =>
                new(t, wall, ms, seg, target, m, fail ? failOutcome : ProbeOutcome.Ok, fail ? null : Math.Round(baseMs + rnd.Next() * baseMs * 0.3, 3), fail ? "synthetic" : null);
            probes.Add(P(ConnectionSegment.Gateway, gw, ProbeMethod.Icmp, down, gwSpike ? 260 : 3.2));
            if (t % 2 == 0)
            {
                probes.Add(P(ConnectionSegment.IspHop, isp, ProbeMethod.IcmpTtl, down || (t is >= 200 and < 210), 9));      // ISP hop silent 200..209 while internet fine
                foreach (var target in inet)
                {
                    probes.Add(P(ConnectionSegment.Internet, target, ProbeMethod.Tcp, down || inetOut, gwSpike ? 280 : 18 + (target == "9.9.9.9" ? 7 : 0), ProbeOutcome.Refused));
                    probes.Add(P(ConnectionSegment.Internet, target, ProbeMethod.Icmp, down || inetOut || t % 46 == 0, 17));
                }
            }
            if (t % 10 == 0) foreach (var dns in new[] { gw, "203.0.113.53" }) probes.Add(P(ConnectionSegment.Dns, dns, ProbeMethod.Dns, down || inetOut || (dns == "203.0.113.53" && t is >= 800 and < 840), 22, ProbeOutcome.DnsError));
            if (t % 30 == 0) probes.Add(P(ConnectionSegment.Web, "https://example.invalid/trace", ProbeMethod.Https, down || inetOut, 140, ProbeOutcome.HttpError));
        }
        var speed = new List<SpeedTestResult>
        {
            new() { StartUtc = T0.AddSeconds(120), ElapsedMs = 120_000, Provider = "LAN responder", Server = "192.0.2.10:5410", Streams = 4, SecondsPerDirection = 10, DownloadMbps = 412.5, UploadMbps = 288.1,
                BytesDown = 515_625_000, BytesUp = 360_125_000, IdleLatencyMs = 3.1, LoadedLatencyDownMs = 41.2, LoadedLatencyUpMs = 27.9, LoadedLatencyP95Ms = 66.3, LatencyIncreaseMs = 38.1,
                BufferbloatGrade = ConnectionTestRunner.BufferbloatGrade(38.1), ResponsivenessRpm = Math.Round(60000 / 41.2), LatencyTarget = "router 192.0.2.1" },
            new() { StartUtc = T0.AddSeconds(800), ElapsedMs = 800_000, Provider = "Custom HTTP server", Server = "https://speed.example.invalid/1G", Error = "no data received" }
        };
        var basic = new TwoWayResult { Responder = "192.0.2.10:5410", Seconds = 10, PacketsPerSecond = 50, PayloadBytes = 172 };
        var received = new Dictionary<int, (long, long, long, long)>();
        for (int s = 0; s < 500; s++)
        {
            if (s % 37 == 5 || s == 222) continue;
            long t1 = 1_000_000 + s * 20_000L, t2 = 5_000_000_000 + s * 20_000L + 1500 + (s % 7) * 90, t3 = t2 + 40, t4 = t1 + 3100 + (s % 5) * 120 + (s % 11) * 30;
            received[s] = (t1, t2, t3, t4);
        }
        var two = ResponderClient.Compute(basic, 500, 492, 1, 2, 493, received, 1, 3) with { StartUtc = T0.AddSeconds(450), ElapsedMs = 450_000 };
        var cfg = new ConnectionTestConfig
        {
            TestId = Guid.Parse("22222222-3333-4444-5555-666666666666"), Name = "Parity connection", Duration = TimeSpan.FromMinutes(15), Location = "Lab",
            ContextAtStart = new ConnectionContext { WifiConnected = true, Ssid = "PlantOps", Bssid = "02:AB:CD:00:00:01", LocalIPv4 = "192.0.2.50", Gateways = new[] { gw }, DnsServers = new[] { gw, "203.0.113.53", "2001:db8::53" },
                IspHop = isp, IspHopTtl = 2, PathHops = new[] { gw, isp } }
        };
        var markers = new List<ConnectionMarker> { new(T0.AddSeconds(250), 250_000, "Microwave on") };
        var r = new ConnectionAnalyzer().Analyze(cfg, probes, link, speed, new[] { two }, markers, T0, T0.AddSeconds(900), ConnectionTestStatus.Completed, 899.5);
        Write("connection.json", new
        {
            t0 = T0, config = new { cfg.TestId, cfg.TickMilliseconds, cfg.ContextAtStart }, estimatedKbps = cfg.EstimatedKbps(),
            estimatedKbpsDetailed = (cfg with { TickMilliseconds = 500, Intensity = ConnectionIntensity.Detailed }).EstimatedKbps(),
            probes, link, speed, twoWayInput = new { sent = 500, unique = 492, dup = 1, upReordered = 2, replies = 493, clientDuplicates = 1, downReordered = 3,
                received = received.OrderBy(kv => kv.Key).Select(kv => new[] { kv.Key, kv.Value.Item1, kv.Value.Item2, kv.Value.Item3, kv.Value.Item4 }) },
            markers, activeSeconds = 899.5,
            expected = new { twoWay = two, r.Segments, r.Targets, r.Events, problemsBySegment = r.ProblemsBySegment.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), r.Verdicts, r.VoiceRFactor, r.VoiceMos, r.Link, r.Totals },
            grades = new double?[] { null, 0, 4.99, 5, 29.9, 30, 59, 60, 199, 200, 399, 400, 1000 }.Select(v => new { v, g = ConnectionTestRunner.BufferbloatGrade(v) }),
            local = new[] { "10.1.2.3", "172.16.0.1", "172.32.0.1", "192.168.1.1", "169.254.3.4", "127.0.0.1", "100.64.0.1", "8.8.8.8", "::1", "x" }.Select(ip => new { ip, local = PathDiscovery.IsLocalAddress(ip) })
        });
    }

    // ------------------------------------------------------------------ responder protocol bytes

    [Test]
    public void Responder()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        var nonce = Enumerable.Range(0, 16).Select(i => (byte)(255 - i * 11)).ToArray();
        var packets = new[]
        {
            new ResponderProtocol.UdpPacket(1, 0x0102030405060708, 42, 123456789, 0, 0, 0),
            new ResponderProtocol.UdpPacket(2, -77, 7, 5, 6_000_000_000, 6_000_000_040, 41)
        };
        Write("responder.json", new
        {
            key = Convert.ToHexString(key), keyText = ResponderProtocol.KeyToText(key), nonce = Convert.ToHexString(nonce),
            clientProof = Convert.ToHexString(ResponderProtocol.ClientProof(key, nonce)),
            packets = packets.Select(p => new { p.Type, p.Session, p.Seq, p.T1, p.T2, p.T3, p.ServerCount, length = 172, bytes = Convert.ToHexString(ResponderProtocol.BuildUdp(key, p, 172)) }),
            shortPacket = Convert.ToHexString(ResponderProtocol.BuildUdp(key, packets[0], 10))
        });
    }

    // ------------------------------------------------------------------ network selection

    [Test]
    public void Selection()
    {
        var aps = new List<AccessPoint>
        {
            Ap("02:00:00:00:02:01", "Alpha", "5 GHz", 36, 80, -50, 100), Ap("02:00:00:00:02:02", "Alpha", "2.4 GHz", 1, 20, -60, 100),
            Ap("02:00:00:00:02:03", "Beta", "5 GHz", 44, 40, -45, 100), Ap("02:00:00:00:02:04", "Gamma", "5 GHz", 149, 80, -70, 100),
            Ap("02:00:00:00:02:05", "", "5 GHz", 149, 80, -40, 100), Ap("02:00:00:00:02:06", "Delta", "2.4 GHz", 11, 20, -80, 100)
        };
        var steps = new List<object>();
        var sel = new NetworkSelection();
        IReadOnlyCollection<string> Of(string ssid) => aps.Where(a => a.SSID == ssid).Select(a => a.BSSID).ToList();
        void Snap(string op, bool ok) => steps.Add(new
        {
            // materialised now: the selection's collections are live views
            op, ok, scope = sel.DeriveScope(), describe = sel.Describe(s => Of(s).Count), focus = sel.FocusOrder.ToList(), ssids = sel.Ssids.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            bssids = sel.Bssids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            matches = aps.Select(a => sel.Matches(a.SSID, a.BSSID)).ToList(), states = new[] { "Alpha", "Beta", "Gamma" }.Select(s => sel.StateOf(s, Of(s))).ToList(),
            slots = aps.Select(a => sel.FocusSlot(a.SSID, aps)).ToList()
        });
        Snap("empty", true);
        Snap("toggleGroup Alpha", sel.ToggleGroup("Alpha", Of("Alpha")));
        Snap("toggleAp Alpha 02:00:00:00:02:02", sel.ToggleAp("Alpha", "02:00:00:00:02:02", Of("Alpha")));
        Snap("toggleAp Beta", sel.ToggleAp("Beta", "02:00:00:00:02:03", Of("Beta")));
        Snap("toggleGroup hidden", sel.ToggleGroup("", Of("")));
        Snap("toggleGroup Gamma", sel.ToggleGroup("Gamma", Of("Gamma")));
        sel.Lock();
        Snap("locked toggle Delta", sel.ToggleGroup("Delta", Of("Delta")));
        sel.Unlock();
        Snap("toggleAp Alpha 02:00:00:00:02:02 again", sel.ToggleAp("Alpha", "02:00:00:00:02:02", Of("Alpha")));
        Snap("toggleGroup Alpha (full)", sel.ToggleGroup("Alpha", Of("Alpha")));
        Snap("clear", sel.Clear());
        Write("selection.json", new { aps = aps.Select(ApDto), steps, autoFocus = NetworkSelection.AutoFocus(aps) });
    }

    // ------------------------------------------------------------------ 802.11 information elements

    [Test]
    public void InformationElements()
    {
        static byte[] E(int id, params int[] body) => new[] { (byte)id, (byte)body.Length }.Concat(body.Select(b => (byte)b)).ToArray();
        static int[] Wps()
        {
            var b = new List<int> { 0x00, 0x50, 0xF2, 0x04 };
            void Tlv(int type, byte[] v) { b.AddRange(new[] { type >> 8, type & 0xFF, v.Length >> 8, v.Length & 0xFF }); b.AddRange(v.Select(x => (int)x)); }
            Tlv(0x1021, System.Text.Encoding.UTF8.GetBytes("Synthetic Networks"));
            Tlv(0x1023, System.Text.Encoding.UTF8.GetBytes("Gateway"));
            Tlv(0x1024, System.Text.Encoding.UTF8.GetBytes("SN-210 "));
            Tlv(0x1011, System.Text.Encoding.UTF8.GetBytes("  "));
            Tlv(0x1047, Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray());
            return b.ToArray();
        }
        int[] rsn(params int[] akms) => new[] { 1, 0, 0x00, 0x0F, 0xAC, 4, 1, 0, 0x00, 0x0F, 0xAC, 4, akms.Length, 0 }.Concat(akms.SelectMany(a => new[] { 0x00, 0x0F, 0xAC, a })).ToArray();
        var cases = new List<(string Name, string Band, bool Privacy, byte[] Ie)>
        {
            ("open", "2.4 GHz", false, E(1, 0x82, 0x84, 0x8B, 0x96)),
            ("wep", "2.4 GHz", true, E(1, 0x82)),
            ("wpa2psk ht40", "2.4 GHz", true, E(48, rsn(2)).Concat(E(61, 6, 0x07, 0, 0, 0)).Concat(E(50, 0x0C, 0x12, 0x18)).ToArray()),
            ("wpa3 vht80 bssload wps", "5 GHz", true, E(48, rsn(8)).Concat(E(61, 36, 0x05)).Concat(E(192, 1, 42, 0)).Concat(E(11, 7, 0, 128, 0x10, 0x27)).Concat(E(221, Wps())).ToArray()),
            ("transition vht160", "5 GHz", true, E(48, rsn(2, 8)).Concat(E(192, 1, 42, 50)).ToArray()),
            ("enterprise 80+80", "5 GHz", true, E(48, rsn(1)).Concat(E(192, 1, 42, 106)).ToArray()),
            ("suiteb", "5 GHz", true, E(48, rsn(12)).ToArray()),
            ("owe", "5 GHz", false, E(48, rsn(18)).ToArray()),
            ("wpa1", "2.4 GHz", true, E(221, 0x00, 0x50, 0xF2, 0x01, 1, 0)),
            ("rsn unknown akm", "2.4 GHz", true, E(48, 1, 0)),
            ("he6 160", "6 GHz", true, E(48, rsn(8)).Concat(E(255, 36, 0x00, 0x00, 0x02, 0x3F, 0xFC, 0xFF, 37, 0x03, 15, 0, 1)).ToArray()),
            ("truncated", "5 GHz", true, new byte[] { 48, 20, 1, 0 })
        };
        Write("ie.json", new
        {
            cases = cases.Select(c =>
            {
                var load = WlanAdapter.ParseBssLoad(c.Ie);
                var wps = WlanAdapter.ParseWps(c.Ie);
                return new { name = c.Name, band = c.Band, privacy = c.Privacy, ie = Convert.ToHexString(c.Ie), security = WlanAdapter.ParseSecurity(c.Ie, (ushort)(c.Privacy ? 0x0011 : 0x0001)),
                    width = WlanAdapter.ParseChannelWidth(c.Ie, c.Band), rates = WlanAdapter.ParseRates(c.Ie),
                    bssLoad = load is { } l ? new { stations = l.Stations, utilizationPct = l.UtilizationPct, admissionCapacity = l.AdmissionCapacity } : null, wps };
            }).ToList()
        });
    }
}
