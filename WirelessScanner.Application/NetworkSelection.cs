using System;
using System.Collections.Generic;
using System.Linq;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

public enum GroupSelectionState
{
    None,
    Partial,
    All
}

/// <summary>Current physical-unit grouping (DR-0013), implemented by <see cref="PhysicalUnitGrouper"/>.</summary>
public interface IUnitResolver
{
    string? UnitOf(string bssid);
    PhysicalUnit? Get(string unitId);
}

/// <summary>
/// The one network selection shared by the live view, capture sessions and saturation tests (DR-0010, DR-0011).
/// Three kinds of entries:
///  - whole SSIDs: every BSSID of that SSID, including ones that appear later ("follows new APs");
///  - fixed BSSIDs: exactly those radios;
///  - physical units (DR-0013): every BSSID the unit resolver currently groups into that unit (all its SSIDs and radios).
///    While locked, unit membership is frozen to what it was at lock time.
/// An empty selection means "everything" (unchanged 1.0 capture semantics). While locked (a saturation test is
/// running) every mutation is refused so the test scope cannot drift.
/// </summary>
public sealed class NetworkSelection
{
    /// <summary>Overlapping spectrum shapes need all-pairs colour separation; only 3 validated slots pass (DR-0010).</summary>
    public const int MaxFocusColors = 3;

    private readonly HashSet<string> _ssids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bssids = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _ssidOfBssid = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _focusOrder = new();
    private readonly HashSet<string> _units = new(StringComparer.Ordinal);
    private HashSet<string>? _frozenUnitBssids;

    public event Action? Changed;

    public IReadOnlyCollection<string> Ssids => _ssids;
    public IReadOnlyCollection<string> Bssids => _bssids;
    public IReadOnlyCollection<string> Units => _units;
    public bool IsEmpty => _ssids.Count == 0 && _bssids.Count == 0 && _units.Count == 0;
    public bool IsLocked { get; private set; }

    /// <summary>Physical-unit grouping used to resolve unit entries (DR-0013). Null = no unit entries can match.</summary>
    public IUnitResolver? UnitResolver { get; set; }

    /// <summary>Is this AP part of what is shown/captured? (Empty selection = everything.)</summary>
    public bool Matches(string ssid, string bssid) => IsEmpty || IsExplicitlySelected(ssid, bssid);

    public bool IsExplicitlySelected(string ssid, string bssid) => _ssids.Contains(ssid) || _bssids.Contains(bssid) || InSelectedUnit(bssid);

    public bool IsUnitSelected(string unitId) => _units.Contains(unitId);

    private bool InSelectedUnit(string bssid)
    {
        if (_units.Count == 0) return false;
        if (_frozenUnitBssids != null) return _frozenUnitBssids.Contains(bssid);
        return UnitResolver?.UnitOf(bssid) is { } u && _units.Contains(u);
    }

    /// <summary>BSSIDs the selected units contain now (frozen while locked).</summary>
    public IReadOnlyCollection<string> UnitBssids()
    {
        if (_frozenUnitBssids != null) return _frozenUnitBssids;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _units)
            if (UnitResolver?.Get(id) is { } u) foreach (var m in u.Members) set.Add(m.Bssid);
        return set;
    }

    /// <summary>Unit checkbox state: All when the unit is selected, else how many of its members are selected otherwise.</summary>
    public GroupSelectionState StateOfUnit(PhysicalUnit unit)
    {
        if (_units.Contains(unit.Id)) return GroupSelectionState.All;
        var members = unit.Members.ToList();
        int n = members.Count(m => _ssids.Contains(m.Ssid) || _bssids.Contains(m.Bssid));
        return n == 0 ? GroupSelectionState.None : n == members.Count ? GroupSelectionState.All : GroupSelectionState.Partial;
    }

    /// <summary>
    /// Unit checkbox: none/partial -> the whole unit (all its SSIDs and radios; follows regrouping until a test locks it);
    /// selected -> nothing. Fixed BSSIDs of the unit are absorbed into the unit entry.
    /// </summary>
    public bool ToggleUnit(PhysicalUnit unit)
    {
        if (IsLocked) return false;
        var members = unit.Members.ToList();
        if (!_units.Remove(unit.Id))
        {
            foreach (var m in members) RemoveBssid(m.Bssid);
            _units.Add(unit.Id);
        }
        foreach (var ssid in members.Select(m => m.Ssid).Distinct()) Touch(ssid, notify: false);
        Changed?.Invoke();
        return true;
    }

    public bool IsWholeSsid(string ssid) => _ssids.Contains(ssid);

    public GroupSelectionState StateOf(string ssid, IReadOnlyCollection<string> bssidsOfSsid)
    {
        if (_ssids.Contains(ssid)) return GroupSelectionState.All;
        int n = bssidsOfSsid.Count(b => _bssids.Contains(b) || InSelectedUnit(b));
        return n == 0 ? GroupSelectionState.None : n == bssidsOfSsid.Count ? GroupSelectionState.All : GroupSelectionState.Partial;
    }

    public static bool IsHiddenSsid(string ssid) => string.IsNullOrEmpty(ssid) || ssid == "<Hidden SSID>";

    /// <summary>Group checkbox: none/partial -> whole SSID (follows new APs); whole -> nothing. Hidden SSIDs are refused.</summary>
    public bool ToggleGroup(string ssid, IReadOnlyCollection<string> bssidsOfSsid)
    {
        if (IsLocked || IsHiddenSsid(ssid)) return false;
        bool wholeOrFull = StateOf(ssid, bssidsOfSsid) == GroupSelectionState.All;
        foreach (var b in bssidsOfSsid) RemoveBssid(b);
        if (wholeOrFull)
        {
            _ssids.Remove(ssid);
        }
        else
        {
            _ssids.Add(ssid);
        }
        Touch(ssid);
        return true;
    }

    /// <summary>
    /// AP checkbox. Toggling an AP of a whole-SSID selection converts the group to fixed BSSIDs (minus this one); toggling an
    /// AP of a selected unit converts the unit to its other members as fixed BSSIDs.
    /// </summary>
    public bool ToggleAp(string ssid, string bssid, IReadOnlyCollection<string> bssidsOfSsid)
    {
        if (IsLocked) return false;
        if (UnitResolver?.UnitOf(bssid) is { } unitId && _units.Contains(unitId) && UnitResolver.Get(unitId) is { } unit)
        {
            _units.Remove(unitId);
            foreach (var m in unit.Members.Where(m => !string.Equals(m.Bssid, bssid, StringComparison.OrdinalIgnoreCase))) AddBssid(m.Ssid, m.Bssid);
            foreach (var s in unit.Members.Select(m => m.Ssid).Distinct()) Touch(s, notify: false);
            Changed?.Invoke();
            return true;
        }
        if (_ssids.Remove(ssid))
        {
            foreach (var b in bssidsOfSsid.Where(b => !string.Equals(b, bssid, StringComparison.OrdinalIgnoreCase))) AddBssid(ssid, b);
        }
        else if (_bssids.Contains(bssid))
        {
            RemoveBssid(bssid);
        }
        else
        {
            AddBssid(ssid, bssid);
        }
        Touch(ssid);
        return true;
    }

    /// <summary>Legacy bridge (SSID view): select/deselect a whole SSID.</summary>
    public bool SetSsid(string ssid, bool selected)
    {
        if (IsLocked) return false;
        if (selected) _ssids.Add(ssid); else _ssids.Remove(ssid);
        Touch(ssid);
        return true;
    }

    /// <summary>Legacy bridge (AP view): select/deselect one BSSID.</summary>
    public bool SetBssid(string ssid, string bssid, bool selected)
    {
        if (IsLocked) return false;
        if (selected) AddBssid(ssid, bssid); else RemoveBssid(bssid);
        Touch(ssid);
        return true;
    }

    public bool Clear()
    {
        if (IsLocked) return false;
        _ssids.Clear();
        _bssids.Clear();
        _ssidOfBssid.Clear();
        _units.Clear();
        _focusOrder.Clear();
        Changed?.Invoke();
        return true;
    }

    /// <summary>Locks the selection for a running test; unit membership is frozen so the live view matches the test.</summary>
    public void Lock()
    {
        _frozenUnitBssids = _units.Count == 0 ? null : new HashSet<string>(UnitBssids(), StringComparer.OrdinalIgnoreCase);
        IsLocked = true;
        Changed?.Invoke();
    }

    public void Unlock()
    {
        IsLocked = false;
        _frozenUnitBssids = null;
        Changed?.Invoke();
    }

    private void AddBssid(string ssid, string bssid)
    {
        _bssids.Add(bssid);
        _ssidOfBssid[bssid] = ssid;
    }

    private void RemoveBssid(string bssid)
    {
        _bssids.Remove(bssid);
        _ssidOfBssid.Remove(bssid);
    }

    /// <summary>Keep focus order stable: colour follows the network, never its rank (dataviz rule).</summary>
    private void Touch(string ssid, bool notify = true)
    {
        bool hasAny = _ssids.Contains(ssid) || _ssidOfBssid.Values.Contains(ssid, StringComparer.Ordinal)
                      || _units.Any(id => UnitResolver?.Get(id) is { } u && u.Members.Any(m => m.Ssid == ssid));
        if (hasAny && !_focusOrder.Contains(ssid)) _focusOrder.Add(ssid);
        if (!hasAny) _focusOrder.Remove(ssid);
        if (notify) Changed?.Invoke();
    }

    /// <summary>SSIDs in the order they were selected.</summary>
    public IReadOnlyList<string> FocusOrder => _focusOrder;

    /// <summary>
    /// Colour slot (0..2) for a network, or -1 for "context". With an empty selection the 3 strongest networks
    /// are auto-focused so the view is never monotone.
    /// </summary>
    public int FocusSlot(string ssid, IEnumerable<AccessPoint> visible)
    {
        var order = IsEmpty ? AutoFocus(visible) : _focusOrder;
        int i = -1;
        for (int k = 0; k < order.Count; k++)
        {
            if (order[k] == ssid) { i = k; break; }
        }
        return i >= 0 && i < MaxFocusColors ? i : -1;
    }

    public static IReadOnlyList<string> AutoFocus(IEnumerable<AccessPoint> visible) =>
        visible.Where(a => !IsHiddenSsid(a.SSID)).GroupBy(a => a.SSID).OrderByDescending(g => g.Max(a => a.RSSI)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxFocusColors).Select(g => g.Key).ToList();

    /// <summary>Saturation scope implied by the selection (DR-0011).</summary>
    public SaturationScope DeriveScope() => _units.Count > 0
        ? (_ssids.Count, _bssids.Count, _units.Count) switch
        {
            (0, 0, 1) => SaturationScope.SingleUnit,
            (0, 0, _) => SaturationScope.MultipleUnits,
            _ => SaturationScope.Mixed
        }
        : (_ssids.Count, _bssids.Count) switch
    {
        (0, 0) => SaturationScope.AllVisible,
        (1, 0) => SaturationScope.SingleSsid,
        ( > 1, 0) => SaturationScope.MultipleSsids,
        (0, 1) => SaturationScope.SingleAp,
        (0, > 1) => SaturationScope.MultipleAps,
        _ => SaturationScope.Mixed
    };

    /// <summary>Plain-language scope, e.g. "1 SSID (3 APs, follows new APs) + 1 AP (fixed)".</summary>
    public string Describe(Func<string, int> apsInSsid)
    {
        if (IsEmpty) return "Nothing selected: everything is shown and captured; a saturation test would ask before covering all networks.";
        var parts = new List<string>();
        if (_ssids.Count > 0)
        {
            int aps = _ssids.Sum(apsInSsid);
            parts.Add($"{_ssids.Count} SSID{(_ssids.Count == 1 ? "" : "s")} ({aps} AP{(aps == 1 ? "" : "s")} now, follows new APs)");
        }
        if (_units.Count > 0)
        {
            int members = UnitBssids().Count;
            parts.Add($"{_units.Count} access point{(_units.Count == 1 ? "" : "s")} (hardware: {members} BSSID{(members == 1 ? "" : "s")}, all SSIDs and radios)");
        }
        if (_bssids.Count > 0) parts.Add($"{_bssids.Count} AP{(_bssids.Count == 1 ? "" : "s")} (fixed)");
        return string.Join(" + ", parts) + (DeriveScope() == SaturationScope.Mixed ? " · mixed" : "");
    }
}
