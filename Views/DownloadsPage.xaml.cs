using System.Windows.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views;

public partial class DownloadsPage : Page
{
    public DownloadsPage(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }


    private void DataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("[DOWNLOADS PLAYBACK] DataGrid_MouseDoubleClick fired");
        if (sender is DataGrid grid && grid.SelectedItem is PlaylistTrackViewModel track)
        {
             System.Diagnostics.Debug.WriteLine($"[DOWNLOADS PLAYBACK] Playing: {track.Artist} - {track.Title}");
             if (DataContext is MainViewModel vm && track.Model?.ResolvedFilePath != null)
             {
                 System.Diagnostics.Debug.WriteLine($"[DOWNLOADS PLAYBACK] File path: {track.Model.ResolvedFilePath}");
                 vm.PlayerViewModel.PlayTrack(track.Model.ResolvedFilePath, track.Title ?? "Unknown", track.Artist ?? "Unknown Artist");
                 vm.IsPlayerSidebarVisible = true;
             }
             else
             {
                 System.Diagnostics.Debug.WriteLine("[DOWNLOADS PLAYBACK] WARNING: No ViewModel or no file path");
             }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[DOWNLOADS PLAYBACK] WARNING: Sender is not DataGrid or SelectedItem is not PlaylistTrackViewModel");
        }
    }
}
