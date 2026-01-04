using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;

using Microsoft.ML.Trainers.LightGbm;

namespace SLSKDONET.Services.AI;public class PersonalClassifierService
{
    private readonly MLContext _mlContext;
    private ITransformer? _trainedModel;
    private PredictionEngine<VibeInput, VibePrediction>? _predictionEngine;
    private const string ModelPath = "Data/Models/personal_vibe_model.zip";

    public PersonalClassifierService()
    {
        _mlContext = new MLContext(seed: 42); // Seed for reproducibility to ensure consistent results
        LoadModel();
    }

    public class VibeInput
    {
        [VectorType(128)]
        public float[] Embedding { get; set; } = Array.Empty<float>();
        
        [LoadColumn(1)]
        public string Label { get; set; } = string.Empty;
    }

    public class VibePrediction
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; } = string.Empty;

        public float[] Score { get; set; } = Array.Empty<float>();
    }

    /// <summary>
    /// Trains the LightGBM model with the provided dictionary of labeled embeddings.
    /// </summary>
    public async Task<bool> TrainModelAsync(string modelName, Dictionary<string, List<float[]>> trainingData)
    {
        return await Task.Run(() => 
        {
            try
            {
                // 1. Flatten the dictionary into a list of inputs
                var dataPoints = new List<VibeInput>();
                foreach (var kvp in trainingData)
                {
                    var label = kvp.Key;
                    foreach (var embedding in kvp.Value)
                    {
                        if (embedding.Length != 128) continue; // Safety check for dimension mismatch
                        dataPoints.Add(new VibeInput { Embedding = embedding, Label = label });
                    }
                }

                // Need valid data to train
                if (dataPoints.Count < 5)
                {
                    Console.WriteLine("Not enough training data to train model.");
                    return false;
                }

                // 2. Load Data
                var trainingDataView = _mlContext.Data.LoadFromEnumerable(dataPoints);

                // 3. Define Pipeline
                var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label")
                    .Append(_mlContext.MulticlassClassification.Trainers.LightGbm(
                        new LightGbmMulticlassTrainer.Options
                        {
                            NumberOfIterations = 50,
                            LearningRate = 0.1f,
                            UseSoftmax = true 
                        }))
                    .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

                // 4. Train
                _trainedModel = pipeline.Fit(trainingDataView);
                
                // 5. Save Model
                var modelDirectory = Path.GetDirectoryName(ModelPath);
                if (!string.IsNullOrEmpty(modelDirectory))
                    Directory.CreateDirectory(modelDirectory);
                    
                _mlContext.Model.Save(_trainedModel, trainingDataView.Schema, ModelPath);
                
                // 6. Refresh Prediction Engine
                CreatePredictionEngine();
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Training Failed: {ex.Message}");
                return false;
            }
        });
    }

    private void LoadModel()
    {
        if (File.Exists(ModelPath))
        {
            try 
            {
                _trainedModel = _mlContext.Model.Load(ModelPath, out _);
                CreatePredictionEngine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load model: {ex.Message}");
                // Suppress, just means we can't predict yet
            }
        }
    }

    private void CreatePredictionEngine()
    {
        if (_trainedModel != null)
        {
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<VibeInput, VibePrediction>(_trainedModel);
        }
    }

    /// <summary>
    /// Predicts the vibe/genre from an audio embedding.
    /// </summary>
    public (string Vibe, float Confidence) Predict(float[] embedding)
    {
        if (_predictionEngine == null || embedding == null || embedding.Length != 128)
            return ("Unknown (No Model)", 0f);

        var prediction = _predictionEngine.Predict(new VibeInput { Embedding = embedding });

        // Get max confidence score
        float maxConfidence = 0f;
        if (prediction.Score != null && prediction.Score.Length > 0)
        {
            maxConfidence = prediction.Score.Max();
        }

        // "Confidence Cliff" Logic
        // If the machine is unsure, don't guess.
        if (maxConfidence < 0.6f)
        {
            return ("Unknown / Mixed", maxConfidence);
        }

        return (prediction.PredictedLabel, maxConfidence);
    }

    /// <summary>
    /// Phase 16.2: Finds likely matches using Cosine Similarity on embeddings.
    /// </summary>
    /// <param name="targetVector">The 128-float embedding of the source track.</param>
    /// <param name="targetBpm">The BPM of the source track for pre-filtering.</param>
    /// <param name="candidates">List of candidate tracks with their embeddings and magnitudes.</param>
    /// <param name="limit">Number of results to return.</param>
    /// <returns>List of matching TrackUniqueHashes with similarity scores.</returns>
    public List<(string TrackHash, float Similarity)> FindSimilarTracks(
        float[] targetVector, 
        float targetBpm, 
        List<Data.Entities.AudioFeaturesEntity> candidates, 
        int limit = 50)
    {
        if (targetVector == null || targetVector.Length != 128) return new();

        var matches = new List<(string, float)>();
        float targetMag = (float)Math.Sqrt(targetVector.Sum(x => x * x));

        // Parallel processing for speed with large libraries
        var results = new System.Collections.Concurrent.ConcurrentBag<(string, float)>();

        Parallel.ForEach(candidates, candidate =>
        {
            // 1. Pre-Filter: BPM & Existence
            // Skip if BPM is too far off (+/- 30% range for wide vibe match, or tighter for mixing)
            // Let's use a loose +/- 20 BPM filter to keep it "Vibe" focused, not just "Mix" focused
            if (candidate.Bpm > 0 && Math.Abs(candidate.Bpm - targetBpm) > 30) // e.g. 174 vs 140 is allowed, 174 vs 120 is not
            {
                 return;
            }

            if (string.IsNullOrEmpty(candidate.AiEmbeddingJson)) return;

            // 2. Deserialize Vector
            // TODO: In future, cache these in memory to avoid deserialize overhead
            float[]? candidateVector = null;
            try
            {
                 candidateVector = System.Text.Json.JsonSerializer.Deserialize<float[]>(candidate.AiEmbeddingJson);
            }
            catch { return; }

            if (candidateVector == null || candidateVector.Length != 128) return;

            // 3. Cosine Similarity
            // Sim = DotProduct(A, B) / (MagA * MagB)
            float dotProduct = 0f;
            for (int i = 0; i < 128; i++)
            {
                dotProduct += targetVector[i] * candidateVector[i];
            }

            // Use cached magnitude if available, else calc
            float candMag = candidate.EmbeddingMagnitude > 0 
                ? candidate.EmbeddingMagnitude 
                : (float)Math.Sqrt(candidateVector.Sum(x => x * x));

            if (targetMag * candMag == 0) return;

            float similarity = dotProduct / (targetMag * candMag);

            if (similarity > 0.8f) // Filter low relevance
            {
                results.Add((candidate.TrackUniqueHash, similarity));
            }
        });

        return results.OrderByDescending(x => x.Item2).Take(limit).ToList();
    }

    /// <summary>
    /// Phase 4: Future-proofing for ONNX GPU inference.
    /// Provides session options for DirectML (cross-vendor GPU acceleration).
    /// </summary>
    public static Microsoft.ML.OnnxRuntime.SessionOptions GetOnnxOptions()
    {
        var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        
        try 
        {
            // DirectML is the best cross-platform option for Windows (AMD/NVIDIA/Intel)
            // It translates ONNX ops to DirectX 12 commands.
            options.AppendExecutionProvider_DML(0); // Device 0 = Primary GPU
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GPU] DirectML not available, falling back to CPU: {ex.Message}");
            options.AppendExecutionProvider_CPU();
        }
        
        return options;
    }
}
