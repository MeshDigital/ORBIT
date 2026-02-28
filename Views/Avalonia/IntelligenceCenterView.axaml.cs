using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using global::Avalonia.Input;
using Splat;
using SLSKDONET.Services;
using SLSKDONET.Models;
using ReactiveUI;
using System.Reactive.Linq;

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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ViewModels.IntelligenceCenterViewModel vm)
        {
            vm.WhenAnyValue(x => x.NormalizedPosition)
              .ObserveOn(RxApp.MainThreadScheduler)
              .Subscribe(pos => UpdateScanLine(pos));
        }
    }

    private void UpdateScanLine(float normalizedPosition)
    {
        var scanLine = this.FindControl<global::Avalonia.Controls.Shapes.Line>("ScanLineLine");
        var spectrogramBorder = this.FindControl<Border>("SpectrogramBorder");
        
        if (scanLine != null && spectrogramBorder != null)
        {
            double x = normalizedPosition * spectrogramBorder.Bounds.Width;
            if (scanLine.RenderTransform is global::Avalonia.Media.TranslateTransform tt)
            {
                tt.X = x;
            }
        }
    }

    private void Spectrogram_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            var pos = e.GetPosition(border);
            double percent = pos.X / border.Bounds.Width;
            
            if (DataContext is SLSKDONET.ViewModels.IntelligenceCenterViewModel vm)
            {
                var eventBus = Splat.Locator.Current.GetService<SLSKDONET.Services.IEventBus>();
                eventBus?.Publish(new SLSKDONET.Models.SeekRequestEvent(percent));
            }
        }
    }
}
