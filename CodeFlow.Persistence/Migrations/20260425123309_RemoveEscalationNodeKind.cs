using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEscalationNodeKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "escalated_from_node_id",
                table: "workflow_sagas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "escalated_from_node_id",
                table: "workflow_sagas",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");
        }
    }
}
