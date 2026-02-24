using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ReactiveUI;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// ViewModel wrapper for a detected musical phrase.
/// Handles bars/beats math and energy visualization logic.
/// </summary>
public class PhraseItemViewModel : ReactiveObject
{
    private readonly TrackPhraseEntity _entity;
    private bool _isActive;

    public PhraseItemViewModel(TrackPhraseEntity entity, float bpm)
    {
        _entity = entity;
        Type = entity.Type;
        Label = entity.Label ?? entity.Type.ToString();
        StartTimeSeconds = entity.StartTimeSeconds;
        EndTimeSeconds = entity.EndTimeSeconds;
        DurationSeconds = entity.DurationSeconds;
        
        // Task 1: Bars/Beats Calculation
        // Formula: (BPM * DurationSeconds) / 60 / 4
        if (bpm > 0)
        {
            double bars = (bpm * DurationSeconds) / 240.0;
            // Round to nearest integer for DJ-friendly display
            BarsLabel = $"{Math.Round(bars)} Bars ({TimeSpan.FromSeconds(DurationSeconds):m\\:ss})";
        }
        else
        {
            BarsLabel = $"{TimeSpan.FromSeconds(DurationSeconds):m\\:ss}";
        }

        // Task 1: Energy Normalization
        // Using exactly 5 values normalized between 0.0 and 1.0. 
        // Since we have a single EnergyLevel for the phrase, we create a slight variation micro-graph.
        EnergyLevels = GenerateMicroGraph(entity.EnergyLevel);

        IsGhost = entity.Confidence < 0.99f;
    }

    public PhraseType Type { get; }
    public string Label { get; }
    public float StartTimeSeconds { get; }
    public float EndTimeSeconds { get; }
    public float DurationSeconds { get; }
    public string BarsLabel { get; }
    public List<double> EnergyLevels { get; }
    public bool IsGhost { get; }

    /// <summary>
    /// Indicates if this phrase is currently playing in the active track.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    private List<double> GenerateMicroGraph(float baseEnergy)
    {
        // For Phase 2, we generate 5 normalized bars based on the phrase's energy level.
        // We add small random fluctuations to give it a "live" feel while remaining accurate to the base level.
        var random = new Random((int)(StartTimeSeconds * 1000));
        var result = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            double variaton = (random.NextDouble() * 0.4) - 0.2; // -0.2 to +0.2
            result.Add(Math.Clamp(baseEnergy + variaton, 0.1, 1.0));
        }
        return result;
    }
}
