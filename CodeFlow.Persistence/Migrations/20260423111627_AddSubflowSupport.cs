using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubflowSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "global_inputs_json",
                table: "workflow_sagas",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_node_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_round_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_trace_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<int>(
                name: "subflow_depth",
                table: "workflow_sagas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "subflow_key",
                table: "workflow_nodes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "subflow_version",
                table: "workflow_nodes",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_sagas_parent_trace_id",
                table: "workflow_sagas",
                column: "parent_trace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_sagas_parent_trace_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "global_inputs_json",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_node_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_round_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_trace_id",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "subflow_depth",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "subflow_key",
                table: "workflow_nodes");

            migrationBuilder.DropColumn(
                name: "subflow_version",
                table: "workflow_nodes");
        }
    }
}
