using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views
{
    public partial class LibraryPage : Page
    {
        public LibraryPage(LibraryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel vm)
            {
                // Only allow dragging if pending (Warehouse view)
                if (vm.State == PlaylistTrackState.Pending)
                {
                    DragDrop.DoDragDrop(row, vm, DragDropEffects.Move);
                    e.Handled = true;
                }
            }
        }

        private void DataGridRow_Drop(object sender, DragEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel targetVm)
            {
                if (e.Data.GetData(typeof(PlaylistTrackViewModel)) is PlaylistTrackViewModel sourceVm)
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
