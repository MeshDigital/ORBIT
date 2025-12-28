using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels
{
    public class TrackInspectorViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Services.IAudioAnalysisService _audioAnalysisService;
        private readonly Services.IEventBus _eventBus;
        private readonly CompositeDisposable _disposables = new();
        private Data.Entities.AudioAnalysisEntity? _analysis;
        private Data.Entities.AudioFeaturesEntity? _audioFeatures; // Phase 4: Musical Intelligence
        
        private bool _isAnalyzing;
        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set => SetProperty(ref _isAnalyzing, value);
        }

        public TrackInspectorViewModel(Services.IAudioAnalysisService audioAnalysisService, Services.IEventBus eventBus)
        {
            _audioAnalysisService = audioAnalysisService;
            _eventBus = eventBus;

            // Phase 12.6: Listen for global track selection
            _eventBus.GetEvent<TrackSelectionChangedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt => Track = evt.Track)
                .DisposeWith(_disposables);

            // Phase B: Listen for audio analysis completion
            _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnAnalysisCompleted)
                .DisposeWith(_disposables);
        }

        private PlaylistTrack? _track;
        public PlaylistTrack? Track
        {
            get => _track;
            set
            {
                if (SetProperty(ref _track, value))
                {
                    OnPropertyChanged(nameof(HasTrack));
                    OnPropertyChanged(nameof(CamelotKey));
                    OnPropertyChanged(nameof(BitrateLabel));
                    OnPropertyChanged(nameof(AudioGuardColor));
                    OnPropertyChanged(nameof(AudioGuardIcon));
                    OnPropertyChanged(nameof(FrequencyCutoffLabel));
                    OnPropertyChanged(nameof(ConfidenceLabel));
                    OnPropertyChanged(nameof(IsTrustworthy));
                    OnPropertyChanged(nameof(Details));
                    OnPropertyChanged(nameof(TrustColor));
                    OnPropertyChanged(nameof(Energy));
                    OnPropertyChanged(nameof(Danceability));
                    OnPropertyChanged(nameof(Valence));
                    
                    // Reset analysis
                    _analysis = null;
                    _analysis = null;
                    _audioFeatures = null; // Phase 4
                    ForensicLogs.Clear(); // Phase 4.7
                    NotifyAnalysisProperties();
                    NotifyMusicalIntelligenceProperties(); // Phase 4
                    OnPropertyChanged(nameof(ForensicLogs));
                    
                    if (value != null && !string.IsNullOrEmpty(value.TrackUniqueHash))
                    {
                        LoadAnalysisAsync(value.TrackUniqueHash);
                        LoadAudioFeaturesAsync(value.TrackUniqueHash); // Phase 4
                        LoadForensicLogsAsync(value.TrackUniqueHash); // Phase 4.7
                    }
                }
            }
        }

        private async void LoadAnalysisAsync(string hash)
        {
            IsAnalyzing = true;
            try
            {
                _analysis = await _audioAnalysisService.GetAnalysisAsync(hash);
                NotifyAnalysisProperties();
            }
            catch (Exception) { /* Fail silently */ }
            finally
            {
                IsAnalyzing = false;
            }
        }
        
        // Phase 4: Load Musical Intelligence data from AudioFeaturesEntity
        private async void LoadAudioFeaturesAsync(string trackHash)
        {
            try
            {
                // Day 0 Adjustment #3: Use Task.Run to avoid UI thread blocking
                _audioFeatures = await System.Threading.Tasks.Task.Run(async () =>
                {
                    using var db = new Data.AppDbContext();
                    return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                        .FirstOrDefaultAsync(db.AudioFeatures, f => f.TrackUniqueHash == trackHash);
                });
                
                NotifyMusicalIntelligenceProperties();
            }
            catch (Exception) { /* Fail silently */ }
        }

        /// <summary>
        /// Handles TrackAnalysisCompletedEvent. Refreshes inspector if currently viewing the analyzed track.
        /// </summary>
        private void OnAnalysisCompleted(TrackAnalysisCompletedEvent evt)
        {
            // Only refresh if we're currently inspecting the analyzed track
            if (Track?.TrackUniqueHash != evt.TrackGlobalId)
                return;

            if (evt.Success)
            {
                // Reload analysis data from DB
                LoadAnalysisAsync(evt.TrackGlobalId);
                LoadAudioFeaturesAsync(evt.TrackGlobalId);

                // Refresh all analysis-related properties
                NotifyAnalysisProperties();
                NotifyMusicalIntelligenceProperties();

                // Force refresh of key derived properties
                OnPropertyChanged(nameof(BitrateLabel));
                OnPropertyChanged(nameof(AudioGuardColor));
                OnPropertyChanged(nameof(AudioGuardIcon));
                OnPropertyChanged(nameof(FrequencyCutoffLabel));
                OnPropertyChanged(nameof(ConfidenceLabel));
                OnPropertyChanged(nameof(IsTrustworthy));
                OnPropertyChanged(nameof(TrustColor));
                
                System.Diagnostics.Debug.WriteLine($"[Inspector] Analysis completed for {evt.TrackGlobalId}, UI refreshed");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Inspector] Analysis failed for {evt.TrackGlobalId}: {evt.ErrorMessage}");
            }
        }

        // Phase 4.7: Load Forensic Logs
        public ObservableCollection<ForensicLogEntry> ForensicLogs { get; } = new();

        private async void LoadForensicLogsAsync(string trackHash)
        {
            try
            {
                var logs = await Task.Run(async () =>
                {
                    using var db = new Data.AppDbContext();
                    return await db.ForensicLogs
                        .Where(l => l.CorrelationId == trackHash)
                        .OrderByDescending(l => l.Timestamp)
                        .ToListAsync();
                });

                ForensicLogs.Clear();
                foreach (var log in logs)
                {
                    ForensicLogs.Add(log);
                }
                 OnPropertyChanged(nameof(ForensicLogs));
            }
            catch (Exception) { /* Fail silently */ }
        }

        public void NotifyAnalysisProperties()
        {
            OnPropertyChanged(nameof(LoudnessLabel));
            OnPropertyChanged(nameof(TruePeakLabel));
            OnPropertyChanged(nameof(DynamicRangeLabel));
            OnPropertyChanged(nameof(TechDetailsLabel));
            
            // Integrity Scout
            OnPropertyChanged(nameof(IntegrityStatusText));
            OnPropertyChanged(nameof(IntegrityStatusColor));
            OnPropertyChanged(nameof(SpectralCutoffLabel));
            OnPropertyChanged(nameof(QualityConfidenceLabel));
        }
        
        // Phase 4: Musical Intelligence Properties Notification
        private void NotifyMusicalIntelligenceProperties()
        {
            OnPropertyChanged(nameof(EssentiaBpm));
            OnPropertyChanged(nameof(BpmConfidence));
            OnPropertyChanged(nameof(EssentiaCamelotKey));
            OnPropertyChanged(nameof(EssentiaEnergy));
            OnPropertyChanged(nameof(DropTime));
            OnPropertyChanged(nameof(CueIntro));
            OnPropertyChanged(nameof(CueBuild));
            OnPropertyChanged(nameof(CuePhraseStart));
            OnPropertyChanged(nameof(HasMusicalIntelligence));
            OnPropertyChanged(nameof(HasCuePoints));
        }

        public double Energy => Track?.Energy ?? 0;
        public double Danceability => Track?.Danceability ?? 0;
        public double Valence => Track?.Valence ?? 0;

        // Audio Analysis Properties
        public string LoudnessLabel => _analysis != null ? $"{_analysis.LoudnessLufs:F1} LUFS" : "--";
        public string TruePeakLabel => _analysis != null ? $"{_analysis.TruePeakDb:F1} dBTP" : "--";
        public string DynamicRangeLabel => _analysis != null ? $"{_analysis.DynamicRange:F1} LU" : "--";
        public string TechDetailsLabel => _analysis != null ? $"{_analysis.Codec.ToUpper()} | {_analysis.SampleRate}Hz | {_analysis.Channels}ch" : "Technical analysis pending...";

        // Integrity Scout Properties
        public string IntegrityStatusText 
        {
            get
            {
                if (_analysis == null) return "Unknown";
                return _analysis.IsUpscaled ? "UPSCALED / FAKE" : "VERIFIED CLEAN";
            }
        }

        public string IntegrityStatusColor
        {
            get
            {
                if (_analysis == null) return "#666666";
                return _analysis.IsUpscaled ? "#D32F2F" : "#1DB954"; // Red for fake, Green for clean
            }
        }

        public string SpectralCutoffLabel => _analysis != null ? $"{_analysis.FrequencyCutoff / 1000.0:F1} kHz" : "--";
        public string QualityConfidenceLabel => _analysis != null ? $"{_analysis.QualityConfidence:P0}" : "--";

        public bool HasTrack => Track != null;

        public string CamelotKey => MapToCamelot(Track?.Key);

        public string BitrateLabel => Track?.Bitrate > 0 ? $"{Track.Bitrate} kbps" : "Unknown Bitrate";

        public string AudioGuardColor => GetAudioGuardColor();
        public string AudioGuardIcon => GetAudioGuardIcon();

        public string FrequencyCutoffLabel => Track?.FrequencyCutoff > 0 ? $"{Track.FrequencyCutoff / 1000.0:F1} kHz" : "Analysing...";
        public string ConfidenceLabel => Track?.QualityConfidence >= 0 ? $"{Track.QualityConfidence:P0}" : "??%";
        public bool IsTrustworthy => Track?.IsTrustworthy ?? true;
        public string Details => Track?.QualityDetails ?? "Analysis pending or no data available.";
        public string TrustColor => IsTrustworthy ? "#1DB954" : "#D32F2F";
        
        // Phase 4: Musical Intelligence Properties (from Essentia via AudioFeaturesEntity)
        public float? EssentiaBpm => _audioFeatures?.Bpm > 0 ? _audioFeatures.Bpm : null;
        public string BpmLabel => EssentiaBpm.HasValue ? $"{EssentiaBpm.Value:F1} BPM" : "--";
        public float? BpmConfidence => _audioFeatures?.BpmConfidence;
        public string BpmConfidenceLabel => BpmConfidence.HasValue ? $"({BpmConfidence.Value:P0})" : "";
        
        public string EssentiaCamelotKey
        {
            get
            {
                if (_audioFeatures == null || string.IsNullOrEmpty(_audioFeatures.Key)) return "";
                
                // Use KeyConverter to ensure Camelot format (Day 0 Adjustment #2)
                var camelot = Utils.KeyConverter.ToCamelot(_audioFeatures.CamelotKey);
                if (!string.IsNullOrEmpty(camelot)) return camelot;
                
                // Fallback: convert from raw Essentia key
                return Utils.KeyConverter.ToCamelot($"{_audioFeatures.Key}{(_audioFeatures.Scale == "minor" ? "m" : "")}");
            }
        }
        
        public float? EssentiaEnergy => _audioFeatures?.Energy;
        public float? Danceability2 => _audioFeatures?.Danceability; // Essentia version
        
        // Cue Points
        public float? DropTime => _audioFeatures?.DropTimeSeconds;
        public string DropTimeLabel => DropTime.HasValue ? $"{DropTime.Value:F1}s" : "--";
        
        public float? CueIntro => _audioFeatures?.CueIntro;
        public string CueIntroLabel => CueIntro.HasValue ? $"Intro: {CueIntro.Value:F1}s" : "--";
        
        public float? CueBuild => _audioFeatures?.CueBuild;
        public string CueBuildLabel => CueBuild.HasValue ? $"Build: {CueBuild.Value:F1}s" : "--";
        
        public float? CuePhraseStart => _audioFeatures?.CuePhraseStart;
        public string CuePhraseStartLabel => CuePhraseStart.HasValue ? $"Phrase: {CuePhraseStart.Value:F1}s" : "--";
        
        // Computed
        public bool HasMusicalIntelligence => EssentiaBpm.HasValue || !string.IsNullOrEmpty(EssentiaCamelotKey);
        public bool HasCuePoints => DropTime.HasValue || CueIntro.HasValue;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string MapToCamelot(string? key)
        {
            if (string.IsNullOrEmpty(key)) return "??";

            // Basic mapping for common key formats (e.g., "C Major", "Am", "8A")
            return key.ToUpper() switch
            {
                "C" or "C MAJOR" or "8B" => "8B",
                "AM" or "A MINOR" or "8A" => "8A",
                "G" or "G MAJOR" or "9B" => "9B",
                "EM" or "E MINOR" or "9A" => "9A",
                "D" or "D MAJOR" or "10B" => "10B",
                "BM" or "B MINOR" or "10A" => "10A",
                "A" or "A MAJOR" or "11B" => "11B",
                "F#M" or "F# MINOR" or "11A" => "11A",
                "E" or "E MAJOR" or "12B" => "12B",
                "C#M" or "C# MINOR" or "12A" => "12A",
                "B" or "B MAJOR" or "1B" => "1B",
                "G#M" or "G# MINOR" or "1A" => "1A",
                "F#" or "F# MAJOR" or "Gb" or "2B" => "2B",
                "D#M" or "D# MINOR" or "EBM" or "2A" => "2A",
                "C#" or "C# MAJOR" or "Db" or "3B" => "3B",
                "A#M" or "A# MINOR" or "BBM" or "3A" => "3A",
                "G#" or "G# MAJOR" or "Ab" or "4B" => "4B",
                "FM" or "F MINOR" or "4A" => "4A",
                "D#" or "D# MAJOR" or "Eb" or "5B" => "5B",
                "CM" or "C MINOR" or "5A" => "5A",
                "A#" or "A# MAJOR" or "Bb" or "6B" => "6B",
                "GM" or "G MINOR" or "6A" => "6A",
                "F" or "F MAJOR" or "7B" => "7B",
                "DM" or "D MINOR" or "7A" => "7A",
                _ => key
            };
        }

        private string GetAudioGuardColor()
        {
            if (Track == null) return "#333333";
            if (Track.Bitrate >= 1000 || (Track.Format?.Equals("FLAC", StringComparison.OrdinalIgnoreCase) ?? false)) return "#00A3FF"; // Lossless
            if (Track.Bitrate >= 320) return "#1DB954"; // High Quality
            if (Track.Bitrate >= 192) return "#FFCC00"; // Mid Quality
            return "#D32F2F"; // Low Quality
        }

        private string GetAudioGuardIcon()
        {
            if (Track == null) return "❓";
            if (Track.Bitrate >= 1000 || (Track.Format?.Equals("FLAC", StringComparison.OrdinalIgnoreCase) ?? false)) return "💎";
            if (Track.Bitrate >= 320) return "✅";
            if (Track.Bitrate >= 192) return "⚠️";
            return "❌";
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return false;
        }

        public void Dispose()
        {
            _disposables?.Dispose();
        }
    }
}
