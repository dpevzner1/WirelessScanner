using System;
using Microsoft.EntityFrameworkCore;

namespace WirelessScanner.Infrastructure;

public class WlanDbContext : DbContext
{
    public DbSet<DbSessionRecord> Sessions { get; set; } = null!;
    public DbSet<DbSampleRecord> Samples { get; set; } = null!;
    public DbSet<DbSettingRecord> Settings { get; set; } = null!;

    // Signal Saturation Test (SPEC-SAT-001 §9). Cycles/observations are canonical; reports are derived.
    public DbSet<DbSaturationTest> SaturationTests { get; set; } = null!;
    public DbSet<DbSaturationCycle> SaturationCycles { get; set; } = null!;
    public DbSet<DbSaturationObservation> SaturationObservations { get; set; } = null!;
    public DbSet<DbSaturationMarker> SaturationMarkers { get; set; } = null!;

    public WlanDbContext()
    {
    }

    public WlanDbContext(DbContextOptions<WlanDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite(AppPaths.ConnectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbSessionRecord>()
            .HasKey(s => s.SessionId);
            
        modelBuilder.Entity<DbSessionRecord>()
            .HasIndex(s => new { s.Name, s.FacilityName, s.StartTime });

        modelBuilder.Entity<DbSampleRecord>()
            .HasKey(s => s.SampleId);

        modelBuilder.Entity<DbSampleRecord>()
            .HasOne(s => s.Session)
            .WithMany()
            .HasForeignKey(s => s.SessionId)
            .OnDelete(DeleteBehavior.Cascade); // Cascade purge child records

        modelBuilder.Entity<DbSampleRecord>()
            .HasIndex(s => s.SessionId);

        modelBuilder.Entity<DbSettingRecord>()
            .HasKey(s => s.Key);

        modelBuilder.Entity<DbSaturationTest>().HasKey(t => t.TestId);
        modelBuilder.Entity<DbSaturationTest>().HasIndex(t => t.StartTime);

        modelBuilder.Entity<DbSaturationCycle>().HasKey(c => c.Id);
        modelBuilder.Entity<DbSaturationCycle>().HasOne<DbSaturationTest>().WithMany().HasForeignKey(c => c.TestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DbSaturationCycle>().HasIndex(c => new { c.TestId, c.Sequence }).IsUnique();

        modelBuilder.Entity<DbSaturationObservation>().HasKey(o => o.Id);
        modelBuilder.Entity<DbSaturationObservation>().HasOne<DbSaturationTest>().WithMany().HasForeignKey(o => o.TestId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DbSaturationObservation>().HasIndex(o => new { o.TestId, o.CycleSequence });

        modelBuilder.Entity<DbSaturationMarker>().HasKey(m => m.Id);
        modelBuilder.Entity<DbSaturationMarker>().HasOne<DbSaturationTest>().WithMany().HasForeignKey(m => m.TestId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class DbSessionRecord
{
    public Guid SessionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string FacilityName { get; set; } = string.Empty;
    public string InterfaceName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Mode { get; set; }
    public int Status { get; set; }
}

public class DbSampleRecord
{
    public int SampleId { get; set; }
    public Guid SessionId { get; set; }
    public DbSessionRecord Session { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public string SurveyPoint { get; set; } = string.Empty;
    public string BSSID { get; set; } = string.Empty;
    public string SSID { get; set; } = string.Empty;
    public string Band { get; set; } = string.Empty;
    public int Channel { get; set; }
    public int RSSI { get; set; }
    public int SNR { get; set; }
    public int Quality { get; set; }
    public double Jitter { get; set; }
}

public class DbSettingRecord
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class DbSaturationTest
{
    public Guid TestId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Scope { get; set; }
    public string ConfigJson { get; set; } = string.Empty;
    public string InterfaceName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Facility { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int Status { get; set; }
    /// <summary>Measured active duration (schema 3, DR-0012); null for tests recorded before 1.3.</summary>
    public double? ActiveSeconds { get; set; }
}

public class DbSaturationCycle
{
    public long Id { get; set; }
    public Guid TestId { get; set; }
    public long Sequence { get; set; }
    public DateTime WallUtc { get; set; }
    public long ElapsedMs { get; set; }
    public int Status { get; set; }
    public double DurationMs { get; set; }
    public int BssCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DbSaturationObservation
{
    public long Id { get; set; }
    public Guid TestId { get; set; }
    public long CycleSequence { get; set; }
    public string Bssid { get; set; } = string.Empty;
    public string Ssid { get; set; } = string.Empty;
    public int Status { get; set; }
    public int? Rssi { get; set; }
    public int? LinkQuality { get; set; }
    public int? Channel { get; set; }
    public string? Band { get; set; }
    public int? ChannelWidth { get; set; }
    public double? BeaconAgeMs { get; set; }
    public int? CoChannelCount { get; set; }
    public int? OverlapCount { get; set; }
    // schema 3 (SAT-ANALYTICS-2, DR-0012); all nullable so older rows stay valid
    public string? Security { get; set; }
    public string? Vendor { get; set; }
    public string? PhyType { get; set; }
    public int? BeaconIntervalTu { get; set; }
    public double? CenterFrequencyMhz { get; set; }
    public int? StationCount { get; set; }
    public double? ChannelUtilizationPct { get; set; }
    public int? StrongestOverlapRssi { get; set; }
}

public class DbSaturationMarker
{
    public long Id { get; set; }
    public Guid TestId { get; set; }
    public DateTime WallUtc { get; set; }
    public long ElapsedMs { get; set; }
    public string Text { get; set; } = string.Empty;
}
