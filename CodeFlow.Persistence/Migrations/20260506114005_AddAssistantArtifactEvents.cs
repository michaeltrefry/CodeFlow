using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantArtifactEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_artifact_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    conversation_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    message_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<int>(type: "int", nullable: false),
                    name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    relative_path = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    snapshot_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    summary_json = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    superseded_by_event_id = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    expired_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_artifact_events", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_artifact_events_conversation_id_expired_at",
                table: "assistant_artifact_events",
                columns: new[] { "conversation_id", "expired_at" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_artifact_events_conversation_id_sequence",
                table: "assistant_artifact_events",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_artifact_events_snapshot_id",
                table: "assistant_artifact_events",
                column: "snapshot_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_artifact_events");
        }
    }
}
