using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Windows.Input;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Custom Avalonia control that renders vocal pocket segments as a heatmap.
/// Designed for hardware-grade booth visibility with pill-shaped segments.
/// Supports click interaction to select segments.
/// </summary>
public class VocalPocketRenderer : Control
{
    public static readonly StyledProperty<Services.Musical.VocalPocketRenderModel?> ModelProperty =
        AvaloniaProperty.Register<VocalPocketRenderer, Services.Musical.VocalPocketRenderModel?>(nameof(Model));

    public static readonly StyledProperty<Services.Musical.VocalPocketSegment?> SelectedSegmentProperty =
        AvaloniaProperty.Register<VocalPocketRenderer, Services.Musical.VocalPocketSegment?>(nameof(SelectedSegment));

    public static readonly StyledProperty<ICommand?> SegmentSelectedCommandProperty =
        AvaloniaProperty.Register<VocalPocketRenderer, ICommand?>(nameof(SegmentSelectedCommand));

    public Services.Musical.VocalPocketRenderModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public Services.Musical.VocalPocketSegment? SelectedSegment
    {
        get => GetValue(SelectedSegmentProperty);
        set => SetValue(SelectedSegmentProperty, value);
    }

    public ICommand? SegmentSelectedCommand
    {
        get => GetValue(SegmentSelectedCommandProperty);
        set => SetValue(SegmentSelectedCommandProperty, value);
    }

    static VocalPocketRenderer()
    {
        AffectsRender<VocalPocketRenderer>(ModelProperty, SelectedSegmentProperty);
    }

    // Forensic color palette — optimized for dark booth environments
    private static readonly Color InstColor = Color.Parse("#1A1A1A");       // Dark/Empty — Safe Harbor
    private static readonly Color SparseColor = Color.Parse("#2A4A8A");     // Electric Blue — Light vocals
    private static readonly Color HookColor = Color.Parse("#CC7700");       // Warning Orange — Hook/chorus
    private static readonly Color DenseColor = Color.Parse("#CC2222");      // Danger Red — Full lyrics
    
    // Safe zone glow
    private static readonly Color SafeGlowColor = Color.Parse("#22FF22");
    private static readonly Color SelectionColor = Color.Parse("#FFFFFF");

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        
        var model = Model;
        if (model == null || model.Segments.Count == 0) return;

        var point = e.GetPosition(this);
        double duration = model.TrackDurationSeconds;
        double width = Bounds.Width;
        
        // Convert click position to time
        double clickTime = (point.X / width) * duration;
        
        // Find segment at click time
        foreach (var segment in model.Segments)
        {
            if (clickTime >= segment.StartSeconds && clickTime <= segment.EndSeconds)
            {
                SelectedSegment = segment;
                SegmentSelectedCommand?.Execute(segment);
                InvalidateVisual();
                break;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var model = Model;
        if (model == null || model.Segments.Count == 0 || model.TrackDurationSeconds <= 0)
        {
            // Draw placeholder bar
            context.DrawRectangle(
                new SolidColorBrush(Color.Parse("#0D0D0D")), 
                null, 
                new Rect(0, 0, Bounds.Width, Bounds.Height),
                2, 2);
            return;
        }

        double width = Bounds.Width;
        double height = Bounds.Height;
        double duration = model.TrackDurationSeconds;

        foreach (var segment in model.Segments)
        {
            // Time-to-Pixel mapping
            double x = (segment.StartSeconds / duration) * width;
            double segWidth = ((segment.EndSeconds - segment.StartSeconds) / duration) * width;
            
            // Clamp to avoid rendering artifacts
            if (segWidth < 1) segWidth = 1;
            if (x + segWidth > width) segWidth = width - x;

            var rect = new Rect(x, 0, segWidth, height);
            var color = GetColorForType(segment.ZoneType);
            
            // Check if this is the selected segment
            bool isSelected = SelectedSegment != null && 
                              segment.StartSeconds == SelectedSegment.StartSeconds &&
                              segment.EndSeconds == SelectedSegment.EndSeconds;
            
            // Draw segment with pill-shaped corners
            context.DrawRectangle(
                new SolidColorBrush(color), 
                isSelected ? new Pen(new SolidColorBrush(SelectionColor), 2) : null, 
                rect, 
                2, 2);

            // Safe Zone Glow: Add top highlight to Instrumental pockets
            // This is the DJ's "green runway" indicator
            if (segment.ZoneType == Services.Musical.VocalZoneType.Instrumental && segWidth > 4)
            {
                var glowRect = new Rect(x, 0, segWidth, 3);
                context.DrawRectangle(
                    new SolidColorBrush(SafeGlowColor, 0.4), 
                    null, 
                    glowRect,
                    2, 0);
            }
        }
    }

    private static Color GetColorForType(Services.Musical.VocalZoneType type) => type switch
    {
        Services.Musical.VocalZoneType.Instrumental => InstColor,
        Services.Musical.VocalZoneType.Sparse => SparseColor,
        Services.Musical.VocalZoneType.Hook => HookColor,
        Services.Musical.VocalZoneType.Dense => DenseColor,
        _ => Colors.Transparent
    };
}

