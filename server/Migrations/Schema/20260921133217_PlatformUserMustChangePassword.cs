using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Marks an administrator who must replace a server-generated password before
    /// the account can be used for anything else.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT backfilled. Every existing administrator chose their own
    /// password, so the column defaults to false and nobody is forced into a change
    /// screen by the deployment that adds it. A default of true would lock every
    /// current administrator out of the console simultaneously.
    /// </remarks>
    public partial class PlatformUserMustChangePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "endpoint_platform",
                table: "platform_users");
        }
    }
}
