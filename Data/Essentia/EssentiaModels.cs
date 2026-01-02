using System.Text.Json.Serialization;

namespace SLSKDONET.Data.Essentia;

// DTOs for mapping Essentia's JSON output
public class EssentiaOutput
{
    [JsonPropertyName("rhythm")]
    public RhythmData? Rhythm { get; set; }

    [JsonPropertyName("tonal")]
    public TonalData? Tonal { get; set; }

    [JsonPropertyName("lowlevel")]
    public LowLevelData? LowLevel { get; set; }

    [JsonPropertyName("highlevel")]
    public HighLevelData? HighLevel { get; set; } // Phase 13C: AI Layer
}

public class RhythmData
{
    [JsonPropertyName("bpm")]
    public float Bpm { get; set; }

    [JsonPropertyName("danceability")]
    public float Danceability { get; set; }

    [JsonPropertyName("onset_rate")]
    public float OnsetRate { get; set; }

    // Phase 13A: BPM Drift Detection
    [JsonPropertyName("bpm_histogram")]
    public float[]? BpmHistogram { get; set; }
}

public class TonalData
{
    [JsonPropertyName("key_edma")]
    public KeyData? KeyEdma { get; set; }
    
    [JsonPropertyName("key_krumhansl")]
    public KeyData? KeyKrumhansl { get; set; }

    // Phase 13C: Chord Extraction
    [JsonPropertyName("chords_key")]
    public string[]? ChordsKey { get; set; }
}

public class KeyData
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("scale")]
    public string Scale { get; set; } = string.Empty;

    [JsonPropertyName("strength")]
    public float Strength { get; set; }
}

public class LowLevelData
{
    [JsonPropertyName("average_loudness")]
    public float AverageLoudness { get; set; }

    [JsonPropertyName("dynamic_complexity")]
    public float DynamicComplexity { get; set; }

    [JsonPropertyName("spectral_centroid")]
    public StatsData? SpectralCentroid { get; set; }
    
    [JsonPropertyName("spectral_complexity")]
    public StatsData? SpectralComplexity { get; set; }
}

public class StatsData
{
    [JsonPropertyName("mean")]
    public float Mean { get; set; }
}

// Phase 13C: AI Layer - TensorFlow Model Outputs
public class HighLevelData
{
    [JsonPropertyName("voice_instrumental")]
    public ModelPrediction? VoiceInstrumental { get; set; }

    [JsonPropertyName("danceability")]
    public ModelPrediction? Danceability { get; set; }

    [JsonPropertyName("mood_happy")]
    public ModelPrediction? MoodHappy { get; set; }

    [JsonPropertyName("mood_aggressive")]
    public ModelPrediction? MoodAggressive { get; set; }
}

public class ModelPrediction
{
    [JsonPropertyName("all")]
    public ModelClasses? All { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("probability")]
    public float Probability { get; set; }
}

public class ModelClasses
{
    [JsonPropertyName("instrumental")]
    public float Instrumental { get; set; }

    [JsonPropertyName("voice")]
    public float Voice { get; set; }

    [JsonPropertyName("danceable")]
    public float Danceable { get; set; }

    [JsonPropertyName("not_danceable")]
    public float NotDanceable { get; set; }

    [JsonPropertyName("happy")]
    public float Happy { get; set; }

    [JsonPropertyName("not_happy")]
    public float NotHappy { get; set; }

    [JsonPropertyName("aggressive")]
    public float Aggressive { get; set; }

    [JsonPropertyName("not_aggressive")]
    public float NotAggressive { get; set; }
}
