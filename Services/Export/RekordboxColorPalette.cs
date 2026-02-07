namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Simple RGB color representation (cross-platform, no System.Drawing dependency).
    /// </summary>
    public readonly struct RgbColor
    {
        public byte R { get; init; }
        public byte G { get; init; }
        public byte B { get; init; }

        public RgbColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public override string ToString() => $"RGB({R}, {G}, {B})";
    }

    /// <summary>
    /// Rekordbox cue point color palette.
    /// These RGB values match Pioneer's official color palette for CDJ/Rekordbox compatibility.
    /// </summary>
    public static class RekordboxColorPalette
    {
        // Rekordbox Color Definitions (RGB)
        public static readonly RgbColor Red = new(255, 0, 0);        // Drops, high-energy moments
        public static readonly RgbColor Orange = new(255, 165, 0);   // Builds, transitions
        public static readonly RgbColor Yellow = new(255, 255, 0);   // Breakdowns, mid-energy
        public static readonly RgbColor Green = new(0, 255, 0);      // Intros, outros
        public static readonly RgbColor Blue = new(0, 0, 255);       // Vocals, special moments
        public static readonly RgbColor Purple = new(128, 0, 128);   // Custom/user-defined
        public static readonly RgbColor Pink = new(255, 192, 203);   // Experimental
        public static readonly RgbColor White = new(255, 255, 255);  // Default

        /// <summary>
        /// Maps ORBIT's CueColor enum to Rekordbox RGB values.
        /// </summary>
        public static RgbColor GetRgbColor(SLSKDONET.Models.CueColor cueColor)
        {
            return cueColor switch
            {
                SLSKDONET.Models.CueColor.Red => Red,
                SLSKDONET.Models.CueColor.Orange => Orange,
                SLSKDONET.Models.CueColor.Yellow => Yellow,
                SLSKDONET.Models.CueColor.Green => Green,
                SLSKDONET.Models.CueColor.Blue => Blue,
                SLSKDONET.Models.CueColor.Purple => Purple,
                SLSKDONET.Models.CueColor.Pink => Pink,
                SLSKDONET.Models.CueColor.White => White,
                _ => White
            };
        }

        /// <summary>
        /// Gets the Rekordbox color index (0-7) for XML export.
        /// </summary>
        public static int GetColorIndex(SLSKDONET.Models.CueColor cueColor)
        {
            return cueColor switch
            {
                SLSKDONET.Models.CueColor.Red => 0,
                SLSKDONET.Models.CueColor.Orange => 1,
                SLSKDONET.Models.CueColor.Yellow => 2,
                SLSKDONET.Models.CueColor.Green => 3,
                SLSKDONET.Models.CueColor.Blue => 4,
                SLSKDONET.Models.CueColor.Purple => 5,
                SLSKDONET.Models.CueColor.Pink => 6,
                SLSKDONET.Models.CueColor.White => 7,
                _ => 7
            };
        }

        /// <summary>
        /// Maps ORBIT structural labels to appropriate cue colors.
        /// </summary>
        public static SLSKDONET.Models.CueColor GetColorForStructuralLabel(string label)
        {
            return label?.ToUpperInvariant() switch
            {
                "INTRO" => SLSKDONET.Models.CueColor.Green,
                "BUILD" => SLSKDONET.Models.CueColor.Orange,
                "DROP" => SLSKDONET.Models.CueColor.Red,
                "BREAKDOWN" => SLSKDONET.Models.CueColor.Yellow,
                "OUTRO" => SLSKDONET.Models.CueColor.Green,
                "VOCAL" => SLSKDONET.Models.CueColor.Blue,
                "TRANSITION" => SLSKDONET.Models.CueColor.Purple,
                _ => SLSKDONET.Models.CueColor.White
            };
        }

        /// <summary>
        /// Maps an ORBIT energy score (1-10) to a Rekordbox cue color.
        /// </summary>
        public static SLSKDONET.Models.CueColor GetColorForEnergy(int energyScore)
        {
            return energyScore switch
            {
                >= 9 => SLSKDONET.Models.CueColor.Red,
                >= 7 => SLSKDONET.Models.CueColor.Orange,
                >= 5 => SLSKDONET.Models.CueColor.Yellow,
                >= 3 => SLSKDONET.Models.CueColor.Green,
                _ => SLSKDONET.Models.CueColor.Blue
            };
        }
    }
}
