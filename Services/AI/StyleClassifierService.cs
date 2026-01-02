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

    public StyleClassifierService(IDbContextFactory<AppDbContext> dbFactory, ILogger<StyleClassifierService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Recalculates the centroid for a specific style based on its reference tracks.
    /// </summary>
    public async Task TrainStyleAsync(Guid styleId)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var style = await context.StyleDefinitions.FindAsync(styleId);
        if (style == null) return;

        var referenceHashes = style.ReferenceTrackHashes;
        if (!referenceHashes.Any()) return;

        // Fetch features for reference tracks
        var features = await context.AudioFeatures
            .Where(f => referenceHashes.Contains(f.TrackUniqueHash))
            .ToListAsync();

        if (!features.Any()) return;

        // Calculate Centroid
        var vectors = features.Select(ExtractFeatureVector).ToList();
        var centroid = CalculateCentroid(vectors);

        style.Centroid = centroid;
        await context.SaveChangesAsync();
        
        _logger.LogInformation("Trained style '{StyleName}' with {Count} tracks", style.Name, features.Count);
    }

    /// <summary>
    /// Predicts the style of a track based on its audio features.
    /// </summary>
    public async Task<StylePrediction> PredictAsync(AudioFeaturesEntity features)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var styles = await context.StyleDefinitions.ToListAsync();
        
        if (!styles.Any()) 
            return new StylePrediction { StyleName = "Unknown", Confidence = 0f };

        var trackVector = ExtractFeatureVector(features);
        var distances = new Dictionary<StyleDefinitionEntity, double>();

        foreach (var style in styles)
        {
            if (style.Centroid == null || !style.Centroid.Any()) continue;
            var dist = EuclideanDistance(trackVector, style.Centroid);
            distances[style] = dist;
        }

        if (!distances.Any())
            return new StylePrediction { StyleName = "Unknown", Confidence = 0f };

        // Find nearest neighbor
        var sorted = distances.OrderBy(x => x.Value).ToList();
        var bestMatch = sorted.First();
        
        // Simple confidence calculation (inverse of distance, normalized)
        // This is a naive heuristic - can be improved later
        // Assuming max meaningful distance is ~2.0 after normalization
        float confidence = (float)Math.Max(0, 1.0 - (bestMatch.Value / 2.0));

        // Build distribution
        var distribution = new Dictionary<string, float>();
        double totalInverseDist = sorted.Take(3).Sum(x => 1.0 / (x.Value + 0.001)); // Top 3
        foreach(var match in sorted.Take(3))
        {
            distribution[match.Key.Name] = (float)((1.0 / (match.Value + 0.001)) / totalInverseDist);
        }

        return new StylePrediction
        {
            StyleName = bestMatch.Key.Name,
            ColorHex = bestMatch.Key.ColorHex,
            Confidence = confidence,
            Distribution = distribution
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
