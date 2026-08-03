using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CharacterId",
                table: "GatheringSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatheringSessions_CharacterId",
                table: "GatheringSessions",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Characters_Name",
                table: "Characters",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_GatheringSessions_Characters_CharacterId",
                table: "GatheringSessions",
                column: "CharacterId",
                principalTable: "Characters",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GatheringSessions_Characters_CharacterId",
                table: "GatheringSessions");

            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.DropIndex(
                name: "IX_GatheringSessions_CharacterId",
                table: "GatheringSessions");

            migrationBuilder.DropColumn(
                name: "CharacterId",
                table: "GatheringSessions");
        }
    }
}
