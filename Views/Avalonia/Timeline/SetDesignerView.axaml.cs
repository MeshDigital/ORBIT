using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia.Timeline;

public partial class SetDesignerView : UserControl
{
    public SetDesignerView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
