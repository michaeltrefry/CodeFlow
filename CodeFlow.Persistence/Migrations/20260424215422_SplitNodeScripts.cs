using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitNodeScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "script",
                table: "workflow_nodes",
                newName: "output_script");

            migrationBuilder.AddColumn<string>(
                name: "input_script",
                table: "workflow_nodes",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "input_script",
                table: "workflow_nodes");

            migrationBuilder.RenameColumn(
                name: "output_script",
                table: "workflow_nodes",
                newName: "script");
        }
    }
}
