using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Repositories;

public class TrackRepository : ITrackRepository
{
    private readonly ILogger<TrackRepository> _logger;
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public TrackRepository(ILogger<TrackRepository> logger)
    {
        _logger = logger;
    }

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.Tracks.ToListAsync();
    }

    public async Task<TrackEntity?> FindTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        return await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        const int maxRetries = 5;
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var existingTrack = await context.Tracks
                        .FirstOrDefaultAsync(t => t.GlobalId == track.GlobalId);

                    if (existingTrack == null)
                    {
                        context.Tracks.Add(track);
                    }
                    else
                    {
                        // Update properties
                        context.Entry(existingTrack).CurrentValues.SetValues(track);
                    }

                    await context.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 5)
                {
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogWarning("SQLite database locked saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(100 * (attempt + 1));
                        continue;
                    }
                    throw;
                }
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdateTrackFilePathAsync(string globalId, string newPath)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
            if (track != null)
            {
                track.Filename = newPath;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
            if (track != null)
            {
                context.Tracks.Remove(track);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid playlistId)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(t => t.PlaylistId == playlistId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid playlistId, string hash)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks.FirstOrDefaultAsync(t => t.PlaylistId == playlistId && t.TrackUniqueHash == hash);
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.Id);
            if (existing == null)
            {
                context.PlaylistTracks.Add(track);
            }
            else
            {
                context.Entry(existing).CurrentValues.SetValues(track);
            }
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks.ToListAsync();
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.LibraryEntries.FindAsync(uniqueHash);
            if (existing != null)
            {
                if (result.Success)
                {
                    existing.SpotifyTrackId = result.SpotifyId;
                    existing.SpotifyAlbumId = result.SpotifyAlbumId;
                    existing.SpotifyArtistId = result.SpotifyArtistId;
                    if (!string.IsNullOrEmpty(result.ISRC)) existing.ISRC = result.ISRC;
                    if (!string.IsNullOrEmpty(result.AlbumArtUrl)) existing.AlbumArtUrl = result.AlbumArtUrl;
                    
                    if (result.Bpm > 0 || !string.IsNullOrEmpty(result.MusicalKey))
                    {
                        existing.BPM = result.Bpm;
                        existing.Energy = result.Energy;
                        existing.Valence = result.Valence;
                        existing.Danceability = result.Danceability;
                        if (!string.IsNullOrEmpty(result.MusicalKey)) existing.MusicalKey = result.MusicalKey;
                        
                        // Phase 12.7: Style Classification
                        if (!string.IsNullOrEmpty(result.DetectedSubGenre)) existing.DetectedSubGenre = result.DetectedSubGenre;
                        if (result.SubGenreConfidence > 0) existing.SubGenreConfidence = result.SubGenreConfidence;
                    }
                }
                else
                {
                     // Mark as failed to prevent infinite retry loop
                    existing.SpotifyTrackId = "FAILED";
                    _logger.LogWarning("Marking LibraryEntry {Hash} as Enrichment FAILED", uniqueHash);
                }
                
                // IMPORTANT: Always mark as enriched if we attempted identification, 
                // so Stage 1 doesn't pick it up again. If Success=true, Stage 2 (Features) 
                // will pick it up if IsEnriched is still false.
                // Wait, if I set IsEnriched = true here, Stage 2 will SKIP it.
                // So if Success=true, we should only set IsEnriched = true if we actually GOT the features.
                // But if Success=false, we MUST set IsEnriched = true to stop the loop.
                
                if (!result.Success) 
                {
                    existing.IsEnriched = true;
                }
                else if (result.Bpm > 0)
                {
                    existing.IsEnriched = true;
                }

                existing.LastUsedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(id);
            if (track != null)
            {
                if (result.Success)
                {
                    track.SpotifyTrackId = result.SpotifyId;
                    track.SpotifyAlbumId = result.SpotifyAlbumId;
                    track.SpotifyArtistId = result.SpotifyArtistId;
                    if (!string.IsNullOrEmpty(result.ISRC)) track.ISRC = result.ISRC;
                    if (!string.IsNullOrEmpty(result.AlbumArtUrl)) track.AlbumArtUrl = result.AlbumArtUrl;
                    if (result.Bpm > 0 || !string.IsNullOrEmpty(result.MusicalKey))
                    {
                        track.BPM = result.Bpm;
                        track.Energy = result.Energy;
                        track.Valence = result.Valence;
                        track.Danceability = result.Danceability;
                        if (!string.IsNullOrEmpty(result.MusicalKey)) track.MusicalKey = result.MusicalKey;

                        // Phase 12.7: Style Classification
                        if (!string.IsNullOrEmpty(result.DetectedSubGenre)) track.DetectedSubGenre = result.DetectedSubGenre;
                        if (result.SubGenreConfidence > 0) track.SubGenreConfidence = result.SubGenreConfidence;
                    }
                }
                else
                {
                    // Mark as failed to prevent infinite retry loop
                    track.SpotifyTrackId = "FAILED";
                    _logger.LogWarning("Marking PlaylistTrack {Id} as Enrichment FAILED", id);
                }

                // If identification failed, mark as enriched to stop the cycle.
                // If it succeeded but we don't have features yet, Stage 2 will pick it up (IsEnriched is still false).
                if (!result.Success || result.Bpm > 0)
                {
                    track.IsEnriched = true;
                }
                
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingFeaturesAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => e.SpotifyTrackId != null && !e.IsEnriched)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingFeaturesAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(e => e.SpotifyTrackId != null && !e.IsEnriched)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }
    public async Task UpdateLibraryEntriesFeaturesAsync(Dictionary<string, SpotifyAPI.Web.TrackAudioFeatures> featuresMap)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var ids = featuresMap.Keys.ToList();
            
            // 1. Update Library Entries
            var entries = await context.LibraryEntries
                .Where(e => e.SpotifyTrackId != null && ids.Contains(e.SpotifyTrackId))
                .ToListAsync();

            foreach (var entry in entries)
            {
                if (entry.SpotifyTrackId != null && featuresMap.TryGetValue(entry.SpotifyTrackId, out var f))
                {
                    entry.BPM = f.Tempo;
                    entry.Energy = f.Energy;
                    entry.Valence = f.Valence;
                    entry.Danceability = f.Danceability;
                    
                    var camelotNum = (f.Key + 7) % 12 + 1;
                    entry.MusicalKey = $"{camelotNum}{(f.Mode == 1 ? "B" : "A")}";
                    
                    entry.IsEnriched = true;
                }
            }

            // 2. Update Playlist Tracks (Intelligence Sync)
            var pTracks = await context.PlaylistTracks
                .Where(e => e.SpotifyTrackId != null && ids.Contains(e.SpotifyTrackId))
                .ToListAsync();

            foreach (var pt in pTracks)
            {
                if (pt.SpotifyTrackId != null && featuresMap.TryGetValue(pt.SpotifyTrackId, out var f))
                {
                    pt.BPM = f.Tempo;
                    pt.Energy = f.Energy;
                    pt.Valence = f.Valence;
                    pt.Danceability = f.Danceability;
                    
                    var camelotNum = (f.Key + 7) % 12 + 1;
                    pt.MusicalKey = $"{camelotNum}{(f.Mode == 1 ? "B" : "A")}";
                    
                    pt.IsEnriched = true;
                }
            }
            
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            // 1. Find all PlaylistTrack entries for this global track hash
            var playlistTracks = await context.PlaylistTracks
                .Where(pt => pt.TrackUniqueHash == trackUniqueHash)
                .ToListAsync();

            if (playlistTracks.Count == 0) return new List<Guid>();

            var distinctJobIds = playlistTracks.Select(pt => pt.PlaylistId).Distinct().Cast<Guid>().ToList();

            // 2. Update their status
            foreach (var pt in playlistTracks)
            {
                pt.Status = newStatus;
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    pt.ResolvedFilePath = resolvedPath;
                }
            }
            
            // 3. Fetch all affected jobs and all their related tracks
            var jobsToUpdate = await context.Projects
                .Where(j => distinctJobIds.Contains(j.Id))
                .ToListAsync();

            var allRelatedTracks = await context.PlaylistTracks
                .Where(t => distinctJobIds.Contains(t.PlaylistId))
                .AsNoTracking()
                .ToListAsync();

            // 4. Recalculate counts for each job
            foreach (var job in jobsToUpdate)
            {
                var currentJobTracks = allRelatedTracks
                    .Where(t => t.PlaylistId == job.Id && t.TrackUniqueHash != trackUniqueHash)
                    .ToList();
                currentJobTracks.AddRange(playlistTracks.Where(pt => pt.PlaylistId == job.Id));

                job.SuccessfulCount = currentJobTracks.Count(t => t.Status == TrackStatus.Downloaded);
                job.FailedCount = currentJobTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
            }

            // 5. Update Library Health stats
            await UpdateLibraryHealthAsync(context);

            await context.SaveChangesAsync();
            return distinctJobIds;
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            foreach (var t in tracks)
            {
                var existing = await context.PlaylistTracks.FindAsync(t.Id);
                if (existing == null) context.PlaylistTracks.Add(t);
                else context.Entry(existing).CurrentValues.SetValues(t);
            }
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeletePlaylistTracksAsync(Guid playlistId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks.Where(t => t.PlaylistId == playlistId).ToListAsync();
            context.PlaylistTracks.RemoveRange(tracks);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks
                .Where(t => t.PlaylistId == playlistId && t.Status == TrackStatus.Missing)
                .ToListAsync();
            
            foreach (var track in tracks)
            {
                track.Priority = newPriority;
            }
            
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(trackId);
            if (track != null)
            {
                track.Priority = newPriority;
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task DeleteSinglePlaylistTrackAsync(Guid trackId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var track = await context.PlaylistTracks.FindAsync(trackId);
            if (track != null)
            {
                context.PlaylistTracks.Remove(track);
                await context.SaveChangesAsync();
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<TrackTechnicalEntity?> GetTrackTechnicalDetailsAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        return await context.TechnicalDetails.FirstOrDefaultAsync(t => t.PlaylistTrackId == playlistTrackId);
    }

    public async Task<TrackTechnicalEntity> GetOrCreateTechnicalDetailsAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        var existing = await context.TechnicalDetails.FirstOrDefaultAsync(t => t.PlaylistTrackId == playlistTrackId);
        
        if (existing != null)
            return existing;

        return new TrackTechnicalEntity
        {
            Id = Guid.NewGuid(),
            PlaylistTrackId = playlistTrackId,
            LastUpdated = DateTime.UtcNow
        };
    }

    public async Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var existing = await context.TechnicalDetails.FindAsync(details.Id);
            if (existing == null) context.TechnicalDetails.Add(details);
            else context.Entry(existing).CurrentValues.SetValues(details);
            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.AsNoTracking().ToListAsync();
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && e.Genres == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && t.Genres == null)
            .OrderByDescending(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap)
    {
        if (!artistGenreMap.Any()) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var artistIds = artistGenreMap.Keys.ToList();
            
            var entries = await context.LibraryEntries
                .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && artistIds.Contains(e.SpotifyArtistId))
                .ToListAsync();

            foreach (var entry in entries)
            {
                if (artistGenreMap.TryGetValue(entry.SpotifyArtistId!, out var genres))
                {
                    entry.Genres = string.Join(", ", genres);
                }
            }

            var tracks = await context.PlaylistTracks
                .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && artistIds.Contains(t.SpotifyArtistId))
                .ToListAsync();

            foreach (var track in tracks)
            {
                if (artistGenreMap.TryGetValue(track.SpotifyArtistId!, out var genres))
                {
                    track.Genres = string.Join(", ", genres);
                }
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task MarkTrackAsVerifiedAsync(string trackHash)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var tracks = await context.PlaylistTracks
                .Include(pt => pt.TechnicalDetails)
                .Where(pt => pt.TrackUniqueHash == trackHash)
                .ToListAsync();
                
            foreach (var track in tracks)
            {
                if (track.TechnicalDetails != null)
                {
                    track.TechnicalDetails.IsReviewNeeded = false;
                }
            }

            var features = await context.AudioFeatures
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash);
                
            if (features != null)
            {
                features.CurationConfidence = CurationConfidence.High;
                features.Source = DataSource.Manual;
                
                var provenance = new 
                {
                     Action = "Verified",
                     By = "User",
                     Timestamp = DateTime.UtcNow
                };
                features.ProvenanceJson = System.Text.Json.JsonSerializer.Serialize(provenance);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private async Task UpdateLibraryHealthAsync(AppDbContext context)
    {
        try
        {
            var totalTracks = await context.PlaylistTracks.CountAsync();
            var hqTracks = await context.PlaylistTracks.CountAsync(t => t.Bitrate >= 256 || (t.Format != null && t.Format.ToLower() == "flac"));
            var lowBitrateTracks = await context.PlaylistTracks.CountAsync(t => t.Status == TrackStatus.Downloaded && t.Bitrate > 0 && t.Bitrate < 256);
            
            var health = await context.LibraryHealth.FindAsync(1) ?? new LibraryHealthEntity { Id = 1 };
            
            health.TotalTracks = totalTracks;
            health.HqTracks = hqTracks;
            health.UpgradableCount = lowBitrateTracks;
            health.LastScanDate = DateTime.Now;
            
            if (context.Entry(health).State == EntityState.Detached)
            {
                context.LibraryHealth.Add(health);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update library health cache during track update");
        }
    }
}
