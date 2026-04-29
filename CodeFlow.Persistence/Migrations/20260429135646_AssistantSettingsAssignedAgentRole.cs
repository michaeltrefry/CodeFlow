using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantSettingsAssignedAgentRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "assigned_agent_role_id",
                table: "assistant_settings",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_settings_assigned_agent_role_id",
                table: "assistant_settings",
                column: "assigned_agent_role_id");

            migrationBuilder.AddForeignKey(
                name: "FK_assistant_settings_agent_roles_assigned_agent_role_id",
                table: "assistant_settings",
                column: "assigned_agent_role_id",
                principalTable: "agent_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assistant_settings_agent_roles_assigned_agent_role_id",
                table: "assistant_settings");

            migrationBuilder.DropIndex(
                name: "IX_assistant_settings_assigned_agent_role_id",
                table: "assistant_settings");

            migrationBuilder.DropColumn(
                name: "assigned_agent_role_id",
                table: "assistant_settings");
        }
    }
}
