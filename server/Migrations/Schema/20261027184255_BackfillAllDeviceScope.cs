using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Gives all-device scope back to every administrator created with none at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Between the 2026-08-18 device-scope migration and this one, account creation
    /// granted all-device scope only to new Super Administrators. Nothing in the
    /// product creates <c>admin_device_scopes</c> rows and there is no surface to
    /// grant scope afterwards, so every IT Administrator, Helpdesk and Auditor
    /// account created in that window was left with neither the flag nor a single
    /// row -- harmless while most routes ignored scope, an empty console once they
    /// all enforce it. The 2026-08-18 migration had already recorded the intended
    /// model by granting every existing account all-device scope: no
    /// where-restriction configured means every device; the role governs what.
    /// </para>
    /// <para>
    /// Only accounts with the flag false AND no scope rows are touched. An account
    /// that has rows was restricted deliberately and keeps exactly that
    /// restriction. Idempotent: once applied, nothing matches the predicate.
    /// </para>
    /// </remarks>
    public partial class BackfillAllDeviceScope : Migration
    {
        /// <summary>The exact statement, exposed so a test can pin which rows it touches.</summary>
        public const string BackfillSql = """
            UPDATE endpoint_platform.platform_users AS u
            SET has_all_device_scope = TRUE,
                updated_at = now()
            WHERE u.has_all_device_scope = FALSE
              AND NOT EXISTS (
                  SELECT 1
                  FROM endpoint_platform.admin_device_scopes AS s
                  WHERE s.platform_user_id = u.id);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately nothing. Which accounts this migration changed is not
            // recorded, and revoking the flag from every account without rows would
            // also strip it from accounts legitimately created with it. Migrations
            // here are forward-only; a restriction is applied by clearing the flag
            // and adding scope rows, not by rolling this back.
        }
    }
}
