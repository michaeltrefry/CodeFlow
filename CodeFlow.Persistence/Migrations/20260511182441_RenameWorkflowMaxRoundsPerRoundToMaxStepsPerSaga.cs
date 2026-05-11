using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameWorkflowMaxRoundsPerRoundToMaxStepsPerSaga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "max_rounds_per_round",
                table: "workflows",
                newName: "max_steps_per_saga");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "max_steps_per_saga",
                table: "workflows",
                newName: "max_rounds_per_round");
        }
    }
}
