using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaCurrentInputRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "current_input_ref",
                table: "workflow_sagas",
                type: "varchar(1024)",
                maxLength: 1024,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "current_input_ref",
                table: "workflow_sagas");
        }
    }
}
