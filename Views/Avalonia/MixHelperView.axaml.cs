using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class MixHelperView : UserControl
{
    public MixHelperView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
