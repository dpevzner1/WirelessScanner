using System;
using System.Collections.Generic;

namespace WirelessScanner.Domain;

/// <summary>
/// Outcome of one acquisition cycle (DR-0003).
/// Fresh: Windows confirmed a new scan completed before the list was read.
/// Stale: no completion was confirmed (timeout or driver scan_fail); the list may be cached.
/// Failed: the list could not be read (adapter missing, service stopped, API error).
/// Skipped: no acquisition was attempted (e.g. paused).
/// </summary>
public enum ScanCycleStatus
{
    Fresh,
    Stale,
    Failed,
    Skipped
}

public record ScanCycleResult(
    ScanCycleStatus Status,
    DateTime RequestedUtc,
    DateTime CompletedUtc,
    double DurationMs,
    IReadOnlyList<AccessPoint> AccessPoints,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static ScanCycleResult Failure(DateTime requestedUtc, string code, string message) =>
        new(ScanCycleStatus.Failed, requestedUtc, DateTime.UtcNow, (DateTime.UtcNow - requestedUtc).TotalMilliseconds,
            Array.Empty<AccessPoint>(), code, message);
}

/// <summary>Translates Win32/WLAN error codes into actionable operator text (finding F13).</summary>
public static class WlanErrorText
{
    public static string Describe(uint code) => code switch
    {
        0 => "Success",
        2 => "The WLAN interface was not found (ERROR_FILE_NOT_FOUND). Re-select the adapter.",
        5 => "Access denied (ERROR_ACCESS_DENIED). Run the application as Administrator.",
        21 => "The adapter is not ready (ERROR_NOT_READY). Check that Wi-Fi is switched on and airplane mode is off.",
        50 => "The adapter or driver does not support this request (ERROR_NOT_SUPPORTED).",
        87 => "Invalid parameter (ERROR_INVALID_PARAMETER). The selected adapter GUID may be stale; re-select the adapter.",
        1062 => "The WLAN AutoConfig service is not running (ERROR_SERVICE_NOT_ACTIVE). Start the 'WLAN AutoConfig' service.",
        1168 => "The adapter was not found (ERROR_NOT_FOUND). It may have been unplugged, disabled or re-enumerated; re-select the adapter.",
        1169 => "No matching device (ERROR_NO_MATCH).",
        _ => $"WLAN API error {code}."
    };
}
