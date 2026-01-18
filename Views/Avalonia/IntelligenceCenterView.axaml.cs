using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using global::Avalonia.Input;
using Splat;
using SLSKDONET.Services;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia;

public partial class IntelligenceCenterView : UserControl
{
    public IntelligenceCenterView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void Spectrogram_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            var pos = e.GetPosition(border);
            double percent = pos.X / border.Bounds.Width;
            
            // Phase C: Update Spectral Scanline visual
            var scanLine = this.FindControl<global::Avalonia.Controls.Shapes.Line>("ScanLineLine");
            if (scanLine?.RenderTransform is global::Avalonia.Media.TranslateTransform tt)
            {
                tt.X = pos.X;
            }

            if (DataContext is SLSKDONET.ViewModels.IntelligenceCenterViewModel vm)
            {
                var eventBus = Splat.Locator.Current.GetService<SLSKDONET.Services.IEventBus>();
                eventBus?.Publish(new SLSKDONET.Models.SeekRequestEvent(percent));
            }
        }
    }
}
