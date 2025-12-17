using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class Phase0_MusicalIntelligence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryEntries",
                columns: table => new
                {
                    UniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Album = table.Column<string>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    Bitrate = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FilePathUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MusicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    BPM = table.Column<double>(type: "REAL", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryEntries", x => x.UniqueHash);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceTitle = table.Column<string>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    DestinationFolder = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalTracks = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessfulCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistTrackId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QueuePosition = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsCurrentTrack = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpotifyMetadataCache",
                columns: table => new
                {
                    SpotifyId = table.Column<string>(type: "TEXT", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CachedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpotifyMetadataCache", x => x.SpotifyId);
                });

            migrationBuilder.CreateTable(
                name: "Tracks",
                columns: table => new
                {
                    GlobalId = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Filename = table.Column<string>(type: "TEXT", nullable: false),
                    SoulseekUsername = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CoverArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tracks", x => x.GlobalId);
                });

            migrationBuilder.CreateTable(
                name: "ActivityLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityLogs_PlaylistJobs_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "PlaylistJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlaylistTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Album = table.Column<string>(type: "TEXT", nullable: false),
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedFilePath = table.Column<string>(type: "TEXT", nullable: false),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<int>(type: "INTEGER", nullable: false),
                    IsLiked = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlayCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPlayedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    SpotifyTrackId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyAlbumId = table.Column<string>(type: "TEXT", nullable: true),
                    SpotifyArtistId = table.Column<string>(type: "TEXT", nullable: true),
                    AlbumArtUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ArtistImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", nullable: true),
                    Popularity = table.Column<int>(type: "INTEGER", nullable: true),
                    CanonicalDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MusicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    BPM = table.Column<double>(type: "REAL", nullable: true),
                    CuePointsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AudioFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    BitrateScore = table.Column<int>(type: "INTEGER", nullable: true),
                    AnalysisOffset = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaylistTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaylistTracks_PlaylistJobs_PlaylistId",
                        column: x => x.PlaylistId,
                        principalTable: "PlaylistJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_PlaylistId",
                table: "ActivityLogs",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistJob_CreatedAt",
                table: "PlaylistJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTrack_PlaylistId",
                table: "PlaylistTracks",
                column: "PlaylistId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTrack_Status",
                table: "PlaylistTracks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistTracks_TrackUniqueHash",
                table: "PlaylistTracks",
                column: "TrackUniqueHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityLogs");

            migrationBuilder.DropTable(
                name: "LibraryEntries");

            migrationBuilder.DropTable(
                name: "PlaylistTracks");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "SpotifyMetadataCache");

            migrationBuilder.DropTable(
                name: "Tracks");

            migrationBuilder.DropTable(
                name: "PlaylistJobs");
        }
    }
}
