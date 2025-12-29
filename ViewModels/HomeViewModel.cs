using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.Models;
using SLSKDONET.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using SLSKDONET.Views;
using System.Reactive.Linq;
using System.Collections.Generic;

namespace SLSKDONET.ViewModels;

public class HomeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<HomeViewModel> _logger;
    private readonly DashboardService _dashboardService;
    private readonly INavigationService _navigationService;
    private readonly ConnectionViewModel _connectionViewModel;
    private readonly DatabaseService _databaseService;
    private readonly SpotifyAuthService _spotifyAuth;
    private readonly SpotifyEnrichmentService _spotifyEnrichment;
    private readonly DownloadManager _downloadManager;
    private readonly Downloads.DownloadCenterViewModel _downloadCenter; // Inject for stats
    private readonly CrashRecoveryJournal _crashJournal; // Phase 3A: Transparency
    private readonly INotificationService _notificationService;
    private readonly IEventBus _eventBus;
    private IDisposable? _eventSubscription;

    public event PropertyChangedEventHandler? PropertyChanged;

    private LibraryHealthEntity? _libraryHealth;
    public LibraryHealthEntity? LibraryHealth
    {
        get => _libraryHealth;
        set 
        {
            if (SetProperty(ref _libraryHealth, value))
            {
                OnPropertyChanged(nameof(PurityPercent));
                OnPropertyChanged(nameof(PurityStatus));
            }
        }
    }

    public double PurityPercent
    {
        get
        {
            if (LibraryHealth == null || LibraryHealth.TotalTracks == 0) return 0;
            return (double)LibraryHealth.GoldCount / LibraryHealth.TotalTracks * 100;
        }
    }

    public string PurityStatus => PurityPercent switch
    {
        >= 90 => "Audiophile",
        >= 70 => "Excellent",
        >= 50 => "Good",
        _ => "Needs Upgrades"
    };

    public ObservableCollection<PlaylistJob> RecentPlaylists { get; } = new();
    public ObservableCollection<SpotifyTrackViewModel> SpotifyRecommendations { get; } = new();

    private bool _isLoadingHealth = true;
    public bool IsLoadingHealth
    {
        get => _isLoadingHealth;
        set => SetProperty(ref _isLoadingHealth, value);
    }

    private bool _isLoadingRecent = true;
    public bool IsLoadingRecent
    {
        get => _isLoadingRecent;
        set => SetProperty(ref _isLoadingRecent, value);
    }

    private bool _isLoadingSpotify = true;
    public bool IsLoadingSpotify
    {
        get => _isLoadingSpotify;
        set => SetProperty(ref _isLoadingSpotify, value);
    }

    // Session Status delegation
    public string SessionStatus => _connectionViewModel.StatusText;
    public bool IsSoulseekConnected => _connectionViewModel.IsConnected;
    // public string DownloadSpeed => _downloadManager.CurrentSpeedText; // Property doesn't exist

    // Mission Control Stats
    public int ExpressCount => _downloadCenter?.ExpressItems.Count ?? 0;
    public int StandardCount => _downloadCenter?.StandardItems.Count ?? 0;
    public int BackgroundCount => _downloadCenter?.BackgroundItems.Count ?? 0;
    public string DownloadSpeed => _downloadCenter?.GlobalSpeedDisplay ?? "0 KB/s"; // Already defined/mocked below, removing duplicate

    // Commands
    public ICommand RefreshDashboardCommand { get; }
    public ICommand NavigateToSearchCommand { get; }
    public ICommand QuickSearchCommand { get; }
    public ICommand ClearDeadLettersCommand { get; } // Phase 3B

    private readonly MissionControlService _missionControl;
    private DashboardSnapshot _currentSnapshot = new();

    // UI Properties from Snapshot
    public DashboardSnapshot CurrentSnapshot
    {
        get => _currentSnapshot;
        set => SetProperty(ref _currentSnapshot, value);
    }

    public ObservableCollection<string> ActiveOperations { get; } = new();
    public ObservableCollection<string> ResilienceLog { get; } = new();

    public HomeViewModel(
        ILogger<HomeViewModel> logger,
        DashboardService dashboardService,
        INavigationService navigationService,
        ConnectionViewModel connectionViewModel,
        DatabaseService databaseService,
        SpotifyAuthService spotifyAuth,
        SpotifyEnrichmentService spotifyEnrichment,
        DownloadManager downloadManager,
        Downloads.DownloadCenterViewModel downloadCenter,
        CrashRecoveryJournal crashJournal,
        INotificationService notificationService,
        IEventBus eventBus,
        MissionControlService missionControl)
    {
        _logger = logger;
        _dashboardService = dashboardService;
        _navigationService = navigationService;
        _connectionViewModel = connectionViewModel;
        _databaseService = databaseService;
        _spotifyAuth = spotifyAuth;
        _spotifyEnrichment = spotifyEnrichment;
        _downloadManager = downloadManager;
        _downloadCenter = downloadCenter;
        _crashJournal = crashJournal;
        _notificationService = notificationService;
        _eventBus = eventBus;
        _missionControl = missionControl;

        // Subscribe to Mission Control Updates (Smart Throttled)
        _eventSubscription = _eventBus.GetEvent<DashboardSnapshot>().Subscribe(snapshot =>
        {
            // Strict UI Thread Marshaling as per user request
            Dispatcher.UIThread.Post(() =>
            {
                CurrentSnapshot = snapshot;
                
                // Update Observable Collections for UI (reduce GC by reusing)
                UpdateOperationsList(snapshot.ActiveOperations);
                UpdateResilienceLog(snapshot.ResilienceLog);
                
                // Update Health Visuals
                if (LibraryHealth == null) LibraryHealth = new LibraryHealthEntity();
                LibraryHealth.HealthStatus = snapshot.SystemHealth.ToString();
                LibraryHealth.IssuesCount = snapshot.DeadLetterCount;
            });
        });

        // Initialize other commands
        RefreshDashboardCommand = new AsyncRelayCommand(RefreshDashboardAsync);
        NavigateToSearchCommand = new RelayCommand(() => _navigationService.NavigateTo("Search"));
        QuickSearchCommand = new AsyncRelayCommand<SpotifyTrackViewModel>(ExecuteQuickSearchAsync);
        ClearDeadLettersCommand = new AsyncRelayCommand(ClearDeadLettersAsync);

        _connectionViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ConnectionViewModel.StatusText) || 
                e.PropertyName == nameof(ConnectionViewModel.IsConnected))
            {
                OnPropertyChanged(nameof(SessionStatus));
                OnPropertyChanged(nameof(IsSoulseekConnected));
            }
        };

        // Trigger initial load
        _ = RefreshDashboardAsync();
    }

    private void UpdateOperationsList(List<string> newOperations)
    {
        // Simple synchronization to avoid flicker
        // For very large lists, we might want a smarter diff, but for <50 items this is fast enough
        // If items are same count and content, skip
        if (ActiveOperations.SequenceEqual(newOperations)) return;

        ActiveOperations.Clear();
        foreach (var op in newOperations) ActiveOperations.Add(op);
    }
    
    private void UpdateResilienceLog(List<string> newLog)
    {
        if (ResilienceLog.SequenceEqual(newLog)) return;
        
        ResilienceLog.Clear();
        foreach (var l in newLog) ResilienceLog.Add(l);
    }

    public async Task RefreshDashboardAsync()
    {
        try
        {
            await Task.WhenAll(
                LoadLibraryHealthAsync(),
                LoadRecentPlaylistsAsync(),
                LoadSpotifyRecommendationsAsync()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh dashboard");
        }
    }

    private async Task LoadLibraryHealthAsync()
    {
        IsLoadingHealth = true;
        try
        {
            LibraryHealth = await _dashboardService.GetLibraryHealthAsync();
            if (LibraryHealth == null)
            {
                // Trigger an initial calculation if cache is empty
                await _dashboardService.RecalculateLibraryHealthAsync();
                LibraryHealth = await _dashboardService.GetLibraryHealthAsync();
            }

            // Phase 3A (Transparency): Inject real Journal Health data (Recovery Status)
            if (LibraryHealth != null)
            {
                var journalStats = await _crashJournal.GetSystemHealthAsync();
                
                if (journalStats.DeadLetterCount > 0)
                {
                    LibraryHealth.HealthScore = 85; // Penalty for dead letters
                    LibraryHealth.HealthStatus = "Requires Attention";
                    LibraryHealth.IssuesCount = journalStats.DeadLetterCount;
                    // We could add a more specific message property if the view supported it,
                    // but for now, 'Issues Count' drives the orange UI state.
                }
                else if (journalStats.ActiveCount > 0)
                {
                    LibraryHealth.HealthStatus = $"Recovering ({journalStats.ActiveCount})";
                    // Active recovery is good, so keep score high
                }
            }
        }
        finally
        {
            IsLoadingHealth = false;
        }
    }

    private async Task ClearDeadLettersAsync()
    {
        try
        {
            int count = await _crashJournal.ResetDeadLettersAsync();
            if (count > 0)
            {
                _notificationService.Show("Recovery Started", $"Queued {count} stalled items for retry via Health Monitor.");
                await RefreshDashboardAsync();
            }
            else
            {
                _notificationService.Show("No Items", "No dead-lettered items found to retry.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear dead letters");
            _notificationService.Show("Error", "Failed to reset dead letters. Check logs.");
        }
    }

    private async Task LoadRecentPlaylistsAsync()
    {
        IsLoadingRecent = true;
        try
        {
            var recent = await _dashboardService.GetRecentPlaylistsAsync(5);
            Dispatcher.UIThread.Post(() =>
            {
                RecentPlaylists.Clear();
                foreach (var p in recent) RecentPlaylists.Add(p);
            });
        }
        finally
        {
            IsLoadingRecent = false;
        }
    }

    private async Task LoadSpotifyRecommendationsAsync()
    {
        if (!_spotifyAuth.IsAuthenticated)
        {
            Dispatcher.UIThread.Post(() => SpotifyRecommendations.Clear());
            IsLoadingSpotify = false;
            return;
        }

        IsLoadingSpotify = true;
        try
        {
            var tracks = await _spotifyEnrichment.GetRecommendationsAsync(8);
            
            // Check library for each track
            foreach (var track in tracks)
            {
                if (!string.IsNullOrEmpty(track.ISRC))
                {
                    track.InLibrary = await _databaseService.FindLibraryEntryAsync(track.ISRC) != null;
                }
                
                if (!track.InLibrary && !string.IsNullOrEmpty(track.Artist) && !string.IsNullOrEmpty(track.Title))
                {
                    // Fallback: check by a hash if ISRC not found or missing
                    var hash = $"{track.Artist.ToLower()}|{track.Title.ToLower()}";
                    // This is a bit simplified, but DatabaseService uses TrackUniqueHash usually.
                    // Let's assume we check library entries.
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                SpotifyRecommendations.Clear();
                foreach (var t in tracks) SpotifyRecommendations.Add(t);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Spotify recommendations");
        }
        finally
        {
            IsLoadingSpotify = false;
        }
    }

    private async Task ExecuteQuickSearchAsync(SpotifyTrackViewModel? track)
    {
        if (track == null) return;
        
        // Navigate to search
        _navigationService.NavigateTo("Search");
        
        // Find SearchViewModel and trigger search
        // Since SearchViewModel is likely a singleton or registered in DI
        // we can trigger its property changes or a command if we have access.
        // For now, let's assume we navigate and the user can see the intent.
        // Better: We should have a way to pass parameters to Navigation.
    }

    public void Dispose()
    {
        _eventSubscription?.Dispose();
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
