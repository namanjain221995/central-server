using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Records when an administrator's most recent sign-in failure happened, so the
    /// failure counter can decay instead of ratcheting toward a permanent lockout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Closes a denial of service. Before this column the counter had no age, so it
    /// only ever moved toward locked: five failures locked the account for fifteen
    /// minutes and then left it at <c>Locked</c> with the count still at five, one
    /// failure away from re-locking. A single wrong password every fifteen minutes
    /// kept a named administrator locked out indefinitely, and the e-mail address
    /// needed to aim it is simply their username.
    /// </para>
    /// <para>
    /// <b>Nullable and deliberately NOT backfilled.</b> Null means "the counter is
    /// clear". Defaulting existing rows to the deployment timestamp would make every
    /// administrator look as though they had just failed a sign-in, which would keep
    /// stale counters alive for a full decay window rather than forgetting them -
    /// the opposite of what the column is for.
    /// </para>
    /// </remarks>
    public partial class PlatformUserFailedSignInDecay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_failed_sign_in_at",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_failed_sign_in_at",
                schema: "endpoint_platform",
                table: "platform_users");
        }
    }
}
