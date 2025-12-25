using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpgradeAt",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpgradeScanAt",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousBitrate",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpgradeSource",
                table: "Tracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "PlaylistTracks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourcePlaylistId",
                table: "PlaylistTracks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePlaylistName",
                table: "PlaylistTracks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpgradeAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "LastUpgradeScanAt",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "PreviousBitrate",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "UpgradeSource",
                table: "Tracks");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SourcePlaylistId",
                table: "PlaylistTracks");

            migrationBuilder.DropColumn(
                name: "SourcePlaylistName",
                table: "PlaylistTracks");
        }
    }
}
