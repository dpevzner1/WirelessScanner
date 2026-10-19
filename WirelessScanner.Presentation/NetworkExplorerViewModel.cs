using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WirelessScanner.Application;
using WirelessScanner.Domain;

namespace WirelessScanner.Presentation;

public enum ExplorerSort { Signal, Name, Channel }

/// <summary>Explorer grouping (DR-0013): by network name (SSID) or by inferred physical access point (hardware).</summary>
public enum ExplorerGrouping { Network, AccessPoint }

/// <summary>
/// Grouped network explorer (DR-0010): SSID groups with BSSID children, tri-state group selection, search, chips,
/// sort, 12-scan sparklines and identity swatches. Items are reconciled in place by key on every scan, so
/// expansion, selection and scroll position survive refreshes (capacity C4).
/// </summary>
public class NetworkExplorerViewModel : INotifyPropertyChanged
{
    /// <summary>Validated categorical identity colours (dark steps, all-pairs safe for slots 1-3) and the context colour.</summary>
    public static readonly string[] IdentityColors = { "#3987E5", "#D95926", "#199E70" };
    public const string ContextColor = "#5B5246";

    private readonly NetworkSelection _selection;
    private readonly Dictionary<string, Queue<int>> _history = new(StringComparer.OrdinalIgnoreCase);
    private List<AccessPoint> _all = new();
    private readonly Dictionary<string, NetworkGroupItem> _groups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly PhysicalUnitGrouper? _units;
    private readonly Dictionary<string, UnitGroupItem> _unitItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedUnits = new(StringComparer.Ordinal);

    public NetworkExplorerViewModel(NetworkSelection selection, PhysicalUnitGrouper? units = null)
    {
        _selection = selection;
        _selection.Changed += Refresh;
        _units = units;
        if (_units != null) _units.Changed += () => { if (_grouping == ExplorerGrouping.AccessPoint) Refresh(); };
        ToggleUnitCommand = new ParamCommand(p => { if (p is UnitGroupItem u) ToggleUnit(u); });
        ToggleUnitExpandCommand = new ParamCommand(p => { if (p is UnitGroupItem u) { u.IsExpanded = !u.IsExpanded; if (u.IsExpanded) _expandedUnits.Add(u.Id); else _expandedUnits.Remove(u.Id); } });
        ToggleGroupCommand = new ParamCommand(p => { if (p is NetworkGroupItem g) ToggleGroup(g); });
        ToggleApCommand = new ParamCommand(p => { if (p is NetworkApItem a) ToggleAp(a); });
        ToggleExpandCommand = new ParamCommand(p => { if (p is NetworkGroupItem g) { g.IsExpanded = !g.IsExpanded; if (g.IsExpanded) _expanded.Add(g.Ssid); else _expanded.Remove(g.Ssid); } });
        ClearCommand = new RelayCommand(() => _selection.Clear());
        SelectShownCommand = new RelayCommand(SelectShown);
    }

    public NetworkSelection Selection => _selection;
    public ObservableCollection<NetworkGroupItem> Groups { get; } = new();

    private string? _connectedBssid;
    /// <summary>BSSID this PC is associated with (CONNECTED badge, DR-0014); null when not connected.</summary>
    public string? ConnectedBssid
    {
        get => _connectedBssid;
        set { if (string.Equals(_connectedBssid, value, StringComparison.OrdinalIgnoreCase)) return; _connectedBssid = value; OnPropertyChanged(); Refresh(); }
    }
    private bool IsConnected(string bssid) => _connectedBssid != null && string.Equals(_connectedBssid, bssid, StringComparison.OrdinalIgnoreCase);
    /// <summary>Access-point (hardware) view: one row per inferred physical unit (DR-0013).</summary>
    public ObservableCollection<UnitGroupItem> UnitGroups { get; } = new();
    public PhysicalUnitGrouper? UnitGrouper => _units;

    private ExplorerGrouping _grouping = ExplorerGrouping.Network;
    public ExplorerGrouping Grouping
    {
        get => _grouping;
        set
        {
            if (value == ExplorerGrouping.AccessPoint && _units == null) value = ExplorerGrouping.Network;
            _grouping = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnitView));
            OnPropertyChanged(nameof(IsNetworkView));
            OnPropertyChanged(nameof(GroupingIndex));
            Refresh();
        }
    }
    public IReadOnlyList<ExplorerGrouping> GroupingOptions { get; } = new[] { ExplorerGrouping.Network, ExplorerGrouping.AccessPoint };
    /// <summary>Combo-box binding: 0 = Network, 1 = Access point.</summary>
    public int GroupingIndex
    {
        get => (int)_grouping;
        set { Grouping = value == 1 ? ExplorerGrouping.AccessPoint : ExplorerGrouping.Network; OnPropertyChanged(); }
    }
    public bool IsUnitView => _grouping == ExplorerGrouping.AccessPoint;
    public bool IsNetworkView => !IsUnitView;

    // ---- filters (chips), search, sort ----
    private string _search = "";
    public string SearchText { get => _search; set { _search = value ?? ""; OnPropertyChanged(); Refresh(); } }
    private bool _f24, _f5, _f6, _fFair, _fSecure, _fSelected;
    public bool Filter24 { get => _f24; set { _f24 = value; OnPropertyChanged(); Refresh(); } }
    public bool Filter5 { get => _f5; set { _f5 = value; OnPropertyChanged(); Refresh(); } }
    public bool Filter6 { get => _f6; set { _f6 = value; OnPropertyChanged(); Refresh(); } }
    public bool FilterFairOrBetter { get => _fFair; set { _fFair = value; OnPropertyChanged(); Refresh(); } }
    public bool FilterSecured { get => _fSecure; set { _fSecure = value; OnPropertyChanged(); Refresh(); } }
    public bool FilterSelectedOnly { get => _fSelected; set { _fSelected = value; OnPropertyChanged(); Refresh(); } }
    private ExplorerSort _sort = ExplorerSort.Signal;
    public ExplorerSort Sort { get => _sort; set { _sort = value; OnPropertyChanged(); Refresh(); } }
    public IReadOnlyList<ExplorerSort> SortOptions { get; } = new[] { ExplorerSort.Signal, ExplorerSort.Name, ExplorerSort.Channel };

    public string CountText { get; private set; } = "";
    public string ScopeText { get; private set; } = "";
    public string EmptyText { get; private set; } = "";
    public bool HasEmptyText => !string.IsNullOrEmpty(EmptyText);
    public bool IsLocked => _selection.IsLocked;

    public ICommand ToggleGroupCommand { get; }
    public ICommand ToggleApCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SelectShownCommand { get; }
    public ICommand ToggleUnitCommand { get; }
    public ICommand ToggleUnitExpandCommand { get; }

    // ---- physical units (DR-0013): selection and the user's corrections ----
    public void ToggleUnit(UnitGroupItem u)
    {
        if (_units?.Get(u.Id) is { } unit && _selection.ToggleUnit(unit)) _expandedUnits.Add(u.Id);
    }

    public bool CanEditUnits => _units != null && !_selection.IsLocked;

    public void RenameUnit(string unitId, string? name) { if (CanEditUnits) _units!.Rename(unitId, name); }

    /// <summary>"This is a separate device": split one BSSID into its own unit.</summary>
    public void SplitAp(string bssid) { if (CanEditUnits) _units!.MoveToNewUnit(bssid); }

    /// <summary>"Belongs to that device": move one BSSID into another unit.</summary>
    public void MoveAp(string bssid, string unitId) { if (CanEditUnits) _units!.MoveToUnit(bssid, unitId); }

    public void ResetUnit(string unitId) { if (CanEditUnits) _units!.Reset(unitId); }

    /// <summary>Units a BSSID could be moved into (all other current units, strongest first).</summary>
    public IReadOnlyList<(string Id, string Name)> MoveTargets(string bssid)
    {
        if (_units == null) return Array.Empty<(string, string)>();
        string? current = _units.UnitOf(bssid);
        return _units.Units.Where(u => u.Id != current).Select(u => (u.Id, u.Name)).ToList();
    }

    public IReadOnlyList<AccessPoint> AllAccessPoints => _all;

    /// <summary>Feed one scan. Fresh entries extend the sparkline history (max 12 values per BSSID).</summary>
    public void Update(IReadOnlyList<AccessPoint> aps, bool cycleFresh)
    {
        _all = aps.ToList();
        foreach (var ap in aps)
        {
            bool fresh = ap.BeaconAgeMs is double age ? age <= 10_000 : cycleFresh;
            if (!fresh) continue;
            if (!_history.TryGetValue(ap.BSSID, out var q)) _history[ap.BSSID] = q = new Queue<int>();
            q.Enqueue(ap.RSSI);
            while (q.Count > 12) q.Dequeue();
        }
        Refresh();
    }

    public IReadOnlyList<int> HistoryOf(string bssid) => _history.TryGetValue(bssid, out var q) ? q.ToList() : Array.Empty<int>();

    public IReadOnlyCollection<string> BssidsOf(string ssid) => _all.Where(a => a.SSID == ssid).Select(a => a.BSSID).ToList();

    public void ToggleGroup(NetworkGroupItem g)
    {
        if (_selection.ToggleGroup(g.Ssid, BssidsOf(g.Ssid)) && !g.IsHidden)
        {
            _expanded.Add(g.Ssid);
        }
    }

    public void ToggleAp(NetworkApItem a) => _selection.ToggleAp(a.Ssid, a.Bssid, BssidsOf(a.Ssid));

    /// <summary>Select every shown network: visible SSID groups whole, hidden networks per BSSID ("Select shown").</summary>
    private void SelectShown()
    {
        foreach (var g in Groups.ToList())
        {
            if (g.IsHidden)
            {
                foreach (var a in g.Aps) if (!a.IsSelected) _selection.ToggleAp(a.Ssid, a.Bssid, BssidsOf(a.Ssid));
            }
            else if (!_selection.IsWholeSsid(g.Ssid))
            {
                _selection.ToggleGroup(g.Ssid, BssidsOf(g.Ssid));
            }
        }
    }

    private bool PassesFilters(AccessPoint a, string security)
    {
        bool anyBand = _f24 || _f5 || _f6;
        if (anyBand && !((_f24 && a.Band == "2.4 GHz") || (_f5 && a.Band == "5 GHz") || (_f6 && a.Band == "6 GHz"))) return false;
        if (_fFair && a.RSSI < -70) return false;
        if (_fSecure && (security == "Open" || security.StartsWith("OWE", StringComparison.Ordinal))) return false;
        if (_fSelected && !_selection.IsExplicitlySelected(a.SSID, a.BSSID)) return false;
        if (_search.Length > 0)
        {
            var q = _search.Trim();
            if (!(a.SSID.Contains(q, StringComparison.OrdinalIgnoreCase) || a.BSSID.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || (a.VendorOUI ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    public string? IdentityColorFor(string ssid)
    {
        int slot = _selection.FocusSlot(ssid, _all);
        return slot >= 0 ? IdentityColors[slot] : null;
    }

    /// <summary>Rebuild the visible tree from the latest scan, reusing item instances by key.</summary>
    public void Refresh()
    {
        if (_grouping == ExplorerGrouping.AccessPoint && _units != null)
        {
            RefreshUnits();
            return;
        }
        var bySsid = _all.GroupBy(a => a.SSID).ToList();
        var visible = new List<NetworkGroupItem>();
        foreach (var grp in bySsid)
        {
            var shownAps = grp.Where(a => PassesFilters(a, a.SecurityType)).ToList();
            if (shownAps.Count == 0) continue;
            if (!_groups.TryGetValue(grp.Key, out var item))
            {
                _groups[grp.Key] = item = new NetworkGroupItem(grp.Key) { IsExpanded = _expanded.Contains(grp.Key) };
            }
            var state = _selection.StateOf(grp.Key, grp.Select(a => a.BSSID).ToList());
            item.Update(grp.ToList(), shownAps, state, _selection.IsWholeSsid(grp.Key), IdentityColorFor(grp.Key),
                _selection.IsExplicitlySelected, HistoryOf, _selection.IsLocked, IsConnected);
            visible.Add(item);
        }

        visible = _sort switch
        {
            ExplorerSort.Name => visible.OrderBy(g => g.IsHidden).ThenBy(g => g.Ssid, StringComparer.OrdinalIgnoreCase).ToList(),
            ExplorerSort.Channel => visible.OrderBy(g => g.LowestChannel).ThenByDescending(g => g.BestRssi).ToList(),
            _ => visible.OrderByDescending(g => g.BestRssi).ThenBy(g => g.Ssid, StringComparer.Ordinal).ToList()
        };

        // reconcile ObservableCollection order without recreating items
        for (int i = 0; i < visible.Count; i++)
        {
            int existing = Groups.IndexOf(visible[i]);
            if (existing < 0) Groups.Insert(i, visible[i]);
            else if (existing != i) Groups.Move(existing, i);
        }
        while (Groups.Count > visible.Count) Groups.RemoveAt(Groups.Count - 1);

        int ssidCount = bySsid.Count, apCount = _all.Count;
        int sel = _all.Count(a => _selection.IsExplicitlySelected(a.SSID, a.BSSID));
        CountText = $"{ssidCount} SSIDs · {apCount} APs · {sel} selected";
        ScopeText = (_selection.IsLocked ? "LOCKED (test running) · " : "") + _selection.Describe(s => _all.Count(a => a.SSID == s));
        EmptyText = _all.Count == 0 ? "No networks yet. Choose a Wi-Fi adapter; scanning starts automatically."
                  : visible.Count == 0 ? "No networks match these filters. Clear a chip or the search." : "";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(IsLocked));
    }

    /// <summary>Access-point view: units from the grouper, member rows from the latest scan, same filters and sort.</summary>
    private void RefreshUnits()
    {
        var byBssid = _all.GroupBy(a => a.BSSID, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var visible = new List<UnitGroupItem>();
        foreach (var unit in _units!.Units)
        {
            var members = unit.Members.Where(m => byBssid.ContainsKey(m.Bssid)).Select(m => byBssid[m.Bssid]).ToList();
            var shown = members.Where(a => PassesFilters(a, a.SecurityType) || unit.Name.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase) && _search.Length > 0 && PassesNonSearch(a)).ToList();
            if (members.Count == 0 || shown.Count == 0) continue;
            if (!_unitItems.TryGetValue(unit.Id, out var item))
                _unitItems[unit.Id] = item = new UnitGroupItem(unit.Id) { IsExpanded = _expandedUnits.Contains(unit.Id) };
            item.Update(unit, members, shown, _selection.StateOfUnit(unit), _selection.IsUnitSelected(unit.Id), _units.Units,
                IdentityColorFor, _selection.IsExplicitlySelected, HistoryOf, _selection.IsLocked, IsConnected);
            visible.Add(item);
        }
        visible = _sort switch
        {
            ExplorerSort.Name => visible.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ExplorerSort.Channel => visible.OrderBy(u => u.LowestChannel).ThenByDescending(u => u.BestRssi).ToList(),
            _ => visible.OrderByDescending(u => u.BestRssi).ThenBy(u => u.Id, StringComparer.Ordinal).ToList()
        };
        for (int i = 0; i < visible.Count; i++)
        {
            int existing = UnitGroups.IndexOf(visible[i]);
            if (existing < 0) UnitGroups.Insert(i, visible[i]);
            else if (existing != i) UnitGroups.Move(existing, i);
        }
        while (UnitGroups.Count > visible.Count) UnitGroups.RemoveAt(UnitGroups.Count - 1);

        int unitCount = _units.Units.Count(u => u.Members.Any(m => byBssid.ContainsKey(m.Bssid)));
        int multi = _units.Units.Count(u => u.Members.Count(m => byBssid.ContainsKey(m.Bssid)) > 1);
        int sel = _all.Count(a => _selection.IsExplicitlySelected(a.SSID, a.BSSID));
        CountText = $"{unitCount} access points ({multi} with several BSSIDs) · {_all.Count} BSSIDs · {sel} selected";
        ScopeText = (_selection.IsLocked ? "LOCKED (test running) · " : "") + _selection.Describe(s => _all.Count(a => a.SSID == s));
        EmptyText = _all.Count == 0 ? "No networks yet. Choose a Wi-Fi adapter; scanning starts automatically."
                  : visible.Count == 0 ? "No access points match these filters. Clear a chip or the search." : "";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanEditUnits));
    }

    private bool PassesNonSearch(AccessPoint a)
    {
        var saved = _search;
        _search = "";
        try { return PassesFilters(a, a.SecurityType); } finally { _search = saved; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One inferred physical access point (DR-0013) in the explorer's hardware view.</summary>
public class UnitGroupItem : INotifyPropertyChanged
{
    public UnitGroupItem(string id) => Id = id;

    public string Id { get; }
    public string Name { get; private set; } = "";
    public ObservableCollection<NetworkApItem> Aps { get; } = new();
    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { _expanded = value; Notify(nameof(IsExpanded)); Notify(nameof(CaretGlyph)); } }
    public string CaretGlyph => _expanded ? "▾" : "▸";

    public UnitConfidence Confidence { get; private set; }
    public string ConfidenceText => Confidence switch
    {
        UnitConfidence.High => "HIGH",
        UnitConfidence.Medium => "MEDIUM",
        UnitConfidence.Manual => "MANUAL",
        _ => "SINGLE"
    };
    /// <summary>Badge colours: status-free (identity, not good/bad), legible on the HUD background.</summary>
    public string ConfidenceBrush => Confidence switch
    {
        UnitConfidence.High => "#FF8800",
        UnitConfidence.Medium => "#C2B5A3",
        UnitConfidence.Manual => "#3987E5",
        _ => "#7A6A55"
    };
    public string EvidenceText { get; private set; } = "";
    public string SubText { get; private set; } = "";
    public string MeshText { get; private set; } = "";
    public bool IsMeshCandidate { get; private set; }
    public int BestRssi { get; private set; }
    public int LowestChannel { get; private set; }
    public int Bars { get; private set; }
    public string BarsOn => "▂▄▆█"[..Bars];
    public string BarsOff => "▂▄▆█"[Bars..];
    public string RssiText => $"{BestRssi} dBm";
    public string TierText { get; private set; } = "";
    public string TierBrush { get; private set; } = "#AA4400";

    public bool? CheckState { get; private set; }
    public string CheckGlyph => CheckState == true ? "✓" : CheckState == null ? "–" : "";
    public bool CanToggle { get; private set; }
    public bool IsWhole { get; private set; }
    public string CheckToolTip => IsWhole ? "Whole access point selected: every SSID and radio it broadcasts."
        : "Select this access point: every SSID and radio it broadcasts (hidden ones included).";
    public bool IsSelectedAny => CheckState != false;

    public bool IsConnected { get; private set; }

    internal void Update(PhysicalUnit unit, List<AccessPoint> members, List<AccessPoint> shown, GroupSelectionState state, bool whole,
        IReadOnlyList<PhysicalUnit> allUnits, Func<string, string?> identity, Func<string, string, bool> isSelected,
        Func<string, IReadOnlyList<int>> history, bool locked, Func<string, bool>? connected = null)
    {
        IsConnected = connected != null && unit.Members.Any(m => connected(m.Bssid));
        Name = unit.Name;
        Confidence = unit.Confidence;
        BestRssi = members.Max(a => a.RSSI);
        LowestChannel = members.Min(a => a.Channel);
        (TierText, TierBrush) = NetworkGroupItem.Tier(BestRssi);
        Bars = NetworkGroupItem.BarsFor(BestRssi);
        int radios = unit.Radios.Count, ssids = unit.Members.Select(m => m.Ssid).Distinct().Count(), bssids = unit.Members.Count();
        string bands = string.Join(" / ", unit.Radios.Select(r => r.Band).Distinct().OrderBy(b => b));
        SubText = $"{radios} radio{(radios == 1 ? "" : "s")} · {ssids} SSID{(ssids == 1 ? "" : "s")} · {bssids} BSSID{(bssids == 1 ? "" : "s")} · {bands}";
        IsMeshCandidate = unit.IsMeshCandidate;
        var names = unit.SameSsidUnitIds.Select(id => allUnits.FirstOrDefault(u => u.Id == id)?.Name).Where(n => n != null).ToList();
        MeshText = IsMeshCandidate ? $"Same SSID as {string.Join(", ", names)}: mesh node or extender candidate" : "";
        EvidenceText = $"{ConfidenceText} confidence (inferred; Windows does not report device identity)\n" + string.Join("\n", unit.Evidence.Select(e => "• " + e))
                       + "\nRight-click to rename, split or reset this grouping.";
        CheckState = state switch { GroupSelectionState.All => true, GroupSelectionState.Partial => null, _ => false };
        IsWhole = whole;
        CanToggle = !locked;

        var ordered = shown.OrderBy(a => a.Band).ThenByDescending(a => a.RSSI).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var ap = ordered[i];
            var item = Aps.FirstOrDefault(x => string.Equals(x.Bssid, ap.BSSID, StringComparison.OrdinalIgnoreCase));
            if (item == null) { item = new NetworkApItem(ap.SSID, ap.BSSID); Aps.Insert(Math.Min(i, Aps.Count), item); }
            else if (Aps.IndexOf(item) != i) Aps.Move(Aps.IndexOf(item), i);
            item.Update(ap, isSelected(ap.SSID, ap.BSSID), identity(ap.SSID), history(ap.BSSID), !locked, connected?.Invoke(ap.BSSID) == true);
        }
        while (Aps.Count > ordered.Count) Aps.RemoveAt(Aps.Count - 1);
        Notify(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class NetworkGroupItem : INotifyPropertyChanged
{
    public NetworkGroupItem(string ssid) => Ssid = ssid;

    public string Ssid { get; }
    public string DisplayName => NetworkSelection.IsHiddenSsid(Ssid) ? "<Hidden SSID>" : Ssid;
    public bool IsHidden => NetworkSelection.IsHiddenSsid(Ssid);
    public ObservableCollection<NetworkApItem> Aps { get; } = new();
    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { _expanded = value; Notify(nameof(IsExpanded)); Notify(nameof(CaretGlyph)); } }
    public string CaretGlyph => _expanded ? "▾" : "▸";

    public int ApCount { get; private set; }
    public int BestRssi { get; private set; }
    public int LowestChannel { get; private set; }
    public string Security { get; private set; } = "";
    public string Vendor { get; private set; } = "";
    public string BandsText { get; private set; } = "";
    public string SubText { get; private set; } = "";
    public string TierText { get; private set; } = "";
    public string TierBrush { get; private set; } = "#AA4400";
    public int Bars { get; private set; }
    public string BarsOn => "▂▄▆█"[..Bars];
    public string BarsOff => "▂▄▆█"[Bars..];
    public string RssiText => $"{BestRssi} dBm";

    /// <summary>true = all / whole SSID, null = partial, false = none.</summary>
    public bool? CheckState { get; private set; }
    public string CheckGlyph => CheckState == true ? "✓" : CheckState == null ? "–" : "";
    public bool IsWhole { get; private set; }
    public bool CanToggle { get; private set; }
    public string CheckToolTip => IsHidden ? "Hidden networks can only be selected per access point (BSSID)."
        : IsWhole ? "Whole network selected: follows access points that appear later." : "Select every access point of this network (follows new ones).";
    public string? SwatchBrush { get; private set; }
    public bool HasSwatch => SwatchBrush != null;
    public bool IsSelectedAny => CheckState != false;

    public bool IsConnected { get; private set; }

    internal void Update(List<AccessPoint> all, List<AccessPoint> shown, GroupSelectionState state, bool whole, string? identity,
        Func<string, string, bool> isSelected, Func<string, IReadOnlyList<int>> history, bool locked, Func<string, bool>? connected = null)
    {
        IsConnected = connected != null && all.Any(a => connected(a.BSSID));
        ApCount = all.Count;
        BestRssi = all.Max(a => a.RSSI);
        LowestChannel = all.Min(a => a.Channel);
        Security = all.GroupBy(a => a.SecurityType).OrderByDescending(g => g.Count()).First().Key;
        Vendor = all.Select(a => a.VendorOUI).FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != "Unknown Vendor") ?? "Unknown Vendor";
        BandsText = string.Join(" / ", all.Select(a => a.Band).Distinct().OrderBy(b => b));
        SubText = $"{ApCount} AP{(ApCount == 1 ? "" : "s")} · {Security} · {BandsText} · {Vendor}";
        (TierText, TierBrush) = Tier(BestRssi);
        Bars = BarsFor(BestRssi);
        CheckState = state switch { GroupSelectionState.All => true, GroupSelectionState.Partial => null, _ => false };
        IsWhole = whole;
        CanToggle = !IsHidden && !locked;
        SwatchBrush = state != GroupSelectionState.None || identity != null ? identity ?? NetworkExplorerViewModel.ContextColor : null;

        var ordered = shown.OrderByDescending(a => a.RSSI).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var ap = ordered[i];
            var item = Aps.FirstOrDefault(x => string.Equals(x.Bssid, ap.BSSID, StringComparison.OrdinalIgnoreCase));
            if (item == null) { item = new NetworkApItem(ap.SSID, ap.BSSID); Aps.Insert(Math.Min(i, Aps.Count), item); }
            else if (Aps.IndexOf(item) != i) Aps.Move(Aps.IndexOf(item), i);
            item.Update(ap, isSelected(ap.SSID, ap.BSSID) || whole, identity, history(ap.BSSID), !locked, connected?.Invoke(ap.BSSID) == true);
        }
        while (Aps.Count > ordered.Count) Aps.RemoveAt(Aps.Count - 1);
        Notify(string.Empty);
    }

    internal static (string, string) Tier(int rssi) => rssi switch
    {
        >= -55 => ("EXCELLENT", "#0CA30C"),
        >= -67 => ("GOOD", "#0CA30C"),
        >= -70 => ("FAIR", "#FAB219"),
        >= -80 => ("WEAK", "#EC835A"),
        _ => ("UNUSABLE", "#D03B3B")
    };

    internal static int BarsFor(int rssi) => rssi >= -55 ? 4 : rssi >= -67 ? 3 : rssi >= -75 ? 2 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class NetworkApItem : INotifyPropertyChanged
{
    public NetworkApItem(string ssid, string bssid) { Ssid = ssid; Bssid = bssid; }
    public string Ssid { get; }
    public string Bssid { get; }
    public string DisplaySsid => NetworkSelection.IsHiddenSsid(Ssid) ? "<Hidden SSID>" : Ssid;
    public int Rssi { get; private set; }
    public int Channel { get; private set; }
    public string Band { get; private set; } = "";
    public string WidthText { get; private set; } = "";
    public string DetailText { get; private set; } = "";
    public string TierText { get; private set; } = "";
    public string TierBrush { get; private set; } = "#AA4400";
    public int Bars { get; private set; }
    public string BarsOn => "▂▄▆█"[..Bars];
    public string BarsOff => "▂▄▆█"[Bars..];
    public string RssiText => $"{Rssi} dBm";
    public bool IsSelected { get; private set; }
    public string CheckGlyph => IsSelected ? "✓" : "";
    public bool CanToggle { get; private set; }
    public string SparkPoints { get; private set; } = "";
    public string SparkBrush { get; private set; } = "#C2B5A3";
    public string ToolTipText { get; private set; } = "";

    public bool IsConnected { get; private set; }

    internal void Update(AccessPoint ap, bool selected, string? identity, IReadOnlyList<int> history, bool canToggle, bool connected = false)
    {
        IsConnected = connected;
        Rssi = ap.RSSI;
        Channel = ap.Channel;
        Band = ap.Band;
        WidthText = ap.ChannelWidth is int w ? $"{w} MHz" : "width ?";
        DetailText = $"ch {ap.Channel} · {ap.Band} · {WidthText}";
        (TierText, TierBrush) = NetworkGroupItem.Tier(ap.RSSI);
        Bars = NetworkGroupItem.BarsFor(ap.RSSI);
        IsSelected = selected;
        CanToggle = canToggle;
        SparkBrush = identity ?? "#C2B5A3";
        SparkPoints = Spark(history);
        ToolTipText = $"{(NetworkSelection.IsHiddenSsid(ap.SSID) ? "<Hidden SSID>" : ap.SSID)}\n{ap.BSSID} · {ap.VendorOUI}\nch {ap.Channel} · {ap.Band} · {WidthText}\n" +
                      $"RSSI {ap.RSSI} dBm ({TierText}) · quality {ap.Quality}% · SNR est. ~{ap.SNR} dB\nSecurity: {ap.SecurityType}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal static string Spark(IReadOnlyList<int> values)
    {
        if (values.Count < 2) return "";
        int lo = values.Min() - 1, hi = values.Max() + 1;
        return string.Join(" ", values.Select((v, i) => string.Create(CultureInfo.InvariantCulture,
            $"{i * 52.0 / (values.Count - 1) + 1:0.0},{15 - (v - lo) * 13.0 / (hi - lo):0.0}")));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>ICommand with a parameter (RelayCommand in this project is parameterless).</summary>
public class ParamCommand : ICommand
{
    private readonly Action<object?> _execute;
    public ParamCommand(Action<object?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
