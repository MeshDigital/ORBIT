using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <summary>
    /// Phase 13: Audio Intelligence Upgrade
    /// Adds deep learning AI fields (vocal detection, mood analysis)
    /// and forensic metrics (BPM stability, dynamic compression detection)
    /// </summary>
    public partial class Phase13_AudioIntelligenceUpgrade : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 13A: Forensic Librarian
            migrationBuilder.AddColumn<float>(
                name: "BpmStability",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0f);

            migrationBuilder.AddColumn<bool>(
                name: "IsDynamicCompressed",
                table: "audio_features",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Phase 13C: AI Layer (Vocals & Mood)
            migrationBuilder.AddColumn<float>(
                name: "InstrumentalProbability",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<string>(
                name: "MoodTag",
                table: "audio_features",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<float>(
                name: "MoodConfidence",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            // Advanced Harmonic Mixing
            migrationBuilder.AddColumn<string>(
                name: "ChordProgression",
                table: "audio_features",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BpmStability", table: "audio_features");
            migrationBuilder.DropColumn(name: "IsDynamicCompressed", table: "audio_features");
            migrationBuilder.DropColumn(name: "InstrumentalProbability", table: "audio_features");
            migrationBuilder.DropColumn(name: "MoodTag", table: "audio_features");
            migrationBuilder.DropColumn(name: "MoodConfidence", table: "audio_features");
            migrationBuilder.DropColumn(name: "ChordProgression", table: "audio_features");
        }
    }
}
