using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForEachNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "current_foreach_index",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "foreach_item_outputs_json",
                table: "workflow_sagas",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "foreach_total_items",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "collection_expression",
                table: "workflow_nodes",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "item_var",
                table: "workflow_nodes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "current_foreach_index",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "foreach_item_outputs_json",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "foreach_total_items",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "collection_expression",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "item_var",
                table: "workflow_nodes");
        }
    }
}
