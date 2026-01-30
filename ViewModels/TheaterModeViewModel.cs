using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels;

public class TheaterModeViewModel : ReactiveObject
{
    private readonly PlayerViewModel _playerViewModel;
    private readonly INavigationService _navigationService;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;

    public PlayerViewModel Player => _playerViewModel;

    private bool _isLibraryVisible = true;
    public bool IsLibraryVisible
    {
        get => _isLibraryVisible;
        set => this.RaiseAndSetIfChanged(ref _isLibraryVisible, value);
    }

    private bool _isTechnicalVisible = true;
    public bool IsTechnicalVisible
    {
        get => _isTechnicalVisible;
        set => this.RaiseAndSetIfChanged(ref _isTechnicalVisible, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public ObservableCollection<PlaylistTrackViewModel> SearchResults { get; } = new();

    public TheaterModeViewModel(
        PlayerViewModel playerViewModel, 
        INavigationService navigationService,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache)
    {
        _playerViewModel = playerViewModel;
        _navigationService = navigationService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        
        CloseTheaterCommand = ReactiveCommand.Create(CloseTheater);
        ToggleLibraryCommand = ReactiveCommand.Create(() => IsLibraryVisible = !IsLibraryVisible);
        ToggleTechnicalCommand = ReactiveCommand.Create(() => IsTechnicalVisible = !IsTechnicalVisible);
        PlayTrackCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(PlayTrack);
        AddToQueueCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(AddToQueue);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async query => await PerformSearchAsync(query));
    }

    public System.Windows.Input.ICommand CloseTheaterCommand { get; }
    public System.Windows.Input.ICommand ToggleLibraryCommand { get; }
    public System.Windows.Input.ICommand ToggleTechnicalCommand { get; }
    public System.Windows.Input.ICommand PlayTrackCommand { get; }
    public System.Windows.Input.ICommand AddToQueueCommand { get; }

    private void CloseTheater()
    {
        _navigationService.GoBack();
    }

    private void PlayTrack(PlaylistTrackViewModel track)
    {
        if (track == null) return;
        _eventBus.Publish(new SLSKDONET.Models.PlayTrackRequestEvent(track));
    }

    private void AddToQueue(PlaylistTrackViewModel track)
    {
        if (track == null) return;
        _eventBus.Publish(new SLSKDONET.Models.AddToQueueRequestEvent(track));
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            return;
        }

        try
        {
            var results = await _libraryService.SearchLibraryEntriesWithStatusAsync(query, 20);
            SearchResults.Clear();
            foreach (var entry in results)
            {
                // Map LibraryEntry to PlaylistTrackViewModel
                var model = new PlaylistTrack
                {
                    Artist = entry.Artist,
                    Title = entry.Title,
                    Album = entry.Album,
                    TrackUniqueHash = entry.UniqueHash,
                    ResolvedFilePath = entry.FilePath,
                    Status = TrackStatus.Downloaded,
                    Format = entry.Format,
                    Bitrate = entry.Bitrate,
                    MusicalKey = entry.MusicalKey,
                    BPM = entry.BPM
                };
                
                var vm = new PlaylistTrackViewModel(model, _eventBus, _libraryService, _artworkCache);
                SearchResults.Add(vm);
            }
        }
        catch (Exception)
        {
            // Log error
        }
    }
}
