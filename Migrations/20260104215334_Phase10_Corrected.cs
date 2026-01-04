using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class Phase10_Corrected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TechnicalDetails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistTrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WaveformData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RmsData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LowData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    MidData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    HighData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AiEmbeddingJson = table.Column<string>(type: "TEXT", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    SpectralHash = table.Column<string>(type: "TEXT", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsPrepared = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    PrimaryGenre = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TechnicalDetails_PlaylistTracks_PlaylistTrackId",
                        column: x => x.PlaylistTrackId,
                        principalTable: "PlaylistTracks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalDetails_PlaylistTrackId",
                table: "TechnicalDetails",
                column: "PlaylistTrackId",
                unique: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrepared",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryGenre",
                table: "PlaylistTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CuePointsJson",
                table: "LibraryEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPrepared",
                table: "LibraryEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryGenre",
                table: "LibraryEntries",
                type: "TEXT",
                nullable: true);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TechnicalDetails_PlaylistTracks_PlaylistTrackId",
                table: "TechnicalDetails");

            migrationBuilder.DropPrimaryKey(
                name: "PK_TechnicalDetails",
                table: "TechnicalDetails");

            migrationBuilder.DropColumn(
                name: "IsPrepared",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "PrimaryGenre",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "CuePointsJson",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "IsPrepared",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "PrimaryGenre",
                table: "LibraryEntries");

            migrationBuilder.DropColumn(
                name: "IsPrepared",
                table: "TechnicalDetails");

            migrationBuilder.DropColumn(
                name: "PrimaryGenre",
                table: "TechnicalDetails");

            migrationBuilder.RenameTable(
                name: "TechnicalDetails",
                newName: "TrackTechnicalDetails");

            migrationBuilder.RenameIndex(
                name: "IX_TechnicalDetails_PlaylistTrackId",
                table: "TrackTechnicalDetails",
                newName: "IX_TrackTechnicalDetails_PlaylistTrackId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_TrackTechnicalDetails",
                table: "TrackTechnicalDetails",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_TrackTechnicalDetails_PlaylistTracks_PlaylistTrackId",
                table: "TrackTechnicalDetails",
                column: "PlaylistTrackId",
                principalTable: "PlaylistTracks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
