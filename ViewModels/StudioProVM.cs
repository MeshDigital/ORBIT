using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Features.LibrarySidebar.ViewModels;
using SLSKDONET.ViewModels.Studio;

namespace SLSKDONET.ViewModels;

public class StudioProViewModel : ReactiveObject, IDisposable
{
    private readonly SLSKDONET.Services.ILibraryService _libraryService;
    private readonly SLSKDONET.Services.IEventBus _eventBus;
    private readonly SLSKDONET.Services.ArtworkCacheService _artworkCacheService;
    private readonly CompositeDisposable _disposables = new();
    private CancellationTokenSource? _contextCts;

    // Injected AI tool ViewModels
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

        // THE MASTER UI CASCADE: Observe selection with throttle to prevent "Arrow-Key Spam"
        this.WhenAnyValue(x => x.SelectedStudioTrack)
            .Throttle(TimeSpan.FromMilliseconds(150), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async track => await OnTrackSelectedAsync(track))
            .DisposeWith(_disposables);
    }

    private async Task OnTrackSelectedAsync(IDisplayableTrack? track)
    {
        // 1. Cancel previous pending hydrations
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _contextCts = new CancellationTokenSource();
        var token = _contextCts.Token;

        try
        {
            if (track == null)
            {
                ClearAllModuleContexts();
                return;
            }

            // 2. Parallel Hydration across all DAW panels
            // Note: We cast to IStudioModuleViewModel for Phase 3/4 support
            var modules = new[] 
            { 
                CueViewModel as IStudioModuleViewModel, 
                StemViewModel as IStudioModuleViewModel, 
                ProberViewModel as IStudioModuleViewModel, 
                PlayerViewModel as IStudioModuleViewModel 
            }.Where(m => m != null).Select(m => m!).ToList();

            await Task.WhenAll(modules.Select(m => m.LoadTrackContextAsync(track, token)));
        }
        catch (OperationCanceledException)
        {
            // Expected when user is scrolling fast
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Error during Studio Track hydration cascade");
        }
    }

    private void ClearAllModuleContexts()
    {
        (CueViewModel as IStudioModuleViewModel)?.ClearContext();
        (StemViewModel as IStudioModuleViewModel)?.ClearContext();
        (ProberViewModel as IStudioModuleViewModel)?.ClearContext();
        (PlayerViewModel as IStudioModuleViewModel)?.ClearContext();
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

    public void Dispose()
    {
        _contextCts?.Cancel();
        _contextCts?.Dispose();
        _disposables.Dispose();
    }
}
