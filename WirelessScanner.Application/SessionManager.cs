using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

public class SessionManager : ISessionManager
{
    private readonly ISessionRepository _sessionRepository;
    private readonly IAppLogger _logger;
    private readonly OrderedPersistenceWriter _writer;
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
        : this(sessionRepository, logger, new OrderedPersistenceWriter(logger))
    {
    }

    public SessionManager(ISessionRepository sessionRepository, IAppLogger logger, OrderedPersistenceWriter writer)
    {
        _sessionRepository = sessionRepository;
        _logger = logger;
        _writer = writer;
    }

    public CaptureSession StartSession(string name, string scope, string facility, NetworkInterfaceInfo device, CaptureSessionMode mode)
    {
        if (_activeSession != null && _activeSession.Status is CaptureSessionStatus.Active or CaptureSessionStatus.Paused)
        {
            throw new InvalidOperationException("A session is already running. Stop it before starting a new one.");
        }

        _lastRssiByBssid.Clear();

        var session = new CaptureSession(
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
        _activeSession = session;

        // Enqueued first: every sample of this session is written after the session row (F02).
        _writer.Enqueue($"save-session {session.SessionId}", () => _sessionRepository.SaveSessionAsync(session));

        SessionStateChanged?.Invoke(session);
        return session;
    }

    public void PauseSession() => Transition(CaptureSessionStatus.Active, CaptureSessionStatus.Paused, endTime: null);

    public void ResumeSession() => Transition(CaptureSessionStatus.Paused, CaptureSessionStatus.Active, endTime: null);

    public void StopSession()
    {
        if (_activeSession == null || _activeSession.Status == CaptureSessionStatus.Completed) return;
        Transition(_activeSession.Status, CaptureSessionStatus.Completed, endTime: DateTime.UtcNow);
    }

    private void Transition(CaptureSessionStatus from, CaptureSessionStatus to, DateTime? endTime)
    {
        if (_activeSession == null || _activeSession.Status != from) return;

        var updated = _activeSession with { Status = to, EndTime = endTime ?? _activeSession.EndTime };
        _activeSession = updated;
        _writer.Enqueue($"status {updated.SessionId} {to}",
            () => _sessionRepository.UpdateSessionStatusAsync(updated.SessionId, updated.Status, endTime));

        SessionStateChanged?.Invoke(updated);
    }

    /// <summary>
    /// Scan-to-scan RSSI variation in dB: |RSSI(t) - RSSI(t-1)| per BSSID.
    /// Historically labelled "jitter"; it is not packet latency jitter (F14, DR-0004).
    /// </summary>
    public double CalculateJitterFor(string bssid, int rssi)
    {
        if (string.IsNullOrWhiteSpace(bssid)) return 0.0;

        double variation = 0.0;
        if (_lastRssiByBssid.TryGetValue(bssid, out int lastRssi))
        {
            variation = Math.Abs(rssi - lastRssi);
        }

        _lastRssiByBssid[bssid] = rssi;
        return variation;
    }

    public void SaveSample(TelemetrySample sample) => SaveSamples(new[] { sample });

    public void SaveSamples(IReadOnlyList<TelemetrySample> samples)
    {
        var session = _activeSession;
        if (session == null || session.Status != CaptureSessionStatus.Active || samples.Count == 0) return;

        var batch = samples is List<TelemetrySample> list ? list.ToArray() : samples;
        _writer.Enqueue($"samples {session.SessionId} x{batch.Count}", async () =>
        {
            await _sessionRepository.SaveSamplesAsync(batch, session.SessionId);
            foreach (var sample in batch)
            {
                SampleLogged?.Invoke(sample);
            }
        });
    }

    public Task FlushAsync() => _writer.FlushAsync();

    public void HandleScanError(Exception ex)
    {
        _logger.Error($"SessionManager Error: {ex.Message}", ex);
    }
}
