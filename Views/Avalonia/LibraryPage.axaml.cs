using Avalonia.Controls;
using Avalonia.Interactivity;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class LibraryPage : UserControl
    {
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
                // Setup context menu
                SetupContextMenu();
            }
        }

        private void SetupContextMenu()
        {
            if (_dataGrid == null || DataContext is not LibraryViewModel viewModel) return;

            var contextMenu = new ContextMenu();
            
            // Get the LibraryActionProvider from the service provider
            // For now, we'll create basic menu items directly
            var playItem = new MenuItem { Header = "▶️ Play" };
            playItem.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItem is PlaylistTrackViewModel track)
                {
                    viewModel.PlayTrackCommand?.Execute(track);
                }
            };
            
            var removeItem = new MenuItem { Header = "🗑️ Remove from Playlist" };
            removeItem.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItem is PlaylistTrackViewModel track)
                {
                    viewModel.RemoveTrackCommand?.Execute(track);
                }
            };
            
            var openFolderItem = new MenuItem { Header = "📁 Open Folder" };
            openFolderItem.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItem is PlaylistTrackViewModel track && 
                    !string.IsNullOrEmpty(track.Model.ResolvedFilePath))
                {
                    var folder = System.IO.Path.GetDirectoryName(track.Model.ResolvedFilePath);
                    if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = folder,
                            UseShellExecute = true
                        });
                    }
                }
            };
            
            var retryItem = new MenuItem { Header = "♻️ Retry Download" };
            retryItem.Click += (s, e) =>
            {
                if (_dataGrid.SelectedItem is PlaylistTrackViewModel track)
                {
                    track.FindNewVersionCommand?.Execute(null);
                }
            };
            
            contextMenu.Items.Add(playItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(openFolderItem);
            contextMenu.Items.Add(retryItem);
            contextMenu.Items.Add(new Separator());
            contextMenu.Items.Add(removeItem);
            
            _dataGrid.ContextMenu = contextMenu;
        }
    }
}
