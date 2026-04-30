using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentInvocationAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_invocation_authority",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    trace_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    round_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    agent_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    agent_version = table.Column<int>(type: "int", nullable: true),
                    workflow_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    workflow_version = table.Column<int>(type: "int", nullable: true),
                    envelope_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    blocked_axes_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tiers_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    resolved_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_invocation_authority", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_invocation_authority_agent_key_resolved_at",
                table: "agent_invocation_authority",
                columns: new[] { "agent_key", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_invocation_authority_trace_id_resolved_at",
                table: "agent_invocation_authority",
                columns: new[] { "trace_id", "resolved_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_invocation_authority");
        }
    }
}
