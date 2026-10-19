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

/// <summary>
/// The one network selection shared by the live view, capture sessions and saturation tests (DR-0010, DR-0011).
/// Two kinds of entries:
///  - whole SSIDs: every BSSID of that SSID, including ones that appear later ("follows new APs");
///  - fixed BSSIDs: exactly those radios.
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

    public event Action? Changed;

    public IReadOnlyCollection<string> Ssids => _ssids;
    public IReadOnlyCollection<string> Bssids => _bssids;
    public bool IsEmpty => _ssids.Count == 0 && _bssids.Count == 0;
    public bool IsLocked { get; private set; }

    /// <summary>Is this AP part of what is shown/captured? (Empty selection = everything.)</summary>
    public bool Matches(string ssid, string bssid) => IsEmpty || IsExplicitlySelected(ssid, bssid);

    public bool IsExplicitlySelected(string ssid, string bssid) => _ssids.Contains(ssid) || _bssids.Contains(bssid);

    public bool IsWholeSsid(string ssid) => _ssids.Contains(ssid);

    public GroupSelectionState StateOf(string ssid, IReadOnlyCollection<string> bssidsOfSsid)
    {
        if (_ssids.Contains(ssid)) return GroupSelectionState.All;
        int n = bssidsOfSsid.Count(b => _bssids.Contains(b));
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

    /// <summary>AP checkbox. Toggling an AP of a whole-SSID selection converts the group to fixed BSSIDs (minus this one).</summary>
    public bool ToggleAp(string ssid, string bssid, IReadOnlyCollection<string> bssidsOfSsid)
    {
        if (IsLocked) return false;
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
        _focusOrder.Clear();
        Changed?.Invoke();
        return true;
    }

    public void Lock()
    {
        IsLocked = true;
        Changed?.Invoke();
    }

    public void Unlock()
    {
        IsLocked = false;
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
    private void Touch(string ssid)
    {
        bool hasAny = _ssids.Contains(ssid) || _ssidOfBssid.Values.Contains(ssid, StringComparer.Ordinal);
        if (hasAny && !_focusOrder.Contains(ssid)) _focusOrder.Add(ssid);
        if (!hasAny) _focusOrder.Remove(ssid);
        Changed?.Invoke();
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
    public SaturationScope DeriveScope() => (_ssids.Count, _bssids.Count) switch
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
        if (_bssids.Count > 0) parts.Add($"{_bssids.Count} AP{(_bssids.Count == 1 ? "" : "s")} (fixed)");
        return string.Join(" + ", parts) + (DeriveScope() == SaturationScope.Mixed ? " · mixed" : "");
    }
}
