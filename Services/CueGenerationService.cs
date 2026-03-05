using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Generates DJ cue points based on detected phrases and genre-specific templates.
/// </summary>
public class CueGenerationService
{
    private readonly ILogger<CueGenerationService> _logger;
    private readonly PhraseDetectionService _phraseDetection;

    public CueGenerationService(
        ILogger<CueGenerationService> logger,
        PhraseDetectionService phraseDetection)
    {
        _logger = logger;
        _phraseDetection = phraseDetection;
    }

    /// <summary>
    /// Represents a generated cue point.
    /// </summary>
    public class CuePoint
    {
        public int Index { get; set; } // 1-8
        public float TimeSeconds { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Color { get; set; } = "#FF0000";
        public PhraseType SourcePhrase { get; set; }
        public float Confidence { get; set; }
    }

    /// <summary>
    /// Phase 7: Generates smart cue points based on detected TrackPhrases.
    /// </summary>
    public List<OrbitCue> GenerateSmartCuesFromPhrases(IEnumerable<TrackPhraseEntity> phrases, float bpm)
    {
        var cues = new List<OrbitCue>();
        var sortedPhrases = phrases.OrderBy(p => p.StartTimeSeconds).ToList();
        if (sortedPhrases.Count == 0) return cues;

        float barDuration = bpm > 0 ? (60f / bpm) * 4 : 2f;

        // 1. Intro Start
        var intro = sortedPhrases.FirstOrDefault(p => p.Label != null && p.Label.Contains("Intro", StringComparison.OrdinalIgnoreCase));
        if (intro != null)
        {
            cues.Add(new OrbitCue { Name = "INTRO", Role = CueRole.Intro, Timestamp = intro.StartTimeSeconds, Color = "#00FF00" });
        }

        // 2. Main Drop
        var drop = sortedPhrases.FirstOrDefault(p => p.Label != null && p.Label.Contains("Drop", StringComparison.OrdinalIgnoreCase));
        if (drop != null)
        {
            // Drop Start
            cues.Add(new OrbitCue { Name = "DROP", Role = CueRole.Drop, Timestamp = drop.StartTimeSeconds, Color = "#FF0000" });
            
            // 16 Bars before Drop (Build-up warning)
            float buildStart = drop.StartTimeSeconds - (barDuration * 16);
            if (buildStart > 0 && (intro == null || buildStart > intro.StartTimeSeconds + 5))
            {
                cues.Add(new OrbitCue { Name = "BUILD-16", Role = CueRole.Build, Timestamp = buildStart, Color = "#FFFF00" });
            }
        }

        // 3. Breakdown
        var breakdown = sortedPhrases.FirstOrDefault(p => p.Label != null && p.Label.Contains("Break", StringComparison.OrdinalIgnoreCase));
        if (breakdown != null)
        {
            cues.Add(new OrbitCue { Name = "BREAK", Role = CueRole.Breakdown, Timestamp = breakdown.StartTimeSeconds, Color = "#9C27B0" });
        }

        // 4. Outro / Mix-out
        var outro = sortedPhrases.LastOrDefault(p => p.Label != null && p.Label.Contains("Outro", StringComparison.OrdinalIgnoreCase));
        if (outro != null)
        {
            cues.Add(new OrbitCue { Name = "OUTRO", Role = CueRole.Outro, Timestamp = outro.StartTimeSeconds, Color = "#0000FF" });
        }

        return cues;
    }

    /// <summary>
    /// Generates cue points for a track based on genre template.
    /// </summary>
    public async Task<List<CuePoint>> GenerateCuesAsync(
        string trackHash,
        string genre,
        byte[] waveformData,
        byte[] rmsData,
        float durationSeconds,
        float bpm)
    {
        var cues = new List<CuePoint>();

        try
        {
            // Detect phrases first
            var phrases = await _phraseDetection.DetectPhrasesAsync(
                trackHash, waveformData, rmsData, durationSeconds, bpm);

            if (phrases.Count == 0)
            {
                _logger.LogWarning("No phrases detected for {TrackHash}, using fallback cues", trackHash);
                return GenerateFallbackCues(durationSeconds, bpm);
            }

            // Get template for genre
            var template = await GetTemplateForGenreAsync(genre);

            // Calculate bar duration for offsets
            float barDurationSeconds = (60f / bpm) * 4;

            // Map template cues to detected phrases
            cues.AddRange(MapTemplateToPhrases(template, phrases, barDurationSeconds));

            _logger.LogInformation("🎵 CUE GENERATION: Generated {Count} cues for {TrackHash} (Genre: {Genre})",
                cues.Count, trackHash, genre);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cue generation failed for {TrackHash}", trackHash);
        }

        return cues;
    }

    /// <summary>
    /// Maps a genre template to detected phrases.
    /// </summary>
    private List<CuePoint> MapTemplateToPhrases(
        GenreCueTemplateEntity template,
        List<TrackPhraseEntity> phrases,
        float barDurationSeconds)
    {
        var cues = new List<CuePoint>();

        // Cue 1
        var cue1 = MapSingleCue(1, template.Cue1Target, template.Cue1OffsetBars, 
            template.Cue1Color, template.Cue1Label, phrases, barDurationSeconds);
        if (cue1 != null) cues.Add(cue1);

        // Cue 2
        var cue2 = MapSingleCue(2, template.Cue2Target, template.Cue2OffsetBars,
            template.Cue2Color, template.Cue2Label, phrases, barDurationSeconds);
        if (cue2 != null) cues.Add(cue2);

        // Cue 3
        var cue3 = MapSingleCue(3, template.Cue3Target, template.Cue3OffsetBars,
            template.Cue3Color, template.Cue3Label, phrases, barDurationSeconds);
        if (cue3 != null) cues.Add(cue3);

        // Cue 4
        var cue4 = MapSingleCue(4, template.Cue4Target, template.Cue4OffsetBars,
            template.Cue4Color, template.Cue4Label, phrases, barDurationSeconds);
        if (cue4 != null) cues.Add(cue4);

        // Optional cues 5-8
        if (template.Cue5Target.HasValue)
        {
            var cue5 = MapSingleCue(5, template.Cue5Target.Value, template.Cue5OffsetBars ?? 0,
                template.Cue5Color ?? "#FF00FF", template.Cue5Label, phrases, barDurationSeconds);
            if (cue5 != null) cues.Add(cue5);
        }
        if (template.Cue6Target.HasValue)
        {
            var cue6 = MapSingleCue(6, template.Cue6Target.Value, template.Cue6OffsetBars ?? 0,
                template.Cue6Color ?? "#00FFFF", template.Cue6Label, phrases, barDurationSeconds);
            if (cue6 != null) cues.Add(cue6);
        }
        if (template.Cue7Target.HasValue)
        {
            var cue7 = MapSingleCue(7, template.Cue7Target.Value, template.Cue7OffsetBars ?? 0,
                template.Cue7Color ?? "#FFFFFF", template.Cue7Label, phrases, barDurationSeconds);
            if (cue7 != null) cues.Add(cue7);
        }
        if (template.Cue8Target.HasValue)
        {
            var cue8 = MapSingleCue(8, template.Cue8Target.Value, template.Cue8OffsetBars ?? 0,
                template.Cue8Color ?? "#888888", template.Cue8Label, phrases, barDurationSeconds);
            if (cue8 != null) cues.Add(cue8);
        }

        return cues;
    }

    /// <summary>
    /// Maps a single cue to a detected phrase.
    /// </summary>
    private CuePoint? MapSingleCue(
        int index,
        PhraseType targetType,
        int offsetBars,
        string color,
        string? label,
        List<TrackPhraseEntity> phrases,
        float barDurationSeconds)
    {
        var targetPhrase = phrases.FirstOrDefault(p => p.Type == targetType);
        if (targetPhrase == null)
        {
            // Fallback: use first phrase if target type not found
            targetPhrase = phrases.FirstOrDefault();
            if (targetPhrase == null) return null;
        }

        float cueTime = targetPhrase.StartTimeSeconds + (offsetBars * barDurationSeconds);
        cueTime = Math.Max(0, cueTime); // Don't go below 0

        return new CuePoint
        {
            Index = index,
            TimeSeconds = cueTime,
            Label = label ?? targetType.ToString(),
            Color = color,
            SourcePhrase = targetPhrase.Type,
            Confidence = targetPhrase.Confidence
        };
    }

    /// <summary>
    /// Gets the template for a genre, or default template if not found.
    /// </summary>
    private async Task<GenreCueTemplateEntity> GetTemplateForGenreAsync(string genre)
    {
        using var db = new AppDbContext();
        
        var template = await db.GenreCueTemplates
            .FirstOrDefaultAsync(t => t.GenreName.ToLower() == genre.ToLower());

        if (template != null) return template;

        // Return default template
        _logger.LogInformation("No template found for genre '{Genre}', using default", genre);
        return GetDefaultTemplate();
    }

    /// <summary>
    /// Returns the default cue template (DnB-style).
    /// </summary>
    private GenreCueTemplateEntity GetDefaultTemplate()
    {
        return new GenreCueTemplateEntity
        {
            GenreName = "Default",
            DisplayName = "Default (DnB Style)",
            IsBuiltIn = true,
            Cue1Target = PhraseType.Drop,
            Cue1OffsetBars = 0,
            Cue1Color = "#FF0000",
            Cue1Label = "Drop",
            Cue2Target = PhraseType.Build,
            Cue2OffsetBars = 0,
            Cue2Color = "#FFFF00",
            Cue2Label = "Build",
            Cue3Target = PhraseType.Intro,
            Cue3OffsetBars = 0,
            Cue3Color = "#00FF00",
            Cue3Label = "Intro",
            Cue4Target = PhraseType.Breakdown,
            Cue4OffsetBars = 0,
            Cue4Color = "#0000FF",
            Cue4Label = "Breakdown"
        };
    }

    /// <summary>
    /// Returns fallback cues when phrase detection fails.
    /// </summary>
    private List<CuePoint> GenerateFallbackCues(float durationSeconds, float bpm)
    {
        float barDurationSeconds = bpm > 0 ? (60f / bpm) * 4 : 2f;
        
        return new List<CuePoint>
        {
            new CuePoint { Index = 1, TimeSeconds = 0, Label = "Intro", Color = "#00FF00" },
            new CuePoint { Index = 2, TimeSeconds = barDurationSeconds * 16, Label = "16 Bars", Color = "#FFFF00" },
            new CuePoint { Index = 3, TimeSeconds = durationSeconds * 0.3f, Label = "30%", Color = "#FF0000" },
            new CuePoint { Index = 4, TimeSeconds = durationSeconds * 0.5f, Label = "50%", Color = "#0000FF" }
        };
    }

    /// <summary>
    /// Seeds the database with default genre templates.
    /// </summary>
    public async Task SeedDefaultTemplatesAsync()
    {
        using var db = new AppDbContext();
        
        if (await db.GenreCueTemplates.AnyAsync())
        {
            _logger.LogInformation("Genre templates already exist, skipping seed");
            return;
        }

        var templates = new List<GenreCueTemplateEntity>
        {
            // DnB / Jungle
            new GenreCueTemplateEntity
            {
                GenreName = "DnB",
                DisplayName = "Drum & Bass",
                IsBuiltIn = true,
                Cue1Target = PhraseType.Drop, Cue1OffsetBars = 0, Cue1Color = "#FF0000", Cue1Label = "Drop",
                Cue2Target = PhraseType.Build, Cue2OffsetBars = -16, Cue2Color = "#FFFF00", Cue2Label = "Build-16",
                Cue3Target = PhraseType.Intro, Cue3OffsetBars = 0, Cue3Color = "#00FF00", Cue3Label = "Intro",
                Cue4Target = PhraseType.Breakdown, Cue4OffsetBars = 0, Cue4Color = "#9C27B0", Cue4Label = "Breakdown"
            },
            // House
            new GenreCueTemplateEntity
            {
                GenreName = "House",
                DisplayName = "House / Deep House",
                IsBuiltIn = true,
                Cue1Target = PhraseType.Intro, Cue1OffsetBars = 32, Cue1Color = "#00FF00", Cue1Label = "Intro+32",
                Cue2Target = PhraseType.Build, Cue2OffsetBars = 0, Cue2Color = "#FFFF00", Cue2Label = "Build",
                Cue3Target = PhraseType.Drop, Cue3OffsetBars = 0, Cue3Color = "#FF0000", Cue3Label = "Drop",
                Cue4Target = PhraseType.Outro, Cue4OffsetBars = -32, Cue4Color = "#0000FF", Cue4Label = "Outro-32"
            },
            // Techno
            new GenreCueTemplateEntity
            {
                GenreName = "Techno",
                DisplayName = "Techno / Industrial",
                IsBuiltIn = true,
                Cue1Target = PhraseType.Intro, Cue1OffsetBars = 64, Cue1Color = "#00FF00", Cue1Label = "Intro+64",
                Cue2Target = PhraseType.Build, Cue2OffsetBars = 0, Cue2Color = "#FFFF00", Cue2Label = "Peak",
                Cue3Target = PhraseType.Drop, Cue3OffsetBars = 0, Cue3Color = "#FF0000", Cue3Label = "Energy",
                Cue4Target = PhraseType.Breakdown, Cue4OffsetBars = 0, Cue4Color = "#9C27B0", Cue4Label = "Break"
            },
            // Dubstep
            new GenreCueTemplateEntity
            {
                GenreName = "Dubstep",
                DisplayName = "Dubstep / Riddim",
                IsBuiltIn = true,
                Cue1Target = PhraseType.Drop, Cue1OffsetBars = 0, Cue1Color = "#FF0000", Cue1Label = "Drop",
                Cue2Target = PhraseType.Build, Cue2OffsetBars = -8, Cue2Color = "#FFFF00", Cue2Label = "Build-8",
                Cue3Target = PhraseType.Breakdown, Cue3OffsetBars = 0, Cue3Color = "#9C27B0", Cue3Label = "Breakdown",
                Cue4Target = PhraseType.Intro, Cue4OffsetBars = 0, Cue4Color = "#00FF00", Cue4Label = "Intro"
            },
            // Trance
            new GenreCueTemplateEntity
            {
                GenreName = "Trance",
                DisplayName = "Trance / Progressive",
                IsBuiltIn = true,
                Cue1Target = PhraseType.Intro, Cue1OffsetBars = 64, Cue1Color = "#00FFFF", Cue1Label = "Intro+64",
                Cue2Target = PhraseType.Breakdown, Cue2OffsetBars = 0, Cue2Color = "#9C27B0", Cue2Label = "Melodic",
                Cue3Target = PhraseType.Drop, Cue3OffsetBars = 0, Cue3Color = "#FF0000", Cue3Label = "Euphoric",
                Cue4Target = PhraseType.Outro, Cue4OffsetBars = -32, Cue4Color = "#0000FF", Cue4Label = "Outro-32"
            }
        };

        db.GenreCueTemplates.AddRange(templates);
        await db.SaveChangesAsync();
        
        _logger.LogInformation("🎵 Seeded {Count} default genre cue templates", templates.Count);
    }
}
