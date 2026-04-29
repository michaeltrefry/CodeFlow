using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveAssistantConversationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_conversations_user_id_scope_key",
                table: "assistant_conversations");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_conversations_user_id_scope_key_updated_at",
                table: "assistant_conversations",
                columns: new[] { "user_id", "scope_key", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assistant_conversations_user_id_scope_key_updated_at",
                table: "assistant_conversations");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_conversations_user_id_scope_key",
                table: "assistant_conversations",
                columns: new[] { "user_id", "scope_key" },
                unique: true);
        }
    }
}
