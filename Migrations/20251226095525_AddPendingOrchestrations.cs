using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SLSKDONET.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingOrchestrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLogs_PlaylistJobs_PlaylistId",
                table: "ActivityLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistTracks_PlaylistJobs_PlaylistId",
                table: "PlaylistTracks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PlaylistJobs",
                table: "PlaylistJobs");

            migrationBuilder.RenameTable(
                name: "PlaylistJobs",
                newName: "Projects");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Projects",
                table: "Projects",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "PendingOrchestrations",
                columns: table => new
                {
                    TrackUniqueHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingOrchestrations", x => x.TrackUniqueHash);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLogs_Projects_PlaylistId",
                table: "ActivityLogs",
                column: "PlaylistId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistTracks_Projects_PlaylistId",
                table: "PlaylistTracks",
                column: "PlaylistId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLogs_Projects_PlaylistId",
                table: "ActivityLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_PlaylistTracks_Projects_PlaylistId",
                table: "PlaylistTracks");

            migrationBuilder.DropTable(
                name: "PendingOrchestrations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Projects",
                table: "Projects");

            migrationBuilder.RenameTable(
                name: "Projects",
                newName: "PlaylistJobs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PlaylistJobs",
                table: "PlaylistJobs",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLogs_PlaylistJobs_PlaylistId",
                table: "ActivityLogs",
                column: "PlaylistId",
                principalTable: "PlaylistJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlaylistTracks_PlaylistJobs_PlaylistId",
                table: "PlaylistTracks",
                column: "PlaylistId",
                principalTable: "PlaylistJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
