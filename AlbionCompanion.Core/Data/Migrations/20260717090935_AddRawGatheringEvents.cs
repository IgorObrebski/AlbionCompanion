using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRawGatheringEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RawGatheringEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PhotonCode = table.Column<byte>(type: "INTEGER", nullable: false),
                    SemanticEventCode = table.Column<byte>(type: "INTEGER", nullable: true),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawGatheringEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RawGatheringEvents_GatheringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GatheringSessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RawGatheringEvents_SessionId",
                table: "RawGatheringEvents",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RawGatheringEvents_Timestamp",
                table: "RawGatheringEvents",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RawGatheringEvents");
        }
    }
}
