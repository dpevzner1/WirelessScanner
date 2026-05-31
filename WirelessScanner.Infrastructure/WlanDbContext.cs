using System;
using Microsoft.EntityFrameworkCore;

namespace WirelessScanner.Infrastructure;

public class WlanDbContext : DbContext
{
    public DbSet<DbSessionRecord> Sessions { get; set; } = null!;
    public DbSet<DbSampleRecord> Samples { get; set; } = null!;
    public DbSet<DbSettingRecord> Settings { get; set; } = null!;

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
            optionsBuilder.UseSqlite("Data Source=wireless_scanner.db");
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
