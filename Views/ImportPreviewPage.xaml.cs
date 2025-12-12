using System.Windows.Controls;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views
{
    /// <summary>
    /// Interaction logic for ImportPreviewPage.xaml
    /// </summary>
    public partial class ImportPreviewPage : Page
    {
        private readonly ImportPreviewViewModel _viewModel;

        public ImportPreviewPage(ImportPreviewViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;
        }

        private void OnTrackSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
        {
            // This is a simple way to trigger the count update without complex eventing inside the track view model.
            // For a more advanced implementation, the IsSelected property on the track VM would raise an event.
            _viewModel.UpdateSelectedCount();
        }
    }
}