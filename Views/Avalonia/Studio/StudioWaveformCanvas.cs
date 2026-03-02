using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using SLSKDONET.Models;

using System.Diagnostics;
using Avalonia.Threading;

namespace SLSKDONET.Views.Avalonia.Studio;

public class StudioWaveformCanvas : Control
{
    public static readonly StyledProperty<WaveformAnalysisData> WaveformDataProperty =
        AvaloniaProperty.Register<StudioWaveformCanvas, WaveformAnalysisData>(nameof(WaveformData));

    public static readonly StyledProperty<IEnumerable<PhraseSegment>> PhrasesProperty =
        AvaloniaProperty.Register<StudioWaveformCanvas, IEnumerable<PhraseSegment>>(nameof(Phrases));

    public static readonly StyledProperty<IEnumerable<OrbitCue>> CuesProperty =
        AvaloniaProperty.Register<StudioWaveformCanvas, IEnumerable<OrbitCue>>(nameof(Cues));

    public static readonly StyledProperty<double> PlayheadPositionProperty =
        AvaloniaProperty.Register<StudioWaveformCanvas, double>(nameof(PlayheadPosition));

    private double _smoothedPlayhead;
    private readonly Stopwatch _renderStopwatch = Stopwatch.StartNew();
    private long _lastFrameTicks;
    private DispatcherTimer? _lerpTimer;

    static StudioWaveformCanvas()
    {
        AffectsRender<StudioWaveformCanvas>(WaveformDataProperty, PhrasesProperty, CuesProperty, PlayheadPositionProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lerpTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTimerTick);
        _lerpTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _lerpTimer?.Stop();
        _lerpTimer = null;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        long currentTicks = _renderStopwatch.ElapsedTicks;
        double deltaTime = (double)(currentTicks - _lastFrameTicks) / Stopwatch.Frequency;
        _lastFrameTicks = currentTicks;

        double target = PlayheadPosition;
        
        // If target jumped (more than 1 second), snap immediately
        if (Math.Abs(target - _smoothedPlayhead) > 1.0)
        {
            _smoothedPlayhead = target;
        }
        else
        {
            // Lerp towards target: current + (target - current) * factor
            // A factor of 10.0 gives a nice smooth glide
            _smoothedPlayhead += (target - _smoothedPlayhead) * Math.Min(1.0, deltaTime * 10.0);
        }

        InvalidateVisual();
    }

    public WaveformAnalysisData WaveformData
    {
        get => GetValue(WaveformDataProperty);
        set => SetValue(WaveformDataProperty, value);
    }

    public IEnumerable<PhraseSegment> Phrases
    {
        get => GetValue(PhrasesProperty);
        set => SetValue(PhrasesProperty, value);
    }

    public IEnumerable<OrbitCue> Cues
    {
        get => GetValue(CuesProperty);
        set => SetValue(CuesProperty, value);
    }

    public double PlayheadPosition
    {
        get => GetValue(PlayheadPositionProperty);
        set => SetValue(PlayheadPositionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new WaveformDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            WaveformData,
            Phrases,
            Cues,
            _smoothedPlayhead));
    }

    private class WaveformDrawOperation : ICustomDrawOperation
    {
        private readonly WaveformAnalysisData _data;
        private readonly IEnumerable<PhraseSegment>? _phrases;
        private readonly IEnumerable<OrbitCue>? _cues;
        private readonly double _playhead;

        public WaveformDrawOperation(Rect bounds, WaveformAnalysisData data, IEnumerable<PhraseSegment>? phrases, IEnumerable<OrbitCue>? cues, double playhead)
        {
            Bounds = bounds;
            _data = data;
            _phrases = phrases;
            _cues = cues;
            _playhead = playhead;
        }

        public Rect Bounds { get; }
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Clear(SKColors.Transparent);
            if (_data == null || _data.PeakData == null || _data.PeakData.Length == 0) return;

            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;
            float centerY = height / 2;

            // 1. Draw Phrases (Structural background)
            if (_phrases != null)
            {
                float maxDuration = (float)_data.DurationSeconds;
                if (maxDuration <= 0) maxDuration = 1.0f;

                foreach (var p in _phrases)
                {
                    float startX = (float)(p.Start / maxDuration * width);
                    float phraseEndX = (float)((p.Start + p.Duration) / maxDuration * width);
                    
                    using var phrasePaint = new SKPaint
                    {
                        Color = GetPhraseColor(p.Label).WithAlpha(40),
                        Style = SKPaintStyle.Fill
                    };
                    canvas.DrawRect(startX, 0, Math.Max(1.0f, phraseEndX - startX), height, phrasePaint);
                }
            }

            // 2. Draw Waveform Peaks
            using var peakPaint = new SKPaint
            {
                Color = SKColors.Cyan,
                StrokeWidth = 1,
                IsAntialias = false 
            };

            int peaks = _data.PeakData.Length;
            float step = width / peaks;

            for (int i = 0; i < peaks; i++)
            {
                float peakHeight = (float)(_data.PeakData[i] * height * 0.8 / 255.0);
                float x = i * step;
                canvas.DrawLine(x, centerY - peakHeight/2, x, centerY + peakHeight/2, peakPaint);
            }

            // 3. Draw Cues (Vertical Markers)
            if (_cues != null)
            {
                float maxDuration = (float)_data.DurationSeconds;
                if (maxDuration <= 0) maxDuration = 1.0f;

                foreach (var c in _cues)
                {
                    float x = (float)(c.Timestamp / maxDuration * width);
                    using var cuePaint = new SKPaint
                    {
                        Color = SKColor.Parse(c.Color ?? "#FFFFFF"),
                        StrokeWidth = 2,
                        IsAntialias = true
                    };
                    canvas.DrawLine(x, 0, x, height, cuePaint);
                }
            }

            // 4. Draw Playhead
            float playheadX = (float)(_playhead / (_data.DurationSeconds > 0 ? _data.DurationSeconds : 1.0) * width);
            using var playheadPaint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 3,
                IsAntialias = true
            };
            canvas.DrawLine(playheadX, 0, playheadX, height, playheadPaint);
        }

        private SKColor GetPhraseColor(string? label)
        {
            return (label?.ToLower() ?? "") switch
            {
                "intro" => SKColor.Parse("#4CAF50"), // Green
                "verse" => SKColor.Parse("#2196F3"), // Blue
                "chorus" => SKColor.Parse("#9C27B0"), // Purple
                "drop" => SKColor.Parse("#F44336"),   // Red
                "outro" => SKColor.Parse("#FFC107"),  // Amber
                _ => SKColor.Parse("#9E9E9E")         // Grey
            };
        }
    }
}
