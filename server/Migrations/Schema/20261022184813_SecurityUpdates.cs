using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class SecurityUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_security_updates",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    update_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    kb_ids = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    is_preview = table.Column<bool>(type: "boolean", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_security_updates", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_security_updates_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_security_updates_device_id",
                schema: "endpoint_platform",
                table: "device_security_updates",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_device_security_updates_device_update",
                schema: "endpoint_platform",
                table: "device_security_updates",
                columns: new[] { "device_id", "update_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_security_updates",
                schema: "endpoint_platform");
        }
    }
}
