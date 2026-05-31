using System;
using System.Collections.Generic;

namespace WirelessScanner.Domain;

public enum CaptureSessionMode
{
    Roaming,
    Stationary
}

public enum CaptureSessionStatus
{
    Active,
    Paused,
    Completed,
    Error
}

public record CaptureSession(
    Guid SessionId,
    string Name,
    string Scope,
    string FacilityName,
    Guid InterfaceId,
    string InterfaceName,
    DateTime StartTime,
    DateTime? EndTime,
    CaptureSessionMode Mode,
    CaptureSessionStatus Status
);

public interface ISessionManager
{
    CaptureSession? ActiveSession { get; }
    string ActiveSurveyPoint { get; set; }
    
    // Starts a new tracking session
    CaptureSession StartSession(string name, string scope, string facility, NetworkInterfaceInfo device, CaptureSessionMode mode);
    
    // Control interfaces
    void PauseSession();
    void ResumeSession();
    void StopSession();

    // Diagnostics and state operations
    double CalculateJitterFor(string bssid, int rssi);
    void HandleScanError(Exception ex);
    void SaveSample(TelemetrySample sample);

    event Action<CaptureSession>? SessionStateChanged;
    event Action<TelemetrySample>? SampleLogged;
}
