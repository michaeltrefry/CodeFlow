using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistantConversationCompaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "compacted_through_sequence",
                table: "assistant_conversations",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compacted_through_sequence",
                table: "assistant_conversations");
        }
    }
}
