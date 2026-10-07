using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Restart Management: department-wide restarts the server holds until a
    /// chosen moment and then sends as ordinary restart tasks.
    /// </summary>
    /// <remarks>
    /// Three tables. <c>restart_schedules</c> is the plan: which group, when, with
    /// what warning, who asked, and whether it was dispatched, cancelled or
    /// missed; one Pending row per group is enforced by a partial unique index.
    /// <c>restart_schedule_exclusions</c> are the devices an administrator took
    /// out of a pending plan. <c>restart_schedule_devices</c> is what dispatch did
    /// to each member and, later, how its cancellation went; the live state of
    /// each restart is the task the row points at, so nothing here duplicates it.
    /// </remarks>
    public partial class RestartSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "restart_schedules",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    warning_seconds = table.Column<int>(type: "integer", nullable: false),
                    restart_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatch_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    missed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restart_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_restart_schedules_device_groups_device_group_id",
                        column: x => x.device_group_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restart_schedule_devices",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    dispatch_outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    restart_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cancel_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_requested_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restart_schedule_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_restart_schedule_devices_device_tasks_cancel_task_id",
                        column: x => x.cancel_task_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_restart_schedule_devices_device_tasks_restart_task_id",
                        column: x => x.restart_task_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_restart_schedule_devices_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_restart_schedule_devices_restart_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "restart_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restart_schedule_exclusions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    excluded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    excluded_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    excluded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restart_schedule_exclusions", x => x.id);
                    table.ForeignKey(
                        name: "fk_restart_schedule_exclusions_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_restart_schedule_exclusions_restart_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "restart_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedule_devices_cancel_task_id",
                schema: "endpoint_platform",
                table: "restart_schedule_devices",
                column: "cancel_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedule_devices_device_id",
                schema: "endpoint_platform",
                table: "restart_schedule_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedule_devices_restart_task_id",
                schema: "endpoint_platform",
                table: "restart_schedule_devices",
                column: "restart_task_id");

            migrationBuilder.CreateIndex(
                name: "ux_restart_schedule_devices_schedule_id_device_id",
                schema: "endpoint_platform",
                table: "restart_schedule_devices",
                columns: new[] { "schedule_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedule_exclusions_device_id",
                schema: "endpoint_platform",
                table: "restart_schedule_exclusions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_restart_schedule_exclusions_schedule_id_device_id",
                schema: "endpoint_platform",
                table: "restart_schedule_exclusions",
                columns: new[] { "schedule_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedules_device_group_id_created_at",
                schema: "endpoint_platform",
                table: "restart_schedules",
                columns: new[] { "device_group_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_restart_schedules_status_dispatch_at",
                schema: "endpoint_platform",
                table: "restart_schedules",
                columns: new[] { "status", "dispatch_at" });

            migrationBuilder.CreateIndex(
                name: "ux_restart_schedules_pending_per_group",
                schema: "endpoint_platform",
                table: "restart_schedules",
                column: "device_group_id",
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "restart_schedule_devices",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "restart_schedule_exclusions",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "restart_schedules",
                schema: "endpoint_platform");
        }
    }
}
