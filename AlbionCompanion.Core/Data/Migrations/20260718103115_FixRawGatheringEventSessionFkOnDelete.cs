using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixRawGatheringEventSessionFkOnDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RawGatheringEvents_GatheringSessions_SessionId",
                table: "RawGatheringEvents");

            migrationBuilder.AddForeignKey(
                name: "FK_RawGatheringEvents_GatheringSessions_SessionId",
                table: "RawGatheringEvents",
                column: "SessionId",
                principalTable: "GatheringSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RawGatheringEvents_GatheringSessions_SessionId",
                table: "RawGatheringEvents");

            migrationBuilder.AddForeignKey(
                name: "FK_RawGatheringEvents_GatheringSessions_SessionId",
                table: "RawGatheringEvents",
                column: "SessionId",
                principalTable: "GatheringSessions",
                principalColumn: "Id");
        }
    }
}
