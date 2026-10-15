using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Announcements: short plain-text notices an administrator broadcasts to
    /// device screens over the existing task channel.
    /// </summary>
    /// <remarks>
    /// Two tables. <c>announcements</c> is the send: what was said, whether it
    /// was aimed at a group or an explicit device selection, and who sent it;
    /// the group reference is a bare column on purpose, so the history outlives
    /// the group it once named. <c>announcement_devices</c> is one row per
    /// targeted device, carrying either the <c>ShowAnnouncement</c> task that
    /// delivers the notice or the reason nothing was queued (retired, or an
    /// agent predating the display executor); the task id is a plain value, not
    /// a foreign key, because this history outlives the tasks too.
    /// </remarks>
    public partial class Announcements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "announcements",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    target_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_announcements", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "announcement_devices",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    announcement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    skip_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_announcement_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_announcement_devices_announcements_announcement_id",
                        column: x => x.announcement_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "announcements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_announcement_devices_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_announcement_devices_device_id",
                schema: "endpoint_platform",
                table: "announcement_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_announcement_devices_announcement_id_device_id",
                schema: "endpoint_platform",
                table: "announcement_devices",
                columns: new[] { "announcement_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_announcements_organization_id_created_at",
                schema: "endpoint_platform",
                table: "announcements",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "announcement_devices",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "announcements",
                schema: "endpoint_platform");
        }
    }
}
