using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;

namespace SLSKDONET.Services.AI;

public class PersonalClassifierService
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
    public void Train(string modelName, Dictionary<string, List<float[]>> trainingData)
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
            // In a real scenario, we might want to just log this or handle it gracefully,
            // but for now we'll return or throw. A service shouldn't crash the app though.
            Console.WriteLine("Not enough training data to train model.");
            return;
        }

        // 2. Load Data
        var trainingDataView = _mlContext.Data.LoadFromEnumerable(dataPoints);

        // 3. Define Pipeline
        // MapValueToKey: Converts string labels to keys (0, 1, 2...)
        // LightGbm: Fast, accurate tree-based gradient boosting
        // MapKeyToValue: Converts the predicted key back to the string label
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
}
