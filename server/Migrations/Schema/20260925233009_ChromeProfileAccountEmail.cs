using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Adds the signed-in Google account's e-mail address to a Chrome profile.
    /// </summary>
    /// <remarks>
    /// A product decision, not an oversight being corrected: the first version
    /// deliberately carried only the person's name and the account's domain, and
    /// the administrators who use the console asked for the address itself,
    /// because it is what traces a profile to a person. Nullable and additive:
    /// rows written by agents that predate the field simply have none, and a
    /// profile nobody is signed in to has none either.
    /// </remarks>
    public partial class ChromeProfileAccountEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "account_email",
                schema: "endpoint_platform",
                table: "chrome_profiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "account_email",
                schema: "endpoint_platform",
                table: "chrome_profiles");
        }
    }
}
