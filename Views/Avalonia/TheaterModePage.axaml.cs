using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia;

public partial class TheaterModePage : UserControl
{
    public TheaterModePage()
    {
        InitializeComponent();
    }

    public TheaterModePage(TheaterModeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
