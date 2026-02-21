using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using System.Windows.Input;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class ContextualSidebarViewModel : ReactiveObject, IDisposable
{
    private readonly SimilaritySidebarViewModel _similarityVm;
    private readonly BulkActionSidebarViewModel _bulkVm;
    private readonly MetadataSidebarViewModel _metadataVm;
    private readonly ForensicSidebarViewModel _forensicVm;
    private readonly PlayerViewModel _playerVm;
    private readonly CompositeDisposable _disposables = new();

    private LibrarySidebarMode _activeMode;
    public LibrarySidebarMode ActiveMode
    {
        get => _activeMode;
        private set => this.RaiseAndSetIfChanged(ref _activeMode, value);
    }

    private bool _isSidebarOpen;
    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set => this.RaiseAndSetIfChanged(ref _isSidebarOpen, value);
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

    public ICommand HideCommand { get; }
    public ICommand ShowPlayerCommand { get; }

    public ContextualSidebarViewModel(
        SimilaritySidebarViewModel similarityVm,
        BulkActionSidebarViewModel bulkVm,
        MetadataSidebarViewModel metadataVm,
        ForensicSidebarViewModel forensicVm,
        PlayerViewModel playerVm)
    {
        _similarityVm = similarityVm;
        _bulkVm = bulkVm;
        _metadataVm = metadataVm;
        _forensicVm = forensicVm;
        _playerVm = playerVm;

        HideCommand = ReactiveCommand.Create(Hide);
        ShowPlayerCommand = ReactiveCommand.CreateFromTask(() => ShowModeInternalAsync(LibrarySidebarMode.Player, null, null));

        // Automatically hide if sidebar is closed manually
        this.WhenAnyValue(x => x.IsSidebarOpen)
            .Where(open => !open)
            .Subscribe(_ => DeactivateCurrent())
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
            Hide();
            return;
        }

        if (selectedTracks.Count > 1)
        {
            await ShowModeInternalAsync(LibrarySidebarMode.BulkActions, null, selectedTracks);
        }
        else
        {
            var leadTrack = selectedTracks[0];
            
            // Forensic auto-trigger: LeadTrack.Confidence < 0.6
            // Note: Confidence is handled via a heuristic if not directly present
            if (IsLowConfidence(leadTrack))
            {
                await ShowModeInternalAsync(LibrarySidebarMode.Forensic, leadTrack, null);
            }
            else
            {
                await ShowModeInternalAsync(LibrarySidebarMode.Similarity, leadTrack, null);
            }
        }
    }

    private bool IsLowConfidence(PlaylistTrackViewModel track)
    {
        // Heuristic: If it needs review or has low curation confidence
        // This logic will be refined in Phase 3
        return false; 
    }

    public async Task SwitchToModeAsync(LibrarySidebarMode mode, PlaylistTrackViewModel? leadTrack = null)
    {
        await ShowModeInternalAsync(mode, leadTrack, null);
    }

    private async Task ShowModeInternalAsync(LibrarySidebarMode mode, PlaylistTrackViewModel? leadTrack = null, IReadOnlyList<PlaylistTrackViewModel>? selectedTracks = null)
    {
        DeactivateCurrent();
        ActiveMode = mode;
        IsSidebarOpen = true;

        switch (mode)
        {
            case LibrarySidebarMode.Similarity:
                CurrentContent = _similarityVm;
                ModeLabel = "Similarity Discovery";
                ModeIcon = "🎯";
                if (leadTrack != null) await _similarityVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.BulkActions:
                CurrentContent = _bulkVm;
                ModeLabel = "Bulk Actions";
                ModeIcon = "⚡";
                if (selectedTracks != null) await _bulkVm.ActivateBulkAsync(selectedTracks);
                break;
            case LibrarySidebarMode.Forensic:
                CurrentContent = _forensicVm;
                ModeLabel = "Forensic Repair";
                ModeIcon = "🔬";
                if (leadTrack != null) await _forensicVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.Metadata:
                CurrentContent = _metadataVm;
                ModeLabel = "Metadata Inspector";
                ModeIcon = "🏷️";
                if (leadTrack != null) await _metadataVm.ActivateAsync(leadTrack);
                break;
            case LibrarySidebarMode.Player:
                ModeLabel = "Now Playing";
                ModeIcon = "🎵";
                CurrentContent = _playerVm;
                break;
        }
    }

    private void DeactivateCurrent()
    {
        _similarityVm.Deactivate();
        _bulkVm.Deactivate();
        _metadataVm.Deactivate();
        _forensicVm.Deactivate();
    }

    public void Hide()
    {
        IsSidebarOpen = false;
        ActiveMode = LibrarySidebarMode.None;
        CurrentContent = null;
        DeactivateCurrent();
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
