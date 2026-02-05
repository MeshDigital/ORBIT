using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Musical
{
    /// <summary>
    /// Analyzes AI-extracted vocal density curves to derive high-level metrics.
    /// Used to classify track vocal types and detect transition conflicts.
    /// </summary>
    public class VocalIntelligenceService
    {
        /// <summary>
        /// Analyzes a vocal density curve and track duration to produce vocal metrics.
        /// </summary>
        /// <param name="densityCurve">Time-series vocal density data (0.0 to 1.0).</param>
        /// <param name="trackDuration">Total duration of the track in seconds.</param>
        /// <returns>Analyzed vocal metrics.</returns>
        public VocalMetrics AnalyzeVocalDensity(float[]? densityCurve, double trackDuration)
        {
            if (densityCurve == null || densityCurve.Length == 0 || trackDuration <= 0)
            {
                return new VocalMetrics 
                { 
                    Type = VocalType.Instrumental,
                    Intensity = 0f
                };
            }

            double avgDensity = densityCurve.Average();
            double peakDensity = densityCurve.Max();
            double densityStepSeconds = trackDuration / densityCurve.Length;

            // Find boundaries and continuous regions
            int firstIdx = -1;
            int lastIdx = -1;
            int continuousPeakCount = 0;
            int maxContinuousPeakCount = 0;
            int totalActiveStates = 0;

            const float activeThreshold = 0.15f;

            for (int i = 0; i < densityCurve.Length; i++)
            {
                if (densityCurve[i] >= activeThreshold)
                {
                    if (firstIdx == -1) firstIdx = i;
                    lastIdx = i;
                    continuousPeakCount++;
                    maxContinuousPeakCount = Math.Max(maxContinuousPeakCount, continuousPeakCount);
                    totalActiveStates++;
                }
                else
                {
                    continuousPeakCount = 0;
                }
            }

            var metrics = new VocalMetrics
            {
                Intensity = (float)Math.Clamp(avgDensity * 2.5, 0.0, 1.0), // Normalize density to intensity score
                StartSeconds = firstIdx != -1 ? (float)(firstIdx * densityStepSeconds) : null,
                EndSeconds = lastIdx != -1 ? (float)(lastIdx * densityStepSeconds) : null
            };

            // Classification Heuristics
            double maxContinousSeconds = maxContinuousPeakCount * densityStepSeconds;
            
            if (avgDensity < 0.02 && peakDensity < 0.1)
            {
                metrics.Type = VocalType.Instrumental;
            }
            else if (avgDensity < 0.1 || maxContinousSeconds < 8.0)
            {
                metrics.Type = VocalType.SparseVocals;
            }
            else if (avgDensity < 0.25 || maxContinousSeconds < 24.0)
            {
                metrics.Type = VocalType.HookOnly;
            }
            else
            {
                metrics.Type = VocalType.FullLyrics;
            }

            return metrics;
        }

        /// <summary>
        /// Calculates the overlap hazard between two vocal curves at a given offset.
        /// Returns a score from 0.0 (Safe) to 1.0 (Critical Conflict).
        /// </summary>
        public double CalculateOverlapHazard(
            float[] curveA, 
            float[] curveB, 
            double offsetSeconds, 
            double trackADuration, 
            double trackBDuration)
        {
            if (curveA == null || curveB == null || curveA.Length == 0 || curveB.Length == 0)
                return 0.0;

            double stepA = trackADuration / curveA.Length;
            double stepB = trackBDuration / curveB.Length;

            double hazardSum = 0;
            double overlapSampleCount = 0;

            // We only care about the region where Track B overlaps Track A
            // Track B starts at offsetSeconds in Track A's timeline
            double overlapStart = Math.Max(0, offsetSeconds);
            double overlapEnd = Math.Min(trackADuration, offsetSeconds + trackBDuration);

            if (overlapEnd <= overlapStart) return 0.0;

            // Iterating with a fixed tiny step for high-precision overlap check
            double checkStep = 0.5; // check every half second
            for (double t = overlapStart; t < overlapEnd; t += checkStep)
            {
                // Find index in curve A (absolute time t)
                int idxA = (int)(t / stepA);
                // Find index in curve B (relative time t - offsetSeconds)
                int idxB = (int)((t - offsetSeconds) / stepB);

                if (idxA >= 0 && idxA < curveA.Length && idxB >= 0 && idxB < curveB.Length)
                {
                    double valA = curveA[idxA];
                    double valB = curveB[idxB];
                    
                    // The hazard is high when BOTH are high
                    // Using a non-linear penalty for high-density overlap
                    hazardSum += (valA * valB);
                    overlapSampleCount++;
                }
            }

            if (overlapSampleCount == 0) return 0.0;

            // Normalize and scale
            double hazard = (hazardSum / overlapSampleCount) * 10.0; // Scale to detectable range
            return Math.Clamp(hazard, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Result of a vocal density analysis.
    /// </summary>
    public class VocalMetrics
    {
        public VocalType Type { get; set; }
        public float Intensity { get; set; }
        public float? StartSeconds { get; set; }
        public float? EndSeconds { get; set; }
    }
}
