using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// The Company Portal catalog: approved packages offered to end users for
    /// self-service install (<c>docs/company-portal.md</c>).
    /// </summary>
    /// <remarks>
    /// Three additive tables, nothing altered. <c>catalog_apps</c> references the
    /// package with RESTRICT so an offered package cannot be deleted from under
    /// the portal; <c>catalog_app_groups</c> links an entry to the device groups
    /// that may see it; <c>catalog_install_requests</c> is history and carries no
    /// foreign key to the app or the task, only to the device, so a request
    /// still reads the same after the entry is removed.
    /// </remarks>
    public partial class CatalogApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_apps",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    icon = table.Column<byte[]>(type: "bytea", nullable: true),
                    visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_published = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_by_display = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_apps", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_apps_software_packages_package_id",
                        column: x => x.package_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "software_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_install_requests",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    requested_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_install_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_install_requests_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_app_groups",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_group_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_app_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_catalog_app_groups_catalog_apps_catalog_app_id",
                        column: x => x.catalog_app_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "catalog_apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_catalog_app_groups_device_groups_device_group_id",
                        column: x => x.device_group_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_app_groups_app_id_group_id",
                schema: "endpoint_platform",
                table: "catalog_app_groups",
                columns: new[] { "catalog_app_id", "device_group_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_app_groups_device_group_id",
                schema: "endpoint_platform",
                table: "catalog_app_groups",
                column: "device_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_apps_organization_id_package_id",
                schema: "endpoint_platform",
                table: "catalog_apps",
                columns: new[] { "organization_id", "package_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_apps_package_id",
                schema: "endpoint_platform",
                table: "catalog_apps",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_install_requests_device_id",
                schema: "endpoint_platform",
                table: "catalog_install_requests",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_catalog_install_requests_org_created",
                schema: "endpoint_platform",
                table: "catalog_install_requests",
                columns: new[] { "organization_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_app_groups",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "catalog_install_requests",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "catalog_apps",
                schema: "endpoint_platform");
        }
    }
}
