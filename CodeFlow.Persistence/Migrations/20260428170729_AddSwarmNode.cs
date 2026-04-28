using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSwarmNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "current_swarm_coordinator_node_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "pending_parallel_round_ids_json",
                table: "workflow_sagas",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "contributor_agent_key",
                table: "workflow_nodes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "contributor_agent_version",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "coordinator_agent_key",
                table: "workflow_nodes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "coordinator_agent_version",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "swarm_n",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "swarm_protocol",
                table: "workflow_nodes",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "swarm_token_budget",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "synthesizer_agent_key",
                table: "workflow_nodes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "synthesizer_agent_version",
                table: "workflow_nodes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "current_swarm_coordinator_node_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "pending_parallel_round_ids_json",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "contributor_agent_key",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "contributor_agent_version",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "coordinator_agent_key",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "coordinator_agent_version",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "swarm_n",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "swarm_protocol",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "swarm_token_budget",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "synthesizer_agent_key",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "synthesizer_agent_version",
                table: "workflow_nodes");
        }
    }
}
