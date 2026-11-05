using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class WorkTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_activity_samples",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_seconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_activity_samples", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_activity_samples_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_time_settings",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    utc_offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    shift_start_minutes = table.Column<int>(type: "integer", nullable: false),
                    shift_end_minutes = table.Column<int>(type: "integer", nullable: false),
                    idle_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    break_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_work_time_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_work_time_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_activity_samples_device_sampled",
                schema: "endpoint_platform",
                table: "device_activity_samples",
                columns: new[] { "device_id", "sampled_at" });

            migrationBuilder.CreateIndex(
                name: "ux_device_activity_samples_device_user_minute",
                schema: "endpoint_platform",
                table: "device_activity_samples",
                columns: new[] { "device_id", "user_name", "sampled_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_work_time_settings_organization_id",
                schema: "endpoint_platform",
                table: "work_time_settings",
                column: "organization_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_activity_samples",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "work_time_settings",
                schema: "endpoint_platform");
        }
    }
}
