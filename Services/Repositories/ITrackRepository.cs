using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services.Repositories;

public interface ITrackRepository
{
    Task<List<TrackEntity>> LoadTracksAsync();
    Task<TrackEntity?> FindTrackAsync(string globalId);
    Task SaveTrackAsync(TrackEntity track);
    Task UpdateTrackFilePathAsync(string globalId, string newPath);
    Task RemoveTrackAsync(string globalId);
    Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid playlistId);
    Task<PlaylistTrackEntity?> GetPlaylistTrackByHashAsync(Guid playlistId, string hash);
    Task SavePlaylistTrackAsync(PlaylistTrackEntity track);
    Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync();
    Task<int> GetPlaylistTrackCountAsync(Guid playlistId, string? filter = null, bool? downloadedOnly = null);
    Task<List<PlaylistTrackEntity>> GetPagedPlaylistTracksAsync(Guid playlistId, int skip, int take, string? filter = null, bool? downloadedOnly = null);
    Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit);
    Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result);
    Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit);
    Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result);
    Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath);
    Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks);
    Task DeletePlaylistTracksAsync(Guid playlistId);
    Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority);
    Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority);
    Task DeleteSinglePlaylistTrackAsync(Guid trackId);
    Task<TrackTechnicalEntity?> GetTrackTechnicalDetailsAsync(Guid playlistTrackId);
    Task<TrackTechnicalEntity> GetOrCreateTechnicalDetailsAsync(Guid playlistTrackId);
    Task SaveTechnicalDetailsAsync(TrackTechnicalEntity details);
    Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync();
    Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit);
    Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit);
    Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap);
    Task MarkTrackAsVerifiedAsync(string trackHash);
    Task<int> GetTotalLibraryTrackCountAsync(string? filter = null, bool? downloadedOnly = null);
    Task<List<PlaylistTrackEntity>> GetPagedAllTracksAsync(int skip, int take, string? filter = null, bool? downloadedOnly = null);
    Task<List<LibraryEntryEntity>> SearchLibraryFtsAsync(string searchTerm, int limit = 100);
    Task UpdateAllInstancesMetadataAsync(string trackHash, TrackEnrichmentResult result);
}
