using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Musical
{
    public interface IPhraseAlignmentService
    {
        (double Time, string Reason)? DetermineOptimalTransitionTime(LibraryEntryEntity trackA, LibraryEntryEntity trackB, TransitionArchetype archetype);
    }

    public class PhraseAlignmentService : IPhraseAlignmentService
    {
        private readonly ILogger<PhraseAlignmentService> _logger;

        public PhraseAlignmentService(ILogger<PhraseAlignmentService> logger)
        {
            _logger = logger;
        }

        public (double Time, string Reason)? DetermineOptimalTransitionTime(LibraryEntryEntity trackA, LibraryEntryEntity trackB, TransitionArchetype archetype)
        {
            // 1. Overrides first
            var overrideResult = CheckOverrides(trackA, trackB, archetype);
            if (overrideResult.HasValue) return overrideResult.Value;

            // 2. Identify Candidates
            var candidates = IdentifyCandidates(trackA);

            if (!candidates.Any()) return null;

            // 3. Score Candidates
            var bestCandidate = candidates
                .Select(c => new { Time = c, ScoreDetails = ScoreCandidate(c, trackA, trackB) })
                .OrderByDescending(x => x.ScoreDetails.Score)
                .FirstOrDefault();

            if (bestCandidate != null && bestCandidate.ScoreDetails.Score > 0)
            {
                return (bestCandidate.Time, bestCandidate.ScoreDetails.Reason);
            }

            return null;
        }

        private (double Time, string Reason)? CheckOverrides(LibraryEntryEntity trackA, LibraryEntryEntity trackB, TransitionArchetype archetype)
        {
            // Build-to-Drop
            if (archetype == TransitionArchetype.BuildToDrop)
            {
                var dropTimeB = GetDropTime(trackB);
                var buildEndA = GetBuildEndTime(trackA);
                
                if (dropTimeB > 0 && buildEndA > 0)
                {
                    double startBAt = Math.Max(0, buildEndA - dropTimeB);
                    return (startBAt, "4 bars before Track B's drop for a Build-to-Drop slam");
                }
            }

            // Drop-Swap
            if (archetype == TransitionArchetype.DropSwap)
            {
                var dropTimeA = GetDropTime(trackA);
                var dropTimeB = GetDropTime(trackB);
                
                // Drop Swap: Both drops hit at same time.
                if (dropTimeA > 0 && dropTimeB > 0)
                {
                    double startBAt = Math.Max(0, dropTimeA - dropTimeB);
                    return (startBAt, "Drop-Swap alignment to hit both drops simultaneously");
                }
            }

            return null;
        }

        private double GetDropTime(LibraryEntryEntity track)
        {
            return track.AudioFeatures?.DropTimeSeconds ?? track.DropTimestamp ?? 0;
        }

        private double GetBuildEndTime(LibraryEntryEntity track)
        {
            var segments = ParseSegments(track);
            var build = segments.FirstOrDefault(s => 
                s.Label.Contains("Build", StringComparison.OrdinalIgnoreCase) || 
                s.Label.Contains("Rise", StringComparison.OrdinalIgnoreCase));
            
            if (build != null)
            {
                // Assuming we want the END of the build
                return build.Start + build.Duration;
            }
            return 0;
        }

        private List<double> IdentifyCandidates(LibraryEntryEntity track)
        {
            var candidates = new List<double>();
            
            // Phrases
            var segments = ParseSegments(track);
            foreach (var seg in segments)
            {
                candidates.Add(seg.Start); // Start of phrase
                candidates.Add(seg.Start + seg.Duration); // End of phrase
            }

            // Vocal Ends
            if (track.VocalEndSeconds.HasValue && track.VocalEndSeconds > 10)
            {
                candidates.Add(track.VocalEndSeconds.Value);
            }

            // Filter out very early candidates (< 30s) or very late (> duration - 10s)
            // Or just return all and let scoring handle.
            return candidates.Distinct().Where(t => t > 30).ToList();
        }

        private (double Score, string Reason) ScoreCandidate(double time, LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            double score = 0;
            var reasons = new List<string>();

            // 1. Phrase Alignment
            if (IsPhraseBoundary(trackA, time)) 
            {
                score += 3;
                reasons.Add("phrase boundary aligns");
            }

            // 2. Vocal Clearance
            if (trackA.VocalEndSeconds.HasValue && time > trackA.VocalEndSeconds.Value)
            {
                score += 3;
                reasons.Add("vocals clear");
            }
            else if (trackA.VocalEndSeconds.HasValue && Math.Abs(time - trackA.VocalEndSeconds.Value) < 5)
            {
                score += 2; // Close to end
                reasons.Add("vocals ending");
            }

            // Penalties
            // If inside vocal region
            if (trackA.VocalStartSeconds.HasValue && trackA.VocalEndSeconds.HasValue && 
                time > trackA.VocalStartSeconds && time < trackA.VocalEndSeconds)
            {
                score -= 3;
            }

            // If no positive reasons, return generic if score > 0?
            if (reasons.Count == 0 && score > 0) reasons.Add("good structural point");

            return (score, string.Join(" and ", reasons));
        }

        private bool IsPhraseBoundary(LibraryEntryEntity track, double time)
        {
            var segments = ParseSegments(track);
            return segments.Any(s => Math.Abs(s.Start - time) < 1.0 || Math.Abs((s.Start + s.Duration) - time) < 1.0);
        }

        private List<PhraseSegment> ParseSegments(LibraryEntryEntity track)
        {
            if (string.IsNullOrEmpty(track.AudioFeatures?.PhraseSegmentsJson) || track.AudioFeatures.PhraseSegmentsJson == "[]")
                return new List<PhraseSegment>();
            
            try
            {
                return JsonSerializer.Deserialize<List<PhraseSegment>>(track.AudioFeatures.PhraseSegmentsJson) ?? new List<PhraseSegment>();
            }
            catch
            {
                return new List<PhraseSegment>();
            }
        }
    }
}
