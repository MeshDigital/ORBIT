using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AI;

public class StylePrediction
{
    public string StyleName { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public string ColorHex { get; set; } = "#888888";
    public Dictionary<string, float> Distribution { get; set; } = new();
}

public interface IStyleClassifierService
{
    Task TrainStyleAsync(Guid styleId);
    Task<StylePrediction> PredictAsync(AudioFeaturesEntity features);
    Task ScanLibraryAsync();
}

public class StyleClassifierService : IStyleClassifierService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<StyleClassifierService> _logger;
    private readonly PersonalClassifierService _personalClassifier;

    public StyleClassifierService(
        IDbContextFactory<AppDbContext> dbFactory, 
        ILogger<StyleClassifierService> logger,
        PersonalClassifierService personalClassifier)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _personalClassifier = personalClassifier;
    }

    /// <summary>
    /// Triggers a global retraining of the ML model using all defined styles as ground truth.
    /// Note: 'styleId' is ignored in this implementation as LightGBM trains globally.
    /// </summary>
    public async Task TrainStyleAsync(Guid styleId)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var styles = await context.StyleDefinitions.ToListAsync();
        
        var trainingData = new Dictionary<string, List<float[]>>();
        int totalTracks = 0;

        foreach (var style in styles)
        {
            if (!style.ReferenceTrackHashes.Any()) continue;
            
            var features = await context.AudioFeatures
                .Where(f => style.ReferenceTrackHashes.Contains(f.TrackUniqueHash))
                .Select(f => f.AiEmbeddingJson)
                .ToListAsync();

            var embeddings = new List<float[]>();
            foreach (var json in features)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    // Assuming raw JSON array: [0.1, 0.2, ...]
                    // Basic parsing for speed or use JsonSerializer
                    var vec = System.Text.Json.JsonSerializer.Deserialize<float[]>(json);
                    if (vec != null && vec.Length == 128)
                    {
                        embeddings.Add(vec);
                    }
                }
                catch 
                { 
                    // Ignore bad data 
                }
            }

            if (embeddings.Any())
            {
                trainingData[style.Name] = embeddings;
                totalTracks += embeddings.Count;
            }
        }

        if (totalTracks >= 5)
        {
            await Task.Run(() => _personalClassifier.Train("UserStyleModel", trainingData));
            _logger.LogInformation("Trained PersonalClassifier with {Count} tracks across {Styles} styles", totalTracks, trainingData.Count);
        }
        else
        {
            _logger.LogWarning("Insufficient training data. Need at least 5 tracks with embeddings.");
        }
    }

    /// <summary>
    /// Predicts the style of a track based on its audio features (Specifically AiEmbedding).
    /// </summary>
    public async Task<StylePrediction> PredictAsync(AudioFeaturesEntity features)
    {
        // Must have embedding
        if (string.IsNullOrEmpty(features.AiEmbeddingJson))
            return new StylePrediction { StyleName = "Unknown (No Embedding)", Confidence = 0f };

        float[]? embedding = null;
        try
        {
            embedding = System.Text.Json.JsonSerializer.Deserialize<float[]>(features.AiEmbeddingJson);
        }
        catch { }

        if (embedding == null || embedding.Length != 128)
            return new StylePrediction { StyleName = "Unknown (Bad Embedding)", Confidence = 0f };

        // Use ML.NET Service
        var (vibe, confidence) = _personalClassifier.Predict(embedding);

        // Fetch Color from DB (Optimization: Cache styles in memory)
        string color = "#888888";
        using (var context = await _dbFactory.CreateDbContextAsync())
        {
            var style = await context.StyleDefinitions.FirstOrDefaultAsync(s => s.Name == vibe);
            if (style != null) color = style.ColorHex;
        }

        return new StylePrediction
        {
            StyleName = vibe,
            Confidence = confidence,
            ColorHex = color,
            Distribution = new Dictionary<string, float> { { vibe, confidence } } // TODO: Get full probabilities from LightGBM if needed
        };
    }

    public async Task ScanLibraryAsync()
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var allFeatures = await context.AudioFeatures.ToListAsync();
        // Optimization: In reality we should batch this or only do unclassified ones
        
        foreach (var feature in allFeatures)
        {
            var prediction = await PredictAsync(feature);
            
            // Update entity
            feature.DetectedSubGenre = prediction.StyleName;
            feature.SubGenreConfidence = prediction.Confidence;
            feature.PredictedVibe = prediction.StyleName; // Keep synced
            feature.PredictionConfidence = prediction.Confidence;
            feature.GenreDistributionJson = System.Text.Json.JsonSerializer.Serialize(prediction.Distribution);
        }

        await context.SaveChangesAsync();
    }

    // ==========================================
    // Math Helpers
    // ==========================================

    private List<float> ExtractFeatureVector(AudioFeaturesEntity f)
    {
        // Normalize roughly to 0-1 range based on typical DnB values
        return new List<float>
        {
            Math.Clamp((f.Bpm - 70) / 130f, 0f, 1f),      // BPM (70-200)
            f.Energy,                                     // 0-1
            f.Danceability,                               // 0-1
            f.SpectralCentroid / 5000f,                   // ~0-5000Hz normalized
            f.SpectralComplexity,                         // 0-1
            f.DynamicComplexity / 10f,                    // ~0-10
            Math.Clamp((f.LoudnessLUFS + 30) / 30f, 0f, 1f) // -30 to 0 LUFS
        };
    }

    private List<float> CalculateCentroid(List<List<float>> vectors)
    {
        int dims = vectors[0].Count;
        var centroid = new float[dims];
        
        foreach (var v in vectors)
        {
            for (int i = 0; i < dims; i++)
            {
                centroid[i] += v[i];
            }
        }

        for (int i = 0; i < dims; i++)
        {
            centroid[i] /= vectors.Count;
        }

        return centroid.ToList();
    }

    private double EuclideanDistance(List<float> v1, List<float> v2)
    {
        double sum = 0;
        for (int i = 0; i < Math.Min(v1.Count, v2.Count); i++)
        {
            sum += Math.Pow(v1[i] - v2[i], 2);
        }
        return Math.Sqrt(sum);
    }
}
