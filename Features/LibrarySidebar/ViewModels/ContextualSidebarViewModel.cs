using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using System.Windows.Input;
using SLSKDONET.ViewModels;
using System.Linq;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// Orchestrator for the Docked Contextual Workspace. 
/// Manages navigation between modules and selection state tracking.
/// </summary>
public class ContextualSidebarViewModel : ReactiveObject, IDisposable
{
    private readonly SimilaritySidebarViewModel _similarityVm;
    private readonly BulkActionSidebarViewModel _bulkVm;
    private readonly MetadataSidebarViewModel _metadataVm;
    private readonly ForensicSidebarViewModel _forensicVm;
    private readonly PlayerViewModel _playerVm;
    private readonly CueSidebarViewModel _cueVm;
    private readonly StemSidebarViewModel _stemVm;
    private readonly VibeSidebarViewModel _vibeVm;
    private readonly TransitionProberViewModel _transitionVm;
    private readonly CompositeDisposable _disposables = new();

    private LibrarySidebarMode _activeMode = LibrarySidebarMode.None;
    public LibrarySidebarMode ActiveMode
    {
        get => _activeMode;
        set 
        {
            if (_activeMode != value)
            {
                this.RaiseAndSetIfChanged(ref _activeMode, value);
                _ = ShowModeInternalAsync(value);
            }
        }
    }

    private bool _isSidebarOpen;
    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => this.RaiseAndSetIfChanged(ref _isSidebarOpen, value);
    }

    private ISidebarContent? _activeContent;
    public ISidebarContent? ActiveContent
    {
        get => _activeContent;
        private set 
        {
            this.RaiseAndSetIfChanged(ref _activeContent, value);
            CurrentContent = value;
        }
    }

    private object? _currentContent;
    public object? CurrentContent
    {
        get => _currentContent;
        private set => this.RaiseAndSetIfChanged(ref _currentContent, value);
    }

    private string _modeLabel = string.Empty;
    public string ModeLabel
    {
        get => _modeLabel;
        private set => this.RaiseAndSetIfChanged(ref _modeLabel, value);
    }

    private string _modeIcon = string.Empty;
    public string ModeIcon
    {
        get => _modeIcon;
        private set => this.RaiseAndSetIfChanged(ref _modeIcon, value);
    }

    private PlaylistTrackViewModel? _primarySelection;
    public PlaylistTrackViewModel? PrimarySelection
    {
        get => _primarySelection;
        set => this.RaiseAndSetIfChanged(ref _primarySelection, value);
    }

    private PlaylistTrackViewModel? _secondarySelection;
    public PlaylistTrackViewModel? SecondarySelection
    {
        get => _secondarySelection;
        set => this.RaiseAndSetIfChanged(ref _secondarySelection, value);
    }

    public ICommand HideCommand { get; }
    public ICommand ShowPlayerCommand { get; }
    public ICommand SetModeCommand { get; }

    public ContextualSidebarViewModel(
        SimilaritySidebarViewModel similarityVm,
        BulkActionSidebarViewModel bulkVm,
        MetadataSidebarViewModel metadataVm,
        ForensicSidebarViewModel forensicVm,
        PlayerViewModel playerVm,
        CueSidebarViewModel cueVm,
        StemSidebarViewModel stemVm,
        VibeSidebarViewModel vibeVm,
        TransitionProberViewModel transitionVm)
    {
        _similarityVm = similarityVm;
        _bulkVm = bulkVm;
        _metadataVm = metadataVm;
        _forensicVm = forensicVm;
        _playerVm = playerVm;
        _cueVm = cueVm;
        _stemVm = stemVm;
        _vibeVm = vibeVm;
        _transitionVm = transitionVm;

        HideCommand = ReactiveCommand.Create(Hide);
        ShowPlayerCommand = ReactiveCommand.CreateFromTask(() => ShowModeInternalAsync(LibrarySidebarMode.Player, null, null));
        SetModeCommand = ReactiveCommand.Create<LibrarySidebarMode>(mode => ActiveMode = mode);

        // Automatically hide if sidebar is closed manually
        this.WhenAnyValue(x => x.IsSidebarOpen)
            .Where(open => !open)
            .Subscribe(_ => DeactivateCurrent())
            .DisposeWith(_disposables);

        // Monitor Secondary Selection from Similarity VM
        _similarityVm.WhenAnyValue(x => x.SelectedMatch)
            .Subscribe(match => 
            {
                SecondarySelection = match?.TrackVm;
            })
            .DisposeWith(_disposables);

        // Propagate Secondary Selection to child modules
        this.WhenAnyValue(x => x.SecondarySelection)
            .Subscribe(track => 
            {
                _vibeVm.SetSecondaryTrack(track);
                _transitionVm.SetSecondaryTrack(track);
            })
            .DisposeWith(_disposables);

        // Task 3 Hook: Auto-trigger Transition Prober when both selections are present
        this.WhenAnyValue(x => x.PrimarySelection, x => x.SecondarySelection)
            .Subscribe(async items => 
            {
                if (items.Item1 != null && items.Item2 != null)
                {
                   // We don't necessarily force the mode, but we prepare the VM
                   await _transitionVm.ActivateAsync(items.Item1);
                   // _transitionVm.SetSecondaryTrack(items.Item2); // Handled by propagation above
                }
            })
            .DisposeWith(_disposables);
    }

    public void AttachToSelection(IObservable<IReadOnlyList<PlaylistTrackViewModel>> selectionStream)
    {
        selectionStream
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async tracks => await UpdateContextAsync(tracks))
            .DisposeWith(_disposables);
    }

    private async Task UpdateContextAsync(IReadOnlyList<PlaylistTrackViewModel> selectedTracks)
    {
        if (selectedTracks == null || selectedTracks.Count == 0)
        {
            PrimarySelection = null;
            // Don't fully hide yet? Or maybe just stop updates?
            // If primary is lost, we logically can't have a sidebar context
            Hide();
            return;
        }

        if (selectedTracks.Count > 1)
        {
            PrimarySelection = null;
            await ShowModeInternalAsync(LibrarySidebarMode.BulkActions, null, selectedTracks);
        }
        else
        {
            var leadTrack = selectedTracks[0];
            PrimarySelection = leadTrack;
            
            // Logic Persistence: If we are already in a mode (like Stems), staying there is preferred
            if (ActiveMode != LibrarySidebarMode.None && ActiveMode != LibrarySidebarMode.BulkActions)
            {
                await ShowModeInternalAsync(ActiveMode, leadTrack, null);
            }
            else
            {
                // Forensic auto-trigger: LeadTrack.Confidence < 0.6
                if (IsLowConfidence(leadTrack))
                {
                    await ShowModeInternalAsync(LibrarySidebarMode.Forensic, leadTrack, null);
                }
                else
                {
                    // Default to similarity
                    await ShowModeInternalAsync(LibrarySidebarMode.Similarity, leadTrack, null);
                }
            }
        }
    }

    private bool IsLowConfidence(PlaylistTrackViewModel track)
    {
        // Placeholder for Phase 3/4 logic
        return false; 
    }

    public async Task SwitchToModeAsync(LibrarySidebarMode mode, PlaylistTrackViewModel? leadTrack = null)
    {
        await ShowModeInternalAsync(mode, leadTrack ?? PrimarySelection, null);
    }

    private async Task ShowModeInternalAsync(LibrarySidebarMode mode, PlaylistTrackViewModel? leadTrack = null, IReadOnlyList<PlaylistTrackViewModel>? selectedTracks = null)
    {
        // Don't deactivate if we are already in the target mode (just updating track)
        bool isInitialActivation = _activeMode != mode;
        
        if (isInitialActivation)
        {
            DeactivateCurrent();
            _activeMode = mode;
            this.RaisePropertyChanged(nameof(ActiveMode));
        }

        IsSidebarOpen = true;

        switch (mode)
        {
            case LibrarySidebarMode.Similarity:
                ActiveContent = _similarityVm;
                ModeLabel = "Similarity Discovery";
                ModeIcon = "🎯";
                if (leadTrack != null) await _similarityVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.BulkActions:
                ActiveContent = _bulkVm;
                ModeLabel = "Bulk Actions";
                ModeIcon = "⚡";
                if (selectedTracks != null) await _bulkVm.ActivateBulkAsync(selectedTracks);
                break;
            case LibrarySidebarMode.Forensic:
                ActiveContent = _forensicVm;
                ModeLabel = "Forensic Repair";
                ModeIcon = "🔬";
                if (leadTrack != null) await _forensicVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.Metadata:
                ActiveContent = _metadataVm;
                ModeLabel = "Metadata Inspector";
                ModeIcon = "🏷️";
                if (leadTrack != null) await _metadataVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.Player:
                ActiveContent = _playerVm;
                ModeLabel = "Now Playing";
                ModeIcon = "🎵";
                break;
            case LibrarySidebarMode.Cues:
                ActiveContent = _cueVm;
                ModeLabel = "Cue & Phrase Inspector";
                ModeIcon = "📍";
                if (leadTrack != null) await _cueVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.Stems:
                ActiveContent = _stemVm;
                ModeLabel = "STEMS Manipulation";
                ModeIcon = "🎛️";
                if (leadTrack != null) await _stemVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.VibeLab:
                ActiveContent = _vibeVm;
                ModeLabel = "Vibe Lab";
                ModeIcon = "🎭";
                if (leadTrack != null) await _vibeVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.TransitionProber:
                ActiveContent = _transitionVm;
                ModeLabel = "Transition Prober";
                ModeIcon = "🔄";
                if (leadTrack != null) await _transitionVm.ActivateAsync(leadTrack);
                if (SecondarySelection != null) _transitionVm.SetSecondaryTrack(SecondarySelection);
                break;
        }
    }

    private void DeactivateCurrent()
    {
        _similarityVm.Deactivate();
        _bulkVm.Deactivate();
        _metadataVm.Deactivate();
        _forensicVm.Deactivate();
        _cueVm.Deactivate();
        _stemVm.Deactivate();
        _playerVm.Deactivate();
        _vibeVm.Deactivate();
        _transitionVm.Deactivate();
    }

    public void Hide()
    {
        IsSidebarOpen = false;
        _activeMode = LibrarySidebarMode.None;
        this.RaisePropertyChanged(nameof(ActiveMode));
        ActiveContent = null;
        DeactivateCurrent();
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
