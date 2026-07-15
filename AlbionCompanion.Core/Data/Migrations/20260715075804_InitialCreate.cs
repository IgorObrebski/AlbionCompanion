using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FlipLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    OrderType = table.Column<string>(type: "TEXT", nullable: false),
                    PricePerItem = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxPaid = table.Column<int>(type: "INTEGER", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlipLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GatheringSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartLocation = table.Column<string>(type: "TEXT", nullable: false),
                    TotalFameEarned = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatheringSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemDictionaries",
                columns: table => new
                {
                    UniqueName = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayNamePL = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayNameEN = table.Column<string>(type: "TEXT", nullable: false),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemGroup = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemDictionaries", x => x.UniqueName);
                });

            migrationBuilder.CreateTable(
                name: "PriceCaches",
                columns: table => new
                {
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    SellPriceMin = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyPriceMax = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceCaches", x => new { x.ItemId, x.Location });
                });

            migrationBuilder.CreateTable(
                name: "FameLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FameType = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FameLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FameLogs_GatheringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GatheringSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GatheredItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<string>(type: "TEXT", nullable: false),
                    Amount = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatheredItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GatheredItems_GatheringSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "GatheringSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FameLogs_SessionId",
                table: "FameLogs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_GatheredItems_SessionId",
                table: "GatheredItems",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FameLogs");

            migrationBuilder.DropTable(
                name: "FlipLogs");

            migrationBuilder.DropTable(
                name: "GatheredItems");

            migrationBuilder.DropTable(
                name: "ItemDictionaries");

            migrationBuilder.DropTable(
                name: "PriceCaches");

            migrationBuilder.DropTable(
                name: "GatheringSessions");
        }
    }
}
