using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class UpgradeScoutView : UserControl
{
    public UpgradeScoutView()
    {
        InitializeComponent();
    }

    public UpgradeScoutView(ViewModels.UpgradeScoutViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
