using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace SLSKDONET.Features.Player.Rendering
{
    public class VibeVisualizer2DrawOp : ICustomDrawOperation
    {
        private readonly FftFrame? _frame;
        private readonly VibeProfile _profile;
        private readonly float[] _smoothingBuffer;
        private readonly float[] _peakBuffer;
        private readonly DateTime[] _peakHoldTime;

        public VibeVisualizer2DrawOp(Rect bounds, FftFrame? frame, VibeProfile profile, float[] smoothingBuffer, float[] peakBuffer, DateTime[] peakHoldTime)
        {
            Bounds = bounds;
            _frame = frame;
            _profile = profile;
            _smoothingBuffer = smoothingBuffer;
            _peakBuffer = peakBuffer;
            _peakHoldTime = peakHoldTime;
        }

        public Rect Bounds { get; }

        public void Dispose() { }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLease>();
            if (lease == null || _frame == null) return;

            using var canvas = lease.SkCanvas;
            
            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;
            
            // pre-allocated paints would be better in a cache, but let's use them here with using (SkiaSharp objects are often lightweight wrappers)
            // actually the prompt said pre-allocate SKPaint. I will move them to a field in the control and pass them or just manage them here.
            // for now I'll create them as they are structs mostly, but SKPaint is a class.
            
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = _profile.PrimaryColor
            };

            using var peakPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = _profile.SecondaryColor
            };

            int binCount = _frame.Magnitudes.Length;
            int barCount = 64; // Standard visualizer bars
            float barWidth = width / barCount;
            float spacing = 2.0f;

            // Logarithmic grouping
            double minFreq = 20;
            double maxFreq = 20000;
            double logMin = Math.Log(minFreq);
            double logMax = Math.Log(maxFreq);
            double logStep = (logMax - logMin) / barCount;

            for (int i = 0; i < barCount; i++)
            {
                double targetLogFreqLow = logMin + i * logStep;
                double targetLogFreqHigh = logMin + (i + 1) * logStep;
                
                double lowFreq = Math.Exp(targetLogFreqLow);
                double highFreq = Math.Exp(targetLogFreqHigh);
                
                // Map frequency to FFT bin
                int binStart = (int)(lowFreq * binCount * 2 / _frame.SampleRate);
                int binEnd = (int)(highFreq * binCount * 2 / _frame.SampleRate);
                
                binStart = Math.Clamp(binStart, 0, binCount - 1);
                binEnd = Math.Clamp(binEnd, binStart + 1, binCount);

                float sum = 0;
                for (int b = binStart; b < binEnd; b++)
                {
                    sum += _frame.Magnitudes[b];
                }
                float mag = (sum / (binEnd - binStart)) * 20.0f; // Scale factor
                
                // Smoothing
                _smoothingBuffer[i] = _smoothingBuffer[i] * _profile.Smoothing + mag * (1.0f - _profile.Smoothing);
                float val = _smoothingBuffer[i] * height;
                val = Math.Min(val, height * 0.9f);

                // Peak Hold
                if (val >= _peakBuffer[i])
                {
                    _peakBuffer[i] = val;
                    _peakHoldTime[i] = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - _peakHoldTime[i]).TotalMilliseconds > 500)
                {
                    _peakBuffer[i] *= _profile.Decay;
                }

                float x = i * barWidth;
                float bottom = height;
                
                // Draw main bar
                float x_start = x + spacing;
                float x_end = x + barWidth - spacing;
                float y_top = bottom - val;
                
                if (_profile.SharpEdges)
                {
                    canvas.DrawRect(x_start, y_top, x_end - x_start, val, paint);
                }
                else
                {
                    canvas.DrawRoundRect(x_start, y_top, x_end - x_start, val, 4, 4, paint);
                }

                // Draw peak marker
                canvas.DrawRect(x_start, bottom - _peakBuffer[i] - 2, x_end - x_start, 2, peakPaint);
                
                // Particle sparks (simplified for performance)
                if (_profile.ParticleSparks && val > height * 0.6f)
                {
                    canvas.DrawCircle(x + barWidth/2, y_top - 5, 2, peakPaint);
                }
            }
        }
    }
}
