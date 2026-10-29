using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class SecurityUpdateOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_security_update_install_records",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    update_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_result_code = table.Column<int>(type: "integer", nullable: true),
                    last_hresult = table.Column<int>(type: "integer", nullable: true),
                    last_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_security_update_install_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_security_update_install_records_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_security_update_suppressions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    update_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_security_update_suppressions", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_security_update_suppressions_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_security_update_install_records_device_status",
                schema: "endpoint_platform",
                table: "device_security_update_install_records",
                columns: new[] { "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_device_security_update_install_records_device_update",
                schema: "endpoint_platform",
                table: "device_security_update_install_records",
                columns: new[] { "device_id", "update_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_device_security_update_suppressions_device_update",
                schema: "endpoint_platform",
                table: "device_security_update_suppressions",
                columns: new[] { "device_id", "update_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_security_update_install_records",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "device_security_update_suppressions",
                schema: "endpoint_platform");
        }
    }
}
