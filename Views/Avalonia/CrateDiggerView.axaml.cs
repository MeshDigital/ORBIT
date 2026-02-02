using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class CrateDiggerView : UserControl
{
    public CrateDiggerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
