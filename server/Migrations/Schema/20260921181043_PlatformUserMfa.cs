using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Multi-factor authentication: the TOTP secret on each administrator, the
    /// single-use recovery codes, and the short-lived challenge issued between a
    /// correct password and a session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is backfilled, and the TOTP columns are all nullable.</b> Every
    /// existing administrator lands with no enrolment, which is the only safe
    /// state: a non-null default would describe accounts as holding a second
    /// factor that nobody can produce a code for, locking the console at the
    /// moment of deployment. Enrolment is mandatory, but it is enforced at
    /// sign-in - where the person can actually complete it - rather than by this
    /// migration.
    /// </para>
    /// <para>
    /// <b>The challenge is its own table rather than a flag on a session.</b>
    /// Reusing the session row would mean an attacker holding only a password
    /// holds a real bearer token whose uselessness depends on middleware staying
    /// correct. This row is not a credential for anything but attempting the
    /// second factor.
    /// </para>
    /// <para>
    /// Both tables cascade from <c>platform_users</c>: recovery codes and
    /// half-finished challenges are meaningless without the account, and orphans
    /// would outlive a deleted administrator.
    /// </para>
    /// <para>
    /// No grants are needed here. <c>RuntimeGrantsApplier</c> grants on ALL TABLES
    /// IN SCHEMA and sets ALTER DEFAULT PRIVILEGES, so new tables are covered both
    /// retroactively and prospectively.
    /// </para>
    /// </remarks>
    public partial class PlatformUserMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "totp_confirmed_at",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "totp_last_counter",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "totp_sealed_secret",
                schema: "endpoint_platform",
                table: "platform_users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_mfa_challenges",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_admin_mfa_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_admin_mfa_challenges_platform_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "platform_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_recovery_codes",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_recovery_codes_platform_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "platform_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_admin_mfa_challenges_expires_at",
                schema: "endpoint_platform",
                table: "admin_mfa_challenges",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_admin_mfa_challenges_token_hash",
                schema: "endpoint_platform",
                table: "admin_mfa_challenges",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_admin_mfa_challenges_user_id",
                schema: "endpoint_platform",
                table: "admin_mfa_challenges",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_recovery_codes_user_hash",
                schema: "endpoint_platform",
                table: "mfa_recovery_codes",
                columns: new[] { "user_id", "code_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_mfa_challenges",
                schema: "endpoint_platform");

            migrationBuilder.DropTable(
                name: "mfa_recovery_codes",
                schema: "endpoint_platform");

            migrationBuilder.DropColumn(
                name: "totp_confirmed_at",
                schema: "endpoint_platform",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "totp_last_counter",
                schema: "endpoint_platform",
                table: "platform_users");

            migrationBuilder.DropColumn(
                name: "totp_sealed_secret",
                schema: "endpoint_platform",
                table: "platform_users");
        }
    }
}
