using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Operation "All-Seeing Eye": Forensic Lab ViewModel
/// Aggregates Phase 13 (AI) and Phase 14 (Forensic) data for a single track.
/// This is the "Single Source of Truth" for the Analysis Mission Control dashboard.
/// </summary>
public class ForensicLabViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILibraryService _libraryService;
    private bool _isDisposed;

    // ============================================
    // Track Identity
    // ============================================

    public string TrackUniqueHash { get; private set; } = string.Empty;

    private string _displayName = "No Track Loaded";
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    private string _filePath = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    // ============================================
    // Top Sector: Waveform & Transport
    // ============================================

    private WaveformAnalysisData? _waveformData;
    public WaveformAnalysisData? WaveformData
    {
        get => _waveformData;
        set => SetProperty(ref _waveformData, value);
    }

    private TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        set => SetProperty(ref _duration, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    // ============================================
    // Left Sector: Rhythmic & Tonal
    // ============================================

    private float[] _bpmHistogram = Array.Empty<float>();
    public float[] BpmHistogram
    {
        get => _bpmHistogram;
        set => SetProperty(ref _bpmHistogram, value);
    }

    private float _bpmValue;
    public float BpmValue
    {
        get => _bpmValue;
        set => SetProperty(ref _bpmValue, value);
    }

    private float _bpmConfidence;
    public float BpmConfidence
    {
        get => _bpmConfidence;
        set => SetProperty(ref _bpmConfidence, value);
    }

    private float _bpmStability = 1.0f;
    public float BpmStability
    {
        get => _bpmStability;
        set => SetProperty(ref _bpmStability, value);
    }

    public string BpmStabilityLabel => BpmStability switch
    {
        >= 0.9f => "✓ Stable",
        >= 0.7f => "⚠ Slight Drift",
        _ => "⚠ Unstable (Live/Vinyl?)"
    };

    private string _camelotKey = string.Empty;
    public string CamelotKey
    {
        get => _camelotKey;
        set => SetProperty(ref _camelotKey, value);
    }

    private float _keyConfidence;
    public float KeyConfidence
    {
        get => _keyConfidence;
        set => SetProperty(ref _keyConfidence, value);
    }

    private string _chordProgression = string.Empty;
    public string ChordProgression
    {
        get => _chordProgression;
        set => SetProperty(ref _chordProgression, value);
    }

    // ============================================
    // Center Sector: AI & Vibe (Phase 13)
    // ============================================

    private float _instrumentalProbability;
    public float InstrumentalProbability
    {
        get => _instrumentalProbability;
        set => SetProperty(ref _instrumentalProbability, value);
    }

    public string VocalPresenceLabel => InstrumentalProbability switch
    {
        < 0.2f => "🎤 Vocal Heavy",
        < 0.5f => "🎤 Mixed Vocals",
        < 0.8f => "🎵 Light Vocals",
        _ => "🎵 Instrumental"
    };

    private string _moodTag = "Neutral";
    public string MoodTag
    {
        get => _moodTag;
        set => SetProperty(ref _moodTag, value);
    }

    private float _moodConfidence;
    public float MoodConfidence
    {
        get => _moodConfidence;
        set => SetProperty(ref _moodConfidence, value);
    }

    private float _danceability;
    public float Danceability
    {
        get => _danceability;
        set => SetProperty(ref _danceability, value);
    }

    private float _energy;
    public float Energy
    {
        get => _energy;
        set => SetProperty(ref _energy, value);
    }

    // Future: Style embeddings for subgenre radar
    private Dictionary<string, float> _subgenreRadar = new();
    public Dictionary<string, float> SubgenreRadar
    {
        get => _subgenreRadar;
        set => SetProperty(ref _subgenreRadar, value);
    }

    // ============================================
    // Right Sector: Signal Integrity (Phase 14)
    // ============================================

    private Bitmap? _spectrogramImage;
    public Bitmap? SpectrogramImage
    {
        get => _spectrogramImage;
        set => SetProperty(ref _spectrogramImage, value);
    }

    private int _trueBitrate;
    public int TrueBitrate
    {
        get => _trueBitrate;
        set => SetProperty(ref _trueBitrate, value);
    }

    private int _claimedBitrate;
    public int ClaimedBitrate
    {
        get => _claimedBitrate;
        set => SetProperty(ref _claimedBitrate, value);
    }

    private bool _isFake;
    public bool IsFake
    {
        get => _isFake;
        set => SetProperty(ref _isFake, value);
    }

    public string BitrateVerificationLabel => IsFake 
        ? $"⚠ FAKE ({ClaimedBitrate}kbps claimed, {TrueBitrate}kbps actual)"
        : $"✓ Verified {TrueBitrate}kbps";

    private float _dynamicRange;
    public float DynamicRange
    {
        get => _dynamicRange;
        set => SetProperty(ref _dynamicRange, value);
    }

    private float _loudnessLUFS;
    public float LoudnessLUFS
    {
        get => _loudnessLUFS;
        set => SetProperty(ref _loudnessLUFS, value);
    }

    private bool _isDynamicCompressed;
    public bool IsDynamicCompressed
    {
        get => _isDynamicCompressed;
        set => SetProperty(ref _isDynamicCompressed, value);
    }

    public string DynamicRangeLabel => IsDynamicCompressed
        ? $"⚠ Over-Compressed (DR: {DynamicRange:F1}, {LoudnessLUFS:F1} LUFS)"
        : $"✓ Clean (DR: {DynamicRange:F1})";

    private int _frequencyCutoff;
    public int FrequencyCutoff
    {
        get => _frequencyCutoff;
        set => SetProperty(ref _frequencyCutoff, value);
    }

    // ============================================
    // Bottom Sector: Raw Data
    // ============================================

    private string _essentiaJsonOutput = "No analysis data available";
    public string EssentiaJsonOutput
    {
        get => _essentiaJsonOutput;
        set => SetProperty(ref _essentiaJsonOutput, value);
    }

    // ============================================
    // Loading State
    // ============================================

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _loadingStatus = string.Empty;
    public string LoadingStatus
    {
        get => _loadingStatus;
        set => SetProperty(ref _loadingStatus, value);
    }

    // ============================================
    // Constructor
    // ============================================

    public ForensicLabViewModel(IEventBus eventBus, ILibraryService libraryService)
    {
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
    }

    // ============================================
    // Data Loading
    // ============================================

    /// <summary>
    /// Loads all forensic data for a track from the database.
    /// Aggregates: LibraryEntry, AudioFeaturesEntity, AudioAnalysisEntity
    /// </summary>
    public async Task LoadTrackAsync(string trackHash)
    {
        if (string.IsNullOrEmpty(trackHash))
            throw new ArgumentException("Track hash cannot be null or empty", nameof(trackHash));

        IsLoading = true;
        TrackUniqueHash = trackHash;

        try
        {
            LoadingStatus = "Loading track metadata...";

            // Load from database
            using var db = new AppDbContext();

            // 1. LibraryEntry (basic metadata + file path)
            var libraryEntry = await db.LibraryEntries
                .FirstOrDefaultAsync(e => e.UniqueHash == trackHash);

            if (libraryEntry == null)
            {
                DisplayName = "Track not found in library";
                return;
            }

            DisplayName = $"{libraryEntry.Artist} - {libraryEntry.Title}";
            FilePath = libraryEntry.FilePath;
            ClaimedBitrate = libraryEntry.Bitrate;


            LoadingStatus = "Loading Phase 13 AI data...";

            // Use libraryEntry for waveform data (Phase 0 stored)
            if (libraryEntry.WaveformData != null && libraryEntry.RmsData != null)
            {
                WaveformData = new WaveformAnalysisData
                {
                    PeakData = libraryEntry.WaveformData,
                    RmsData = libraryEntry.RmsData,
                    DurationSeconds = libraryEntry.DurationSeconds ?? 0
                };
                Duration = TimeSpan.FromSeconds(libraryEntry.DurationSeconds ?? 0);
            }

            // 2. AudioFeaturesEntity (Phase 13: Essentia/AI data)
            var audioFeatures = await db.AudioFeatures
                .FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash);

            if (audioFeatures != null)
            {
                BpmValue = audioFeatures.Bpm;
                BpmConfidence = audioFeatures.BpmConfidence;
                BpmStability = audioFeatures.BpmStability;
                CamelotKey = audioFeatures.CamelotKey;
                KeyConfidence = audioFeatures.KeyConfidence;
                ChordProgression = audioFeatures.ChordProgression;
                
                InstrumentalProbability = audioFeatures.InstrumentalProbability;
                MoodTag = audioFeatures.MoodTag;
                MoodConfidence = audioFeatures.MoodConfidence;
                Danceability = audioFeatures.Danceability;
                Energy = audioFeatures.Energy;

                IsDynamicCompressed = audioFeatures.IsDynamicCompressed;
                LoudnessLUFS = audioFeatures.LoudnessLUFS;
                DynamicRange = audioFeatures.DynamicComplexity;

                // Store raw JSON output (if available)
                // TODO: Store Essentia JSON in database for debugging
                EssentiaJsonOutput = "// Raw Essentia JSON not yet stored in DB\n" +
                                   $"// BPM: {BpmValue:F1}, Key: {CamelotKey}, Mood: {MoodTag}";
            }

            LoadingStatus = "Loading Phase 14 forensic data...";

            // 3. AudioAnalysisEntity (Phase 14: Waveform, Spectrogram, Bitrate verification)
            var audioAnalysis = await db.AudioAnalysis
                .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash);

            if (audioAnalysis != null)
            {
                TrueBitrate = audioAnalysis.Bitrate;
                FrequencyCutoff = audioAnalysis.FrequencyCutoff;
                
                // Check if fake using MetadataForensicService
                var track = new Track 
                { 
                    Bitrate = ClaimedBitrate, 
                    Size = libraryEntry.FilePath != null && System.IO.File.Exists(libraryEntry.FilePath)
                        ? new System.IO.FileInfo(libraryEntry.FilePath).Length
                        : 0
                };
                IsFake = MetadataForensicService.IsFake(track);

                // Waveform data is now loaded from LibraryEntry
                if (WaveformData == null) 
                {
                    // Fallback to approximate duration from audio analysis if not set
                    if (audioAnalysis.DurationMs > 0 && Duration == TimeSpan.Zero)
                    {
                        Duration = TimeSpan.FromMilliseconds(audioAnalysis.DurationMs);
                    }
                }
            }

            LoadingStatus = "Loading spectrogram...";

            // 4. Lazy-load spectrogram (optional, heavy resource)
            await LoadSpectrogramAsync();

            LoadingStatus = "Complete";
        }
        catch (Exception ex)
        {
            LoadingStatus = $"Error: {ex.Message}";
            DisplayName = "Failed to load track data";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Lazy-loads the spectrogram image from cache.
    /// Path: %AppData%/ORBIT/Spectrograms/{hash}.png
    /// </summary>
    public async Task LoadSpectrogramAsync()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var spectrogramPath = System.IO.Path.Combine(appData, "ORBIT", "Spectrograms", $"{TrackUniqueHash}.png");

            if (System.IO.File.Exists(spectrogramPath))
            {
                // Load on background thread to avoid UI freeze
                await Task.Run(() =>
                {
                    SpectrogramImage = new Bitmap(spectrogramPath);
                });
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: spectrogram is optional
            System.Diagnostics.Debug.WriteLine($"Failed to load spectrogram: {ex.Message}");
        }
    }

    // ============================================
    // INotifyPropertyChanged
    // ============================================

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    // ============================================
    // Disposal
    // ============================================

    public void Dispose()
    {
        if (_isDisposed) return;

        // Dispose heavy resources (spectrogram bitmap)
        SpectrogramImage?.Dispose();
        SpectrogramImage = null;

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
