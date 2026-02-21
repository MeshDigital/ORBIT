using System;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    private async void OnProjectAdded(ProjectAddedEvent evt)
    {
        try
        {
            _logger.LogInformation("[IMPORT TRACE] LibraryViewModel.OnProjectAdded: Received event for job {JobId}", evt.ProjectId);
            
            // Wait a moment for DB to settle
            await Task.Delay(500);
            
            await LoadProjectsAsync();
            _logger.LogInformation("[IMPORT TRACE] LoadProjectsAsync completed. AllProjects count: {Count}", Projects.AllProjects.Count);
            
            // Select the newly added project
            _logger.LogInformation("[IMPORT TRACE] Attempting to select project {JobId}", evt.ProjectId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var newProject = Projects.AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
                if (newProject != null)
                {
                    Projects.SelectedProject = newProject;
                    _logger.LogInformation("[IMPORT TRACE] Successfully selected project {JobId}", evt.ProjectId);
                }
                else
                {
                    _logger.LogWarning("Could not find project {JobId} in AllProjects after import", evt.ProjectId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle post-import navigation for project {JobId}", evt.ProjectId);
        }
    }

    private async void OnTrackSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var selectedTracks = Tracks.SelectedTracks.ToList();

        // ── Inspector (legacy, always updated) ──────────────────────────────
        var lastSelected = selectedTracks.LastOrDefault();
        if (lastSelected is PlaylistTrackViewModel trackVm)
        {
            TrackInspector.Track = trackVm.Model;
        }

        // Contextual Sidebar: Now handled reactively via Sidebar.AttachToSelection()
        // which is wired up in the LibraryViewModel constructor.

        // ── Discovery Lane (harmonic matches, legacy path) ──────────────────
        if (lastSelected is PlaylistTrackViewModel seedTrack &&
            (IsDiscoveryLaneVisible || CurrentWorkspace == ActiveWorkspace.Preparer))
        {
            _selectionDebounceTimer?.Dispose();
            _selectionDebounceTimer = new System.Threading.Timer(async _ =>
            {
                await LoadHarmonicMatchesAsync(seedTrack, System.Threading.CancellationToken.None);
            }, null, 150, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// Loads all projects from the database.
    /// Delegates to ProjectListViewModel.
    /// </summary>
    public async Task LoadProjectsAsync()
    {
        await Projects.LoadProjectsAsync();
    }

    /// <summary>
    /// Handles project selection event from ProjectListViewModel.
    /// Coordinates loading tracks in TrackListViewModel.
    /// </summary>
    private async void OnProjectSelected(object? sender, PlaylistJob? project)
    {
        if (project != null)
        {
            _logger.LogInformation("LibraryViewModel.OnProjectSelected: Switching to project {Title} (ID: {Id})", project.SourceTitle, project.Id);
            await Tracks.LoadProjectTracksAsync(project);
            
            // If we are in Preparer mode, find matches for the first track automatically
            if (CurrentWorkspace == ActiveWorkspace.Preparer && Tracks.CurrentProjectTracks.Any())
            {
                 var firstTrack = Tracks.CurrentProjectTracks.First();
                 // Delay slightly to ensure UI is ready
                 await Task.Delay(200);
                 await ExecuteFindHarmonicMatchesAsync(firstTrack);
            }
        }
    }

    /// <summary>
    /// Handles smart playlist selection event from SmartPlaylistViewModel.
    /// Coordinates updating track list.
    /// </summary>
    private async void OnSmartPlaylistSelected(object? sender, Library.SmartPlaylist? playlist)
    {
        if (playlist == null) return;

        try
        {
            IsLoading = true;
            
            // Phase 23: Smart Crates (DB-backed)
            if (playlist.Definition != null)
            {
                _notificationService.Show("Smart Crate", $"Evaluating rules for '{playlist.Name}'...", NotificationType.Information);
                
                // 1. Evaluate rules against database (Global Index)
                var ids = await _smartCrateService.GetMatchingTrackIdsAsync(playlist.Definition);
                
                // 2. Load matching tracks via TrackListViewModel
                await Tracks.LoadSmartCrateAsync(ids);
                
                _logger.LogInformation("Loaded Smart Crate '{Name}' with {Count} tracks", playlist.Name, ids.Count);
            }
            else
            {
                // Legacy: In-Memory Smart Playlists
                _notificationService.Show("Smart Playlist", $"Loading {playlist.Name}", NotificationType.Information);
                
                // Execute filter on loaded memory state
                var tracks = SmartPlaylists.RefreshSmartPlaylist(playlist);
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    Tracks.CurrentProjectTracks = tracks;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load smart playlist {Name}", playlist.Name);
            _notificationService.Show("Error", "Failed to load crate.", NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadHarmonicMatchesAsync(PlaylistTrackViewModel trackVm, System.Threading.CancellationToken ct)
    {
        try
        {
            IsLoadingMatches = true;
            MixHelperSeedTrack = trackVm;
            
            // We need the LibraryEntry ID for harmonic matching
            var libraryEntry = await _libraryService.FindLibraryEntryAsync(trackVm.Model.TrackUniqueHash);
            if (libraryEntry == null)
            {
                HarmonicMatches.Clear();
                return;
            }

            var results = await _harmonicMatchService.FindMatchesAsync(libraryEntry.Id);
            
            if (ct.IsCancellationRequested) return;

            HarmonicMatches.Clear();
            foreach (var result in results)
            {
                var vm = new HarmonicMatchViewModel(result, _eventBus, _libraryService, _libraryCacheService);
                HarmonicMatches.Add(vm);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load harmonic matches for sidebar");
        }
        finally
        {
            IsLoadingMatches = false;
        }
    }

    private class SonicMatch
    {
        public SLSKDONET.Models.LibraryEntry Entry { get; set; } = null!;
        public double Score { get; set; }
    }

    private async Task<List<SonicMatch>> GetSonicMatchesInternalAsync(PlaylistTrack track)
    {
        var matches = new List<SonicMatch>();
        var seedFeatures = await _libraryService.GetAudioFeaturesByHashAsync(track.TrackUniqueHash);
        if (seedFeatures == null) return matches;

        var allEntries = await _libraryService.LoadAllLibraryEntriesAsync();
        
        foreach (var entry in allEntries)
        {
            if (entry.UniqueHash == track.TrackUniqueHash) continue;
            
            var targetFeatures = await _libraryService.GetAudioFeaturesByHashAsync(entry.UniqueHash);
            if (targetFeatures == null) continue;

            double dEnergy = seedFeatures.Energy - targetFeatures.Energy;
            double dDance = seedFeatures.Danceability - targetFeatures.Danceability;
            double dValence = seedFeatures.Valence - targetFeatures.Valence;
            
            double distance = Math.Sqrt(dEnergy * dEnergy + dDance * dDance + dValence * dValence);
            double score = Math.Max(0, 100 - (distance * 100));

            if (score > 75)
            {
                matches.Add(new SonicMatch { Entry = entry, Score = score });
            }
        }

        return matches.OrderByDescending(m => m.Score).Take(20).ToList();
    }

    /// <summary>
    /// Handles the event triggered by "Find Similar" context menu.
    /// Dispatches to either Algorithmic (Sonic Twin) or AI (Vibe Match) search.
    /// </summary>
    private async void OnFindSimilarRequest(FindSimilarRequestEvent e)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                IsLoadingMatches = true;
                MixHelperSeedTrack = new PlaylistTrackViewModel(e.SeedTrack);
                
                // Switch Sidebar to Vision
                IsMixHelperVisible = true;

                List<SonicMatch> matches;

                if (e.UseAi)
                {
                    _notificationService.Show("AI Search", "Analyzing neural embeddings...", NotificationType.Information);
                    matches = await GetAiMatchesInternalAsync(e.SeedTrack);
                }
                else
                {
                    matches = await GetSonicMatchesInternalAsync(e.SeedTrack);
                }

                HarmonicMatches.Clear();
                foreach (var match in matches)
                {
                    // "Sonic Twin" or "Vibe Match" label based on mode
                    string label = e.UseAi ? $"Vibe: {match.Score:F0}%" : "Sonic Twin";
                    var vm = new HarmonicMatchViewModel(match.Entry, match.Score, label);
                    HarmonicMatches.Add(vm);
                }
                
                if (!matches.Any())
                {
                    _notificationService.Show("No Matches", "No similar tracks found in library.", NotificationType.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Find Similar request failed");
                _notificationService.Show("Search Error", ex.Message, NotificationType.Error);
            }
            finally
            {
                IsLoadingMatches = false;
            }
        });
    }

    /// <summary>
    /// Phase 12.6: Uses the Personal Classifier (AI) to find matches based on vector embeddings.
    /// </summary>
    private async Task<List<SonicMatch>> GetAiMatchesInternalAsync(PlaylistTrack track)
    {
        var matches = new List<SonicMatch>();
        
        // 1. Get seed embedding
        var seedFeatures = await _databaseService.GetAudioFeaturesByHashAsync(track.TrackUniqueHash);
        
        if (seedFeatures == null || string.IsNullOrEmpty(seedFeatures.AiEmbeddingJson))
        {
            _notificationService.Show("Analysis Required", "This track hasn't been analyzed by the AI yet.", NotificationType.Warning);
            return matches;
        }

        float[]? seedVector = null;
        try 
        {
            seedVector = System.Text.Json.JsonSerializer.Deserialize<float[]>(seedFeatures.AiEmbeddingJson);
        }
        catch { return matches; }

        if (seedVector == null || seedVector.Length != 128) return matches;

        // 2. Load candidates (All features)
        var candidates = await _databaseService.LoadAllAudioFeaturesAsync();
        
        // 3. Perform Vector Search via Classifier Service
        var similarTracks = _personalClassifier.FindSimilarTracks(
            seedVector, 
            (float)(track.BPM ?? 120),
            candidates, 
            limit: 20
        );

        // 4. Hydrate results
        foreach (var (hash, score) in similarTracks)
        {
            if (hash == track.TrackUniqueHash) continue;

            var entry = await _libraryService.FindLibraryEntryAsync(hash);
            if (entry != null)
            {
                matches.Add(new SonicMatch { Entry = entry, Score = score * 100.0 });
            }
        }

        return matches;
    }
}
