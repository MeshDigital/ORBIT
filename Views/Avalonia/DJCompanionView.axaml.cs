using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace SLSKDONET.Views.Avalonia;

public partial class DJCompanionView : UserControl
{
    public DJCompanionView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnMatchHover(object? sender, PointerEventArgs e)
    {
        // Optional: Play preview or highlight match
    }
}
