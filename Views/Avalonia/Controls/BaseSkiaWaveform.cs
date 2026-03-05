using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Skia;
using SkiaSharp;
using System;

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// Base class for all Skia-based waveform and visualizer controls in ORBIT.
    /// Unifies drawing loops, color palettes, and refresh rates to prevent rendering jitter.
    /// </summary>
    public abstract class BaseSkiaWaveform : Control
    {
        // official ORBIT Palette
        protected static readonly SKColor OrbitGreen = SKColor.Parse("#1DB954");
        protected static readonly SKColor HarmonicPink = SKColor.Parse("#FF4081");
        protected static readonly SKColor OrbitBackground = SKColor.Parse("#121212");

        // Standardized Rendering Parameters
        protected const float StandardSampleDensity = 1.0f;
        protected const float StandardGlowBlur = 12.0f;

        protected static readonly SKPaint BasePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };

        protected static readonly SKPaint GlowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, StandardGlowBlur / 2)
        };

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            StartRenderingLoop();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            StopRenderingLoop();
        }

        protected abstract void StartRenderingLoop();
        protected abstract void StopRenderingLoop();

        // Shared helper for coordinate transformations
        protected SKRect GetRenderBounds(Size size) => new SKRect(0, 0, (float)size.Width, (float)size.Height);
    }
}
