using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameGlobalInputsJsonToWorkflowInputsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "global_inputs_json",
                table: "workflow_sagas",
                newName: "workflow_inputs_json");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "workflow_inputs_json",
                table: "workflow_sagas",
                newName: "global_inputs_json");
        }
    }
}
