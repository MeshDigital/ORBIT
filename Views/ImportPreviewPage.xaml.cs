using System.Windows.Controls;
using SLSKDONET.ViewModels;
using SLSKDONET.Services;

namespace SLSKDONET.Views
{
    /// <summary>
    /// Interaction logic for ImportPreviewPage.xaml
    /// </summary>
    public partial class ImportPreviewPage : Page
    {
        private readonly ImportPreviewViewModel _viewModel;

        public ImportPreviewPage(ImportPreviewViewModel viewModel
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            // Subscribe to events when the page is loaded
            Loaded += (s, e) =>
            {
                _viewModel.AddedToLibrary += OnAddedToLibrary;
                _viewModel.Cancelled += OnCancelled;
            };

            // Unsubscribe when the page is unloaded to prevent memory leaks
            Unloaded += (s, e) =>
            {
                _viewModel.AddedToLibrary -= OnAddedToLibrary;
                _viewModel.Cancelled -= OnCancelled;
            };
        }

        private async void OnAddedToLibrary(object? sender, Models.PlaylistJob job) => await _viewModel.HandlePlaylistJobAddedAsync(job);
        private void OnCancelled(object? sender, System.EventArgs e) => _viewModel.HandleCancellation();

        private void OnTrackSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            // This is a simple way to trigger the count update without complex eventing inside the track view model.
            // For a more advanced implementation, the IsSelected property on the track VM would raise an event.
            _viewModel.UpdateSelectedCount();
        }
    }
}