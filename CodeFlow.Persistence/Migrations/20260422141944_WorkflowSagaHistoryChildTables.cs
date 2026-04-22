using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowSagaHistoryChildTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "decision_count",
                table: "workflow_sagas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "logic_evaluation_count",
                table: "workflow_sagas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "workflow_saga_decisions",
                columns: table => new
                {
                    saga_correlation_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ordinal = table.Column<int>(type: "int", nullable: false),
                    trace_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    agent_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    agent_version = table.Column<int>(type: "int", nullable: false),
                    decision = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    decision_payload_json = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    round_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    recorded_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    node_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    output_port_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    input_ref = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    output_ref = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_saga_decisions", x => new { x.saga_correlation_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_workflow_saga_decisions_workflow_sagas_saga_correlation_id",
                        column: x => x.saga_correlation_id,
                        principalTable: "workflow_sagas",
                        principalColumn: "correlation_id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_saga_logic_evaluations",
                columns: table => new
                {
                    saga_correlation_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ordinal = table.Column<int>(type: "int", nullable: false),
                    trace_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    node_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    output_port_name = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    round_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    duration_ticks = table.Column<long>(type: "bigint", nullable: false),
                    logs_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    failure_kind = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    failure_message = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    recorded_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_saga_logic_evaluations", x => new { x.saga_correlation_id, x.ordinal });
                    table.ForeignKey(
                        name: "FK_workflow_saga_logic_evaluations_workflow_sagas_saga_correlat~",
                        column: x => x.saga_correlation_id,
                        principalTable: "workflow_sagas",
                        principalColumn: "correlation_id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_saga_decisions_trace_id_ordinal",
                table: "workflow_saga_decisions",
                columns: new[] { "trace_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_saga_logic_evaluations_trace_id_ordinal",
                table: "workflow_saga_logic_evaluations",
                columns: new[] { "trace_id", "ordinal" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_saga_decisions");

            migrationBuilder.DropTable(
                name: "workflow_saga_logic_evaluations");

            migrationBuilder.DropColumn(
                name: "decision_count",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "logic_evaluation_count",
                table: "workflow_sagas");
        }
    }
}
