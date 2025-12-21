using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives; // Added for TreeDataGridRow
using Avalonia.Controls.Selection; // Added for ITreeDataGridRowSelectionModel
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class LibraryPage : UserControl
{
    public LibraryPage()
    {
        InitializeComponent();
        
        // Enable drag-drop on playlist ListBox
        AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Find the playlist ListBox and enable drop
        var playlistListBox = this.FindControl<ListBox>("PlaylistListBox");
        if (playlistListBox != null)
        {
            DragDrop.SetAllowDrop(playlistListBox, true);
        }
        
        // Find the track TreeDataGrid and enable drag
        var treeDataGrid = this.FindControl<TreeDataGrid>("TracksTreeDataGrid");
        if (treeDataGrid != null)
        {
            treeDataGrid.AddHandler(PointerPressedEvent, OnTrackPointerPressed, RoutingStrategies.Tunnel);
            treeDataGrid.AddHandler(PointerMovedEvent, OnTrackPointerMoved, RoutingStrategies.Tunnel);
            treeDataGrid.AddHandler(PointerReleasedEvent, OnTrackPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    private void OnTreeDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            // The clicked item is in the Source.Selection
            if (sender is TreeDataGrid grid)
            {
                if (grid.Source is ITreeDataGridSource<PlaylistTrackViewModel> source && 
                    source.Selection is ITreeDataGridRowSelectionModel<PlaylistTrackViewModel> selection &&
                    selection.SelectedItem is PlaylistTrackViewModel track)
                {
                {
                    if (vm.PlayTrackCommand.CanExecute(track))
                    {
                        vm.PlayTrackCommand.Execute(track);
                    }
                }
                }
            }
        }
    }

    private Point? _dragStartPoint;
    private PlaylistTrackViewModel? _draggedTrack;
    private DragAdornerService? _adornerService;

    private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Find row in TreeDataGrid
            var row = (e.Source as Control)?.FindAncestorOfType<TreeDataGridRow>();
            if (row?.DataContext is PlaylistTrackViewModel track)
            {
                _dragStartPoint = e.GetPosition(this);
                _draggedTrack = track;
            }
        }
    }

    private async void OnTrackPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint.HasValue && _draggedTrack != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var currentPoint = e.GetPosition(this);
            // The original instruction snippet was malformed. Assuming the intent was to add a dropTarget variable
            // and keep the 'diff' calculation, and that 'parent' was a placeholder for 'this.Parent'.
            // Making a best effort to produce syntactically correct code based on the instruction's intent.
            var dropTarget = this.Parent as Control ?? (Control)this; // Corrected 'parent' to 'this.Parent' for compilation
            var diff = currentPoint - _dragStartPoint.Value;
            
            // Move ghost if it exists
            _adornerService?.MoveGhost(currentPoint);

            // Check if moved past threshold (5 pixels)
            if (Math.Abs(diff.X) > 5 || Math.Abs(diff.Y) > 5)
            {
                // lazy load service
                if (_adornerService == null && Application.Current is App app)
                {
                    _adornerService = (app.Services?.GetService(typeof(DragAdornerService)) as DragAdornerService) 
                                     ?? new DragAdornerService(); // fallback
                }

                // Show visual adorner
                _adornerService?.ShowGhost(((Control?)(e.Source as Control)?.FindAncestorOfType<TreeDataGridRow>()) ?? this, this);

                // Phase 6D: Decoupled D&D Payload
                var data = new DataObject();
                data.Set(DragContext.LibraryTrackFormat, _draggedTrack.GlobalId);
                
                // Set temporary global storage for extra context (Source Project ID)
                if (DataContext is LibraryViewModel vm)
                {
                    data.Set("SourceProjectId", vm.SelectedProject?.Id.ToString() ?? "");
                }

                // Start drag operation
                await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
                
                // Clean up
                _adornerService?.HideGhost();
                _dragStartPoint = null;
                _draggedTrack = null;
            }
        }
    }

    private void OnTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _adornerService?.HideGhost();
        _dragStartPoint = null;
        _draggedTrack = null;
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        // Accept tracks from library or queue
        if (e.Data.Contains(DragContext.LibraryTrackFormat) || e.Data.Contains(DragContext.QueueTrackFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        // Get the target playlist
        var listBoxItem = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not PlaylistJob targetPlaylist)
            return;

        // Get the dragged track GlobalId
        string? trackGlobalId = null;
        if (e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.LibraryTrackFormat) as string;
        }
        else if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.QueueTrackFormat) as string;
        }

        if (string.IsNullOrEmpty(trackGlobalId))
            return;

        // Find the track in the library
        if (DataContext is not LibraryViewModel libraryViewModel)
            return;

        var sourceTrack = libraryViewModel.CurrentProjectTracks
            .FirstOrDefault(t => t.GlobalId == trackGlobalId);

        if (sourceTrack == null)
        {
            // Try to find in player queue
            var playerViewModel = libraryViewModel.PlayerViewModel;
            
            sourceTrack = playerViewModel?.Queue
                .FirstOrDefault(t => t.GlobalId == trackGlobalId);
        }

        if (sourceTrack != null && targetPlaylist != null)
        {
            // Use existing AddToPlaylist method (includes deduplication)
            libraryViewModel.AddToPlaylist(targetPlaylist, sourceTrack);
        }
    }
}
