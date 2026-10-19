using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Screen locks: an administrator puts chosen devices or a group behind a
    /// persistent full-screen lock over the existing task channel, and lifts it
    /// again.
    /// </summary>
    /// <remarks>
    /// Additive. <c>organization_security_settings</c> holds the PBKDF2 verifier of
    /// the per-organization IT unlock code -- never the code itself -- that recovers
    /// a lock on the machine. <c>screen_locks</c> is the engage decision (what the
    /// locked screen says, a group or an explicit device selection, who did it); the
    /// group reference is a bare column on purpose, so the history outlives the group
    /// it once named. <c>screen_lock_devices</c> is one row per targeted device,
    /// carrying the <c>EngageScreenLock</c> task that locked it (and the
    /// <c>ReleaseScreenLock</c> task that lifted it) or the reason nothing was queued
    /// (retired, or an agent predating the lock executor); the task ids are plain
    /// values, not foreign keys, because this history outlives the tasks. Devices
    /// gain a nullable <c>screen_locked_reported</c>: the agent's own last word on
    /// whether it is locked, which the derived status uses so a local IT-unlock-code
    /// release shows through.
    /// </remarks>
    public partial class ScreenLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "screen_locked_reported",
                schema: "endpoint_platform",
                table: "devices",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_security_settings",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    screen_unlock_algo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    screen_unlock_iterations = table.Column<int>(type: "integer", nullable: false),
                    screen_unlock_salt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    screen_unlock_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_security_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_organization_security_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "screen_locks",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    contact_line = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screen_locks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "screen_lock_devices",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    screen_lock_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    engage_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    release_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    skip_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screen_lock_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_screen_lock_devices_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_screen_lock_devices_screen_locks_screen_lock_id",
                        column: x => x.screen_lock_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "screen_locks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_organization_security_settings_organization_id",
                schema: "endpoint_platform",
                table: "organization_security_settings",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_screen_lock_devices_device_id",
                schema: "endpoint_platform",
                table: "screen_lock_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_screen_lock_devices_screen_lock_id_device_id",
                schema: "endpoint_platform",
                table: "screen_lock_devices",
                columns: new[] { "screen_lock_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_screen_locks_organization_id_created_at",
                schema: "endpoint_platform",
                table: "screen_locks",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_security_settings",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "screen_lock_devices",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "screen_locks",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "screen_locked_reported",
                schema: "endpoint_platform",
                table: "devices");
        }
    }
}
