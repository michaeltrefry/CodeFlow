using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewLoopSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "parent_review_max_rounds",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parent_review_round",
                table: "workflow_sagas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "review_max_rounds",
                table: "workflow_nodes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "parent_review_max_rounds",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "parent_review_round",
                table: "workflow_sagas");

            migrationBuilder.DropColumn(
                name: "review_max_rounds",
                table: "workflow_nodes");
        }
    }
}
