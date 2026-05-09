using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeFlow.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentRoleAssignmentVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Drop the old surrogate-id PK and the agent_key-rooted indexes.
            migrationBuilder.DropPrimaryKey(
                name: "PK_agent_role_assignments",
                table: "agent_role_assignments");

            migrationBuilder.DropIndex(
                name: "IX_agent_role_assignments_agent_key",
                table: "agent_role_assignments");

            migrationBuilder.DropIndex(
                name: "IX_agent_role_assignments_agent_key_role_id",
                table: "agent_role_assignments");

            migrationBuilder.DropColumn(
                name: "id",
                table: "agent_role_assignments");

            // 2. Add agent_version with a provisional default of 0 so the AddColumn fills the
            //    existing rows without a NULL pass. Existing rows become "placeholder" rows at
            //    agent_version=0 — we replicate them across every version of the agent below
            //    and then delete the placeholders. Default stays on the column for now: writers
            //    don't specify agent_version until AR-4, so the C#-side `default(int)` of 0
            //    needs a column-side default to round-trip cleanly.
            migrationBuilder.AddColumn<int>(
                name: "agent_version",
                table: "agent_role_assignments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // 3. Backfill: for every (agent_key, role_id) placeholder, replicate the row across
            //    every existing version of that agent_key in `agents`. Preserves current
            //    behavior — every historical trace continues to see the assignment that's live
            //    at migration time, regardless of the agent version it pinned.
            migrationBuilder.Sql(@"
                INSERT INTO agent_role_assignments (agent_key, agent_version, role_id, created_at)
                SELECT ara.agent_key, a.version, ara.role_id, ara.created_at
                FROM agent_role_assignments ara
                INNER JOIN agents a ON a.`key` = ara.agent_key
                WHERE ara.agent_version = 0
                  AND a.version <> 0;
            ");

            // 4. Delete the placeholder rows whose agent_key has at least one row in `agents`
            //    (i.e. those that were successfully replicated). Orphan assignments — where
            //    agent_key has no agents row — keep their version-0 row; they'd never be
            //    invoked anyway, and dropping them silently would change cleanup semantics.
            migrationBuilder.Sql(@"
                DELETE ara FROM agent_role_assignments ara
                INNER JOIN agents a ON a.`key` = ara.agent_key
                WHERE ara.agent_version = 0
                  AND a.version <> 0;
            ");

            // 5. Add the composite PK on (agent_key, agent_version, role_id).
            migrationBuilder.AddPrimaryKey(
                name: "PK_agent_role_assignments",
                table: "agent_role_assignments",
                columns: new[] { "agent_key", "agent_version", "role_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Drop the composite PK so we can collapse rows back to one per (agent_key, role_id).
            migrationBuilder.DropPrimaryKey(
                name: "PK_agent_role_assignments",
                table: "agent_role_assignments");

            // 2. Collapse: for each (agent_key, role_id), keep only the row at MAX(agent_version).
            //    Mirrors the "current assignment" semantics that the pre-migration schema modeled
            //    with a single row per (agent_key, role_id).
            migrationBuilder.Sql(@"
                DELETE ara FROM agent_role_assignments ara
                LEFT JOIN (
                    SELECT agent_key, role_id, MAX(agent_version) AS max_version
                    FROM agent_role_assignments
                    GROUP BY agent_key, role_id
                ) keepers
                  ON keepers.agent_key = ara.agent_key
                 AND keepers.role_id = ara.role_id
                 AND keepers.max_version = ara.agent_version
                WHERE keepers.agent_key IS NULL;
            ");

            migrationBuilder.DropColumn(
                name: "agent_version",
                table: "agent_role_assignments");

            migrationBuilder.AddColumn<long>(
                name: "id",
                table: "agent_role_assignments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_agent_role_assignments",
                table: "agent_role_assignments",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_assignments_agent_key",
                table: "agent_role_assignments",
                column: "agent_key");

            migrationBuilder.CreateIndex(
                name: "IX_agent_role_assignments_agent_key_role_id",
                table: "agent_role_assignments",
                columns: new[] { "agent_key", "role_id" },
                unique: true);
        }
    }
}
