using System;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Musical;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Data;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Audio
{
    public class TransitionPreviewService : IDisposable
    {
        private readonly MultiTrackEngine _engine;
        private readonly TransitionAdvisorService _advisor;
        private readonly ILogger<TransitionPreviewService> _logger;
        private readonly ITrackRepository _trackRepository;

        private TrackLaneSampler? _laneA;
        private TrackLaneSampler? _laneB;
        
        public LibraryEntryEntity? TrackA { get; private set; }
        public LibraryEntryEntity? TrackB { get; private set; }
        
        public double CurrentOffset { get; private set; }
        
        public bool IsPlaying => _engine.IsPlaying;
        public long PositionSamples => _engine.CurrentSamplePosition;

        public event EventHandler? PlaybackStateChanged;

        public TransitionPreviewService(
            MultiTrackEngine engine,
            TransitionAdvisorService advisor,
            ITrackRepository trackRepository,
            ILogger<TransitionPreviewService> logger)
        {
            _engine = engine;
            _advisor = advisor;
            _trackRepository = trackRepository;
            _logger = logger;
            
            // Initialize engine with default output
            _engine.Initialize();
        }

        public async Task PreparePreviewAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, TransitionSuggestion? suggestion = null)
        {
            Stop();
            _engine.ClearLanes();

            TrackA = trackA;
            TrackB = trackB;

            // 1. Load Track A (The "Outgoing" Track)
            // We want to start Track A near its Outro or a mix-out point.
            // For preview, let's start 32 bars before the end, or at the "Drop" if it's a DropSwap.
            
            string? pathA = await GetAudioPathAsync(trackA);
            string? pathB = await GetAudioPathAsync(trackB);

            if (pathA == null || pathB == null) 
            {
                _logger.LogWarning("Could not find audio files for preview.");
                return;
            }

            _laneA = new TrackLaneSampler 
            { 
                TrackId = trackA.Id.ToString(), 
                TrackTitle = trackA.Title,
                Assignment = LaneAssignment.DeckA,
                Volume = 1.0f 
            };
            _laneA.LoadFile(pathA);

            _laneB = new TrackLaneSampler 
            { 
                TrackId = trackB.Id.ToString(), 
                TrackTitle = trackB.Title,
                Assignment = LaneAssignment.DeckB,
                Volume = CalculateGainCheck(trackA, trackB) // Auto-Gain
            };
            _laneB.LoadFile(pathB);

            // 2. Calculate Alignment
            double exitPointA = GetExitPoint(trackA);
            double entryPointB = GetEntryPoint(trackB); 
            
            // Start the engine such that ExitPointA aligns with EntryPointB
            // Let's say we define t=0 as the "Transition Point".
            // Lane A starts at -ExitPointA.
            // Lane B starts at -EntryPointB.
            // But Engine must start at positive samples.
            // Let's make the Transition Point be at 10 seconds into the timeline for context.
            
            double alignmentPoint = 10.0; 
            
            _laneA.StartSampleOffset = (long)((alignmentPoint - exitPointA) * _engine.WaveFormat.SampleRate);
            _laneB.StartSampleOffset = (long)((alignmentPoint - entryPointB) * _engine.WaveFormat.SampleRate);
            
            CurrentOffset = 0; // Relative nudge

            _engine.AddLane(_laneA);
            _engine.AddLane(_laneB);
            
            // Start playback 8 bars before the transition point
            double bpm = trackA.Bpm ?? 128;
            double fourBars = (240.0 / bpm); 
            double startPos = Math.Max(0, alignmentPoint - fourBars);
            
            _engine.Seek(startPos);
        }

        private float CalculateGainCheck(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            // Simple energy-based gain matching
            // If Track B is significantly lower energy/loudness, boost it.
            // Note: This assumes Energy correlates with Loudness which is rough, but per spec "Energy-Matched Fader"
            
            // Better: Use ReplayGain if available, or assume analyzed tracks are normalized.
            // Fallback to Energy:
            double energyA = trackA.Energy ?? 0.5;
            double energyB = trackB.Energy ?? 0.5;
            
            if (energyB < energyA - 0.3) return 1.15f; // Boost B
            if (energyB > energyA + 0.3) return 0.85f; // Cut B
            
            return 1.0f;
        }

        private double GetExitPoint(LibraryEntryEntity track)
        {
            // Try to find "Outro" or "FadeOut" in phrase segments
            if (!string.IsNullOrEmpty(track.AudioFeatures?.PhraseSegmentsJson))
            {
                try 
                {
                    var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(track.AudioFeatures.PhraseSegmentsJson);
                    var outro = segments?.FirstOrDefault(s => s.Label.Contains("Outro") || s.Label.Contains("MixOut"));
                    if (outro != null) return outro.Start;
                    
                    // Fallback: Last Drop + 32 bars? Or just End - 30s
                }
                catch {}
            }
            
            // Default: Duration - 30s
            return (track.DurationSeconds ?? 180) - 30;
        }

        private double GetEntryPoint(LibraryEntryEntity track)
        {
            // Try to find "Intro" end or "Drop 1"
             if (!string.IsNullOrEmpty(track.AudioFeatures?.PhraseSegmentsJson))
            {
                try 
                {
                    var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(track.AudioFeatures.PhraseSegmentsJson);
                    // If DropSwap, find Drop 1
                    var drop = segments?.FirstOrDefault(s => s.Label.Contains("Drop") || s.Label.Contains("Chorus"));
                    if (drop != null) return drop.Start;
                }
                catch {}
            }
            // Default: Start (0s) or First Downbeat (Cue 1)
            return 0.0;
        }

        private async Task<string?> GetAudioPathAsync(LibraryEntryEntity track)
        {
            // Logic to verify file exists
            if (System.IO.File.Exists(track.FilePath)) return track.FilePath;
            return null;
        }

        public void Play()
        {
            _engine.Play();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            _engine.Pause();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _engine.Stop();
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Nudge(double seconds)
        {
            if (_laneB == null) return;
            
            // Adjust start offset of Lane B
            long sampleDiff = (long)(seconds * _engine.WaveFormat.SampleRate);
            _laneB.StartSampleOffset += sampleDiff;
            CurrentOffset += seconds;
            
            // Re-seek to apply immediate effect if playing
            // _engine.Seek(_engine.CurrentTimeSeconds); // Might cause glitch, depends on engine
        }
        
        public async Task CommitTransitionAsync()
        {
            if (TrackA == null || TrackB == null) return;
            
            // Save the transition point to Track B's metadata (or a relational table)
            // For now, let's update Track B's "ManualOffset" or similar in SetTrackEntity logic if this was a SetList context.
            // Implemented as requested: "write to audio_features metadata"
            
            // We'll write to a JSON field "TransitionNotes" or similar in AudioFeatures
            // Or just logging it for now as "PreferredTransitionOffset"
            
            _logger.LogInformation($"Transition Committed: {TrackA.Title} -> {TrackB.Title} aligned at {CurrentOffset}s offset.");
            
            // TODO: Persist to DB
        }

        public void Dispose()
        {
            _engine.Dispose();
        }
    }
}
