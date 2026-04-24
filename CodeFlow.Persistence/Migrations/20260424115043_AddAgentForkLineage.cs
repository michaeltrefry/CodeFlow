using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentForkLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "forked_from_key",
                table: "agents",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "forked_from_version",
                table: "agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owning_workflow_key",
                table: "agents",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agents_owning_workflow_key",
                table: "agents",
                column: "owning_workflow_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agents_owning_workflow_key",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "forked_from_key",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "forked_from_version",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "owning_workflow_key",
                table: "agents");
        }
    }
}
