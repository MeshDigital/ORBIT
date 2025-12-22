using Microsoft.EntityFrameworkCore;
using System.IO;
using SLSKDONET.Models;

namespace SLSKDONET.Data;

public class AppDbContext : DbContext
{
    public DbSet<TrackEntity> Tracks { get; set; }
    public DbSet<LibraryEntryEntity> LibraryEntries { get; set; }
    public DbSet<PlaylistJobEntity> PlaylistJobs { get; set; }
    public DbSet<PlaylistTrackEntity> PlaylistTracks { get; set; }
    public DbSet<PlaylistActivityLogEntity> ActivityLogs { get; set; }
    public DbSet<QueueItemEntity> QueueItems { get; set; }
    public DbSet<Entities.SpotifyMetadataCacheEntity> SpotifyMetadataCache { get; set; }
    public DbSet<LibraryHealthEntity> LibraryHealth { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "SLSKDONET", "library.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
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
    }
}
