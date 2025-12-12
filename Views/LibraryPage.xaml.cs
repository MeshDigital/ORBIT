using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views
{
    public partial class LibraryPage : Page
    {
        private readonly LibraryViewModel _viewModel;

        public LibraryPage(LibraryViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            // This is the critical step: set the DataContext before navigation can overwrite it.
            this.DataContext = _viewModel;
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel vm)
            {
                // Only allow dragging if pending (Warehouse view)
                if (vm.State == PlaylistTrackState.Pending)
                {
                    System.Windows.DragDrop.DoDragDrop(row, vm, System.Windows.DragDropEffects.Move);
                    e.Handled = true;
                }
            }
        }

        private void OnPreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }

        private void DataGridRow_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel targetVm)
            {
                var sourceVm = e.Data.GetData("PlaylistTrackViewModel") as PlaylistTrackViewModel;
                if (sourceVm != null)
                {
                    if (DataContext is LibraryViewModel libraryVm)
                    {
                        libraryVm.ReorderTrack(sourceVm, targetVm);
                    }
                }
            }
        }
    }
}
