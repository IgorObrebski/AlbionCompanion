using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlbionCompanion.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddItemDictionaryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Index",
                table: "ItemDictionaries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing rows (seeded before this column existed) would otherwise all sit at the
            // default 0, an ambiguous non-unique value that breaks GetItemByIndexAsync lookups.
            // ItemDictionaryService.SeedFromJsonAsync only ever populates an empty table, so
            // clearing it here forces a full re-seed (from items.json, over the network) on next
            // startup - safe because this table is pure reference data with no user-generated rows
            // to lose.
            migrationBuilder.Sql("DELETE FROM ItemDictionaries;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Index",
                table: "ItemDictionaries");
        }
    }
}
