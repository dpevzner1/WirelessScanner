using System;
using System.Collections.Generic;

namespace WirelessScanner.Domain;

// Physical units (DR-0013): which BSSIDs/SSIDs are broadcast by the same access point / router.
// Windows does not report this, so it is INFERRED from evidence and always shown with its confidence and reasons.

public enum UnitConfidence
{
    /// <summary>Only one BSSID seen so far: nothing links it to anything else.</summary>
    Single,
    /// <summary>Linked by MAC family only (address pattern).</summary>
    Medium,
    /// <summary>Linked by a shared radio clock (TSF) or a shared WPS device identity.</summary>
    High,
    /// <summary>The user merged, split or moved members: the user's decision wins.</summary>
    Manual
}

/// <summary>One BSSID of a unit.</summary>
public sealed record UnitMember(string Bssid, string Ssid, string Band, int Channel, int Rssi);

/// <summary>One radio of a unit: BSSIDs sharing one TSF clock (or, without TSF evidence, one band + channel).</summary>
public sealed record UnitRadio(string Key, string Band, int Channel, IReadOnlyList<UnitMember> Members);

public sealed record PhysicalUnit(
    string Id,
    string Name,
    bool IsCustomName,
    UnitConfidence Confidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<UnitRadio> Radios,
    string? Vendor,
    string? Model,
    // Other units broadcasting one of this unit (visible) SSIDs: mesh node or extender candidates
    IReadOnlyList<string> SameSsidUnitIds)
{
    public IEnumerable<UnitMember> Members
    {
        get { foreach (var r in Radios) foreach (var m in r.Members) yield return m; }
    }
    public bool IsMeshCandidate => SameSsidUnitIds.Count > 0;
}

/// <summary>
/// The user's corrections, persisted in Settings ("PhysicalUnitOverrides") as JSON.
/// Assignments move a BSSID into a unit (an existing unit id, or "M-..." for a new manual unit); names rename units.
/// </summary>
public sealed class UnitOverrides
{
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A unit chosen as a saturation-test target: resolved at START (membership fixed for the test).</summary>
public sealed record SaturationUnitTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public UnitConfidence Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    /// <summary>Members with their radio key (BSSIDs on one radio should track together).</summary>
    public IReadOnlyList<SaturationUnitMember> Members { get; init; } = Array.Empty<SaturationUnitMember>();
}

public sealed record SaturationUnitMember(string Bssid, string Ssid, string Band, string RadioKey);

/// <summary>Per-radio result of a unit test.</summary>
public sealed record SaturationRadioSummary
{
    public string RadioKey { get; init; } = "";
    public string Band { get; init; } = "";
    public IReadOnlyList<string> Bssids { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Ssids { get; init; } = Array.Empty<string>();
    /// <summary>Mean |RSSI(a) − RSSI(b)| over cycles where both were observed, worst SSID pair; null with fewer than 2 SSIDs.</summary>
    public double? ConsistencyMeanDeltaDb { get; init; }
    public double? ConsistencyMaxDeltaDb { get; init; }
    /// <summary>Per BSSID: % of fresh cycles where the radio was up (another member observed) but this BSSID was missing.</summary>
    public IReadOnlyDictionary<string, double> SsidOnlyOutagePct { get; init; } = new Dictionary<string, double>();
}

public sealed record SaturationUnitSummary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public UnitConfidence Confidence { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public int BssidCount { get; init; }
    public int SsidCount { get; init; }
    /// <summary>% of fresh cycles in which at least one member was observed (the unit was on air).</summary>
    public double? UnitAvailabilityPct { get; init; }
    public double? BestMeanDbm { get; init; }
    public IReadOnlyList<SaturationRadioSummary> Radios { get; init; } = Array.Empty<SaturationRadioSummary>();
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}
