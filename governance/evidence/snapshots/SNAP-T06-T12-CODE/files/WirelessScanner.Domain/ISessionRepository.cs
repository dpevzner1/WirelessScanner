using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WirelessScanner.Domain;

public interface ISessionRepository
{
    Task SaveSessionAsync(CaptureSession session);
    Task SaveSampleAsync(TelemetrySample sample, Guid sessionId);

    // Batched insert of one scan cycle's samples (DR-0003). Default: sequential single inserts.
    async Task SaveSamplesAsync(IReadOnlyList<TelemetrySample> samples, Guid sessionId)
    {
        foreach (var sample in samples)
        {
            await SaveSampleAsync(sample, sessionId);
        }
    }
    Task UpdateSessionStatusAsync(Guid sessionId, CaptureSessionStatus status, DateTime? endTime);
    
    Task<CaptureSession?> GetSessionAsync(Guid sessionId);
    Task<IEnumerable<TelemetrySample>> GetSamplesAsync(Guid sessionId);
    Task<IEnumerable<CaptureSession>> GetAllSessionsAsync();
    Task<IEnumerable<CaptureSession>> GetSessionsByDateAsync(DateTime start, DateTime end);
    Task DeleteSessionAsync(Guid sessionId);
    Task PurgeSessionsByAgeAsync(DateTime threshold);
}
