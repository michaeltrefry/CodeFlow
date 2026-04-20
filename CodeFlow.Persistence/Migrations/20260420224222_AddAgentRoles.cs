using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_roles",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    display_name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "text", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    created_by = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    is_archived = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_roles", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_role_assignments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    agent_key = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    role_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_role_assignments_agent_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "agent_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_role_tool_grants",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    role_id = table.Column<long>(type: "bigint", nullable: false),
                    category = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    tool_identifier = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_role_tool_grants", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_role_tool_grants_agent_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "agent_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_assignments_agent_key",
                table: "agent_role_assignments",
                column: "agent_key");

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_assignments_agent_key_role_id",
                table: "agent_role_assignments",
                columns: new[] { "agent_key", "role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_assignments_role_id",
                table: "agent_role_assignments",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_tool_grants_role_id_category_tool_identifier",
                table: "agent_role_tool_grants",
                columns: new[] { "role_id", "category", "tool_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_roles_is_archived",
                table: "agent_roles",
                column: "is_archived");

            migrationBuilder.CreateIndex(
                name: "IX_agent_roles_key",
                table: "agent_roles",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_role_assignments");

            migrationBuilder.DropTable(
                name: "agent_role_tool_grants");

            migrationBuilder.DropTable(
                name: "agent_roles");
        }
    }
}
