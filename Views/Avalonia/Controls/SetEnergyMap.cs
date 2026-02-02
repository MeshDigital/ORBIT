using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.ViewModels.Timeline;

namespace SLSKDONET.Views.Avalonia.Controls;

/// <summary>
/// Visualizes the energy curve of an entire DJ set.
/// Shows energy levels over time with a heatmap-style gradient.
/// </summary>
public class SetEnergyMap : Control
{
    // === Styled Properties ===
    
    public static readonly StyledProperty<IEnumerable<TrackLaneViewModel>?> TracksProperty =
        AvaloniaProperty.Register<SetEnergyMap, IEnumerable<TrackLaneViewModel>?>(nameof(Tracks));
    
    public IEnumerable<TrackLaneViewModel>? Tracks
    {
        get => GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }
    
    public static readonly StyledProperty<long> TotalDurationSamplesProperty =
        AvaloniaProperty.Register<SetEnergyMap, long>(nameof(TotalDurationSamples), 44100 * 60 * 60); // 1 hour default
    
    public long TotalDurationSamples
    {
        get => GetValue(TotalDurationSamplesProperty);
        set => SetValue(TotalDurationSamplesProperty, value);
    }
    
    public static readonly StyledProperty<long> PlayheadPositionProperty =
        AvaloniaProperty.Register<SetEnergyMap, long>(nameof(PlayheadPosition));
    
    public long PlayheadPosition
    {
        get => GetValue(PlayheadPositionProperty);
        set => SetValue(PlayheadPositionProperty, value);
    }
    
    public static readonly StyledProperty<double> TargetEnergyProperty =
        AvaloniaProperty.Register<SetEnergyMap, double>(nameof(TargetEnergy), 0.7);
    
    /// <summary>
    /// Target energy level (0-1) represented by a horizontal line.
    /// </summary>
    public double TargetEnergy
    {
        get => GetValue(TargetEnergyProperty);
        set => SetValue(TargetEnergyProperty, value);
    }

    static SetEnergyMap()
    {
        AffectsRender<SetEnergyMap>(
            TracksProperty, 
            TotalDurationSamplesProperty, 
            PlayheadPositionProperty,
            TargetEnergyProperty);
    }

    public SetEnergyMap()
    {
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        
        context.Custom(new EnergyMapDrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height),
            Tracks?.ToList() ?? new List<TrackLaneViewModel>(),
            TotalDurationSamples,
            PlayheadPosition,
            TargetEnergy));
    }

    private class EnergyMapDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly List<TrackLaneViewModel> _tracks;
        private readonly long _totalDuration;
        private readonly long _playheadPosition;
        private readonly double _targetEnergy;

        private static readonly SKColor[] EnergyGradient = new[]
        {
            new SKColor(30, 60, 120),    // Deep blue (low energy)
            new SKColor(50, 150, 200),   // Cyan-blue
            new SKColor(100, 200, 100),  // Green (medium)
            new SKColor(255, 200, 50),   // Yellow-orange
            new SKColor(255, 100, 50),   // Orange
            new SKColor(255, 50, 100)    // Red-pink (high energy)
        };

        public EnergyMapDrawOperation(
            Rect bounds,
            List<TrackLaneViewModel> tracks,
            long totalDuration,
            long playheadPosition,
            double targetEnergy)
        {
            _bounds = bounds;
            _tracks = tracks;
            _totalDuration = totalDuration;
            _playheadPosition = playheadPosition;
            _targetEnergy = targetEnergy;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease == null) return;

            using var skiaContext = lease.Lease();
            var canvas = skiaContext.SkCanvas;

            float width = (float)_bounds.Width;
            float height = (float)_bounds.Height;

            // 1. Background
            using var bgPaint = new SKPaint { Color = new SKColor(15, 15, 20) };
            canvas.DrawRect(0, 0, width, height, bgPaint);

            // 2. Calculate energy points
            var energyPoints = CalculateEnergyPoints(width);
            
            // 3. Draw energy fill (area under curve)
            if (energyPoints.Count > 1)
            {
                RenderEnergyFill(canvas, energyPoints, width, height);
                RenderEnergyLine(canvas, energyPoints, width, height);
            }

            // 4. Draw target energy line
            RenderTargetLine(canvas, width, height);

            // 5. Draw track markers
            RenderTrackMarkers(canvas, width, height);

            // 6. Draw playhead
            RenderPlayhead(canvas, width, height);
        }

        private List<(float x, float energy)> CalculateEnergyPoints(float width)
        {
            var points = new List<(float x, float energy)>();
            
            // Sample at regular intervals
            int numSamples = Math.Min((int)width, 200);
            
            for (int i = 0; i < numSamples; i++)
            {
                float x = i * width / numSamples;
                long samplePosition = (long)(i * _totalDuration / numSamples);
                
                // Find tracks active at this position and compute combined energy
                float energy = 0;
                int activeCount = 0;
                
                foreach (var track in _tracks)
                {
                    if (samplePosition >= track.StartSampleOffset && 
                        samplePosition < track.EndSample)
                    {
                        energy += (float)track.Energy;
                        activeCount++;
                    }
                }
                
                if (activeCount > 0)
                {
                    energy /= activeCount;
                }
                
                points.Add((x, energy));
            }
            
            return points;
        }

        private void RenderEnergyFill(SKCanvas canvas, List<(float x, float energy)> points, float width, float height)
        {
            using var path = new SKPath();
            path.MoveTo(0, height);
            
            foreach (var (x, energy) in points)
            {
                float y = height - (energy * height * 0.9f); // 90% of height max
                path.LineTo(x, y);
            }
            
            path.LineTo(width, height);
            path.Close();

            // Gradient fill
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, height),
                new SKPoint(0, 0),
                new[] { EnergyGradient[0], EnergyGradient[2], EnergyGradient[4], EnergyGradient[5] },
                new[] { 0f, 0.3f, 0.7f, 1f },
                SKShaderTileMode.Clamp);

            using var fillPaint = new SKPaint 
            { 
                Shader = shader, 
                Style = SKPaintStyle.Fill,
                Color = new SKColor(255, 255, 255, 120)
            };
            
            canvas.DrawPath(path, fillPaint);
        }

        private void RenderEnergyLine(SKCanvas canvas, List<(float x, float energy)> points, float width, float height)
        {
            using var path = new SKPath();
            bool first = true;
            
            foreach (var (x, energy) in points)
            {
                float y = height - (energy * height * 0.9f);
                
                if (first)
                {
                    path.MoveTo(x, y);
                    first = false;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }

            using var linePaint = new SKPaint
            {
                Color = SKColors.White,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                IsAntialias = true
            };
            
            canvas.DrawPath(path, linePaint);
        }

        private void RenderTargetLine(SKCanvas canvas, float width, float height)
        {
            float y = height - ((float)_targetEnergy * height * 0.9f);
            
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 100),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash(new[] { 5f, 5f }, 0)
            };
            
            canvas.DrawLine(0, y, width, y, paint);
            
            // Label
            using var textPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 150),
                TextSize = 10,
                IsAntialias = true
            };
            canvas.DrawText("Target", 5, y - 3, textPaint);
        }

        private void RenderTrackMarkers(SKCanvas canvas, float width, float height)
        {
            foreach (var track in _tracks)
            {
                float startX = (float)track.StartSampleOffset / _totalDuration * width;
                float endX = (float)track.EndSample / _totalDuration * width;
                
                // Track region indicator at bottom
                using var regionPaint = new SKPaint
                {
                    Color = GetTrackColor(track).WithAlpha(100),
                    Style = SKPaintStyle.Fill
                };
                canvas.DrawRect(startX, height - 8, endX - startX, 8, regionPaint);
                
                // Track start marker
                using var markerPaint = new SKPaint
                {
                    Color = GetTrackColor(track),
                    StrokeWidth = 1,
                    Style = SKPaintStyle.Stroke
                };
                canvas.DrawLine(startX, 0, startX, height - 8, markerPaint);
            }
        }

        private void RenderPlayhead(SKCanvas canvas, float width, float height)
        {
            float x = (float)_playheadPosition / _totalDuration * width;
            
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 2,
                IsAntialias = true
            };
            canvas.DrawLine(x, 0, x, height, paint);
        }

        private SKColor GetTrackColor(TrackLaneViewModel track)
        {
            int index = _tracks.IndexOf(track) % 6;
            return index switch
            {
                0 => new SKColor(255, 100, 150),   // Pink
                1 => new SKColor(100, 200, 255),   // Blue
                2 => new SKColor(100, 255, 150),   // Green
                3 => new SKColor(255, 200, 100),   // Orange
                4 => new SKColor(200, 100, 255),   // Purple
                5 => new SKColor(255, 255, 100),   // Yellow
                _ => SKColors.White
            };
        }
    }
}
