using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSilverLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TotalSilverEarned",
                table: "GatheringSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SilverLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SilverLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SilverLogs_GatheringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GatheringSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SilverLogs_SessionId",
                table: "SilverLogs",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SilverLogs");

            migrationBuilder.DropColumn(
                name: "TotalSilverEarned",
                table: "GatheringSessions");
        }
    }
}
