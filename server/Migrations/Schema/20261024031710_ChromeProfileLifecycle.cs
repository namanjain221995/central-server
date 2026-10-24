using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Adds the two sign-in lifecycle facts to a Chrome profile: whether a Google
    /// account is signed in, and whether that sign-in is broken ("Sign-in paused").
    /// </summary>
    /// <remarks>
    /// Both nullable and additive: null means the agent did not say -- an agent
    /// older than the fields, or a Chrome entry predating the keys they are read
    /// from -- and is never collapsed into false. The profile's console status
    /// (active / dormant / notSignedIn / signInBroken) is deliberately NOT a
    /// column: it is derived at read time from these facts and the existing
    /// last-active instant, so a change of judgement is a redeploy rather than a
    /// data migration or a sweeper.
    /// </remarks>
    public partial class ChromeProfileLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sign_in_broken",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "signed_in",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sign_in_broken",
                schema: "endpoint_platform",
                table: "chrome_profiles");

            migrationBuilder.DropColumn(
                name: "signed_in",
                schema: "endpoint_platform",
                table: "chrome_profiles");
        }
    }
}
