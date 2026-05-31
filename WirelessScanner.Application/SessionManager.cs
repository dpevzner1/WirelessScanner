using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

public class SessionManager : ISessionManager
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, int> _lastRssiByBssid = new();
    
    private CaptureSession? _activeSession;
    private string _activeSurveyPoint = "Default";

    public CaptureSession? ActiveSession => _activeSession;

    public string ActiveSurveyPoint
    {
        get => _activeSurveyPoint;
        set => _activeSurveyPoint = string.IsNullOrWhiteSpace(value) ? "Default" : value.Trim();
    }

    public event Action<CaptureSession>? SessionStateChanged;
    public event Action<TelemetrySample>? SampleLogged;

    public SessionManager(ISessionRepository sessionRepository, IAppLogger logger)
    {
        _sessionRepository = sessionRepository;
        _logger = logger;
    }

    public CaptureSession StartSession(string name, string scope, string facility, NetworkInterfaceInfo device, CaptureSessionMode mode)
    {
        if (_activeSession != null && _activeSession.Status == CaptureSessionStatus.Active)
        {
            throw new InvalidOperationException("A session is already actively running.");
        }

        _lastRssiByBssid.Clear();
        
        _activeSession = new CaptureSession(
            SessionId: Guid.NewGuid(),
            Name: string.IsNullOrWhiteSpace(name) ? $"Session_{DateTime.Now:yyyyMMdd_HHmmss}" : name.Trim(),
            Scope: string.IsNullOrWhiteSpace(scope) ? "All SSIDs" : scope.Trim(),
            FacilityName: string.IsNullOrWhiteSpace(facility) ? "General Facility" : facility.Trim(),
            InterfaceId: device.Id,
            InterfaceName: device.Name,
            StartTime: DateTime.UtcNow,
            EndTime: null,
            Mode: mode,
            Status: CaptureSessionStatus.Active
        );

        // Run fire-and-forget DB write
        Task.Run(async () =>
        {
            try
            {
                await _sessionRepository.SaveSessionAsync(_activeSession);
            }
            catch (Exception ex)
            {
                HandleScanError(ex);
            }
        });

        SessionStateChanged?.Invoke(_activeSession);
        return _activeSession;
    }

    public void PauseSession()
    {
        if (_activeSession == null || _activeSession.Status != CaptureSessionStatus.Active) return;

        _activeSession = _activeSession with { Status = CaptureSessionStatus.Paused };
        
        Task.Run(async () =>
        {
            try
            {
                await _sessionRepository.UpdateSessionStatusAsync(_activeSession.SessionId, _activeSession.Status, null);
            }
            catch (Exception ex)
            {
                HandleScanError(ex);
            }
        });

        SessionStateChanged?.Invoke(_activeSession);
    }

    public void ResumeSession()
    {
        if (_activeSession == null || _activeSession.Status != CaptureSessionStatus.Paused) return;

        _activeSession = _activeSession with { Status = CaptureSessionStatus.Active };
        
        Task.Run(async () =>
        {
            try
            {
                await _sessionRepository.UpdateSessionStatusAsync(_activeSession.SessionId, _activeSession.Status, null);
            }
            catch (Exception ex)
            {
                HandleScanError(ex);
            }
        });

        SessionStateChanged?.Invoke(_activeSession);
    }

    public void StopSession()
    {
        if (_activeSession == null || _activeSession.Status == CaptureSessionStatus.Completed) return;

        _activeSession = _activeSession with 
        { 
            Status = CaptureSessionStatus.Completed,
            EndTime = DateTime.UtcNow 
        };
        
        Task.Run(async () =>
        {
            try
            {
                await _sessionRepository.UpdateSessionStatusAsync(_activeSession.SessionId, _activeSession.Status, _activeSession.EndTime);
            }
            catch (Exception ex)
            {
                HandleScanError(ex);
            }
        });

        SessionStateChanged?.Invoke(_activeSession);
    }

    public double CalculateJitterFor(string bssid, int rssi)
    {
        if (string.IsNullOrWhiteSpace(bssid)) return 0.0;

        double jitter = 0.0;
        
        // Jitter = |RSSI_t - RSSI_t-1|
        if (_lastRssiByBssid.TryGetValue(bssid, out int lastRssi))
        {
            jitter = Math.Abs(rssi - lastRssi);
        }
        
        _lastRssiByBssid[bssid] = rssi;
        return jitter;
    }

    public void SaveSample(TelemetrySample sample)
    {
        if (_activeSession == null || _activeSession.Status != CaptureSessionStatus.Active) return;

        Task.Run(async () =>
        {
            try
            {
                await _sessionRepository.SaveSampleAsync(sample, _activeSession.SessionId);
                SampleLogged?.Invoke(sample);
            }
            catch (Exception ex)
            {
                HandleScanError(ex);
            }
        });
    }

    public void HandleScanError(Exception ex)
    {
        _logger.Error($"SessionManager Error: {ex.Message}", ex);
    }
}
