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
using SLSKDONET.ViewModels.Library;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Views.Avalonia;

public partial class LibraryPage : UserControl
{
    private readonly ILogger<LibraryPage>? _logger;

    public LibraryPage()
    {
        InitializeComponent();
    }

    public LibraryPage(LibraryViewModel viewModel, ILogger<LibraryPage>? logger = null)
    {
        _logger = logger;
        DataContext = viewModel; // CRITICAL: Set DataContext from DI
        InitializeComponent();
        
        // Enable drag-drop on playlist ListBox
        AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        AddHandler(DragDrop.DropEvent, OnPlaylistDrop);

        // DataGrid Professionalization
        var dataGrid = this.FindControl<DataGrid>("ProDataGrid");
        if (dataGrid != null)
        {
            dataGrid.ColumnReordered += OnDataGridColumnReordered;
            // dataGrid.ColumnResized += OnDataGridColumnResized;
            dataGrid.SelectionChanged += OnDataGridSelectionChanged;
            
            // Context menu for headers
            SetupColumnContextMenu(dataGrid);
        }
    }

    private void OnDataGridColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && sender is DataGrid dg)
        {
            // Update DisplayOrder in AvailableColumns
            foreach (var col in dg.Columns)
            {
                var def = vm.AvailableColumns.FirstOrDefault(c => c.Header?.ToString() == col.Header?.ToString());
                if (def != null)
                {
                    def.DisplayOrder = col.DisplayIndex;
                }
            }
            vm.OnColumnLayoutChanged();
        }
    }

    private void OnDataGridColumnResized(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            var def = vm.AvailableColumns.FirstOrDefault(c => c.Header?.ToString() == e.Column.Header?.ToString());
            if (def != null)
            {
                def.Width = (int)e.Column.ActualWidth;
                vm.OnColumnLayoutChanged();
            }
        }
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && sender is DataGrid dg)
        {
            // Sync DataGrid selection to Tracks.SelectedTracks
            // FilteredTracks are PlaylistTrackViewModels
            var selected = dg.SelectedItems.Cast<PlaylistTrackViewModel>().ToList();
            
            // Update VM selection logic (calling internal method if possible or using Commands)
            // For now, we assume simple sync is needed
             vm.Tracks.UpdateSelection(selected);
        }
    }

    private void SetupColumnContextMenu(DataGrid dg)
    {
        // Headers are tricky to catch in Avalonia DataGrid without styles, 
        // but we can add a context menu to the whole grid and filter for header area or just have it everywhere.
        // Professional approach: Context menu on the grid itself that lists columns.
        
        var menu = new ContextMenu();
        
        if (DataContext is LibraryViewModel vm)
        {
            foreach (var colDef in vm.AvailableColumns)
            {
                var item = new MenuItem 
                { 
                    Header = colDef.Header, 
                    Icon = colDef.IsVisible ? "✓" : "",
                    Command = vm.ToggleColumnCommand,
                    CommandParameter = colDef
                };
                
                // Add binding for Icon would be better but let's keep it simple for now
                menu.Items.Add(item);
            }
            
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem 
            { 
                Header = "Reset to Studio Default", 
                Command = vm.ResetViewCommand 
            });
        }
        
        dg.ContextMenu = menu;
    }

    private void CloseRemovalHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.IsRemovalHistoryVisible = false;
        }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // BUGFIX: Ensure projects are loaded when user navigates to Library page
        // Previously only loaded during startup or after imports, not on manual navigation
        if (DataContext is LibraryViewModel vm)
        {
            try
            {
                // FIX: Check if projects are already loaded to prevent aggressive reloading on tab switch
                if (!vm.Projects.AllProjects.Any())
                {
                    _logger?.LogInformation("[DIAGNOSTIC] LibraryPage.OnLoaded: Starting LoadProjectsAsync");
                    _logger?.LogInformation("[DIAGNOSTIC] Current AllProjects count BEFORE load: {Count}", vm.Projects.AllProjects.Count);
                    
                    await vm.LoadProjectsAsync();
                }
                else
                {
                    _logger?.LogInformation("[DIAGNOSTIC] LibraryPage.OnLoaded: Projects already loaded ({Count} items). Skipping re-load.", vm.Projects.AllProjects.Count);
                }
                
                _logger?.LogInformation("[DIAGNOSTIC] LoadProjectsAsync completed. AllProjects count AFTER load: {Count}", vm.Projects.AllProjects.Count);
                
                if (vm.Projects.AllProjects.Count == 0)
                {
                    _logger?.LogWarning("[DIAGNOSTIC] WARNING: AllProjects is still empty after LoadProjectsAsync!");
                }
                else
                {
                    _logger?.LogInformation("[DIAGNOSTIC] Projects loaded successfully. First project: {Title}", vm.Projects.AllProjects[0].SourceTitle);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DIAGNOSTIC] EXCEPTION in LibraryPage.OnLoaded during LoadProjectsAsync");
            }
        }
        else
        {
            _logger?.LogWarning("[DIAGNOSTIC] LibraryPage.OnLoaded: DataContext is NOT LibraryViewModel!");
        }
        
        
        // Find the playlist ListBox and enable drop
        var playlistListBox = this.FindControl<ListBox>("PlaylistListBox");
        if (playlistListBox != null)
        {
            DragDrop.SetAllowDrop(playlistListBox, true);
        }
        
        // TODO: Restore Drag and Drop for the new Track ListBox
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
    
    private void ToggleHelpPanel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.IsHelpPanelOpen = !vm.IsHelpPanelOpen;
        }
    }
}
