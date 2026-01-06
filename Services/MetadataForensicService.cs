using System;
using System.Text.RegularExpressions;
using Soulseek;
using SLSKDONET.Models;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Phase 14: Forensic Core - Pre-download file authenticity verification.
    /// 
    /// WHY THIS EXISTS:
    /// P2P networks are full of fake files - 64kbps MP3s labeled as "320kbps", 
    /// lossy files with .flac extensions, corrupted uploads. Downloading these wastes
    /// bandwidth, disk space, and user trust. This service applies mathematical forensics
    /// to detect fraud BEFORE download using only metadata (size, bitrate, duration).
    /// 
    /// PHILOSOPHY:
    /// "Trust but Verify" - We calculate what a file SHOULD be and compare to reality.
    /// Physics doesn't lie: 320kbps * 5 minutes = predictable file size (±container overhead).
    /// </summary>
    // [CHANGE 1] Class must be static
    public static class MetadataForensicService
    {
        private static readonly Regex VbrRegex = new Regex(@"V\d+|VBR", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LosslessRegex = new Regex(@"\.(flac|wav|aiff|alac)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        
        // WHY: .wma and .ogg are often lower quality in P2P networks (128kbps defaults)
        // .wmv is video (wrong file type entirely - red flag for mislabeled uploads)
        private static readonly Regex SuspiciousExtensions = new Regex(@"\.(wma|ogg|wmv)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // [CHANGE 2] Method must be static
        public static int CalculateTrustScore(Track result)
        {
            // WHY: Start at 50 (neutral) instead of 0 or 100:
            // - Allows both positive rewards and negative penalties
            // - 50+ = "Worth considering", <50 = "Avoid unless desperate"
            // - Final score: 0-100 scale matches user intuition (like percentages)
            int score = 50; // Base score

            // 1. Bitrate Check
            // WHY: Bitrate is the PRIMARY quality indicator we can verify pre-download
            if (result.Bitrate > 0)
            {
                // WHY: 320kbps is "transparent" (indistinguishable from lossless in blind tests)
                if (result.Bitrate >= 320) score += 10;
                // WHY: <128kbps is audibly compressed (artifacts on cymbals, vocals)
                else if (result.Bitrate < 128) score -= 20;
            }

            // 2. Format Trust
            if (string.IsNullOrEmpty(result.Filename)) return score;

            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();
            if (LosslessRegex.IsMatch(result.Filename))
            {
                score += 20; // High reward for lossless formats
                
                // WHY: FLAC verification - Lossless formats have predictable sizes:
                // - CD Audio: 1411kbps (44.1kHz * 16-bit * 2 channels)
                // - FLAC compresses to ~50-60% of WAV (no quality loss, just compression)
                // - Result: ~5-10 MB/minute for typical music
                // - Anything < 2.5 MB/min is mathematically impossible for lossless
                if (result.Length.HasValue && result.Size > 0)
                {
                    double minutes = result.Length.Value / 60.0;
                    if (minutes > 0)
                    {
                        double mbPerMin = (result.Size.Value / 1024.0 / 1024.0) / minutes;
                        // WHY: 2.5 MB/min threshold = ~333kbps average
                        // Real FLAC is 700-900kbps. Below 333 = fake/corrupt.
                        if (mbPerMin < 2.5) score -= 40; // HEAVY PENALTY: Too small for FLAC
                    }
                }
            }
            else if (ext == ".mp3" || ext == ".m4a") score += 5;
            else if (SuspiciousExtensions.IsMatch(result.Filename)) score -= 10;

            // 3. Compression Mismatch (The Fake Detector)
            // WHY: This is the CORE forensic technique - math doesn't lie
            // Bitrate = bits per second, so File Size = (Bitrate * Duration) / 8
            // If actual size << expected size, the file is NOT the claimed bitrate
            if (result.Bitrate > 0 && result.Length.HasValue && result.Length > 0 && result.Size.HasValue)
            {
                if (result.Bitrate >= 320 && (ext == ".mp3"))
                {
                    // MATH: 320kbps = 320,000 bits/sec = 40,000 bytes/sec = 2.4 MB/min
                    // Example: 5-minute track = 12 MB (±10% for ID3 tags, VBR variance)
                    double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                    double actualBytes = result.Size.Value;

                    // WHY: ±15% variance + 10% buffer = 25% allowance
                    // - Real 320kbps: 75-125% of expected
                    // - Fake (64kbps upscaled): ~20% of expected = BUSTED
                    if (actualBytes < (expectedBytes * 0.75)) score -= 50; // HEAVY PENALTY (Fake)
                    else if (actualBytes > (expectedBytes * 1.25)) score -= 10; // Possibly mislabeled WAV/Bloat
                    // WHY: Perfect size match = high confidence in authenticity
                    else score += 10;
                }
            }

            // 4. Availability
            // WHY: Quality means nothing if the file is unreachable
            // Free slots = instant download, queue = potential wait/timeout
            if (result.UploadSpeed > 0) score += 5; // User has bandwidth
            if (result.HasFreeUploadSlot) score += 10; // No queue = reliable

            // WHY: Clamp prevents score overflow/underflow from extreme cases
            return Math.Clamp(score, 0, 100);
        }

        public static string GetForensicAssessment(Track result)
        {
            var notes = new System.Collections.Generic.List<string>();
            if (string.IsNullOrEmpty(result.Filename)) return "Unknown";
            
            var ext = System.IO.Path.GetExtension(result.Filename)?.ToLower();

            if (result.Bitrate >= 320 && result.Length.HasValue && (ext == ".mp3") && result.Size.HasValue)
            {
                double expectedBytes = (result.Bitrate * 1000.0 / 8.0) * result.Length.Value;
                if (result.Size.Value < (expectedBytes * 0.75))
                    notes.Add("⚠️ SIZE MISMATCH: Use caution. File is too small for 320kbps.");
                else if (result.Size.Value > (expectedBytes * 0.90) && result.Size.Value < (expectedBytes * 1.10))
                    notes.Add("✅ VERIFIED: Size matches bitrate perfectly.");
            }

            if (LosslessRegex.IsMatch(result.Filename)) notes.Add("💎 LOSSLESS: High fidelity format.");
            if (result.HasFreeUploadSlot) notes.Add("⚡ INSTANT: Slot available now.");

            if (notes.Count == 0) return "Standard Result";
            return string.Join(" | ", notes);
        }

        public static bool IsGoldenMatch(Track result) => CalculateTrustScore(result) >= 85;
        public static bool IsFake(Track result) => CalculateTrustScore(result) < 40;
    }
}
