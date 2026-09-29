using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Records whether Chrome installed an extension as part of its own default
    /// setup (Chrome's <c>was_installed_by_default</c>).
    /// </summary>
    /// <remarks>
    /// Needed so the console can count extensions the way Chrome's own extensions
    /// menu does -- enabled, not built in, not default-installed -- after a real
    /// profile's menu showed three while the console showed more. Nullable and
    /// additive: rows written by older agents have none, and count as before.
    /// </remarks>
    public partial class ChromeExtensionInstalledByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "installed_by_default",
                schema: "endpoint_platform",
                table: "chrome_extensions",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "installed_by_default",
                schema: "endpoint_platform",
                table: "chrome_extensions");
        }
    }
}
