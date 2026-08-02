using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGatheredItemLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "GatheredItems",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Location",
                table: "GatheredItems");
        }
    }
}
