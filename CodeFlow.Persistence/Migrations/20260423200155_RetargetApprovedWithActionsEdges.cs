using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetargetApprovedWithActionsEdges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The ApprovedWithActions decision kind has been removed; any existing
            // workflow edges wired to its port need to route on "Rejected" instead.
            // Saga history rows retain their historical string values (audit).
            migrationBuilder.Sql(
                "UPDATE workflow_edges SET from_port = 'Rejected' WHERE from_port = 'ApprovedWithActions';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data migration is not reversible: once collapsed into "Rejected",
            // the original "ApprovedWithActions" assignment is unrecoverable.
        }
    }
}
