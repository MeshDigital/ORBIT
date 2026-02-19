using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Services.AI;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.ViewModels.Sidebar;

/// <summary>
/// Sidebar content ViewModel for Similarity Discovery mode (single track selected).
/// Aggregates harmonic matches and AI sonic matches for the selected seed track.
/// </summary>
public class SimilaritySidebarViewModel : INotifyPropertyChanged
{
    private readonly HarmonicMatchService _harmonicMatchService;
    private readonly ISonicMatchService _sonicMatchService;
    private readonly ILibraryService _libraryService;
    private readonly LibraryCacheService _cacheService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<SimilaritySidebarViewModel> _logger;

    private CancellationTokenSource? _cts;

    public SimilaritySidebarViewModel(
        HarmonicMatchService harmonicMatchService,
        ISonicMatchService sonicMatchService,
        ILibraryService libraryService,
        LibraryCacheService cacheService,
        IEventBus eventBus,
        ILogger<SimilaritySidebarViewModel> logger)
    {
        _harmonicMatchService = harmonicMatchService;
        _sonicMatchService = sonicMatchService;
        _libraryService = libraryService;
        _cacheService = cacheService;
        _eventBus = eventBus;
        _logger = logger;
    }

    // ─── Seed Track Info ─────────────────────────────────────────────────────

    private string _seedArtist = string.Empty;
    public string SeedArtist
    {
        get => _seedArtist;
        private set => SetProperty(ref _seedArtist, value);
    }

    private string _seedTitle = string.Empty;
    public string SeedTitle
    {
        get => _seedTitle;
        private set => SetProperty(ref _seedTitle, value);
    }

    private string _seedBpmDisplay = string.Empty;
    public string SeedBpmDisplay
    {
        get => _seedBpmDisplay;
        private set => SetProperty(ref _seedBpmDisplay, value);
    }

    private string _seedKey = string.Empty;
    public string SeedKey
    {
        get => _seedKey;
        private set => SetProperty(ref _seedKey, value);
    }

    private string? _seedArtworkUrl;
    public string? SeedArtworkUrl
    {
        get => _seedArtworkUrl;
        private set => SetProperty(ref _seedArtworkUrl, value);
    }

    // ─── Match Results ────────────────────────────────────────────────────────

    public ObservableCollection<HarmonicMatchViewModel> HarmonicMatches { get; } = new();
    public ObservableCollection<SonicMatch> SonicMatches { get; } = new();

    // ─── Loading State ────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private bool _hasNoResults;
    public bool HasNoResults
    {
        get => _hasNoResults;
        private set => SetProperty(ref _hasNoResults, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task LoadMatchesAsync(PlaylistTrackViewModel track)
    {
        // Cancel any in-flight request
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Update seed track header
        SeedArtist = track.Artist ?? string.Empty;
        SeedTitle = track.Title ?? string.Empty;
        SeedBpmDisplay = track.BPM > 0 ? $"{track.BPM:F0} BPM" : "— BPM";
        SeedKey = track.KeyDisplay;
        SeedArtworkUrl = track.Model?.AlbumArtUrl;

        IsLoading = true;
        HasNoResults = false;
        ErrorMessage = null;
        HarmonicMatches.Clear();
        SonicMatches.Clear();

        try
        {
            // 1. Resolve LibraryEntry.Id (Guid) from hash for HarmonicMatchService
            if (!string.IsNullOrEmpty(track.GlobalId) && !ct.IsCancellationRequested)
            {
                var libraryEntry = await _libraryService.FindLibraryEntryAsync(track.GlobalId);
                if (libraryEntry != null && !ct.IsCancellationRequested)
                {
                    var matches = await _harmonicMatchService.FindMatchesAsync(libraryEntry.Id, limit: 15);
                    if (!ct.IsCancellationRequested)
                    {
                        foreach (var m in matches)
                        {
                            HarmonicMatches.Add(new HarmonicMatchViewModel(m, _eventBus, _libraryService, _cacheService));
                        }
                    }
                }
            }

            // 2. AI Sonic matches (vector embedding similarity)
            if (!string.IsNullOrEmpty(track.GlobalId) && !ct.IsCancellationRequested)
            {
                var sonicMatches = await _sonicMatchService.FindSonicMatchesAsync(
                    track.GlobalId, limit: 10);

                if (!ct.IsCancellationRequested)
                {
                    foreach (var sm in sonicMatches)
                        SonicMatches.Add(sm);
                }
            }

            HasNoResults = HarmonicMatches.Count == 0 && SonicMatches.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // Graceful cancel — new track was selected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimilaritySidebar: Failed to load matches for {Hash}", track.GlobalId);
            ErrorMessage = "Failed to load similarity matches.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ─── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
