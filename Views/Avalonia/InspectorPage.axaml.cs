using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class InspectorPage : UserControl
{
    public InspectorPage()
    {
        InitializeComponent();
    }
    
    // Allow constructor injection of ViewModel if needed, or rely on View resolving it
    public InspectorPage(ViewModels.TrackInspectorViewModel viewModel) : this()
    {
        // Wrap the standard TrackInspectorView content or host it
        // Ideally this page IS the TrackInspectorView, but if we need a wrapper:
        DataContext = viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
