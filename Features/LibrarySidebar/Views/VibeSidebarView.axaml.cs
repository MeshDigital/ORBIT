using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SkiaSharp;
using SkiaSharp.Views.Avalonia;
using SLSKDONET.Features.LibrarySidebar.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.Views;

public partial class VibeSidebarView : UserControl
{
    private readonly SKPaint _bgPointPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = SKColors.Gray.WithAlpha(100),
        IsAntialias = true
    };

    private readonly SKPaint _primaryPointPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = SKColors.Cyan,
        IsAntialias = true,
        ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 8, 8, SKColors.Cyan)
    };

    private readonly SKPaint _secondaryPointPaint = new()
    {
        Style = SKPaintStyle.Fill,
        Color = SKColors.Orange,
        IsAntialias = true,
        ImageFilter = SKImageFilter.CreateDropShadow(0, 0, 8, 8, SKColors.Orange)
    };

    public VibeSidebarView()
    {
        InitializeComponent();
        
        var radarCanvas = this.FindControl<SKCanvasView>("RadarCanvas");
        if (radarCanvas != null)
        {
            radarCanvas.PaintSurface += OnPaintSurface;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is VibeSidebarViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var skCanvas = e.Surface.Canvas;
        skCanvas.Clear(SKColors.Transparent);

        if (DataContext is not VibeSidebarViewModel vm) return;

        float width = e.Info.Width;
        float height = e.Info.Height;

        // 1. Draw Background Projection
        if (vm.LibraryProjection != null)
        {
            foreach (var track in vm.LibraryProjection)
            {
                float x = (float)(track.Valence * width);
                float y = (float)((1.0 - track.Arousal) * height); // Invert Y
                skCanvas.DrawCircle(x, y, 2, _bgPointPaint);
            }
        }

        // 2. Draw Secondary Track (behind primary)
        if (vm.SecondaryTrack != null && vm.SecondaryTrack.Valence.HasValue && vm.SecondaryTrack.Energy.HasValue)
        {
            float x = (float)(vm.SecondaryTrack.Valence.Value * width);
            float y = (float)((1.0 - vm.SecondaryTrack.Energy.Value) * height);
            skCanvas.DrawCircle(x, y, 5, _secondaryPointPaint);
        }

        // 3. Draw Primary Track
        if (vm.PrimaryTrack != null && vm.PrimaryTrack.Valence.HasValue && vm.PrimaryTrack.Energy.HasValue)
        {
            float x = (float)(vm.PrimaryTrack.Valence.Value * width);
            float y = (float)((1.0 - vm.PrimaryTrack.Energy.Value) * height);
            skCanvas.DrawCircle(x, y, 6, _primaryPointPaint);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is VibeSidebarViewModel vm && 
            (e.PropertyName == nameof(VibeSidebarViewModel.LibraryProjection) ||
             e.PropertyName == nameof(VibeSidebarViewModel.PrimaryTrack) ||
             e.PropertyName == nameof(VibeSidebarViewModel.SecondaryTrack)))
        {
            Dispatcher.UIThread.InvokeAsync(() => this.FindControl<SKCanvasView>("RadarCanvas")?.InvalidateVisual());
        }
    }

    private void OnRadarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var radarCanvas = this.FindControl<SKCanvasView>("RadarCanvas");
        if (DataContext is not VibeSidebarViewModel vm || radarCanvas == null) return;

        var pt = e.GetCurrentPoint(radarCanvas).Position;
        
        // Normalize using Avalonia DIP bounds
        float targetV = (float)(pt.X / radarCanvas.Bounds.Width);
        float targetA = (float)(1.0 - (pt.Y / radarCanvas.Bounds.Height)); // Invert Y

        // Clamp to 0-1
        targetV = Math.Clamp(targetV, 0f, 1f);
        targetA = Math.Clamp(targetA, 0f, 1f);

        vm.FindTracksNearCoordinateCommand.Execute((targetV, targetA));
    }
}
