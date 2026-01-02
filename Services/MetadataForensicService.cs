using System;
using System.Text.RegularExpressions;
using Soulseek;
using SLSKDONET.Models;

namespace SLSKDONET.Services
{
    // [AUDIT FIX: Enforce Static Utility]
    public static class MetadataForensicService
    {
        private static readonly Regex VbrRegex = new Regex(@"V\d+|VBR", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LosslessRegex = new Regex(@"\.(flac|wav|aiff|alac)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SuspiciousExtensions = new Regex(@"\.(wma|ogg|wmv)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static int CalculateTrustScore(Track result)
        {
            int score = 50; // Base score

            // 1. Bitrate Check
            if (result.Bitrate > 0)
            {
                if (result.Bitrate >= 320)
                    score += 10;
                else if (result.Bitrate < 128)
                    score -= 20;
            }

            // 2. Format Trust
            if (string.IsNullOrEmpty(result.Filename)) return score; // Safety

            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();
            if (LosslessRegex.IsMatch(result.Filename))
            {
                score += 20;
                // Verify size for lossless (approx 5MB/min minimum)
                if (result.Length.HasValue && result.Size > 0)
                {
                    double minutes = result.Length.Value / 60.0;
                    if (minutes > 0)
                    {
                        double mbPerMin = (result.Size.Value / 1024.0 / 1024.0) / minutes;
                        if (mbPerMin < 2.5) score -= 40; // Too small for FLAC
                    }
                }
            }
            else if (ext == ".mp3" || ext == ".m4a")
            {
                score += 5;
            }
            else if (SuspiciousExtensions.IsMatch(result.Filename))
            {
                score -= 10;
            }

            // 3. Compression Mismatch (The Fake Detector)
            // Expected Size (Bytes) = (Bitrate (kbps) * 1000 / 8) * Duration (Seconds)
            if (result.Bitrate > 0 && result.Length.HasValue && result.Length > 0 && result.Size.HasValue)
            {
                // Strict check for 320kbps MP3s
                if (result.Bitrate >= 320 && (ext == ".mp3"))
                {
                    double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                    double actualBytes = result.Size.Value;

                    // If file is < 70% of expected size, it's mathematically impossible to be CBR 320
                    if (actualBytes < (expectedBytes * 0.70))
                    {
                        score -= 50; // HEAVY PENALTY
                    }
                    // If file is > 130% of expected size, it might include huge art or be mislabeled
                    else if (actualBytes > (expectedBytes * 1.3))
                    {
                        score -= 5; // Slight penalty for bloat
                    }
                    else
                    {
                        score += 10; // Mathematical match verified
                    }
                }
            }

            // 4. Availability
            if (result.UploadSpeed > 0) score += 5;
            if (result.HasFreeUploadSlot) score += 10;

            return Math.Clamp(score, 0, 100);
        }

        public static string GetForensicAssessment(Track result)
        {
            var notes = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(result.Filename)) return "Unknown";
            
            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();

            // Fake Check
            if (result.Bitrate >= 320 && result.Length.HasValue && (ext == ".mp3") && result.Size.HasValue)
            {
                double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                if (result.Size.Value < (expectedBytes * 0.70))
                {
                    notes.Add("⚠️ SIZE MISMATCH: Use caution. File is too small for 320kbps.");
                }
                else if (result.Size.Value > (expectedBytes * 0.95) && result.Size.Value < (expectedBytes * 1.05))
                {
                    notes.Add("✅ VERIFIED: Size matches bitrate perfectly.");
                }
            }

            if (LosslessRegex.IsMatch(result.Filename))
            {
                notes.Add("💎 LOSSLESS: High fidelity format.");
            }

            if (result.HasFreeUploadSlot)
            {
                notes.Add("⚡ INSTANT: Slot available now.");
            }

            if (notes.Count == 0) return "Standard Result";
            return string.Join(" | ", notes);
        }

        public static bool IsGoldenMatch(Track result)
        {
            return CalculateTrustScore(result) >= 85;
        }

        public static bool IsFake(Track result)
        {
            return CalculateTrustScore(result) < 40;
        }
    }
}
