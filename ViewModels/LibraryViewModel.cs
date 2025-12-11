
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand if needed, or stick to CommunityToolkit if avail? Using RelayCommand from Views based on existing code.

namespace SLSKDONET.ViewModels;

public class LibraryViewModel : INotifyPropertyChanged
{
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DownloadManager _downloadManager;
    
    public CollectionViewSource ActiveTracksInit { get; } = new();
    public ICollectionView ActiveTracksView => ActiveTracksInit.View;

    public CollectionViewSource WarehouseTracksInit { get; } = new();
    public ICollectionView WarehouseTracksView => WarehouseTracksInit.View;

    public ICommand HardRetryCommand { get; }

    public LibraryViewModel(ILogger<LibraryViewModel> logger, DownloadManager downloadManager)
    {
        _logger = logger;
        _downloadManager = downloadManager;

        // Initialize Active View
        ActiveTracksInit.Source = _downloadManager.AllGlobalTracks;
        ActiveTracksInit.IsLiveFilteringRequested = true;
        ActiveTracksInit.LiveFilteringProperties.Add("State");
        ActiveTracksInit.IsLiveSortingRequested = true;
        ActiveTracksInit.LiveSortingProperties.Add("Progress");
        ActiveTracksInit.Filter += ActiveTracks_Filter;
        ActiveTracksInit.SortDescriptions.Add(new SortDescription("State", ListSortDirection.Ascending));

        // Initialize Warehouse View
        WarehouseTracksInit.Source = _downloadManager.AllGlobalTracks;
        WarehouseTracksInit.IsLiveFilteringRequested = true;
        WarehouseTracksInit.LiveFilteringProperties.Add("State");
        WarehouseTracksInit.IsLiveSortingRequested = true; // Optional for warehouse
        WarehouseTracksInit.LiveSortingProperties.Add("Artist");
        WarehouseTracksInit.Filter += WarehouseTracks_Filter;
        WarehouseTracksInit.SortDescriptions.Add(new SortDescription("SortOrder", ListSortDirection.Ascending)); 
        
        HardRetryCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteHardRetry);
    }

    public void ReorderTrack(PlaylistTrackViewModel source, PlaylistTrackViewModel target)
    {
        if (source == null || target == null || source == target) return;

        // Simple implementation: Swap SortOrder
        // Better implementation: Insert
        // Renumbering everything is safest for consistency
        
        // Find current indices in the underlying collection? 
        // We really want to change SortOrder values.
        
        // Let's adopt a "dense rank" approach.
        // First, ensure everyone has a SortOrder. if 0, assign based on current index.
        
        var allTracks = _downloadManager.AllGlobalTracks; // This is the source
        // But we are only reordering within "Warehouse" view ideally. 
        // Mixing active/warehouse reordering is tricky.
        // Assuming we drag pending items.
        
        int oldIndex = source.SortOrder;
        int newIndex = target.SortOrder;
        
        if (oldIndex == newIndex) return;
        
        // Shift items
        foreach (var track in allTracks)
        {
            if (oldIndex < newIndex)
            {
                // Moving down: shift items between old and new UP (-1)
                if (track.SortOrder > oldIndex && track.SortOrder <= newIndex)
                {
                    track.SortOrder--;
                }
            }
            else
            {
                // Moving up: shift items between new and old DOWN (+1)
                if (track.SortOrder >= newIndex && track.SortOrder < oldIndex)
                {
                    track.SortOrder++;
                }
            }
        }
        
        source.SortOrder = newIndex;
        // Verify uniqueness? If we started with unique 0..N, we end with unique 0..N
    }

    private void ActiveTracks_Filter(object sender, FilterEventArgs e)
    {
        if (e.Item is PlaylistTrackViewModel vm)
        {
            // Active: Searching, Downloading, Queued
            e.Accepted = vm.State == PlaylistTrackState.Searching ||
                         vm.State == PlaylistTrackState.Downloading ||
                         vm.State == PlaylistTrackState.Queued;
        }
    }

    private void WarehouseTracks_Filter(object sender, FilterEventArgs e)
    {
        if (e.Item is PlaylistTrackViewModel vm)
        {
            // Warehouse: Pending, Completed, Failed, Cancelled
            // Essentially !Active
            e.Accepted = vm.State == PlaylistTrackState.Pending ||
                         vm.State == PlaylistTrackState.Completed ||
                         vm.State == PlaylistTrackState.Failed ||
                         vm.State == PlaylistTrackState.Cancelled;
        }
    }

    private void ExecuteHardRetry(PlaylistTrackViewModel? vm)
    {
        if (vm == null) return;
        
        _logger.LogInformation("Hard Retry requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.HardRetryTrack(vm.GlobalId);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
