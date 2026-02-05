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
using SLSKDONET.Data.Essentia;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels.Library;

using Avalonia.Media.Imaging;
using System.IO;
using System.Text.Json;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels.Surgical;

namespace SLSKDONET.ViewModels
{
    public class TrackInspectorViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Services.IAudioAnalysisService _audioAnalysisService;
        private readonly Services.AnalysisQueueService _analysisQueue;
        private readonly Services.IEventBus _eventBus;
        private readonly Services.DownloadDiscoveryService _discoveryService;
        private readonly Services.SonicIntegrityService _sonicIntegrityService;
        private readonly Services.HarmonicMatchService _harmonicMatchService;
        private readonly Services.ILibraryService _libraryService;
        private readonly Services.Tagging.IUniversalCueService _cueService;
        private readonly Services.TrackForensicLogger _forensicLogger;
        private readonly Services.AI.ISonicMatchService _sonicMatchService;
        private readonly Services.IMusicBrainzService _musicBrainzService;
        private readonly TrackOperationsViewModel _trackOperations; // Phase 11.6
        private readonly ILogger<TrackInspectorViewModel> _logger;
        private readonly CompositeDisposable _disposables = new();

        public void Dispose()
        {
            _forensicLogger.LogGenerated -= OnForensicLogGenerated;
            _disposables.Dispose();
        }

        private void OnForensicLogGenerated(object? sender, ForensicLogEntry entry)
        {
            if (Track == null || (entry.TrackIdentifier != Track.TrackUniqueHash && entry.CorrelationId != Track.TrackUniqueHash))
                return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ForensicLogs.Insert(0, entry);
            });
        }

        private Data.Entities.AudioAnalysisEntity? _analysis;
        public AudioFeaturesEntity? AudioFeatures => _audioFeatures;
        private Data.Entities.AudioFeaturesEntity? _audioFeatures; // Phase 4: Musical Intelligence
        
        private void OnTrackChanged()
        {
             // Trigger async Pro DJ load
             _ = LoadProDjFeaturesAsync();
             // Phase 21: Load analysis history
             _ = LoadAnalysisHistoryAsync();
        } // Phase 4: Musical Intelligence
        
        private bool _isAnalyzing;
        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set => SetProperty(ref _isAnalyzing, value);
        }

        private AnalysisProgressViewModel? _progressModal;
        public AnalysisProgressViewModel? ProgressModal
        {
            get => _progressModal;
            set 
            {
                if (SetProperty(ref _progressModal, value))
                {
                    OnPropertyChanged(nameof(IsProgressModalVisible));
                }
            }
        }

        public bool IsProgressModalVisible => ProgressModal != null;

        private Bitmap? _spectrogramBitmap;
        public Bitmap? SpectrogramBitmap
        {
            get => _spectrogramBitmap;
            set => SetProperty(ref _spectrogramBitmap, value);
        }

        private bool _isGeneratingSpectrogram;
        public bool IsGeneratingSpectrogram
        {
            get => _isGeneratingSpectrogram;
            set => SetProperty(ref _isGeneratingSpectrogram, value);
        }



        // Pro DJ Features
        public ObservableCollection<Services.HarmonicMatchResult> MixesWellMatches { get; } = new();
        public ObservableCollection<Services.AI.SonicMatch> SonicMatches { get; } = new(); // Phase 25
        public ObservableCollection<PlaylistTrack> OtherVersions { get; } = new();
        public ObservableCollection<OrbitCue> Cues { get; } = new(); // Phase 10
        
        // Phase 21: Analysis Run Tracking
        public ObservableCollection<Data.Entities.AnalysisRunEntity> AnalysisHistory { get; } = new();

        // Phase 1: Structural Intelligence
        public ObservableCollection<PhraseSegment> StructuralPhraseSegments { get; } = new();
        public ObservableCollection<float> StructuralEnergyCurve { get; } = new();
        public ObservableCollection<float> StructuralVocalDensityCurve { get; } = new();
        public ObservableCollection<string> StructuralAnomalies { get; } = new();
        
        // Phase 5.1: Vocal Intelligence UI
        private Services.Musical.VocalPocketRenderModel? _vocalPockets;
        public Services.Musical.VocalPocketRenderModel? VocalPockets
        {
            get => _vocalPockets;
            private set => SetProperty(ref _vocalPockets, value);
        }
        
        // Phase 5.2: Forensic Inspector Panel
        private Services.Musical.VocalPocketSegment? _selectedVocalZone;
        public Services.Musical.VocalPocketSegment? SelectedVocalZone
        {
            get => _selectedVocalZone;
            set
            {
                if (SetProperty(ref _selectedVocalZone, value))
                {
                    OnPropertyChanged(nameof(HasSelectedZone));
                    OnPropertyChanged(nameof(SelectedZoneLabel));
                    OnPropertyChanged(nameof(SelectedZoneTimeRange));
                    OnPropertyChanged(nameof(SelectedZoneAdvice));
                }
            }
        }
        
        public bool HasSelectedZone => _selectedVocalZone != null;
        
        public string SelectedZoneLabel => _selectedVocalZone?.ZoneType switch
        {
            Services.Musical.VocalZoneType.Instrumental => "✅ SAFE ZONE",
            Services.Musical.VocalZoneType.Sparse => "⚠️ SPARSE VOCALS",
            Services.Musical.VocalZoneType.Hook => "⚠️ HOOK / CHORUS",
            Services.Musical.VocalZoneType.Dense => "❌ DANGER - DENSE VOCALS",
            _ => "No zone selected"
        };
        
        public string SelectedZoneTimeRange => _selectedVocalZone != null
            ? $"{_selectedVocalZone.StartSeconds:F1}s → {_selectedVocalZone.EndSeconds:F1}s"
            : "";
        
        public string SelectedZoneAdvice => _selectedVocalZone?.ZoneType switch
        {
            Services.Musical.VocalZoneType.Instrumental => 
                "SAFE HARBOR: Perfect for long melodic blends or bringing in complex vocals from Track B. Recommended exit/entry point.",
            Services.Musical.VocalZoneType.Sparse =>
                "LIGHT VOCALS: Ad-libs or vocal chops detected. Use a High-Pass Filter (HPF) on Track B to avoid low-end mud. Safe for quick cuts.",
            Services.Musical.VocalZoneType.Hook =>
                "CHORUS DETECTED: Strong melodic focus. Avoid layering—suggest a 'Power Cut' at the next phrase change or wait for instrumental pocket.",
            Services.Musical.VocalZoneType.Dense =>
                "VOCAL CLASH WARNING: Full lyrics active. DO NOT layer vocals. Recommendation: 'Drop-Swap' or immediate transition to clear frequency.",
            _ => ""
        };
        
        public ICommand SelectZoneCommand { get; }
        
        private void OnZoneSelected(object? parameter)
        {
            if (parameter is Services.Musical.VocalPocketSegment segment)
            {
                SelectedVocalZone = segment;
            }
        }
        
        public ObservableCollection<Models.ForensicVerdictEntry> ForensicVerdicts { get; } = new();
        
        private Dictionary<string, string>? _forensicReasoning;
        public Dictionary<string, string>? ForensicReasoning
        {
            get => _forensicReasoning;
            set => SetProperty(ref _forensicReasoning, value);
        }
        
        // Vibe Radar Data (0-100 scale for UI)
        public double VibeEnergy => (AudioFeatures != null && AudioFeatures.Energy > 0 
            ? AudioFeatures.Energy 
            : (float)(Track?.Energy ?? 0.0)) * 100.0;

        public double VibeDance => (AudioFeatures != null && AudioFeatures.Danceability > 0 
            ? AudioFeatures.Danceability 
            : (float)(Track?.Danceability ?? 0.0)) * 100.0;

        public double VibeMood => (AudioFeatures != null && AudioFeatures.MoodConfidence > 0 
            ? AudioFeatures.MoodConfidence 
            : (float)(Track?.Valence ?? 0.0)) * 100.0;
        
        public bool HasProDjFeatures => HasAnalysis && !string.IsNullOrEmpty(CamelotKey);
        public System.Windows.Input.ICommand ForceReAnalyzeCommand { get; }
        public System.Windows.Input.ICommand ForceReAnalyzeTieredCommand { get; }
        public System.Windows.Input.ICommand ExportLogsCommand { get; }
        public System.Windows.Input.ICommand ReFetchUpgradeCommand { get; }
        public System.Windows.Input.ICommand GenerateSpectrogramCommand { get; }
        public System.Windows.Input.ICommand OpenInLabCommand { get; }
        public System.Windows.Input.ICommand SaveCuesCommand { get; }
        public System.Windows.Input.ICommand AddCueCommand { get; }
        public System.Windows.Input.ICommand DeleteCueCommand { get; }
        public System.Windows.Input.ICommand MarkAsVerifiedCommand { get; } // Phase 11.5
        public System.Windows.Input.ICommand CloneCommand { get; } // Phase 11.6
        public System.Windows.Input.ICommand RevealFileCommand { get; }
        public System.Windows.Input.ICommand OpenStemWorkspaceCommand { get; } // Phase 24
        public System.Windows.Input.ICommand ToggleEditModeCommand { get; } // Phase 2
        public System.Windows.Input.ICommand SegmentUpdatedCommand { get; } // Phase 2
        public System.Windows.Input.ICommand SurgicalRenderCommand { get; } // Phase 2

        private MusicBrainzCredits? _mbCredits;
        public MusicBrainzCredits? MbCredits
        {
            get => _mbCredits;
            set => SetProperty(ref _mbCredits, value);
        }

        private bool _isEditing;
        public bool IsEditing
        {
            get => _isEditing;
            set => SetProperty(ref _isEditing, value);
        }

        private SnappingMode _snappingMode = SnappingMode.Soft;
        public SnappingMode SnappingMode
        {
            get => _snappingMode;
            set => SetProperty(ref _snappingMode, value);
        }

        public float Bpm => AudioFeatures?.Bpm ?? (float)(Track?.BPM ?? 0.0);

        public TrackInspectorViewModel(
            Services.IAudioAnalysisService audioAnalysisService, 
            Services.AnalysisQueueService analysisQueue,
            Services.IEventBus eventBus,
            Services.DownloadDiscoveryService discoveryService,
            Services.SonicIntegrityService sonicIntegrityService,
            Services.HarmonicMatchService harmonicMatchService,
            Services.ILibraryService libraryService,
            Services.Tagging.IUniversalCueService cueService,
            Services.TrackForensicLogger forensicLogger,
            Services.AI.ISonicMatchService sonicMatchService,
            Services.IMusicBrainzService musicBrainzService,
            TrackOperationsViewModel trackOperations,
            ILogger<TrackInspectorViewModel> logger)
        {
            _audioAnalysisService = audioAnalysisService;
            _analysisQueue = analysisQueue;
            _eventBus = eventBus;
            _discoveryService = discoveryService;
            _sonicIntegrityService = sonicIntegrityService;
            _harmonicMatchService = harmonicMatchService;
            _libraryService = libraryService;
            _cueService = cueService;
            _forensicLogger = forensicLogger;
            _sonicMatchService = sonicMatchService;
            _musicBrainzService = musicBrainzService;
            _logger = logger;
            _trackOperations = trackOperations;

            SelectZoneCommand = ReactiveCommand.Create<object?>(OnZoneSelected);

            RevealFileCommand = ReactiveCommand.Create(() =>
            {
                if (Track != null && !string.IsNullOrEmpty(Track.ResolvedFilePath))
                {
                    _eventBus.Publish(new RevealFileRequestEvent(Track.ResolvedFilePath));
                }
            });

            ToggleEditModeCommand = ReactiveCommand.Create(() => IsEditing = !IsEditing);

            SegmentUpdatedCommand = ReactiveCommand.Create<PhraseSegment>(seg =>
            {
                _logger.LogInformation("📏 Surgical Segment Updated: {Label} now {Start}-{End}", seg.Label, seg.Start, seg.Start + seg.Duration);
                // TODO: Logic to persist these changes back to the database or internal model
                _ = SaveStructuralDataAsync();
            });

            SurgicalRenderCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null) return;
                _logger.LogInformation("🚀 Initiating Surgical Render for {Path}", Track.ResolvedFilePath);
                // TODO: Call SurgicalProcessingService
                await Task.Delay(1000); 
            });

            OpenStemWorkspaceCommand = ReactiveCommand.Create(() =>
            {
                if (Track != null)
                {
                    _eventBus.Publish(new OpenStemWorkspaceRequestEvent(Track.TrackUniqueHash));
                }
            });
            
            // Subscribe to live forensic logs
            _forensicLogger.LogGenerated += OnForensicLogGenerated;
            
            // Phase 12.6: Listen for global track selection
            _eventBus.GetEvent<TrackSelectionChangedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt => Track = evt.Track)
                .DisposeWith(_disposables);

            // Phase 1: Listen for analysis started - show progress modal
            _eventBus.GetEvent<TrackAnalysisStartedEvent>()
                .Where(evt => Track?.TrackUniqueHash == evt.TrackGlobalId)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt =>
                {
                    IsAnalyzing = true;
                    ProgressModal = new AnalysisProgressViewModel(evt.TrackGlobalId, _eventBus);
                })
                .DisposeWith(_disposables);

            // Phase B: Listen for audio analysis completion - hide progress modal
            _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(evt =>
                {
                    OnAnalysisCompleted(evt);
                    
                    // Hide progress modal
                    if (ProgressModal?.TrackId == evt.TrackGlobalId)
                    {
                        ProgressModal?.Dispose();
                        ProgressModal = null;
                        IsAnalyzing = false;
                        OnPropertyChanged(nameof(IsProgressModalVisible));
                    }
                })
                .DisposeWith(_disposables);
            
            // Phase 4: Listen for metadata updates (Enrichment)
            _eventBus.GetEvent<TrackMetadataUpdatedEvent>()
                .Where(evt => Track?.TrackUniqueHash == evt.TrackGlobalId)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(async evt =>
                {
                    if (Track != null)
                    {
                        // Reload track data to ensure we have the latest enrichment
                        var freshTrack = await _libraryService.GetPlaylistTrackByHashAsync(Track.PlaylistId, evt.TrackGlobalId);
                        if (freshTrack != null)
                        {
                            _logger.LogDebug("[Inspector] Metadata updated for current track {Hash}, refreshing UI", evt.TrackGlobalId);
                            Track = freshTrack; // Re-assignment triggers property notifications and analysis re-load
                        }
                    }
                })
                .DisposeWith(_disposables);
            
            // Phase 11.5: Verification Command
            MarkAsVerifiedCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null || string.IsNullOrEmpty(Track.TrackUniqueHash)) return;
                try 
                {
                    await _libraryService.MarkTrackAsVerifiedAsync(Track.TrackUniqueHash);
                    await LoadProDjFeaturesAsync(); // Refresh UI
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to verify track");
                }
            });

            CloneCommand = ReactiveCommand.CreateFromTask(async () => 
            {
                if (Track == null) return;
                
                // Wrap in a fake ViewModel expected by TrackOperations
                var trackVm = new PlaylistTrackViewModel(Track, _eventBus);
                _trackOperations.CloneTrackCommand.Execute(trackVm);
            });

            // Interactive Commands
            ForceReAnalyzeCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null || string.IsNullOrEmpty(Track.TrackUniqueHash)) return;
                
                try
                {
                    // 1. Delete existing analysis from database
                    using var db = new Data.AppDbContext();
                    var existing = await db.AudioAnalysis
                        .FirstOrDefaultAsync(a => a.TrackUniqueHash == Track.TrackUniqueHash);
                    if (existing != null)
                    {
                        db.AudioAnalysis.Remove(existing);
                        await db.SaveChangesAsync();
                    }
                    // 2. Clear current analysis in UI
                    _analysis = null;
                    NotifyAnalysisProperties();
                    
                    // 3. Re-queue for analysis
                    if (!string.IsNullOrEmpty(Track.ResolvedFilePath))
                    {
                        IsAnalyzing = true;
                        ProgressModal = new AnalysisProgressViewModel(Track.TrackUniqueHash, _eventBus);
                        OnPropertyChanged(nameof(IsProgressModalVisible));
                        
                        _analysisQueue.QueueAnalysis(Track.ResolvedFilePath, Track.TrackUniqueHash, AnalysisTier.Tier1);
                    }
                    else
                    {
                        // Fallback if no file path
                        LoadAnalysisAsync(Track.TrackUniqueHash);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Re-analyze failed");
                }
            });

            ForceReAnalyzeTieredCommand = ReactiveCommand.CreateFromTask<AnalysisTier>(async (tier) =>
            {
                if (Track == null || string.IsNullOrEmpty(Track.TrackUniqueHash)) return;
                
                try
                {
                    if (!string.IsNullOrEmpty(Track.ResolvedFilePath))
                    {
                        IsAnalyzing = true;
                        ProgressModal = new AnalysisProgressViewModel(Track.TrackUniqueHash, _eventBus);
                        OnPropertyChanged(nameof(IsProgressModalVisible));
                        
                        _analysisQueue.QueueAnalysis(Track.ResolvedFilePath, Track.TrackUniqueHash, tier);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Tiered re-analyze failed");
                }
            });
            
            ExportLogsCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (ForensicLogs.Count == 0) return;
                
                try
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var fileName = $"ForensicLogs_{Track?.TrackUniqueHash?.Substring(0, 8)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                    var filePath = System.IO.Path.Combine(desktop, fileName);
                    
                    var lines = ForensicLogs.Select(log => 
                        $"[{log.Timestamp:HH:mm:ss}] [{log.Stage}] {log.Message}"
                    );
                    
                    await System.IO.File.WriteAllLinesAsync(filePath, lines);
                    System.Diagnostics.Debug.WriteLine($"Logs exported to: {filePath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
                }
            });

            ReFetchUpgradeCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null) return;
                
                try
                {
                    _eventBus.Publish(new SLSKDONET.Models.AnalysisProgressEvent(
                        TrackGlobalId: Track.TrackUniqueHash, 
                        CurrentStep: "Initializing re-fetch search...", 
                        ProgressPercent: 0));
                    
                    // Trigger discovery and queueing
                    await _discoveryService.DiscoverAndQueueTrackAsync(Track);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Re-fetch upgrade failed");
                    _eventBus.Publish(new SLSKDONET.Models.AnalysisProgressEvent(
                        TrackGlobalId: Track.TrackUniqueHash, 
                        CurrentStep: $"Re-fetch failed: {ex.Message}", 
                        ProgressPercent: 0));
                }
            });

            GenerateSpectrogramCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null || string.IsNullOrEmpty(Track.ResolvedFilePath)) return;
                
                try 
                {
                    IsGeneratingSpectrogram = true;
                    var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "Spectrograms");
                    Directory.CreateDirectory(cacheDir);
                    
                    var outputPath = Path.Combine(cacheDir, $"{Track.TrackUniqueHash}.png");
                    
                    // Tip 1: Generate if missing
                    if (!File.Exists(outputPath))
                    {
                        var success = await _sonicIntegrityService.GenerateSpectrogramAsync(Track.ResolvedFilePath, outputPath);
                        if (!success) throw new Exception("FFmpeg generation failed");
                    }
                    
                    if (File.Exists(outputPath))
                    {
                         // UI Tip: Load static image
                         // Use Task.Run to avoid UI freeze during decoding
                         var bitmap = await Task.Run(() => new Bitmap(outputPath));
                         SpectrogramBitmap = bitmap;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate spectrogram");
                }
                finally
                {
                    IsGeneratingSpectrogram = false;
                }
            });

            OpenInLabCommand = ReactiveCommand.Create(() =>
            {
                if (Track != null && !string.IsNullOrEmpty(Track.TrackUniqueHash))
                {
                    _eventBus.Publish(new RequestForensicAnalysisEvent(Track.TrackUniqueHash));
                }
            });

            SaveCuesCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (Track == null) return;
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(Cues.OrderBy(c => c.Timestamp));
                    Track.CuePointsJson = json;
                    
                    // Simple persist via LibraryService if available or direct DB
                    using var db = new Data.AppDbContext();
                    var trackEntity = await db.PlaylistTracks.Include(t => t.TechnicalDetails).FirstOrDefaultAsync(t => t.TrackUniqueHash == Track.TrackUniqueHash);
                    var tech = trackEntity?.TechnicalDetails;
                    if (tech != null)
                    {
                        tech.CuePointsJson = json;
                        tech.IsPrepared = true;
                        await db.SaveChangesAsync();
                    }
                    
                    Track.IsPrepared = true;
                    OnPropertyChanged(nameof(Track)); // Trigger UI refresh
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save cues");
                }
            });

            AddCueCommand = ReactiveCommand.Create<double>(timestamp =>
            {
                Cues.Add(new OrbitCue 
                { 
                    Timestamp = timestamp, 
                    Name = $"Cue {Cues.Count + 1}", 
                    Color = "#00BFFF", 
                    Source = CueSource.User,
                    Role = CueRole.Custom
                });
            });

            DeleteCueCommand = ReactiveCommand.Create<OrbitCue>(cue => Cues.Remove(cue));
        }

        private async Task LoadProDjFeaturesAsync()
        {
            if (Track == null || string.IsNullOrEmpty(Track.TrackUniqueHash)) return;

            MixesWellMatches.Clear();
            OtherVersions.Clear();

            try
            {
                // 1. Mixes Well With (Harmonic Matches)
                // We need the LibraryEntry ID. Since PlaylistTrack might not have it directly populated or might differ,
                // we'll try to find the LibraryEntry by Hash first.
                var libraryEntry = await _libraryService.FindLibraryEntryAsync(Track.TrackUniqueHash);
                if (libraryEntry != null)
                {
                    var matches = await _harmonicMatchService.FindMatchesAsync(libraryEntry.Id, limit: 5);
                    foreach (var match in matches)
                    {
                        MixesWellMatches.Add(match);
                    }
                }

                // 2. Other Versions (Same Title + Artist, different Hash)
                var allTracks = await _libraryService.GetAllPlaylistTracksAsync();
                var versions = allTracks
                    .Where(t => t.Artist.Equals(Track.Artist, StringComparison.OrdinalIgnoreCase) 
                             && t.Title.Equals(Track.Title, StringComparison.OrdinalIgnoreCase)
                             && t.TrackUniqueHash != Track.TrackUniqueHash)
                    .Take(5)
                    .ToList();

                foreach (var v in versions)
                {
                    OtherVersions.Add(v);
                }
                
                OnPropertyChanged(nameof(HasProDjFeatures));
                OnPropertyChanged(nameof(VibeEnergy));
                OnPropertyChanged(nameof(VibeDance));

                // 3. Sonic Matches (AI Similarity)
                SonicMatches.Clear();
                var sonicMatches = await _sonicMatchService.FindSonicMatchesAsync(Track.TrackUniqueHash, limit: 10);
                foreach (var match in sonicMatches)
                {
                    SonicMatches.Add(match);
                }

                // 4. MusicBrainz Credits
                MbCredits = null;
                if (!string.IsNullOrEmpty(Track.MusicBrainzId))
                {
                    MbCredits = await _musicBrainzService.GetCreditsAsync(Track.MusicBrainzId);
                }
                else if (libraryEntry != null && !string.IsNullOrEmpty(libraryEntry.MusicBrainzId))
                {
                    MbCredits = await _musicBrainzService.GetCreditsAsync(libraryEntry.MusicBrainzId);
                }
                else if (!string.IsNullOrEmpty(Track.ISRC))
                {
                     // Try to resolve on the fly if we have ISRC but no MBID yet
                     var mbid = await _musicBrainzService.ResolveMbidFromIsrcAsync(Track.ISRC);
                     if (!string.IsNullOrEmpty(mbid))
                     {
                         MbCredits = await _musicBrainzService.GetCreditsAsync(mbid);
                     }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load Pro DJ features");
            }
        }
        
        // Phase 21: Analysis Run Tracking
        private async Task LoadAnalysisHistoryAsync()
        {
            if (Track == null) return;
            
            try
            {
                using var db = new Data.AppDbContext();
                var runs = await db.AnalysisRuns
                    .Where(r => r.TrackUniqueHash == Track.TrackUniqueHash)
                    .OrderByDescending(r => r.StartedAt)
                    .Take(20) // Limit to last 20 runs
                    .ToListAsync();
                    
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    AnalysisHistory.Clear();
                    foreach (var run in runs)
                    {
                        AnalysisHistory.Add(run);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load analysis history for track {Hash}", Track.TrackUniqueHash);
            }
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
                    _audioFeatures = null; // Phase 4
                    SpectrogramBitmap = null; // Reset spectrogram
                    ForensicLogs.Clear(); // Phase 4.7
                    NotifyAnalysisProperties();
                    NotifyMusicalIntelligenceProperties(); // Phase 4
                    OnPropertyChanged(nameof(ForensicLogs));
                    
                    if (value != null && !string.IsNullOrEmpty(value.TrackUniqueHash))
                    {
                        LoadAnalysisAsync(value.TrackUniqueHash);
                        LoadAudioFeaturesAsync(value.TrackUniqueHash); // Phase 4
                        LoadForensicLogsAsync(value.TrackUniqueHash); // Phase 4.7
                        LoadCues(value); // Phase 10
                        OnTrackChanged(); // Trigger Pro DJ features load
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
                    // Query by TrackIdentifier (which stores the Hash) OR CorrelationId
                    return await db.ForensicLogs
                        .Where(l => l.TrackIdentifier == trackHash || l.CorrelationId == trackHash)
                        .OrderByDescending(l => l.Timestamp)
                        .ToListAsync();
                });

                ForensicLogs.Clear();
                foreach (var log in logs)
                {
                    ForensicLogs.Add(log);
                }
                 OnPropertyChanged(nameof(ForensicLogs)); // Force UI refresh
                 System.Diagnostics.Debug.WriteLine($"[Inspector] Loaded {logs.Count} forensic logs for {trackHash}");
            }
            catch (Exception ex) 
            {
                 System.Diagnostics.Debug.WriteLine($"[Inspector] Failed to load forensic logs: {ex.Message}");
            }
        }

        public void NotifyAnalysisProperties()
        {
            OnPropertyChanged(nameof(LoudnessLabel));
            OnPropertyChanged(nameof(TruePeakLabel));
            OnPropertyChanged(nameof(DynamicRangeLabel));
            OnPropertyChanged(nameof(TechDetailsLabel));
            OnPropertyChanged(nameof(FileSizeLabel));
            
            // Integrity Scout
            OnPropertyChanged(nameof(IntegrityStatusText));
            OnPropertyChanged(nameof(IntegrityStatusColor));
            OnPropertyChanged(nameof(SpectralCutoffLabel));
            OnPropertyChanged(nameof(QualityConfidenceLabel));
            
            // Phase 2: Status properties
            OnPropertyChanged(nameof(HasAnalysis));
            OnPropertyChanged(nameof(AnalysisAge));
            
            // Re-notify Vibe Radar
            OnPropertyChanged(nameof(VibeEnergy));
            OnPropertyChanged(nameof(VibeDance));
            OnPropertyChanged(nameof(VibeMood));
            
            // Phase 4: Musical Intelligence Properties Notification
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
            
            // Re-notify main properties to pick up intelligence data if available
            OnPropertyChanged(nameof(CamelotKey));
            OnPropertyChanged(nameof(BpmLabel));
            
            // Phase 4: Notify Vibe Radar to refresh with forensic truths
            OnPropertyChanged(nameof(VibeEnergy));
            OnPropertyChanged(nameof(VibeDance));
            OnPropertyChanged(nameof(VibeEnergy));
            OnPropertyChanged(nameof(VibeDance));
            OnPropertyChanged(nameof(VibeMood));
            OnPropertyChanged(nameof(MoodTag));
            OnPropertyChanged(nameof(HasMood));

            // Phase 1: Structural Notifications
            UpdateStructuralData();
            OnPropertyChanged(nameof(StructuralPhraseSegments));
            OnPropertyChanged(nameof(StructuralEnergyCurve));
            OnPropertyChanged(nameof(StructuralVocalDensityCurve));
            OnPropertyChanged(nameof(ForensicReasoning));

            // Phase 10.5: Diff View Notifications
            OnPropertyChanged(nameof(CurationConfidence));
            OnPropertyChanged(nameof(AnalysisSource));
            OnPropertyChanged(nameof(HasBpmMismatch));
            OnPropertyChanged(nameof(HasKeyMismatch));
            OnPropertyChanged(nameof(DiffBpmLabel));
            OnPropertyChanged(nameof(DiffKeyLabel));
        }

        public double Energy => Track?.Energy ?? 0;
        public double Danceability => Track?.Danceability ?? 0;
        public double Valence => Track?.Valence ?? 0;
        
        public string MoodTag => AudioFeatures?.MoodTag ?? Track?.MoodTag ?? "Neutral";
        public bool HasMood => !string.IsNullOrEmpty(MoodTag) && MoodTag != "Neutral";

        // Audio Analysis Properties
        public string LoudnessLabel => _analysis != null ? $"{_analysis.LoudnessLufs:F1} LUFS" : "--";
        public string TruePeakLabel => _analysis != null ? $"{_analysis.TruePeakDb:F1} dBTP" : "--";
        public string DynamicRangeLabel => _analysis != null ? $"{_analysis.DynamicRange:F1} LU" : "--";
        public string TechDetailsLabel => _analysis != null ? $"{_analysis.Codec.ToUpper()} | {_analysis.SampleRate}Hz | {_analysis.Channels}ch" : "Technical analysis pending...";
        public string FileSizeLabel 
        {
            get
            {
                if (Track == null || string.IsNullOrEmpty(Track.ResolvedFilePath)) return "-- MB";
                try
                {
                    var fi = new System.IO.FileInfo(Track.ResolvedFilePath);
                    return $"{fi.Length / 1024.0 / 1024.0:F1} MB";
                }
                catch { return "-- MB"; }
            }
        }

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

        public string SpectralCutoffLabel 
        {
            get
            {
                if (_analysis == null || _analysis.FrequencyCutoff <= 0) return "--";
                var opacity = _analysis.FrequencyCutoff >= 20000 ? "(Native)" : "(Upscaled)";
                if (_analysis.FrequencyCutoff >= 22000) opacity = "(Hi-Res)";
                return $"{_analysis.FrequencyCutoff / 1000.0:F1} kHz {opacity}";
            }
        }
        
        public string QualityConfidenceLabel => _analysis != null ? $"{_analysis.QualityConfidence:P0}" : "--";

        // Phase 2: Analysis Status Properties
        public bool HasAnalysis => _analysis != null;
        
        public string AnalysisAge
        {
            get
            {
                if (_analysis?.AnalyzedAt == null) return "";
                
                var age = DateTime.UtcNow - _analysis.AnalyzedAt;
                if (age.TotalSeconds < 60) return "just now";
                if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0}m ago";
                if (age.TotalHours < 24) return $"{age.TotalHours:F0}h ago";
                return $"{age.TotalDays:F0}d ago";
            }
        }
        
        public string StatusBadgeText
        {
            get
            {
                if (IsAnalyzing) return "🔄 Analyzing...";
                if (HasAnalysis) return "✓ Analysis Complete";
                return "⚠️ No Analysis";
            }
        }

        public bool HasTrack => Track != null;

        public string CamelotKey 
        {
            get
            {
                var metaKey = MapToCamelot(Track?.Key);
                if ((string.IsNullOrEmpty(metaKey) || metaKey == "??") && !string.IsNullOrEmpty(EssentiaCamelotKey))
                {
                    return EssentiaCamelotKey;
                }
                return metaKey;
            }
        }

        public string BitrateLabel => Track?.Bitrate > 0 ? $"{Track.Bitrate} kbps" : "Unknown Bitrate";

        public string AudioGuardColor => GetAudioGuardColor();
        public string AudioGuardIcon => GetAudioGuardIcon();

        public string FrequencyCutoffLabel 
        {
            get
            {
                if (Track == null || Track.FrequencyCutoff <= 0) return "Analysing...";
                var suffix = Track.FrequencyCutoff >= 20000 ? "(Native)" : "(Upscaled)";
                return $"{Track.FrequencyCutoff / 1000.0:F1} kHz {suffix}";
            }
        }
        public string ConfidenceLabel => Track?.QualityConfidence >= 0 ? $"{Track.QualityConfidence:P0}" : "??%";
        public bool IsTrustworthy => Track?.IsTrustworthy ?? true;
        public string Details => Track?.QualityDetails ?? "Analysis pending or no data available.";
        public string TrustColor => IsTrustworthy ? "#1DB954" : "#D32F2F";
        
        // Phase 4: Musical Intelligence Properties (from Essentia via AudioFeaturesEntity)
        public float? EssentiaBpm => _audioFeatures?.Bpm > 0 ? _audioFeatures.Bpm : null;
        public string BpmLabel 
        {
            get
            {
                if ((Track?.BPM ?? 0) > 0) return $"{Track.BPM:F1} BPM";
                if (EssentiaBpm > 0) return $"{EssentiaBpm:F1} BPM (Est.)";
                return "--";
            }
        }
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
        
        public float CueIntro => _audioFeatures?.CueIntro ?? 0f;
        public string CueIntroLabel => CueIntro > 0 ? $"Intro: {CueIntro:F1}s" : "--";
        
        public float? CueBuild => _audioFeatures?.CueBuild;
        public string CueBuildLabel => CueBuild.HasValue ? $"Build: {CueBuild.Value:F1}s" : "--";
        
        public float? CuePhraseStart => _audioFeatures?.CuePhraseStart;
        public string CuePhraseStartLabel => CuePhraseStart.HasValue ? $"Phrase: {CuePhraseStart.Value:F1}s" : "--";
        
        // Phase 22.5: Musical Intelligence UI Properties
        public bool HasMusicalIntelligence => _audioFeatures != null;
        public bool HasCuePoints => _audioFeatures != null && (_audioFeatures.DropTimeSeconds.HasValue || _audioFeatures.CuePhraseStart.HasValue);
        

        
        public bool IsDjTool => _audioFeatures?.IsDjTool ?? false;
        
        public string ElectronicSubgenre => _audioFeatures?.ElectronicSubgenre ?? "";
        public bool HasSubgenre => !string.IsNullOrEmpty(ElectronicSubgenre);
        
        public string VoiceInstrumentalLabel
        {
            get
            {
                if (_audioFeatures == null) return "";
                // Logic based on InstrumentalProbability
                // > 0.8 = Instrumental, < 0.2 = Vocal, else Hybrid/Unknown
                if (_audioFeatures.InstrumentalProbability > 0.85f) return "Instrumental";
                if (_audioFeatures.InstrumentalProbability < 0.3f) return "Vocal";
                return ""; // Don't show anything for ambiguous cases
            }
        }
        public bool HasVoiceInstrumental => !string.IsNullOrEmpty(VoiceInstrumentalLabel);

        // Phase 10.5: Reliability & Transparency (Diff View)
        public Data.Entities.CurationConfidence CurationConfidence => _audioFeatures?.CurationConfidence ?? Data.Entities.CurationConfidence.None;
        public Data.Entities.DataSource AnalysisSource => _audioFeatures?.Source ?? Data.Entities.DataSource.Unknown;

        public bool HasBpmMismatch 
        {
            get
            {
                if (!EssentiaBpm.HasValue || (Track?.BPM ?? 0) <= 0) return false;
                return Math.Abs(EssentiaBpm.Value - (float)(Track?.BPM ?? 0)) > 1.0f; // Tolerance of 1 BPM
            }
        }

        public string DiffBpmLabel
        {
            get
            {
                if (!HasBpmMismatch) return "✓ Match";
                var diff = (EssentiaBpm ?? 0) - (Track?.BPM ?? 0);
                return $"{diff:+0.0;-0.0} BPM";
            }
        }

        public bool HasKeyMismatch
        {
            get
            {
                if (string.IsNullOrEmpty(EssentiaCamelotKey) || string.IsNullOrEmpty(CamelotKey)) return false;
                return !EssentiaCamelotKey.Equals(CamelotKey, StringComparison.OrdinalIgnoreCase);
            }
        }

        public string DiffKeyLabel => HasKeyMismatch ? $"Analysis: {EssentiaCamelotKey}" : "✓ Match";

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
            return true;
        }

        private void LoadCues(PlaylistTrack track)
        {
            Cues.Clear();
            if (string.IsNullOrEmpty(track.CuePointsJson)) return;
            
            try
            {
                var cues = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<OrbitCue>>(track.CuePointsJson);
                if (cues != null)
                {
                    foreach (var cue in cues) Cues.Add(cue);
                }
                OnPropertyChanged(nameof(Cues));
                OnPropertyChanged(nameof(HasCues));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize cues");
            }
        }

        public bool HasCues => Cues.Count > 0;

        private async Task SyncTagsAsync()
        {
            if (Track == null || string.IsNullOrEmpty(Track.ResolvedFilePath)) return;
            if (!HasCues) return;

            try
            {
                await _cueService.SyncToTagsAsync(Track.ResolvedFilePath, Cues.ToList());
                _logger.LogInformation("Synced cues to file tags for {Track}", Track.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync tags");
            }
        }


        private void UpdateStructuralData()
        {
            StructuralPhraseSegments.Clear();
            StructuralEnergyCurve.Clear();
            StructuralVocalDensityCurve.Clear();
            StructuralAnomalies.Clear();
            VocalPockets = null; // Phase 5.1: Clear vocal pockets
            ForensicVerdicts.Clear(); // Phase 5.2: Clear verdicts
            SelectedVocalZone = null;

            if (_audioFeatures == null) return;

            try
            {
                if (!string.IsNullOrEmpty(_audioFeatures.PhraseSegmentsJson))
                {
                    var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(_audioFeatures.PhraseSegmentsJson);
                    if (segments != null) foreach (var s in segments) StructuralPhraseSegments.Add(s);
                }

                if (!string.IsNullOrEmpty(_audioFeatures.EnergyCurveJson))
                {
                    var curve = JsonSerializer.Deserialize<List<float>>(_audioFeatures.EnergyCurveJson);
                    if (curve != null) foreach (var v in curve) StructuralEnergyCurve.Add(v);
                }

                if (!string.IsNullOrEmpty(_audioFeatures.VocalDensityCurveJson))
                {
                    var curve = JsonSerializer.Deserialize<List<float>>(_audioFeatures.VocalDensityCurveJson);
                    if (curve != null)
                    {
                        foreach (var v in curve) StructuralVocalDensityCurve.Add(v);
                        
                        // Phase 5.1: Generate VocalPockets render model
                        float duration = (Track?.CanonicalDuration ?? 0) / 1000f;
                        VocalPockets = Services.Musical.VocalPocketMapper.Map(curve, duration);
                    }
                }

                if (!string.IsNullOrEmpty(_audioFeatures.AnalysisReasoningJson))
                {
                    ForensicReasoning = JsonSerializer.Deserialize<Dictionary<string, string>>(_audioFeatures.AnalysisReasoningJson);
                    
                    // Phase 5.2: Parse structured verdicts
                    if (ForensicReasoning != null)
                    {
                        foreach (var kvp in ForensicReasoning)
                        {
                            ParseMentorReasoning(kvp.Value);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_audioFeatures.AnomaliesJson))
                {
                    var anomalies = JsonSerializer.Deserialize<List<string>>(_audioFeatures.AnomaliesJson);
                    if (anomalies != null) foreach (var a in anomalies) StructuralAnomalies.Add(a);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse structural data JSON: {Msg}", ex.Message);
            }
        }

        private void ParseMentorReasoning(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool inVerdict = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine)) continue;

                if (trimmedLine.StartsWith("▓"))
                {
                    var title = trimmedLine.TrimStart('▓', ' ').Trim();
                    if (title.Contains("VERDICT")) inVerdict = true;
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Section(title));
                }
                else if (trimmedLine.StartsWith("•"))
                {
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Bullet(trimmedLine.TrimStart('•', ' ').Trim()));
                }
                else if (trimmedLine.StartsWith("⚠"))
                {
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Warning(trimmedLine.TrimStart('⚠', ' ').Trim()));
                }
                else if (trimmedLine.StartsWith("✓"))
                {
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Success(trimmedLine.TrimStart('✓', ' ').Trim()));
                }
                else if (trimmedLine.StartsWith("→"))
                {
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Detail(trimmedLine.TrimStart('→', ' ').Trim()));
                }
                else if (trimmedLine.StartsWith("🎯"))
                {
                    ForensicVerdicts.Add(Models.ForensicVerdictEntry.Success(trimmedLine.TrimStart('🎯', ' ').Trim()));
                }
                else if (trimmedLine.StartsWith("═"))
                {
                    // Visual separator, skip or handle
                }
                else
                {
                    // Default to bullet or check if we are in verdict section
                    if (inVerdict)
                    {
                        ForensicVerdicts.Add(Models.ForensicVerdictEntry.Verdict(trimmedLine));
                    }
                    else
                    {
                        ForensicVerdicts.Add(Models.ForensicVerdictEntry.Bullet(trimmedLine));
                    }
                }
            }
        }

        public async Task SaveStructuralDataAsync()
        {
            if (_audioFeatures == null) return;

            try
            {
                _audioFeatures.PhraseSegmentsJson = JsonSerializer.Serialize(StructuralPhraseSegments.ToList());
                await _libraryService.UpdateAudioFeaturesAsync(_audioFeatures);
                _logger.LogInformation("💾 Structural data saved for {Track}", Track?.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save structural data");
            }
        }
    }
}
