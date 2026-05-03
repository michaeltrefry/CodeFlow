using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaTraceWorkDir : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_work_dir",
                table: "workflow_sagas",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_work_dir",
                table: "workflow_sagas");
        }
    }
}
