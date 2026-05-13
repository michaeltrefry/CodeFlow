using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoalNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "goal_max_iterations",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "goal_objective",
                table: "workflow_nodes",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "goal_token_budget",
                table: "workflow_nodes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "goal_max_iterations",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "goal_objective",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "goal_token_budget",
                table: "workflow_nodes");
        }
    }
}
