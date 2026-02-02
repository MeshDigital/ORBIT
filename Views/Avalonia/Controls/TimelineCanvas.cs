using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SLSKDONET.ViewModels.Timeline;
using System.Collections.Specialized;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls;

public class TimelineCanvas : Control
{
    public static readonly StyledProperty<IEnumerable<TrackLaneViewModel>> LanesProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable<TrackLaneViewModel>>(nameof(Lanes));

    public IEnumerable<TrackLaneViewModel> Lanes
    {
        get => GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    public static readonly StyledProperty<long> CurrentSamplePositionProperty =
        AvaloniaProperty.Register<TimelineCanvas, long>(nameof(CurrentSamplePosition));

    public long CurrentSamplePosition
    {
        get => GetValue(CurrentSamplePositionProperty);
        set => SetValue(CurrentSamplePositionProperty, value);
    }

    public static readonly StyledProperty<double> ZoomLevelProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(ZoomLevel), 1.0);

    public double ZoomLevel
    {
        get => GetValue(ZoomLevelProperty);
        set => SetValue(ZoomLevelProperty, value);
    }

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(ScrollOffset), 0.0);

    public double ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public static readonly StyledProperty<double> ProjectBpmProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(ProjectBpm), 128.0);

    public double ProjectBpm
    {
        get => GetValue(ProjectBpmProperty);
        set => SetValue(ProjectBpmProperty, value);
    }

    static TimelineCanvas()
    {
        AffectsRender<TimelineCanvas>(CurrentSamplePositionProperty, ZoomLevelProperty, ScrollOffsetProperty, LanesProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Lanes is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnLanesCollectionChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Lanes is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged -= OnLanesCollectionChanged;
        }
    }

    private void OnLanesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Draw background grid
        DrawGrid(context, bounds);

        // Draw track lanes
        if (Lanes != null)
        {
            int laneIndex = 0;
            double laneHeight = 80;
            foreach (var lane in Lanes)
            {
                DrawLane(context, lane, laneIndex, laneHeight);
                laneIndex++;
            }
        }

        // Draw playhead
        DrawPlayhead(context, bounds);
    }

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#222222")), 1);
        var subPen = new Pen(new SolidColorBrush(Color.Parse("#1A1A1A")), 1);

        double samplesPerPixel = 44100.0 / (100.0 * ZoomLevel); // 100px per second at zoom 1.0
        double beatIntervalSamples = (60.0 / ProjectBpm) * 44100.0;
        double pixelInterval = beatIntervalSamples / samplesPerPixel;

        for (double x = -ScrollOffset % pixelInterval; x < bounds.Width; x += pixelInterval)
        {
            if (x < 0) continue;
            context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
        }
    }

    private void DrawLane(DrawingContext context, TrackLaneViewModel lane, int index, double height)
    {
        double y = index * (height + 2);
        double samplesPerPixel = 44100.0 / (100.0 * ZoomLevel);
        double startX = (lane.StartSampleOffset / samplesPerPixel) - ScrollOffset;
        double width = (lane.DurationSamples / samplesPerPixel);

        if (startX + width < 0 || startX > Bounds.Width) return;

        var rect = new Rect(startX, y, width, height);
        var brush = new SolidColorBrush(Color.Parse("#2A2A32"));
        var borderPen = new Pen(new SolidColorBrush(Color.Parse("#444450")), 1);

        context.DrawRectangle(brush, borderPen, rect);

        // Draw track info
        var text = new FormattedText(
            $"{lane.Artist} - {lane.Title}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"),
            12,
            Brushes.White)
        {
            MaxTextWidth = width - 10,
            MaxTextHeight = height
        };

        context.DrawText(text, new Point(startX + 5, y + 5));
        
        // Draw waveform placeholder
        var waveformPen = new Pen(new SolidColorBrush(Color.Parse("#4EC9B0")), 1);
        double midY = y + height / 2 + 10;
        context.DrawLine(waveformPen, new Point(startX, midY), new Point(startX + width, midY));
    }

    private void DrawPlayhead(DrawingContext context, Rect bounds)
    {
        double samplesPerPixel = 44100.0 / (100.0 * ZoomLevel);
        double x = (CurrentSamplePosition / samplesPerPixel) - ScrollOffset;

        if (x < 0 || x > bounds.Width) return;

        var pen = new Pen(Brushes.Lime, 2);
        context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
    }
}
