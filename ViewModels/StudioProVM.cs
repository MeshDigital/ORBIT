using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Features.LibrarySidebar.ViewModels;

namespace SLSKDONET.ViewModels;

public class StudioProViewModel : ReactiveObject, IDisposable
{
    private readonly SLSKDONET.Services.ILibraryService _libraryService;
    private readonly SLSKDONET.Services.IEventBus _eventBus;
    private readonly SLSKDONET.Services.ArtworkCacheService _artworkCacheService;
    private readonly CompositeDisposable _disposables = new();

    // Injected AI tool ViewModels (Reuse of brains)
    public CueSidebarViewModel CueViewModel { get; }
    public StemSidebarViewModel StemViewModel { get; }
    public TransitionProberViewModel ProberViewModel { get; }
    public SLSKDONET.ViewModels.PlayerViewModel PlayerViewModel { get; }

    // Phase 2: High-Fidelity Grid Properties
    public ObservableCollection<IDisplayableTrack> WorkspaceTracks { get; } = new();

    private IDisplayableTrack? _selectedStudioTrack;
    public IDisplayableTrack? SelectedStudioTrack
    {
        get => _selectedStudioTrack;
        set => this.RaiseAndSetIfChanged(ref _selectedStudioTrack, value);
    }

    private string _workspaceTitle = "ORBIT STUDIO";
    public string WorkspaceTitle
    {
        get => _workspaceTitle;
        set => this.RaiseAndSetIfChanged(ref _workspaceTitle, value);
    }

    private int _cpuLoad = 12;
    public int CpuLoad
    {
        get => _cpuLoad;
        set => this.RaiseAndSetIfChanged(ref _cpuLoad, value);
    }

    private int _stemEngineLoad = 45;
    public int StemEngineLoad
    {
        get => _stemEngineLoad;
        set => this.RaiseAndSetIfChanged(ref _stemEngineLoad, value);
    }

    public StudioProViewModel(
        SLSKDONET.Services.ILibraryService libraryService, 
        SLSKDONET.Services.IEventBus eventBus, 
        SLSKDONET.Services.ArtworkCacheService artworkCacheService,
        CueSidebarViewModel cueViewModel,
        StemSidebarViewModel stemViewModel,
        TransitionProberViewModel proberViewModel,
        SLSKDONET.ViewModels.PlayerViewModel playerViewModel)
    {
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _artworkCacheService = artworkCacheService ?? throw new ArgumentNullException(nameof(artworkCacheService));
        
        CueViewModel = cueViewModel ?? throw new ArgumentNullException(nameof(cueViewModel));
        StemViewModel = stemViewModel ?? throw new ArgumentNullException(nameof(stemViewModel));
        ProberViewModel = proberViewModel ?? throw new ArgumentNullException(nameof(proberViewModel));
        PlayerViewModel = playerViewModel ?? throw new ArgumentNullException(nameof(playerViewModel));

        _ = LoadTracksAsync();

        // Handle selection sync
        this.WhenAnyValue(x => x.SelectedStudioTrack)
            .WhereNotNull()
            .Subscribe(track => HydrateInspectorPanels(track))
            .DisposeWith(_disposables);
    }

    private async Task LoadTracksAsync()
    {
        try
        {
            var allTracks = await _libraryService.GetAllPlaylistTracksAsync();
            if (allTracks == null) return;

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                WorkspaceTracks.Clear();
                foreach (var track in allTracks.Take(100))
                {
                    var vm = new SLSKDONET.ViewModels.PlaylistTrackViewModel(track, _eventBus, _libraryService, _artworkCacheService);
                    WorkspaceTracks.Add(vm);
                }
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load tracks for Orbit Studio");
        }
    }

    private void HydrateInspectorPanels(IDisplayableTrack track)
    {
        if (track is SLSKDONET.ViewModels.PlaylistTrackViewModel playlistVM)
        {
            _eventBus.Publish(new SLSKDONET.Models.PlayTrackRequestEvent(playlistVM));
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
