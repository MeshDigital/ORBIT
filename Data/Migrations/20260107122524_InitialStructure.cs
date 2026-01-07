using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurationConfidence",
                table: "TechnicalDetails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsReviewNeeded",
                table: "TechnicalDetails",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "TechnicalDetails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurationConfidence",
                table: "audio_features",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "DropConfidence",
                table: "audio_features",
                type: "REAL",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "audio_features",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "audio_features",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "LibraryFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastScannedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TracksFound = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryFolders", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryFolders");

            migrationBuilder.DropColumn(
                name: "CurationConfidence",
                table: "TechnicalDetails");

            migrationBuilder.DropColumn(
                name: "IsReviewNeeded",
                table: "TechnicalDetails");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "TechnicalDetails");

            migrationBuilder.DropColumn(
                name: "CurationConfidence",
                table: "audio_features");

            migrationBuilder.DropColumn(
                name: "DropConfidence",
                table: "audio_features");

            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "audio_features");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "audio_features");
        }
    }
}
