using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowAndRoleRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_retired",
                table: "workflows",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_retired",
                table: "agent_roles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_key_is_retired",
                table: "workflows",
                columns: new[] { "key", "is_retired" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_roles_is_retired",
                table: "agent_roles",
                column: "is_retired");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflows_key_is_retired",
                table: "workflows");

            migrationBuilder.DropIndex(
                name: "IX_agent_roles_is_retired",
                table: "agent_roles");

            migrationBuilder.DropColumn(
                name: "is_retired",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "is_retired",
                table: "agent_roles");
        }
    }
}
