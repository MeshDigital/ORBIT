using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Musical
{
    /// <summary>
    /// Analyzes AI-extracted vocal density curves to derive high-level metrics.
    /// Used to classify track vocal types and detect transition conflicts.
    /// </summary>
    public class VocalIntelligenceService
    {
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
                Intensity = (float)Math.Clamp(avgDensity * 2.5, 0.0, 1.0),
                StartSeconds = firstIdx != -1 ? (float)(firstIdx * densityStepSeconds) : null,
                EndSeconds = lastIdx != -1 ? (float)(lastIdx * densityStepSeconds) : null
            };

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

        public double CalculateOverlapHazard(float[] curveA, float[] curveB, double offsetSeconds, double trackADuration, double trackBDuration)
        {
            if (curveA == null || curveB == null || curveA.Length == 0 || curveB.Length == 0)
                return 0.0;

            double stepA = trackADuration / curveA.Length;
            double stepB = trackBDuration / curveB.Length;

            double overlapStart = Math.Max(0, offsetSeconds);
            double overlapEnd = Math.Min(trackADuration, offsetSeconds + trackBDuration);

            if (overlapEnd <= overlapStart) return 0.0;

            double hazardSum = 0;
            double overlapSampleCount = 0;
            double checkStep = 0.5;

            for (double t = overlapStart; t < overlapEnd; t += checkStep)
            {
                int idxA = (int)(t / stepA);
                int idxB = (int)((t - offsetSeconds) / stepB);

                if (idxA >= 0 && idxA < curveA.Length && idxB >= 0 && idxB < curveB.Length)
                {
                    hazardSum += (curveA[idxA] * curveB[idxB]);
                    overlapSampleCount++;
                }
            }

            if (overlapSampleCount == 0) return 0.0;
            return Math.Clamp((hazardSum / overlapSampleCount) * 10.0, 0.0, 1.0);
        }

        public double CalculateLocalizedHazard(float[] curveA, float[] curveB, double offsetSeconds, double currentTimeSeconds, double windowSeconds, double trackADuration, double trackBDuration)
        {
            if (curveA == null || curveB == null || curveA.Length == 0 || curveB.Length == 0)
                return 0.0;

            double stepA = trackADuration / curveA.Length;
            double stepB = trackBDuration / curveB.Length;

            double hazardSum = 0;
            int sampleCount = 0;
            double checkStep = 0.25;

            for (double t = currentTimeSeconds; t < currentTimeSeconds + windowSeconds; t += checkStep)
            {
                int idxA = (int)(t / stepA);
                int idxB = (int)((t - offsetSeconds) / stepB);

                if (idxA >= 0 && idxA < curveA.Length && idxB >= 0 && idxB < curveB.Length)
                {
                    hazardSum += (curveA[idxA] * curveB[idxB]);
                    sampleCount++;
                }
            }

            if (sampleCount == 0) return 0.0;
            return Math.Clamp((hazardSum / sampleCount) * 15.0, 0.0, 1.0);
        }
    }

    public class VocalMetrics
    {
        public VocalType Type { get; set; }
        public float Intensity { get; set; }
        public float? StartSeconds { get; set; }
        public float? EndSeconds { get; set; }
    }
}
