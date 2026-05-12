using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForEachChildContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "parent_foreach_count",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parent_foreach_index",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_foreach_item_json",
                table: "workflow_sagas",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "parent_foreach_count",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_foreach_index",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_foreach_item_json",
                table: "workflow_sagas");
        }
    }
}
