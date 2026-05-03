using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "repositories_json",
                table: "workflow_sagas",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "repositories_json",
                table: "workflow_sagas");
        }
    }
}
