using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;

using SLSKDONET.ViewModels; // For SetlistTrackItem

namespace SLSKDONET.Services
{
    public interface ISetIntelligenceService
    {
        Task<SetHealthReport> AnalyzeSetlistAsync(IEnumerable<SetlistTrackItem> tracks);
        Task<IEnumerable<LibraryEntry>> FindStandardMixCandidatesAsync(SetlistTrackItem source);
        Task<IEnumerable<LibraryEntry>> FindBridgeTracksAsync(SetlistTrackItem from, SetlistTrackItem to);
    }

    public record SetHealthReport
    {
        public int Score { get; init; }
        public List<KeyClashIssue> KeyClashes { get; init; } = new();
        public List<EnergyGapIssue> EnergyGaps { get; init; } = new();
        public List<VocalClashIssue> VocalClashes { get; init; } = new();
        public List<MixingAdvice> Advice { get; init; } = new();
    }

    public class MixingAdvice
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "💡";
    }

    public class SetIntelligenceService : ISetIntelligenceService
    {
        private readonly ILibraryService _libraryService; // For finding candidates

        // Camelot Wheel logic
        private static readonly Dictionary<string, HashSet<string>> _camelotMap;

        static SetIntelligenceService()
        {
            _camelotMap = new Dictionary<string, HashSet<string>>();
            InitializeCamelotMap();
        }

        public SetIntelligenceService(ILibraryService libraryService)
        {
             _libraryService = libraryService;
        }

        private static void InitializeCamelotMap()
        {
            // Simple Relative Major/Minor and +/- 1 logic
            // e.g., 8A -> 8A, 9A, 7A, 8B
            for (int i = 1; i <= 12; i++)
            {
                var next = i == 12 ? 1 : i + 1;
                var prev = i == 1 ? 12 : i - 1;
                
                // A (Minor)
                string currentA = $"{i}A";
                _camelotMap[currentA] = new HashSet<string> 
                { 
                    $"{i}A", $"{next}A", $"{prev}A", $"{i}B" 
                };

                // B (Major)
                string currentB = $"{i}B";
                _camelotMap[currentB] = new HashSet<string> 
                { 
                    $"{i}B", $"{next}B", $"{prev}B", $"{i}A" 
                };
            }
        }

        public Task<SetHealthReport> AnalyzeSetlistAsync(IEnumerable<SetlistTrackItem> tracks)
        {
            var report = new SetHealthReport();
            var trackList = tracks.ToList();

            if (trackList.Count < 2) 
            {
                report = report with { Score = 100 };
                return Task.FromResult(report);
            }

            int issues = 0;

            for (int i = 0; i < trackList.Count - 1; i++)
            {
                var current = trackList[i];
                var next = trackList[i + 1];

                // 1. Key Analysis
                // Note: We access properties from SetlistTrackItem which should be populated
                string keyA = current.Key?.ToUpperInvariant() ?? "";
                string keyB = next.Key?.ToUpperInvariant() ?? "";

                if (!string.IsNullOrEmpty(keyA) && !string.IsNullOrEmpty(keyB) && keyA != keyB)
                {
                    bool compatible = false;
                    if (_camelotMap.TryGetValue(keyA, out var options))
                    {
                        compatible = options.Contains(keyB);
                    }
                    
                    // Allow Energy Boost (+2 Semitones / +7 Camelot? No, keeping it strict +/- 1 for now)
                    
                    if (!compatible)
                    {
                         report.KeyClashes.Add(new KeyClashIssue
                         {
                             TrackA = current.Title,
                             TrackB = next.Title,
                             TransitionIndex = i,
                             Description = $"Harmonic Clash: {keyA} is not compatible with {keyB}"
                         });
                         issues++;
                    }
                }

                // 2. Energy Analysis
                double energyA = current.Energy; // 0-10 scale assumed from VM
                double energyB = next.Energy;
                
                // Gap > 4 is jarring (e.g. 8 -> 3)
                if (Math.Abs(energyA - energyB) > 4)
                {
                    report.EnergyGaps.Add(new EnergyGapIssue
                    {
                        TrackIndex = i,
                        FromEnergy = energyA,
                        ToEnergy = energyB,
                        Description = energyB > energyA ? "Severe Energy Spike" : "Energy Drop-off"
                    });
                    issues++;
                }

                // 3. Vocal Analysis (Vocal Overlap)
                // We need timestamps for this, but SetlistTrackItem is high-level. 
                // We check general probability for now.
                // High Vocal Prob on Outro of A + High Vocal Prob on Intro of B = Clash
                
                // For Sprint 3, existing logic used a "VocalProbability" heuristic per track
                if (current.VocalProbability > 0.8 && next.VocalProbability > 0.8)
                {
                    report.VocalClashes.Add(new VocalClashIssue
                    {
                        TrackA = current.Title,
                        TrackB = next.Title,
                        TransitionIndex = i,
                        Description = "Potential Vocal Clash (Both tracks have high vocal density)"
                    });
                    issues++;
                }
            }

            // Advice Generation
            if (trackList.Count > 0)
            {
                // Check flow
                var first = trackList.First();
                var last = trackList.Last();
                if (first.Energy > 8 && last.Energy < 4)
                {
                     report.Advice.Add(new MixingAdvice 
                     { 
                         Title = "Dying Energy Flow", 
                         Description = "Set starts high energy but ends very low. Consider re-ordering." 
                     });
                }
            }

            int score = Math.Max(0, 100 - (issues * 10));
            return Task.FromResult(report with { Score = score });
        }

        public async Task<IEnumerable<LibraryEntry>> FindStandardMixCandidatesAsync(SetlistTrackItem source)
        {
             // TODO: Integrate SmartSorterService logic here 
             // Logic: Find tracks with compatible Key and Energy +/- 2
             return Enumerable.Empty<LibraryEntry>();
        }

        public async Task<IEnumerable<LibraryEntry>> FindBridgeTracksAsync(SetlistTrackItem from, SetlistTrackItem to)
        {
             // Logic: Find track X where Key(From)->Key(X) is valid AND Key(X)->Key(To) is valid
             // And Energy is between From/To
             return Enumerable.Empty<LibraryEntry>();
        }
    }
}
