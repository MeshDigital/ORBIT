using Microsoft.EntityFrameworkCore;
using System.IO;
using SLSKDONET.Models;
using Microsoft.Data.Sqlite; // Phase 1B

namespace SLSKDONET.Data;

public class AppDbContext : DbContext
{
    public DbSet<TrackEntity> Tracks { get; set; }
    public DbSet<LibraryEntryEntity> LibraryEntries { get; set; }
    public DbSet<PlaylistJobEntity> Projects { get; set; }
    public DbSet<PlaylistTrackEntity> PlaylistTracks { get; set; }
    public DbSet<PlaylistActivityLogEntity> ActivityLogs { get; set; }
    public DbSet<QueueItemEntity> QueueItems { get; set; }
    public DbSet<Entities.SpotifyMetadataCacheEntity> SpotifyMetadataCache { get; set; }
    public DbSet<LibraryHealthEntity> LibraryHealth { get; set; }
    public DbSet<Entities.PendingOrchestrationEntity> PendingOrchestrations { get; set; }
    public DbSet<Entities.EnrichmentTaskEntity> EnrichmentTasks { get; set; }
    public DbSet<Entities.AudioAnalysisEntity> AudioAnalysis { get; set; }
    public DbSet<Entities.AudioFeaturesEntity> AudioFeatures { get; set; }
    public DbSet<Entities.ForensicLogEntry> ForensicLogs { get; set; } // Phase 4.7: Forensic Logging
    public DbSet<Entities.StyleDefinitionEntity> StyleDefinitions { get; set; } // Phase 15: Style Lab
    public DbSet<Entities.LibraryActionLogEntity> LibraryActionLogs { get; set; } // Phase 16.1: Ledger

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "ORBIT", "library.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        
        // Phase 1B: Enable WAL Mode for better concurrency
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared // Enable shared cache for better concurrency
        }.ToString();
        
        optionsBuilder.UseSqlite(connectionString, options =>
        {
            options.CommandTimeout(30); // 30 second timeout for long operations
        });
    }

    /// <summary>
    /// Phase 1B: Configures SQLite connection with WAL mode and optimal settings.
    /// Called by DatabaseService.InitAsync during startup.
    /// </summary>
    public void ConfigureSqliteOptimizations(SqliteConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        using var command = connection.CreateCommand();
        
        // Phase 1B: Enable Write-Ahead Logging
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var result = command.ExecuteScalar()?.ToString();
        System.Console.WriteLine($"[Phase 1B] Journal mode set to: {result}");
        
        // Set synchronous mode to NORMAL (safe with WAL, much faster than FULL)
        command.CommandText = "PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
        
        // Increase cache size to 10MB (default is ~2MB)
        command.CommandText = "PRAGMA cache_size=-10000;"; // Negative = KB
        command.ExecuteNonQuery();
        
        // Auto-checkpoint at 1000 pages (~4MB)
        command.CommandText = "PRAGMA wal_autocheckpoint=1000;";
        command.ExecuteNonQuery();
        
        System.Console.WriteLine("[Phase 1B] SQLite WAL mode enabled successfully");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Phase 0B: Map PlaylistJobEntity to "Projects" table (terminology unification)
        modelBuilder.Entity<PlaylistJobEntity>().ToTable("Projects");
        
        // Configure PlaylistJob -> PlaylistTrack relationship
        modelBuilder.Entity<PlaylistJobEntity>()
            .HasMany(j => j.Tracks)
            .WithOne(t => t.Job)
            .HasForeignKey(t => t.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 1A: Add Query Indexes
        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.PlaylistId)
            .HasDatabaseName("IX_PlaylistTrack_PlaylistId");

        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.Status)
            .HasDatabaseName("IX_PlaylistTrack_Status");

        // Data Integrity: Add index for sync checks
        modelBuilder.Entity<PlaylistTrackEntity>()
            .HasIndex(t => t.TrackUniqueHash);

        modelBuilder.Entity<PlaylistJobEntity>()
            .HasIndex(j => j.CreatedAt)
            .HasDatabaseName("IX_PlaylistJob_CreatedAt");

        // Phase 1B: Centralize Status Enum (using EF Core's built-in converter)
        modelBuilder
            .Entity<PlaylistTrackEntity>()
            .Property(e => e.Status)
            .HasConversion<string>();

        // Phase 1C: Implement Global Query Filter for Soft Deletes
        modelBuilder.Entity<PlaylistJobEntity>().HasQueryFilter(j => !j.IsDeleted);
        // Playlist Activity Logs
        modelBuilder.Entity<PlaylistJobEntity>()
            .HasMany<PlaylistActivityLogEntity>()
            .WithOne(l => l.Job)
            .HasForeignKey(l => l.PlaylistId)
            .OnDelete(DeleteBehavior.Cascade);

        // Phase 1: Enrichment Task Indexes
        modelBuilder.Entity<Entities.EnrichmentTaskEntity>()
            .HasIndex(e => e.Status)
            .HasDatabaseName("IX_EnrichmentTasks_Status");

        modelBuilder.Entity<Entities.EnrichmentTaskEntity>()
            .HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("IX_EnrichmentTasks_Status_CreatedAt");
    }
}
