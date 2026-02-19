using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.ViewModels.Sidebar;

/// <summary>
/// Shell ViewModel for the Contextual Sidebar.
/// Hosts the active sidebar content VM and routes to the correct mode
/// based on the current track selection in the Library.
/// </summary>
public class ContextualSidebarViewModel : INotifyPropertyChanged
{
    private readonly SimilaritySidebarViewModel _similarityVm;
    private readonly BulkActionSidebarViewModel _bulkVm;
    private readonly MetadataInspectorViewModel _metadataVm;

    // Last remembered track + context for manual mode switching
    private PlaylistTrackViewModel? _lastSingleTrack;
    private IReadOnlyList<PlaylistTrackViewModel> _lastMultiSelection = Array.Empty<PlaylistTrackViewModel>();

    public ContextualSidebarViewModel(
        SimilaritySidebarViewModel similarityVm,
        BulkActionSidebarViewModel bulkVm,
        MetadataInspectorViewModel metadataVm)
    {
        _similarityVm = similarityVm;
        _bulkVm = bulkVm;
        _metadataVm = metadataVm;

        // Commands
        SwitchToSimilarityCommand = new RelayCommand(
            _ => { if (_lastSingleTrack != null) ShowSimilarity(_lastSingleTrack); },
            _ => CanShowSimilarity);
        SwitchToMetadataCommand = new RelayCommand(
            _ => { if (_lastSingleTrack != null) ShowMetadataSilent(_lastSingleTrack); },
            _ => CanShowMetadata);
        SwitchToBulkCommand = new RelayCommand(
            _ => { if (_lastMultiSelection.Count > 0) ShowBulkActions(_lastMultiSelection); },
            _ => CanShowBulk);
        CloseSidebarCommand = new RelayCommand(_ => Hide());
    }

    // ─── Commands ────────────────────────────────────────────────────────────

    public ICommand SwitchToSimilarityCommand { get; }
    public ICommand SwitchToMetadataCommand { get; }
    public ICommand SwitchToBulkCommand { get; }
    public ICommand CloseSidebarCommand { get; }

    // ─── Bindable Properties ────────────────────────────────────────────────

    private SidebarMode _activeMode = SidebarMode.None;
    public SidebarMode ActiveMode
    {
        get => _activeMode;
        private set
        {
            if (SetProperty(ref _activeMode, value))
            {
                OnPropertyChanged(nameof(CanShowSimilarity));
                OnPropertyChanged(nameof(CanShowMetadata));
                OnPropertyChanged(nameof(CanShowBulk));
            }
        }
    }

    private bool _isSidebarOpen;
    /// <summary>
    /// Bound by LibraryPage.axaml — controls whether the panel is visible.
    /// </summary>
    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        private set => SetProperty(ref _isSidebarOpen, value);
    }

    private object? _currentContent;
    public object? CurrentContent
    {
        get => _currentContent;
        private set => SetProperty(ref _currentContent, value);
    }

    private string _modeLabel = string.Empty;
    public string ModeLabel
    {
        get => _modeLabel;
        private set => SetProperty(ref _modeLabel, value);
    }

    private string _modeIcon = string.Empty;
    public string ModeIcon
    {
        get => _modeIcon;
        private set => SetProperty(ref _modeIcon, value);
    }

    // ─── Tab Availability ────────────────────────────────────────────────────

    /// <summary>Single-track modes available only when exactly 1 track is selected.</summary>
    public bool CanShowSimilarity => _lastSingleTrack != null;
    public bool CanShowMetadata => _lastSingleTrack != null;
    /// <summary>Bulk mode available when 2+ tracks are selected.</summary>
    public bool CanShowBulk => _lastMultiSelection.Count > 1;

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called from LibraryViewModel whenever the track selection changes (debounced).
    /// Must be called on the UI thread.
    /// </summary>
    public void UpdateContext(IReadOnlyList<PlaylistTrackViewModel> selectedTracks)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (selectedTracks.Count == 0)
        {
            _lastSingleTrack = null;
            _lastMultiSelection = Array.Empty<PlaylistTrackViewModel>();
            Hide();
        }
        else if (selectedTracks.Count == 1)
        {
            _lastSingleTrack = selectedTracks[0];
            _lastMultiSelection = Array.Empty<PlaylistTrackViewModel>();
            OnPropertyChanged(nameof(CanShowSimilarity));
            OnPropertyChanged(nameof(CanShowMetadata));
            OnPropertyChanged(nameof(CanShowBulk));
            ShowSimilarity(_lastSingleTrack);
        }
        else
        {
            _lastSingleTrack = null;
            _lastMultiSelection = selectedTracks;
            OnPropertyChanged(nameof(CanShowSimilarity));
            OnPropertyChanged(nameof(CanShowMetadata));
            OnPropertyChanged(nameof(CanShowBulk));
            ShowBulkActions(_lastMultiSelection);
        }
    }

    // ─── Private helpers ─────────────────────────────────────────────────────

    private void Hide()
    {
        IsSidebarOpen = false;
        ActiveMode = SidebarMode.None;
        CurrentContent = null;
        ModeLabel = string.Empty;
        ModeIcon = string.Empty;
    }

    private bool ShowSimilarity(PlaylistTrackViewModel track)
    {
        ActiveMode = SidebarMode.Similarity;
        ModeLabel = "Similarity Discovery";
        ModeIcon = "🎯";
        CurrentContent = _similarityVm;
        IsSidebarOpen = true;
        // Load matches asynchronously — SimilaritySidebarViewModel manages its own loading state
        _ = _similarityVm.LoadMatchesAsync(track);
        return true;
    }

    private bool ShowMetadataSilent(PlaylistTrackViewModel track)
    {
        _metadataVm.Load(track);
        ActiveMode = SidebarMode.Metadata;
        ModeLabel = "Track Inspector";
        ModeIcon = "🔍";
        CurrentContent = _metadataVm;
        IsSidebarOpen = true;
        return true;
    }

    /// <summary>
    /// Manually switch to Metadata mode for the given track (e.g. via inspector button).
    /// </summary>
    public void ShowMetadata(PlaylistTrackViewModel track)
    {
        Dispatcher.UIThread.VerifyAccess();
        _lastSingleTrack = track;
        ShowMetadataSilent(track);
    }

    private bool ShowBulkActions(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        ActiveMode = SidebarMode.BulkActions;
        ModeLabel = "Bulk Actions";
        ModeIcon = "⚡";
        CurrentContent = _bulkVm;
        IsSidebarOpen = true;
        _bulkVm.LoadTracks(tracks);
        return true;
    }

    // ─── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ─── Minimal RelayCommand ─────────────────────────────────────────────────

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
