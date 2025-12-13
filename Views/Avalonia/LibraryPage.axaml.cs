using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class LibraryPage : UserControl
    {
        private PlaylistTrackViewModel? _draggedTrack;
        private DataGrid? _dataGrid;

        public LibraryPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Find the DataGrid and wire up events
            _dataGrid = this.FindControl<DataGrid>("TrackDataGrid");
            
            if (_dataGrid != null)
            {
                // Wire up drag-drop events on the DataGrid itself
                _dataGrid.AddHandler(DragDrop.DropEvent, OnDrop);
                _dataGrid.AddHandler(DragDrop.DragOverEvent, OnDragOver);
                
                // Wire up pointer events for initiating drag
                _dataGrid.AddHandler(PointerPressedEvent, OnPointerPressed, handledEventsToo: true);
                _dataGrid.AddHandler(PointerMovedEvent, OnPointerMoved, handledEventsToo: true);
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Find the row that was clicked
            if (e.Source is Control control)
            {
                var row = control.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is PlaylistTrackViewModel track)
                {
                    var point = e.GetCurrentPoint(control);
                    if (point.Properties.IsLeftButtonPressed)
                    {
                        _draggedTrack = track;
                    }
                }
            }
        }

        private async void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_draggedTrack == null || sender is not Control control) return;

            var point = e.GetCurrentPoint(control);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _draggedTrack = null;
                return;
            }

            // Check if we've moved enough to start dragging
            var position = point.Position;
            if (System.Math.Abs(position.X) > 5 || System.Math.Abs(position.Y) > 5)
            {
                // Start drag operation
                var dragData = new DataObject();
                dragData.Set("PlaylistTrack", _draggedTrack);

                await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
                
                _draggedTrack = null;
            }
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            // Only allow drop if we're dragging a track
            e.DragEffects = e.Data.Contains("PlaylistTrack") 
                ? DragDropEffects.Move 
                : DragDropEffects.None;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (!e.Data.Contains("PlaylistTrack") || DataContext is not LibraryViewModel viewModel)
                return;

            var sourceTrack = e.Data.Get("PlaylistTrack") as PlaylistTrackViewModel;
            if (sourceTrack == null) return;

            // Find the target row
            if (e.Source is Control control)
            {
                var targetRow = control.FindAncestorOfType<DataGridRow>();
                if (targetRow?.DataContext is PlaylistTrackViewModel targetTrack && 
                    targetTrack != sourceTrack)
                {
                    // Call the ViewModel's reorder method
                    viewModel.ReorderTrack(sourceTrack, targetTrack);
                }
            }
        }
    }
}
