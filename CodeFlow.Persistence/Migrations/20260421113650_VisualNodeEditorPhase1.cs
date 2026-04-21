using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VisualNodeEditorPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_edges_workflow_id_from_agent_key_decision_sort_order",
                table: "workflow_edges");

            migrationBuilder.DropColumn(
                name: "escalation_agent_key",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "start_agent_key",
                table: "workflows");

            migrationBuilder.DropColumn(
                name: "escalated_from_agent_key",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "decision",
                table: "workflow_edges");

            migrationBuilder.DropColumn(
                name: "discriminator_json",
                table: "workflow_edges");

            migrationBuilder.RenameColumn(
                name: "to_agent_key",
                table: "workflow_edges",
                newName: "to_port");

            migrationBuilder.RenameColumn(
                name: "from_agent_key",
                table: "workflow_edges",
                newName: "from_port");

            migrationBuilder.AddColumn<Guid>(
                name: "current_node_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "escalated_from_node_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "inputs_json",
                table: "workflow_sagas",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "from_node_id",
                table: "workflow_edges",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "to_node_id",
                table: "workflow_edges",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "node_id",
                table: "hitl_tasks",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "workflow_inputs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    workflow_id = table.Column<long>(type: "bigint", nullable: false),
                    key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    display_name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    kind = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    required = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    default_value_json = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ordinal = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_inputs", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_inputs_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "workflow_nodes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    workflow_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    agent_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    agent_version = table.Column<int>(type: "int", nullable: true),
                    script = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    output_ports_json = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    layout_x = table.Column<double>(type: "double", nullable: false),
                    layout_y = table.Column<double>(type: "double", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_nodes", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_nodes_workflows_workflow_id",
                        column: x => x.workflow_id,
                        principalTable: "workflows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_edges_workflow_id_from_node_id_from_port_sort_order",
                table: "workflow_edges",
                columns: new[] { "workflow_id", "from_node_id", "from_port", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_inputs_workflow_id_key",
                table: "workflow_inputs",
                columns: new[] { "workflow_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_nodes_workflow_id_node_id",
                table: "workflow_nodes",
                columns: new[] { "workflow_id", "node_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_inputs");

            migrationBuilder.DropTable(
                name: "workflow_nodes");

            migrationBuilder.DropIndex(
                name: "IX_workflow_edges_workflow_id_from_node_id_from_port_sort_order",
                table: "workflow_edges");

            migrationBuilder.DropColumn(
                name: "current_node_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "escalated_from_node_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "inputs_json",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "from_node_id",
                table: "workflow_edges");

            migrationBuilder.DropColumn(
                name: "to_node_id",
                table: "workflow_edges");

            migrationBuilder.DropColumn(
                name: "node_id",
                table: "hitl_tasks");

            migrationBuilder.RenameColumn(
                name: "to_port",
                table: "workflow_edges",
                newName: "to_agent_key");

            migrationBuilder.RenameColumn(
                name: "from_port",
                table: "workflow_edges",
                newName: "from_agent_key");

            migrationBuilder.AddColumn<string>(
                name: "escalation_agent_key",
                table: "workflows",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "start_agent_key",
                table: "workflows",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "escalated_from_agent_key",
                table: "workflow_sagas",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "decision",
                table: "workflow_edges",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "discriminator_json",
                table: "workflow_edges",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_edges_workflow_id_from_agent_key_decision_sort_order",
                table: "workflow_edges",
                columns: new[] { "workflow_id", "from_agent_key", "decision", "sort_order" });
        }
    }
}
