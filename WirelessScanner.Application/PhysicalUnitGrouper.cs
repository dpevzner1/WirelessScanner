using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Infers physical units (access points / routers) from scans (DR-0013, MVP). Pure apart from its in-memory evidence.
/// Evidence, strongest first:
///  1. Same radio: the TSF offset (radio timer minus host receive time) of two BSSIDs in the same band agrees within
///     <see cref="SameRadioToleranceMs"/> in the same scan, and agrees at least as often as it disagrees over scans.
///  2. Same WPS device: identical WPS UUID-E.
///  3. MAC family: the same address apart from the locally-administered/low bits of octet 1 and the low nibble of
///     octet 6 (SSID index). For locally-administered addresses octet 4 may also differ (radio index, e.g. 9A:3C:5E:1F/26).
///     Conflicting WPS UUIDs block a family link.
/// The user's moves and names (UnitOverrides) always win. Evidence older than <see cref="Retention"/> is forgotten.
/// </summary>
public sealed class PhysicalUnitGrouper : IUnitResolver
{
    public const double SameRadioToleranceMs = 5;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    public const string HiddenSsid = "<Hidden SSID>";

    private sealed class Seen
    {
        public AccessPoint Ap = null!;
        public DateTime LastUtc;
    }

    private readonly Dictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), (int Agree, int Disagree)> _tsf = new();
    private UnitOverrides _overrides = new();
    private List<PhysicalUnit> _units = new();
    private readonly Dictionary<string, string> _unitOf = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after units were recomputed.</summary>
    public event Action? Changed;
    /// <summary>Raised when the user's overrides changed (persist them).</summary>
    public event Action<UnitOverrides>? OverridesChanged;

    public IReadOnlyList<PhysicalUnit> Units => _units;
    public UnitOverrides Overrides => Clone(_overrides);

    public string? UnitOf(string bssid) => _unitOf.TryGetValue(bssid, out var id) ? id : null;
    public PhysicalUnit? Get(string unitId) => _units.FirstOrDefault(u => u.Id == unitId);

    // ------------------------------------------------------------------ input

    /// <summary>Feed one scan (all access points heard). Fresh and cached entries both carry identity evidence.</summary>
    public void Observe(IReadOnlyList<AccessPoint> aps, DateTime nowUtc)
    {
        foreach (var ap in aps)
        {
            if (string.IsNullOrEmpty(ap.BSSID)) continue;
            _seen[ap.BSSID] = new Seen { Ap = ap, LastUtc = nowUtc };
        }
        foreach (var stale in _seen.Where(kv => nowUtc - kv.Value.LastUtc > Retention).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(stale);
            foreach (var k in _tsf.Keys.Where(k => k.Item1.Equals(stale, StringComparison.OrdinalIgnoreCase) || k.Item2.Equals(stale, StringComparison.OrdinalIgnoreCase)).ToList())
                _tsf.Remove(k);
        }

        // TSF evidence from this scan only (offsets are comparable within one list read)
        var timed = aps.Where(a => a.TsfOffsetMs.HasValue && !string.IsNullOrEmpty(a.BSSID)).ToList();
        for (int i = 0; i < timed.Count; i++)
        {
            for (int j = i + 1; j < timed.Count; j++)
            {
                var a = timed[i];
                var b = timed[j];
                if (a.Band != b.Band) continue;
                var key = PairKey(a.BSSID, b.BSSID);
                var (agree, disagree) = _tsf.GetValueOrDefault(key);
                if (Math.Abs(a.TsfOffsetMs!.Value - b.TsfOffsetMs!.Value) <= SameRadioToleranceMs) agree++;
                else disagree++;
                _tsf[key] = (agree, disagree);
            }
        }
        Rebuild();
    }

    // ------------------------------------------------------------------ user corrections

    public void LoadOverrides(UnitOverrides? overrides)
    {
        _overrides = overrides == null ? new UnitOverrides() : Clone(overrides);
        Rebuild();
    }

    /// <summary>Move one BSSID into an existing unit (merge).</summary>
    public void MoveToUnit(string bssid, string unitId)
    {
        _overrides.Assignments[bssid] = unitId;
        Commit();
    }

    /// <summary>Split one BSSID out into its own manual unit; returns the new unit id.</summary>
    public string MoveToNewUnit(string bssid)
    {
        string id = "M-" + bssid.ToUpperInvariant();
        _overrides.Assignments[bssid] = id;
        Commit();
        return id;
    }

    public void Rename(string unitId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) _overrides.Names.Remove(unitId);
        else _overrides.Names[unitId] = name.Trim();
        Commit();
    }

    /// <summary>Back to automatic grouping for this unit: drops moves into/out of it and its name.</summary>
    public void Reset(string unitId)
    {
        var members = Get(unitId)?.Members.Select(m => m.Bssid).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        foreach (var k in _overrides.Assignments.Where(kv => kv.Value == unitId || members.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            _overrides.Assignments.Remove(k);
        _overrides.Names.Remove(unitId);
        Commit();
    }

    private void Commit()
    {
        Rebuild();
        OverridesChanged?.Invoke(Clone(_overrides));
    }

    // ------------------------------------------------------------------ evidence rules (public for tests and docs)

    /// <summary>Parses "AA:BB:CC:DD:EE:FF" (or with '-') into 6 octets; null when malformed.</summary>
    public static byte[]? Octets(string bssid)
    {
        var parts = bssid.Split(':', '-');
        if (parts.Length != 6) return null;
        var o = new byte[6];
        for (int i = 0; i < 6; i++)
            if (!byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out o[i])) return null;
        return o;
    }

    public static bool IsLocallyAdministered(string bssid) => Octets(bssid) is { } o && (o[0] & 0x02) != 0;

    /// <summary>MAC-family rule (see class summary).</summary>
    public static bool SameFamily(string a, string b)
    {
        if (Octets(a) is not { } x || Octets(b) is not { } y) return false;
        bool core = (x[0] & ~0x06) == (y[0] & ~0x06) && x[1] == y[1] && x[2] == y[2] && x[4] == y[4] && (x[5] >> 4) == (y[5] >> 4);
        if (!core) return false;
        if (x[3] == y[3]) return true;
        return (x[0] & 0x02) != 0 && (y[0] & 0x02) != 0;   // radio index in octet 4: only for locally-administered schemes
    }

    private static (string, string) PairKey(string a, string b)
    {
        a = a.ToUpperInvariant();
        b = b.ToUpperInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
    }

    private bool SameRadioClock(string a, string b) =>
        _tsf.TryGetValue(PairKey(a, b), out var v) && v.Agree > 0 && v.Agree >= v.Disagree;

    // ------------------------------------------------------------------ grouping

    private void Rebuild()
    {
        var bssids = _seen.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var index = bssids.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i, StringComparer.OrdinalIgnoreCase);
        var parent = Enumerable.Range(0, bssids.Count).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int i, int j) { int a = Find(i), b = Find(j); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        var reasons = new Dictionary<int, HashSet<string>>();
        void Reason(int i, string r) { if (!reasons.TryGetValue(i, out var s)) reasons[i] = s = new HashSet<string>(); s.Add(r); }

        for (int i = 0; i < bssids.Count; i++)
        {
            var a = _seen[bssids[i]].Ap;
            for (int j = i + 1; j < bssids.Count; j++)
            {
                var b = _seen[bssids[j]].Ap;
                bool uuidConflict = a.WpsUuid != null && b.WpsUuid != null && !string.Equals(a.WpsUuid, b.WpsUuid, StringComparison.OrdinalIgnoreCase);
                if (a.Band == b.Band && SameRadioClock(a.BSSID, b.BSSID) && !uuidConflict)
                {
                    Union(i, j); Reason(i, "radio clock"); Reason(j, "radio clock");
                }
                else if (a.WpsUuid != null && string.Equals(a.WpsUuid, b.WpsUuid, StringComparison.OrdinalIgnoreCase))
                {
                    Union(i, j); Reason(i, "wps uuid"); Reason(j, "wps uuid");
                }
                else if (!uuidConflict && SameFamily(a.BSSID, b.BSSID))
                {
                    Union(i, j); Reason(i, "mac family"); Reason(j, "mac family");
                }
            }
        }

        // automatic unit ids, then the user's assignments
        var autoMembers = new Dictionary<int, List<string>>();
        for (int i = 0; i < bssids.Count; i++)
        {
            int r = Find(i);
            if (!autoMembers.TryGetValue(r, out var l)) autoMembers[r] = l = new List<string>();
            l.Add(bssids[i]);
        }
        var unitOfBssid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, members) in autoMembers)
        {
            string id = "U-" + members.Min(StringComparer.OrdinalIgnoreCase)!.Replace(":", "").ToUpperInvariant();
            foreach (var m in members) unitOfBssid[m] = id;
        }
        var manualUnits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bssid, target) in _overrides.Assignments)
        {
            if (!unitOfBssid.ContainsKey(bssid)) continue;   // not currently heard
            unitOfBssid[bssid] = target;
            manualUnits.Add(target);
        }

        // members moved OUT also make the source unit manual
        foreach (var (bssid, target) in _overrides.Assignments)
        {
            if (!index.TryGetValue(bssid, out int i)) continue;
            var originalMembers = autoMembers[Find(i)];
            foreach (var m in originalMembers)
                if (!string.Equals(m, bssid, StringComparison.OrdinalIgnoreCase) && unitOfBssid[m] != target) manualUnits.Add(unitOfBssid[m]);
        }

        _unitOf.Clear();
        foreach (var kv in unitOfBssid) _unitOf[kv.Key] = kv.Value;

        var units = new List<PhysicalUnit>();
        foreach (var group in unitOfBssid.GroupBy(kv => kv.Value, kv => kv.Key))
        {
            var members = group.Select(b => _seen[b].Ap).OrderBy(a => a.Band).ThenBy(a => a.BSSID, StringComparer.OrdinalIgnoreCase).ToList();
            var radios = Radios(members);
            var evidence = new List<string>();
            var memberReasons = members.SelectMany(m => reasons.GetValueOrDefault(index[m.BSSID]) ?? new HashSet<string>()).ToHashSet();
            bool manual = manualUnits.Contains(group.Key);
            if (manual) evidence.Add("Grouping corrected by the user.");
            if (memberReasons.Contains("radio clock"))
                evidence.Add($"Same radio clock: {radios.Count(r => r.Members.Count > 1 && r.Key.Contains('#'))} radio(s) where several BSSIDs share one TSF timer (within {SameRadioToleranceMs:0} ms).");
            if (memberReasons.Contains("wps uuid")) evidence.Add("Same WPS device identity (UUID-E).");
            if (memberReasons.Contains("mac family")) evidence.Add("Related MAC addresses (same address family across radios/SSIDs).");
            var models = members.Select(m => m.WpsModelNumber ?? m.WpsModel).Where(x => x != null).Distinct().ToList();
            if (models.Count > 0) evidence.Add("WPS model: " + string.Join(", ", models) + ".");

            var confidence = manual ? UnitConfidence.Manual
                : members.Count == 1 ? UnitConfidence.Single
                : memberReasons.Contains("radio clock") || memberReasons.Contains("wps uuid") || models.Count > 0 ? UnitConfidence.High
                : UnitConfidence.Medium;

            string? vendor = members.Select(m => m.WpsManufacturer).Where(v => v != null).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                             ?? members.Select(m => m.VendorOUI).FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != "Unknown Vendor");
            string? model = members.Select(m => m.WpsModelNumber ?? m.WpsModel).FirstOrDefault(v => v != null);
            string root = members.Select(m => m.BSSID).Min(StringComparer.OrdinalIgnoreCase)!;
            string autoName = (model != null ? $"{vendor} {model}".Trim() : vendor ?? "Access point") + $" ({root[^5..]})";
            bool custom = _overrides.Names.TryGetValue(group.Key, out var customName);
            units.Add(new PhysicalUnit(group.Key, custom ? customName! : autoName, custom, confidence, evidence, radios, vendor, model, Array.Empty<string>()));
        }

        // mesh / extender candidates: the same visible SSID broadcast by different units
        var unitsBySsid = units.SelectMany(u => u.Members.Where(m => !string.IsNullOrEmpty(m.Ssid) && m.Ssid != HiddenSsid).Select(m => (m.Ssid, u.Id)))
            .Distinct().GroupBy(x => x.Ssid).ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());
        _units = units.Select(u =>
        {
            var others = u.Members.Select(m => m.Ssid).Distinct().Where(unitsBySsid.ContainsKey)
                .SelectMany(s => unitsBySsid[s]).Where(id => id != u.Id).Distinct().OrderBy(x => x).ToList();
            return others.Count == 0 ? u : u with
            {
                SameSsidUnitIds = others,
                Evidence = u.Evidence.Append($"Shares an SSID with {others.Count} other unit(s): mesh node or extender candidate.").ToList()
            };
        }).OrderByDescending(u => u.Members.Max(m => m.Rssi)).ThenBy(u => u.Id, StringComparer.Ordinal).ToList();
        Changed?.Invoke();
    }

    /// <summary>Radios: BSSIDs linked by the radio clock; members without clock evidence group by band + channel.</summary>
    private List<UnitRadio> Radios(List<AccessPoint> members)
    {
        var groups = new List<List<AccessPoint>>();
        foreach (var m in members)
        {
            var g = groups.FirstOrDefault(g => g[0].Band == m.Band && g.Any(x => SameRadioClock(x.BSSID, m.BSSID)));
            g ??= groups.FirstOrDefault(g => g[0].Band == m.Band && g[0].Channel == m.Channel && !g.Any(x => _tsf.ContainsKey(PairKey(x.BSSID, m.BSSID))));
            if (g == null) groups.Add(new List<AccessPoint> { m }); else g.Add(m);
        }
        return groups.Select(g =>
        {
            bool clock = g.Count > 1 && g.Skip(1).All(x => SameRadioClock(g[0].BSSID, x.BSSID));
            string key = $"{g[0].Band}{(clock ? "#" : "@")}{g.Select(x => x.BSSID).Min(StringComparer.OrdinalIgnoreCase)}";
            return new UnitRadio(key, g[0].Band, g[0].Channel, g.Select(x => new UnitMember(x.BSSID, x.SSID, x.Band, x.Channel, x.RSSI)).ToList());
        }).OrderBy(r => r.Band).ToList();
    }

    private static UnitOverrides Clone(UnitOverrides o) => new()
    {
        Assignments = new Dictionary<string, string>(o.Assignments, StringComparer.OrdinalIgnoreCase),
        Names = new Dictionary<string, string>(o.Names, StringComparer.Ordinal)
    };
}
