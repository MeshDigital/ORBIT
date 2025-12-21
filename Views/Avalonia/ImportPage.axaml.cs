using Avalonia.Controls;

namespace SLSKDONET.Views.Avalonia;

/// <summary>
/// Phase 6D: Import page for Spotify, CSV, and USB imports.
/// </summary>
public partial class ImportPage : UserControl
{
    public ImportPage()
    {
        InitializeComponent();
    }

    public ImportPage(ViewModels.SpotifyImportViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
