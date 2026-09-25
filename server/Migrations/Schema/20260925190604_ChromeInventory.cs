using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Stores what the agent reports about Google Chrome: the installation, each
    /// Windows user's profiles, and the extensions in each profile. Chrome
    /// Management phase 3 (server ingestion and read-only Admin API).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three tables rather than one, because the three facts have different
    /// lifetimes. <c>chrome_installations</c> is one row per device, upserted, and
    /// never deleted: a device with no row has never reported the section (an agent
    /// older than it), while a <c>NotInstalled</c> row is a device that reported and
    /// said no. <c>chrome_profiles</c> and <c>chrome_extensions</c> are replaced
    /// wholesale per complete upload, and left alone when the agent reports
    /// <c>Error</c>, so a partial enumeration never overwrites a complete one.
    /// </para>
    /// <para>
    /// The two enum columns are text, not ordinals, so reordering the enums can
    /// never reinterpret history. Every string column's width is the wire contract's
    /// limit, which the Agent API and the entities both enforce.
    /// </para>
    /// <para>
    /// <c>ix_chrome_extensions_extension_id</c> backs the fleet-wide question the
    /// feature exists to answer: which devices have extension X. The unique indexes
    /// state each row's identity -- one installation per device, one profile per
    /// (device, Windows SID, profile directory), one extension per (profile, id) --
    /// so a concurrent upload cannot duplicate what ingestion dedupes in memory.
    /// </para>
    /// <para>
    /// There is no membership or grouping table here on purpose. Which group a
    /// device is in is <c>devices.device_group_id</c>, and Chrome reads resolve
    /// group membership through it exactly as the Groups page does.
    /// </para>
    /// <para>
    /// Additive only: new tables, nothing altered, no data touched. No column here
    /// can hold a Google account e-mail address or any credential; the wire contract
    /// has no field for one.
    /// </para>
    /// </remarks>
    public partial class ChromeInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chrome_installations",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    executable_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    architecture = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    installation_scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    installed_for_user = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    updater_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_update_check = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chrome_installations", x => x.id);
                    table.ForeignKey(
                        name: "fk_chrome_installations_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chrome_profiles",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chrome_installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_sid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    user_account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    profile_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    profile_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    profile_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    is_managed = table.Column<bool>(type: "boolean", nullable: true),
                    last_active_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chrome_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_chrome_profiles_chrome_installations_chrome_installation_id",
                        column: x => x.chrome_installation_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "chrome_installations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chrome_profiles_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chrome_extensions",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chrome_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    extension_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    manifest_version = table.Column<int>(type: "integer", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: true),
                    install_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_managed = table.Column<bool>(type: "boolean", nullable: false),
                    from_web_store = table.Column<bool>(type: "boolean", nullable: true),
                    update_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    extension_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    collected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chrome_extensions", x => x.id);
                    table.ForeignKey(
                        name: "fk_chrome_extensions_chrome_profiles_chrome_profile_id",
                        column: x => x.chrome_profile_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "chrome_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chrome_extensions_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chrome_extensions_device_id",
                schema: "endpoint_platform",
                table: "chrome_extensions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_chrome_extensions_extension_id",
                schema: "endpoint_platform",
                table: "chrome_extensions",
                column: "extension_id");

            migrationBuilder.CreateIndex(
                name: "ux_chrome_extensions_profile_extension",
                schema: "endpoint_platform",
                table: "chrome_extensions",
                columns: new[] { "chrome_profile_id", "extension_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_chrome_installations_device_id",
                schema: "endpoint_platform",
                table: "chrome_installations",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chrome_profiles_chrome_installation_id",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                column: "chrome_installation_id");

            migrationBuilder.CreateIndex(
                name: "ix_chrome_profiles_device_id",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ux_chrome_profiles_device_user_profile",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                columns: new[] { "device_id", "user_sid", "profile_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chrome_extensions",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "chrome_profiles",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "chrome_installations",
                schema: "endpoint_platform");
        }
    }
}
