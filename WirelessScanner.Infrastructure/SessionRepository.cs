using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WirelessScanner.Domain;

namespace WirelessScanner.Infrastructure;

public class SessionRepository : ISessionRepository
{
    private readonly IDbContextFactory<WlanDbContext> _contextFactory;

    public SessionRepository(IDbContextFactory<WlanDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
        
        // Ensure database exists and schema is initialized
        using var context = _contextFactory.CreateDbContext();
        context.Database.EnsureCreated();

        try
        {
            if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }
        }
        catch { }
    }

    public async Task SaveSessionAsync(CaptureSession session)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var record = new DbSessionRecord
        {
            SessionId = session.SessionId,
            Name = session.Name,
            Scope = session.Scope,
            FacilityName = session.FacilityName,
            InterfaceName = session.InterfaceName,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            Mode = (int)session.Mode,
            Status = (int)session.Status
        };

        context.Sessions.Add(record);
        await context.SaveChangesAsync();
    }

    public async Task SaveSampleAsync(TelemetrySample sample, Guid sessionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var record = new DbSampleRecord
        {
            SessionId = sessionId,
            Timestamp = sample.Timestamp,
            SurveyPoint = sample.SurveyPoint,
            BSSID = sample.BSSID,
            SSID = sample.SSID,
            Band = sample.Band,
            Channel = sample.Channel,
            RSSI = sample.RSSI,
            SNR = sample.SNR,
            Quality = sample.Quality,
            Jitter = sample.Jitter
        };

        context.Samples.Add(record);
        await context.SaveChangesAsync();
    }

    public async Task UpdateSessionStatusAsync(Guid sessionId, CaptureSessionStatus status, DateTime? endTime)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var sessionRecord = await context.Sessions.FindAsync(sessionId);
        if (sessionRecord != null)
        {
            sessionRecord.Status = (int)status;
            if (endTime.HasValue)
            {
                sessionRecord.EndTime = endTime.Value;
            }
            await context.SaveChangesAsync();
        }
    }

    public async Task<CaptureSession?> GetSessionAsync(Guid sessionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var record = await context.Sessions.FindAsync(sessionId);
        return record == null ? null : MapToDomain(record);
    }

    public async Task<IEnumerable<TelemetrySample>> GetSamplesAsync(Guid sessionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var records = await context.Samples
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.Timestamp)
            .ToListAsync();
            
        return records.Select(MapToDomain);
    }

    public async Task<IEnumerable<CaptureSession>> GetAllSessionsAsync()
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var records = await context.Sessions
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
            
        return records.Select(MapToDomain);
    }

    public async Task<IEnumerable<CaptureSession>> GetSessionsByDateAsync(DateTime start, DateTime end)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var records = await context.Sessions
            .Where(s => s.StartTime >= start && s.StartTime <= end)
            .OrderByDescending(s => s.StartTime)
            .ToListAsync();
            
        return records.Select(MapToDomain);
    }

    public async Task DeleteSessionAsync(Guid sessionId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var record = await context.Sessions.FindAsync(sessionId);
        if (record != null)
        {
            context.Sessions.Remove(record);
            await context.SaveChangesAsync();
            
            // Reclaim database space
            await context.Database.ExecuteSqlRawAsync("VACUUM;");
        }
    }

    public async Task PurgeSessionsByAgeAsync(DateTime threshold)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var records = await context.Sessions
            .Where(s => s.StartTime < threshold)
            .ToListAsync();
            
        if (records.Any())
        {
            context.Sessions.RemoveRange(records);
            await context.SaveChangesAsync();
            
            // Reclaim database space
            await context.Database.ExecuteSqlRawAsync("VACUUM;");
        }
    }

    private static CaptureSession MapToDomain(DbSessionRecord r)
    {
        return new CaptureSession(
            SessionId: r.SessionId,
            Name: r.Name,
            Scope: r.Scope,
            FacilityName: r.FacilityName,
            InterfaceId: Guid.Empty,
            InterfaceName: r.InterfaceName,
            StartTime: r.StartTime,
            EndTime: r.EndTime,
            Mode: (CaptureSessionMode)r.Mode,
            Status: (CaptureSessionStatus)r.Status
        );
    }

    private static TelemetrySample MapToDomain(DbSampleRecord r)
    {
        return new TelemetrySample(
            Timestamp: r.Timestamp,
            SurveyPoint: r.SurveyPoint,
            BSSID: r.BSSID,
            SSID: r.SSID,
            Band: r.Band,
            Channel: r.Channel,
            RSSI: r.RSSI,
            SNR: r.SNR,
            Quality: r.Quality,
            Jitter: r.Jitter,
            NoiseFloor: -95
        );
    }
}
