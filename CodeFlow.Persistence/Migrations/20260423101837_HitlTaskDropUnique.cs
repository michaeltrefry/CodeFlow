using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HitlTaskDropUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_hitl_tasks_trace_id_round_id_agent_key",
                table: "hitl_tasks");

            migrationBuilder.CreateIndex(
                name: "IX_hitl_tasks_trace_id_round_id_agent_key",
                table: "hitl_tasks",
                columns: new[] { "trace_id", "round_id", "agent_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_hitl_tasks_trace_id_round_id_agent_key",
                table: "hitl_tasks");

            migrationBuilder.CreateIndex(
                name: "IX_hitl_tasks_trace_id_round_id_agent_key",
                table: "hitl_tasks",
                columns: new[] { "trace_id", "round_id", "agent_key" },
                unique: true);
        }
    }
}
