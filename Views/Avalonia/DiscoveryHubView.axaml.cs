using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels.Discovery;

namespace SLSKDONET.Views.Avalonia;

public partial class DiscoveryHubView : UserControl
{
    public DiscoveryHubView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
