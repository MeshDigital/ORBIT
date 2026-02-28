using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class UnifiedTrackGridView : UserControl
{
    public UnifiedTrackGridView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
