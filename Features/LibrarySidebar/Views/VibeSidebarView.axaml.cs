using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SLSKDONET.Features.LibrarySidebar.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.Views;

public class RadarRenderControl : Control
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

    public VibeSidebarViewModel? ViewModel { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (ViewModel != null)
        {
            context.Custom(new RadarCustomDrawOperation(Bounds, ViewModel, _bgPointPaint, _primaryPointPaint, _secondaryPointPaint));
        }
    }

    private class RadarCustomDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly VibeSidebarViewModel _vm;
        private readonly SKPaint _bgPointPaint;
        private readonly SKPaint _primaryPointPaint;
        private readonly SKPaint _secondaryPointPaint;

        public RadarCustomDrawOperation(Rect bounds, VibeSidebarViewModel vm, SKPaint bgPointPaint, SKPaint primaryPointPaint, SKPaint secondaryPointPaint)
        {
            _bounds = bounds;
            _vm = vm;
            _bgPointPaint = bgPointPaint;
            _primaryPointPaint = primaryPointPaint;
            _secondaryPointPaint = secondaryPointPaint;
        }

        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;
        public Rect Bounds => _bounds;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var skCanvas = lease.SkCanvas;

            skCanvas.Clear(SKColors.Transparent);

            float width = (float)_bounds.Width;
            float height = (float)_bounds.Height;

            // 1. Draw Background Projection
            if (_vm.LibraryProjection != null)
            {
                foreach (var track in _vm.LibraryProjection)
                {
                    float x = (float)(track.Valence * width);
                    float y = (float)((1.0 - track.Arousal) * height); // Invert Y
                    skCanvas.DrawCircle(x, y, 2, _bgPointPaint);
                }
            }

            // 2. Draw Secondary Track (behind primary)
            if (_vm.SecondaryTrack != null)
            {
                float x = (float)(_vm.SecondaryTrack.Valence * width);
                float y = (float)((1.0 - _vm.SecondaryTrack.Energy) * height);
                skCanvas.DrawCircle(x, y, 5, _secondaryPointPaint);
            }

            // 3. Draw Primary Track
            if (_vm.PrimaryTrack != null)
            {
                float x = (float)(_vm.PrimaryTrack.Valence * width);
                float y = (float)((1.0 - _vm.PrimaryTrack.Energy) * height);
                skCanvas.DrawCircle(x, y, 6, _primaryPointPaint);
            }
        }
    }
}

public partial class VibeSidebarView : UserControl
{
    private RadarRenderControl? _radarRenderer;

    public VibeSidebarView()
    {
        InitializeComponent();
        
        var radarContainer = this.FindControl<ContentControl>("RadarCanvas");
        if (radarContainer != null)
        {
            _radarRenderer = new RadarRenderControl();
            radarContainer.Content = _radarRenderer;
            radarContainer.PointerPressed += OnRadarPointerPressed;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is VibeSidebarViewModel vm)
        {
            if (_radarRenderer != null)
            {
                _radarRenderer.ViewModel = vm;
            }
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is VibeSidebarViewModel vm && 
            (e.PropertyName == nameof(VibeSidebarViewModel.LibraryProjection) ||
             e.PropertyName == nameof(VibeSidebarViewModel.PrimaryTrack) ||
             e.PropertyName == nameof(VibeSidebarViewModel.SecondaryTrack)))
        {
            Dispatcher.UIThread.InvokeAsync(() => _radarRenderer?.InvalidateVisual());
        }
    }

    private void OnRadarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var radarContainer = this.FindControl<ContentControl>("RadarCanvas");
        if (DataContext is not VibeSidebarViewModel vm || radarContainer == null) return;

        var pt = e.GetCurrentPoint(radarContainer).Position;
        
        // Normalize using Avalonia DIP bounds
        float targetV = (float)(pt.X / radarContainer.Bounds.Width);
        float targetA = (float)(1.0 - (pt.Y / radarContainer.Bounds.Height)); // Invert Y

        // Clamp to 0-1
        targetV = Math.Clamp(targetV, 0f, 1f);
        targetA = Math.Clamp(targetA, 0f, 1f);

        vm.FindTracksNearCoordinateCommand.Execute((targetV, targetA));
    }
}
