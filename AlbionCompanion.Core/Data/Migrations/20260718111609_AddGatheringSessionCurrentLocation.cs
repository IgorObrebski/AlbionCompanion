using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatheringSessionCurrentLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentLocation",
                table: "GatheringSessions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Backfill existing rows so history isn't left with a blank CurrentLocation - best
            // available guess for a session that predates this column is where it started.
            migrationBuilder.Sql("UPDATE GatheringSessions SET CurrentLocation = StartLocation;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentLocation",
                table: "GatheringSessions");
        }
    }
}
