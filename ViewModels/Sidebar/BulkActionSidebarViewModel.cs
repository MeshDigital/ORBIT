using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Services; // For IBulkOperationCoordinator
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels.Sidebar;

/// <summary>
/// Sidebar content ViewModel for Bulk Actions mode (multiple tracks selected).
/// Provides batch operation info for the current multi-track selection.
/// </summary>
public class BulkActionSidebarViewModel : INotifyPropertyChanged
{
    private readonly IBulkOperationCoordinator _bulkCoordinator;
    private readonly TrackOperationsViewModel _ops;

    private IReadOnlyList<PlaylistTrackViewModel> _selectedTracks = Array.Empty<PlaylistTrackViewModel>();

    public BulkActionSidebarViewModel(
        IBulkOperationCoordinator bulkCoordinator,
        TrackOperationsViewModel ops)
    {
        _bulkCoordinator = bulkCoordinator;
        _ops = ops;

        DownloadMissingCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // Trigger download retry for tracks that are failed/missing
            if (_ops.RetryOfflineTracksCommand.CanExecute(null))
            {
                _ops.RetryOfflineTracksCommand.Execute(null);
            }
            await Task.CompletedTask;
        });

        RemoveFromPlaylistCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var tracks = _selectedTracks.ToArray(); // Snapshot
            foreach (var track in tracks)
            {
                if (_ops.RemoveTrackCommand.CanExecute(track))
                    _ops.RemoveTrackCommand.Execute(track);
            }
            await Task.CompletedTask;
        });

        ReAnalyzeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // Trigger Industrial Prep for selected tracks
            if (_ops.IndustrialPrepCommand.CanExecute(_selectedTracks))
                _ops.IndustrialPrepCommand.Execute(_selectedTracks);
            await Task.CompletedTask;
        });

        AddToSmartCrateCommand = ReactiveCommand.Create(() =>
        {
            // Placeholder — smart crate assignment is wired at Library level
            // Could use _ops.AddToProjectCommand or similar if generalized
        });
    }

    // ─── Selection Stats ──────────────────────────────────────────────────────

    private string _selectionSummary = string.Empty;
    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => SetProperty(ref _selectionSummary, value);
    }

    private int _selectedCount;
    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    private int _downloadedCount;
    public int DownloadedCount
    {
        get => _downloadedCount;
        private set => SetProperty(ref _downloadedCount, value);
    }

    private int _missingCount;
    public int MissingCount
    {
        get => _missingCount;
        private set => SetProperty(ref _missingCount, value);
    }

    private string _bpmRange = string.Empty;
    public string BpmRange
    {
        get => _bpmRange;
        private set => SetProperty(ref _bpmRange, value);
    }

    private string _keyDistribution = string.Empty;
    public string KeyDistribution
    {
        get => _keyDistribution;
        private set => SetProperty(ref _keyDistribution, value);
    }

    // ─── Commands ─────────────────────────────────────────────────────────────

    public ICommand DownloadMissingCommand { get; }
    public ICommand RemoveFromPlaylistCommand { get; }
    public ICommand ReAnalyzeCommand { get; }
    public ICommand AddToSmartCrateCommand { get; }

    // ─── Public API ───────────────────────────────────────────────────────────

    public void LoadTracks(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        _selectedTracks = tracks;
        var count = tracks.Count;

        SelectedCount = count;
        DownloadedCount = tracks.Count(t => t.IsCompleted);
        MissingCount = count - DownloadedCount;
        SelectionSummary = $"{count} tracks selected";

        // BPM range: BPM is non-nullable double, 0 means unknown
        var bpms = tracks
            .Select(t => t.BPM)
            .Where(b => b > 0)
            .ToList();

        BpmRange = bpms.Count > 0
            ? $"{bpms.Min():F0}–{bpms.Max():F0} BPM"
            : "—";

        // Top 3 keys
        var keys = tracks
            .Where(t => !string.IsNullOrEmpty(t.MusicalKey) && t.MusicalKey != "—")
            .GroupBy(t => t.MusicalKey)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key)
            .ToList();

        KeyDistribution = keys.Count > 0 ? string.Join(" · ", keys) : "—";
    }

    // ─── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
