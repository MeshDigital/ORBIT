using System;
using Avalonia.Media;
using SkiaSharp;

namespace SLSKDONET.Features.Player.Rendering
{
    public class VibeProfile
    {
        public SKColor PrimaryColor { get; set; }
        public SKColor SecondaryColor { get; set; }
        public float Smoothing { get; set; }
        public float Decay { get; set; }
        public float BarWidthScale { get; set; }
        public bool SharpEdges { get; set; }
        public bool ParticleSparks { get; set; }

        public static VibeProfile GetProfile(float energy, string? moodTag)
        {
            var profile = new VibeProfile();

            // Energy Based Base mapping
            if (energy < 0.5f)
            {
                profile.PrimaryColor = SKColors.Cyan;
                profile.SecondaryColor = SKColors.DeepSkyBlue;
                profile.Smoothing = 0.5f;
                profile.Decay = 0.9f; // 100ms-ish decay feel
                profile.BarWidthScale = 1.0f;
            }
            else
            {
                profile.PrimaryColor = SKColors.OrangeRed;
                profile.SecondaryColor = SKColors.Goldenrod;
                profile.Smoothing = 0.2f; // Snappy
                profile.Decay = 0.97f; // Faster visual decay look
                profile.BarWidthScale = 1.2f;
            }

            // MoodTag Overrides
            if (moodTag != null)
            {
                if (moodTag.Contains("Aggressive", StringComparison.OrdinalIgnoreCase))
                {
                    profile.SharpEdges = true;
                    profile.ParticleSparks = true;
                    profile.Smoothing = 0.1f;
                }
                else if (moodTag.Contains("Relaxed", StringComparison.OrdinalIgnoreCase))
                {
                    profile.SharpEdges = false;
                    profile.Smoothing = 0.7f;
                }
            }

            return profile;
        }
    }
}
