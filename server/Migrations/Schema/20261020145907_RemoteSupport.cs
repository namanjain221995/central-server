using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class RemoteSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "remote_support_id",
                schema: "endpoint_platform",
                table: "devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "remote_support_enrollments",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sealed_password = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    configured_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configured_by_display = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    configured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revealed_count = table.Column<int>(type: "integer", nullable: false),
                    last_revealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_revealed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_remote_support_enrollments", x => x.id);
                    table.ForeignKey(
                        name: "fk_remote_support_enrollments_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_remote_support_enrollments_device_id",
                schema: "endpoint_platform",
                table: "remote_support_enrollments",
                column: "device_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_support_enrollments",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "remote_support_id",
                schema: "endpoint_platform",
                table: "devices");
        }
    }
}
