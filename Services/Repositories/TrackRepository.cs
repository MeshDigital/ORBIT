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
            .Include(t => t.TechnicalDetails) 
            .Include(t => t.AudioFeatures) // Phase 21: Eager load Brain data
            .Where(t => t.PlaylistId == playlistId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid playlistId, string hash)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Include(t => t.AudioFeatures)
            .FirstOrDefaultAsync(t => t.PlaylistId == playlistId && t.TrackUniqueHash == hash);
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

    public async Task<int> GetPlaylistTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        var query = context.PlaylistTracks.AsQueryable();
        if (playlistId != Guid.Empty)
        {
            query = query.Where(t => t.PlaylistId == playlistId);
        }
        query = ApplyFilters(query, filter, downloadedOnly);
        return await query.CountAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        var query = context.PlaylistTracks
            .Include(t => t.TechnicalDetails)
            .Include(t => t.AudioFeatures)
            .AsQueryable();
            
        if (playlistId != Guid.Empty)
        {
            query = query.Where(t => t.PlaylistId == playlistId);
        }
            
        query = ApplyFilters(query, filter, downloadedOnly);
        
        return await query
            .OrderBy(t => t.SortOrder)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    private IQueryable<PlaylistTrackEntity> ApplyFilters(IQueryable<PlaylistTrackEntity> query, string? filter, bool? downloadedOnly)
    {
        if (!string.IsNullOrEmpty(filter))
        {
            var lowerFilter = filter.ToLower();
            query = query.Where(t => t.Artist.ToLower().Contains(lowerFilter) || t.Title.ToLower().Contains(lowerFilter));
        }
        if (downloadedOnly == true)
        {
            query = query.Where(t => t.Status == TrackStatus.Downloaded);
        }
        return query;
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        var cooldownDate = DateTime.UtcNow.AddHours(-4).ToString("O");
        
        return await context.LibraryEntries
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null && 
                       (e.LastEnrichmentAttempt == null || e.LastEnrichmentAttempt.CompareTo(cooldownDate) < 0))
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
                // Phase 21: Smart Retry - Track attempts and timestamp
                existing.EnrichmentAttempts = existing.EnrichmentAttempts + 1;
                existing.LastEnrichmentAttempt = DateTime.UtcNow.ToString("O");
                
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
                    
                    // Reset retry tracking on success
                    existing.EnrichmentAttempts = 0;
                    existing.LastEnrichmentAttempt = null;
                }
                else
                {
                    // Phase 21: Only mark as permanently FAILED after max attempts (5)
                    const int MaxAttempts = 5;
                    if (existing.EnrichmentAttempts >= MaxAttempts)
                    {
                        existing.SpotifyTrackId = "FAILED";
                        existing.IsEnriched = true; // Stop retrying
                        _logger.LogWarning("Marking LibraryEntry {Hash} as permanently FAILED after {Attempts} attempts", uniqueHash, existing.EnrichmentAttempts);
                    }
                    else
                    {
                        _logger.LogInformation("LibraryEntry {Hash} enrichment failed (attempt {Attempt}/{Max}), will retry after cooldown", 
                            uniqueHash, existing.EnrichmentAttempts, MaxAttempts);
                    }
                }
                
                // Stage 2 (Features) is removed, so identification success is enough to mark as Enriched
                existing.IsEnriched = result.Success || (existing.SpotifyTrackId == "FAILED");
                
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
        var cooldownDate = DateTime.UtcNow.AddHours(-4).ToString("O");

        return await context.PlaylistTracks
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null &&
                       (e.LastEnrichmentAttempt == null || e.LastEnrichmentAttempt.CompareTo(cooldownDate) < 0))
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
                // Phase 21: Smart Retry - Track attempts and timestamp
                track.EnrichmentAttempts = track.EnrichmentAttempts + 1;
                track.LastEnrichmentAttempt = DateTime.UtcNow.ToString("O");
                
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
                    
                    // Reset retry tracking on success
                    track.EnrichmentAttempts = 0;
                    track.LastEnrichmentAttempt = null;
                }
                else
                {
                    // Phase 21: Only mark as permanently FAILED after max attempts (5)
                    const int MaxAttempts = 5;
                    if (track.EnrichmentAttempts >= MaxAttempts)
                    {
                        track.SpotifyTrackId = "FAILED";
                        track.IsEnriched = true; // Stop retrying
                        _logger.LogWarning("Marking PlaylistTrack {Id} as permanently FAILED after {Attempts} attempts", id, track.EnrichmentAttempts);
                    }
                    else
                    {
                        _logger.LogInformation("PlaylistTrack {Id} enrichment failed (attempt {Attempt}/{Max}), will retry after cooldown", 
                            id, track.EnrichmentAttempts, MaxAttempts);
                    }
                }

                // If identification failed, mark as enriched to stop the cycle.
                // If it succeeded but we don't have features yet, Stage 2 will pick it up (IsEnriched is still false).
                // If Success=true, Stage 2 (Features) will pick it up because SpotifyTrackId is not null but IsEnriched is false.
                // We DON'T set IsEnriched=true here unless we truly have features or reached MaxAttempts.
                track.IsEnriched = (result.Success && result.Bpm > 0) || (track.SpotifyTrackId == "FAILED");
                
                await context.SaveChangesAsync();
            }
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

    public async Task<int> GetTotalLibraryTrackCountAsync(string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        
        if (!string.IsNullOrEmpty(filter))
        {
            var formattedSearch = filter.Trim() + "*";
            var count = await context.Database.ExecuteSqlRawAsync(
                "SELECT COUNT(*) FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
            
            // ExecuteSqlRawAsync returns the number of rows affected, not the count.
            // We need to use a Different approach for Scalar results or just use the query.
            
            var query = context.LibraryEntries.FromSqlRaw(
                "SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
            
            if (downloadedOnly == true)
            {
                query = query.Where(t => t.FilePath != null && t.FilePath != "");
            }
            
            return await query.CountAsync();
        }

        var baseQuery = context.LibraryEntries.AsQueryable();
        if (downloadedOnly == true)
        {
            baseQuery = baseQuery.Where(t => t.FilePath != null && t.FilePath != "");
        }

        return await baseQuery.CountAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPagedAllTracksAsync(int skip, int take, string? filter = null, bool? downloadedOnly = null)
    {
        using var context = new AppDbContext();
        
        IQueryable<LibraryEntryEntity> query;

        // 2. Apply Filters (Use FTS5 if filter is present)
        if (!string.IsNullOrEmpty(filter))
        {
            var formattedSearch = filter.Trim() + "*";
            query = context.LibraryEntries
                .FromSqlRaw("SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch);
        }
        else
        {
            query = context.LibraryEntries.AsQueryable();
        }

        query = query.Include(le => le.AudioFeatures).AsNoTracking();

        // 3. Apply DownloadedOnly
        if (downloadedOnly == true)
        {
            query = query.Where(t => t.FilePath != null && t.FilePath != "");
        }

        // 4. Order & Page
        var entries = await query
            .OrderByDescending(t => t.AddedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        // 5. PROJECT to PlaylistTrackEntity (The Adapter Pattern)
        // This allows the existing UI to consume LibraryEntries without refactoring the ViewModels.
        return entries.Select(e => new PlaylistTrackEntity
        {
            Id = Guid.NewGuid(), // Virtual ID for the view
            PlaylistId = Guid.Empty, // Indicates "Library View"
            
            // Map Core Metadata
            Artist = e.Artist,
            Title = e.Title,
            Album = e.Album,
            TrackUniqueHash = e.UniqueHash,
            
            // Map Status (If it has a path, it's downloaded)
            Status = string.IsNullOrEmpty(e.FilePath) ? TrackStatus.Missing : TrackStatus.Downloaded,
            ResolvedFilePath = e.FilePath,
            
            // Map Enrichment Data
            // Map Enrichment Data (Prefer AudioFeatures if available)
            SpotifyTrackId = e.SpotifyTrackId,
            AlbumArtUrl = e.AlbumArtUrl,
            BPM = (e.AudioFeatures?.Bpm > 0) ? e.AudioFeatures.Bpm : e.BPM,
            Energy = (e.AudioFeatures?.Energy > 0) ? e.AudioFeatures.Energy : e.Energy,
            Danceability = (e.AudioFeatures?.Danceability > 0) ? e.AudioFeatures.Danceability : e.Danceability,
            Valence = (e.AudioFeatures?.Valence > 0) ? e.AudioFeatures.Valence : e.Valence,
            MusicalKey = !string.IsNullOrEmpty(e.AudioFeatures?.Key) ? e.AudioFeatures.Key : e.MusicalKey,
            CanonicalDuration = e.DurationSeconds * 1000, // Convert to MS
            
            // Important: Library entries might not have SortOrder, so we default to 0
            SortOrder = 0,
            AddedAt = e.AddedAt,
            IsEnriched = e.IsEnriched,
            
            // FIX: Map Technical Data (Badges/Quality)
            Bitrate = e.Bitrate,
            Format = e.Format,
            Integrity = e.Integrity, // Now correctly mapped
            BitrateScore = e.Bitrate, // Fallback score
            
            // FIX: Map AI/Curation Data (Vibes/Shields)
            DetectedSubGenre = e.AudioFeatures?.DetectedSubGenre ?? e.DetectedSubGenre,
            SubGenreConfidence = e.AudioFeatures?.SubGenreConfidence ?? e.SubGenreConfidence,
            InstrumentalProbability = e.AudioFeatures?.InstrumentalProbability ?? e.InstrumentalProbability, // FIX: Map Instrumental Pill
            PrimaryGenre = e.PrimaryGenre,
            AudioFeatures = e.AudioFeatures, // Pass the whole object if possible, or map fields
            
            // Map Technical Audio (Loudness/Dynamics)
            Loudness = e.Loudness,
            TruePeak = e.TruePeak,
            DynamicRange = e.DynamicRange,
            
            // Map Trust
            IsTrustworthy = e.Integrity != Data.IntegrityLevel.Suspicious && e.Integrity != Data.IntegrityLevel.None,
            QualityConfidence = (e.Bitrate >= 320 || e.Format == "flac") ? 1.0 : 0.5
        }).ToList();
    }

    public async Task<List<LibraryEntryEntity>> SearchLibraryFtsAsync(string searchTerm, int limit = 100)
    {
        using var context = new AppDbContext();
        var formattedSearch = searchTerm.Trim() + "*";
        
        return await context.LibraryEntries
            .FromSqlRaw("SELECT * FROM LibraryEntries WHERE rowid IN (SELECT rowid FROM LibraryEntriesFts WHERE LibraryEntriesFts MATCH {0})", formattedSearch)
            .Include(le => le.AudioFeatures)
            .AsNoTracking()
            .Take(limit)
            .ToListAsync();
    }
}
