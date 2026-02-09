using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace SLSKDONET.Services.Testing;

/// <summary>
/// Generates and injects mock data into the library for scalability testing.
/// </summary>
public class MockLibraryGenerator
{
    private readonly ILogger<MockLibraryGenerator> _logger;
    private readonly DatabaseService _databaseService;

    public MockLibraryGenerator(ILogger<MockLibraryGenerator> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public async Task<Guid> GenerateMockLibraryAsync(int trackCount, CancellationToken ct = default)
    {
        _logger.LogInformation("🚀 Generating {Count} mock library entries...", trackCount);
        
        var projectId = Guid.NewGuid();
        var projectName = $"🔥 10K MARATHON ({DateTime.Now:HH:mm:ss})";
        
        // Create Project Header
        var project = new PlaylistJobEntity
        {
            Id = projectId,
            SourceTitle = projectName,
            SourceType = "StressTest",
            CreatedAt = DateTime.UtcNow,
            TotalTracks = trackCount,
            IsDeleted = false
        };

        using var context = new AppDbContext();
        context.Projects.Add(project);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation("📦 Created project header: {Id}", projectId);

        // Generate in batches to avoid memory issues and massive transactions
        const int batchSize = 1000;
        int batches = (int)Math.Ceiling((double)trackCount / batchSize);

        for (int i = 0; i < batches; i++)
        {
            if (ct.IsCancellationRequested) break;

            int currentBatchCount = Math.Min(batchSize, trackCount - (i * batchSize));
            var tracks = new List<PlaylistTrackEntity>();
            var libEntries = new List<LibraryEntryEntity>();
            var audioFeaturesList = new List<AudioFeaturesEntity>();

            for (int j = 0; j < currentBatchCount; j++)
            {
                int globalIndex = (i * batchSize) + j;
                var trackHash = $"MOCK_{projectId}_{globalIndex:D6}";
                var artist = $"Mock Artist {globalIndex / 10}";
                var title = $"Stress Track {globalIndex}";
                var album = $"Scalability Album {globalIndex / 100}";
                var filePath = $"C:\\MockMusic\\{artist}\\{album}\\{title}.mp3";

                var track = new PlaylistTrackEntity
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = projectId,
                    Artist = artist,
                    Title = title,
                    Album = album,
                    TrackUniqueHash = trackHash,
                    Status = SLSKDONET.Models.TrackStatus.Downloaded,
                    ResolvedFilePath = filePath,
                    AddedAt = DateTime.UtcNow,
                    SortOrder = globalIndex,
                    IsEnriched = true,
                    BPM = 120 + (globalIndex % 10),
                    MusicalKey = "1A",
                    Energy = 0.8f,
                    Danceability = 0.7f
                };
                tracks.Add(track);

                var libEntry = new LibraryEntryEntity
                {
                    UniqueHash = trackHash,
                    Id = Guid.NewGuid(),
                    Artist = artist,
                    Title = title,
                    Album = album,
                    FilePath = filePath,
                    Format = "mp3",
                    Bitrate = 320,
                    DurationSeconds = 180,
                    AddedAt = DateTime.UtcNow,
                    IsEnriched = true,
                    BPM = track.BPM,
                    MusicalKey = track.MusicalKey,
                    Energy = track.Energy,
                    Danceability = track.Danceability
                };
                libEntries.Add(libEntry);

                var features = new AudioFeaturesEntity
                {
                    TrackUniqueHash = trackHash,
                    Energy = 0.8f,
                    Danceability = 0.7f,
                    Valence = 0.6f,
                    Bpm = (float)track.BPM!,
                    Key = "1A"
                };
                audioFeaturesList.Add(features);
            }

            // Bulk Insert
            await context.PlaylistTracks.AddRangeAsync(tracks, ct);
            await context.LibraryEntries.AddRangeAsync(libEntries, ct);
            await context.AudioFeatures.AddRangeAsync(audioFeaturesList, ct);
            
            await context.SaveChangesAsync(ct);
            _logger.LogInformation("   ✅ Injected batch {Current}/{Total} ({Count} tracks)", i + 1, batches, currentBatchCount);
        }

        _logger.LogInformation("✨ Mock library generation complete. Project Id: {Id}", projectId);
        return projectId;
    }

    public async Task CleanupMockDataAsync(Guid projectId)
    {
        _logger.LogInformation("🧹 Cleaning up mock data for project {Id}...", projectId);
        using var context = new AppDbContext();
        
        // Find hashes to delete from LibraryEntries and AudioFeatures
        var hashes = await context.PlaylistTracks
            .Where(t => t.PlaylistId == projectId)
            .Select(t => t.TrackUniqueHash)
            .ToListAsync();

        _logger.LogInformation("   Found {Count} entries to remove.", hashes.Count);

        // Delete dependencies first
        var features = await context.AudioFeatures.Where(f => hashes.Contains(f.TrackUniqueHash)).ToListAsync();
        context.AudioFeatures.RemoveRange(features);

        var libEntries = await context.LibraryEntries.Where(e => hashes.Contains(e.UniqueHash)).ToListAsync();
        context.LibraryEntries.RemoveRange(libEntries);

        var tracks = await context.PlaylistTracks.Where(t => t.PlaylistId == projectId).ToListAsync();
        context.PlaylistTracks.RemoveRange(tracks);

        var project = await context.Projects.FindAsync(projectId);
        if (project != null) context.Projects.Remove(project);

        await context.SaveChangesAsync();
        _logger.LogInformation("   Cleanup complete.");
    }
}
