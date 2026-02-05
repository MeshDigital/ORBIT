using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Data model for a track in the Emergency Card.
/// </summary>
public class EmergencyCardTrack
{
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public double Bpm { get; set; }
    public string Key { get; set; } = string.Empty;
    public string VocalTag { get; set; } = string.Empty;  // INST, SPARSE, DENSE
    public string MentorVerdict { get; set; } = string.Empty;
    public float Energy { get; set; }
}

/// <summary>
/// QuestPDF document generator for the Emergency Card.
/// Designed for 2:00 AM booth legibility — dark mode, large type, high contrast.
/// </summary>
public class EmergencyCardDocument : IDocument
{
    private readonly string _setTitle;
    private readonly List<EmergencyCardTrack> _tracks;
    private readonly DateTime _generatedAt;

    public EmergencyCardDocument(string setTitle, IEnumerable<EmergencyCardTrack> tracks)
    {
        _setTitle = setTitle;
        _tracks = tracks.ToList();
        _generatedAt = DateTime.Now;
    }

    public DocumentMetadata GetMetadata() => DocumentMetadata.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(1, Unit.Centimetre);
            page.PageColor(Colors.Black);
            page.DefaultTextStyle(x => x.FontSize(12).FontColor(Colors.White));

            page.Header().Element(ComposeHeader);
            page.Content().Element(ComposeContent);
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("ORBIT EMERGENCY CARD")
                    .FontSize(28).ExtraBold().FontColor(Colors.Red.Medium);
                
                col.Item().Text($"SET: {_setTitle}")
                    .FontSize(14).Bold().FontColor(Colors.White);
                
                col.Item().Text($"GENERATED: {_generatedAt:yyyy-MM-dd HH:mm} | TRACKS: {_tracks.Count}")
                    .FontSize(10).FontColor(Colors.Grey.Medium);
            });
        });
    }

    private void ComposeContent(IContainer container)
    {
        container.PaddingTop(15).Table(table =>
        {
            // Column definitions
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(25);   // #
                columns.RelativeColumn(3);    // Track / Artist
                columns.ConstantColumn(50);   // BPM
                columns.ConstantColumn(45);   // Key (Color)
                columns.ConstantColumn(45);   // Vocal Tag
                columns.RelativeColumn(2);    // Mentor Advice
            });

            // Header row
            table.Header(header =>
            {
                header.Cell().Element(HeaderStyle).Text("#");
                header.Cell().Element(HeaderStyle).Text("TRACK / ARTIST");
                header.Cell().Element(HeaderStyle).AlignCenter().Text("BPM");
                header.Cell().Element(HeaderStyle).AlignCenter().Text("KEY");
                header.Cell().Element(HeaderStyle).AlignCenter().Text("VOX");
                header.Cell().Element(HeaderStyle).Text("ADVICE");
            });

            // Track rows
            foreach (var track in _tracks)
            {
                // Index
                table.Cell().Element(RowStyle).Text($"{track.Index}").FontSize(12).Bold();

                // Track / Artist
                table.Cell().Element(RowStyle).Column(c =>
                {
                    c.Item().Text(track.Title).FontSize(12).Bold();
                    c.Item().Text(track.Artist).FontSize(9).FontColor(Colors.Grey.Lighten1);
                });

                // BPM - The Heartbeat (extra large)
                table.Cell().Element(RowStyle).AlignCenter()
                    .Text($"{track.Bpm:F0}").FontSize(16).ExtraBold();

                // Key - Color coded Camelot
                var keyColor = CamelotColorMapper.GetHexColor(track.Key);
                var useBlackText = CamelotColorMapper.ShouldUseBlackText(keyColor);
                
                table.Cell().Element(RowStyle).AlignCenter()
                    .Background(keyColor)
                    .Padding(3)
                    .Text(track.Key)
                    .FontSize(13).ExtraBold()
                    .FontColor(useBlackText ? Colors.Black : Colors.White);

                // Vocal Tag - Color coded
                var vocalColor = GetVocalTagColor(track.VocalTag);
                table.Cell().Element(RowStyle).AlignCenter()
                    .Text(track.VocalTag).FontSize(10).Bold().FontColor(vocalColor);

                // Mentor Advice
                table.Cell().Element(RowStyle)
                    .Text(track.MentorVerdict).FontSize(8).Italic().FontColor(Colors.Grey.Lighten2);
            }
        });
    }

    private void ComposeFooter(IContainer container)
    {
        container.Column(col =>
        {
            // Energy Arc Visualizer
            col.Item().AlignCenter().Row(row =>
            {
                row.AutoItem().Text("ENERGY ARC: ").FontSize(10).FontColor(Colors.Grey.Medium);
                row.AutoItem().Text(GenerateEnergyArc()).FontSize(12).Bold().FontColor(Colors.Cyan.Medium);
            });

            // Attribution
            col.Item().PaddingTop(5).AlignCenter()
                .Text("Generated by ORBIT — The Trusted Gig Companion")
                .FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    /// <summary>
    /// Generates ASCII energy arc visualization.
    /// </summary>
    private string GenerateEnergyArc()
    {
        if (!_tracks.Any()) return "─────────";

        var arcChars = new[] { '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█' };
        var arc = _tracks.Select(t =>
        {
            var level = Math.Clamp((int)(t.Energy * 7), 0, 7);
            return arcChars[level];
        });
        
        return string.Concat(arc);
    }

    /// <summary>
    /// Gets color for vocal tag based on intensity.
    /// </summary>
    private static string GetVocalTagColor(string? vocalTag)
    {
        return vocalTag?.ToUpperInvariant() switch
        {
            "INST" => Colors.Green.Lighten1,      // Safe - no vocals
            "SPARSE" => Colors.Yellow.Medium,     // Caution - some vocals
            "DENSE" => Colors.Orange.Medium,      // Warning - heavy vocals
            _ => Colors.Grey.Medium
        };
    }

    // Style helpers
    private static IContainer HeaderStyle(IContainer container) =>
        container
            .DefaultTextStyle(x => x.SemiBold().FontSize(9).FontColor(Colors.Grey.Lighten1))
            .PaddingVertical(5)
            .BorderBottom(1)
            .BorderColor(Colors.Grey.Darken3);

    private static IContainer RowStyle(IContainer container) =>
        container
            .BorderBottom(1)
            .BorderColor(Colors.Grey.Darken4)
            .PaddingVertical(6)
            .AlignMiddle();
}
