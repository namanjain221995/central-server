using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <summary>
    /// Makes device groups exclusive: every device belongs to exactly one group,
    /// held as <c>devices.device_group_id</c>, with a built-in "All Devices" group
    /// per organization as the fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-written. The scaffold dropped <c>device_group_memberships</c> before
    /// anything read it -- destroying every existing membership -- and added the
    /// new column as NOT NULL defaulting to the empty GUID, which the foreign key
    /// would then refuse. The order here is: create the fallback groups, add the
    /// column nullable, copy memberships across, put everything else in the
    /// fallback, and only then make it required and drop the old table.
    /// </para>
    /// <para>
    /// <b>It refuses rather than guesses.</b> A device in more than one group, or
    /// two groups whose names differ only by case, cannot be mapped onto the new
    /// model without choosing which to lose -- and losing a group can silently
    /// drop a policy assignment or an administrator's authority. The migration
    /// stops with a message naming what to resolve, and changes nothing.
    /// </para>
    /// </remarks>
    public partial class ExclusiveDeviceGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- 1. Refuse data that has no single right answer. -----------------
            migrationBuilder.Sql("""
                DO $$
                DECLARE offenders text;
                BEGIN
                    SELECT string_agg(d.hostname || ' (' || c.groups || ' groups)', ', ')
                      INTO offenders
                      FROM (SELECT device_id, count(*) AS groups
                              FROM endpoint_platform.device_group_memberships
                             GROUP BY device_id
                            HAVING count(*) > 1) c
                      JOIN endpoint_platform.devices d ON d.id = c.device_id;

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION
                            'ExclusiveDeviceGroups: devices belong to more than one group: %. '
                            'A device may now belong to only one. Remove the extra memberships, '
                            'then run the migration again.', offenders;
                    END IF;

                    SELECT string_agg(DISTINCT g.name, ', ')
                      INTO offenders
                      FROM endpoint_platform.device_groups g
                      JOIN endpoint_platform.device_groups o
                        ON o.organization_id = g.organization_id
                       AND o.id <> g.id
                       AND lower(o.name) = lower(g.name);

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION
                            'ExclusiveDeviceGroups: group names now compare case-insensitively, and these '
                            'collide: %. Rename one of each pair, then run the migration again.', offenders;
                    END IF;

                    SELECT string_agg(name, ', ')
                      INTO offenders
                      FROM endpoint_platform.device_groups
                     WHERE lower(name) = 'all devices';

                    IF offenders IS NOT NULL THEN
                        RAISE EXCEPTION
                            'ExclusiveDeviceGroups: "All Devices" is now reserved for the built-in group, '
                            'and a group already uses it (%). Rename that group, then run the migration again.',
                            offenders;
                    END IF;
                END $$;
                """);

            // ---- 2. The built-in group, one per organization. -------------------
            migrationBuilder.AddColumn<bool>(
                name: "is_built_in",
                schema: "endpoint_platform",
                table: "device_groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                INSERT INTO endpoint_platform.device_groups
                    (id, organization_id, name, description, type, is_built_in, created_at, updated_at)
                SELECT gen_random_uuid(), o.id, 'All Devices',
                       'Every device that has not been placed in another group.',
                       'Static', true, now(), now()
                  FROM endpoint_platform.organizations o
                 WHERE NOT EXISTS (SELECT 1 FROM endpoint_platform.device_groups g
                                    WHERE g.organization_id = o.id AND g.is_built_in);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_device_groups_one_built_in_per_organization",
                schema: "endpoint_platform",
                table: "device_groups",
                column: "organization_id",
                unique: true,
                filter: "is_built_in");

            // ---- 3. Every device into exactly one group. ------------------------
            migrationBuilder.AddColumn<Guid>(
                name: "device_group_id",
                schema: "endpoint_platform",
                table: "devices",
                type: "uuid",
                nullable: true);

            // Existing memberships carry over as they are. Step 1 guaranteed at most
            // one per device, so this cannot pick between two.
            migrationBuilder.Sql("""
                UPDATE endpoint_platform.devices d
                   SET device_group_id = m.group_id
                  FROM endpoint_platform.device_group_memberships m
                 WHERE m.device_id = d.id;
                """);

            // Everything else -- including retired devices, which are still rows --
            // goes to its organization's "All Devices".
            migrationBuilder.Sql("""
                UPDATE endpoint_platform.devices d
                   SET device_group_id = g.id
                  FROM endpoint_platform.device_groups g
                 WHERE d.device_group_id IS NULL
                   AND g.organization_id = d.organization_id
                   AND g.is_built_in;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "device_group_id",
                schema: "endpoint_platform",
                table: "devices",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_device_group_id",
                schema: "endpoint_platform",
                table: "devices",
                column: "device_group_id");

            migrationBuilder.AddForeignKey(
                name: "fk_devices_device_groups_device_group_id",
                schema: "endpoint_platform",
                table: "devices",
                column: "device_group_id",
                principalSchema: "endpoint_platform",
                principalTable: "device_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ---- 4. Case-insensitive names. -------------------------------------
            // An expression index, which EF cannot model, so it is raw SQL; its name
            // is DeviceGroupConfiguration.UniqueNameIndex.
            migrationBuilder.DropIndex(
                name: "ix_device_groups_organization_id_name",
                schema: "endpoint_platform",
                table: "device_groups");

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_device_groups_organization_id_lower_name
                    ON endpoint_platform.device_groups (organization_id, lower(name));
                """);

            // ---- 5. Only now, with every membership copied, drop the old table. --
            migrationBuilder.DropTable(
                name: "device_group_memberships",
                schema: "endpoint_platform");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_group_memberships",
                schema: "endpoint_platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_group_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_group_memberships_device_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "device_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_group_memberships_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "endpoint_platform",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Custom-group memberships go back as rows. "All Devices" had no rows
            // before this migration -- a device in no group was simply absent from
            // the table -- so those are not recreated.
            migrationBuilder.Sql("""
                INSERT INTO endpoint_platform.device_group_memberships (id, device_id, group_id, created_at, updated_at)
                SELECT gen_random_uuid(), d.id, d.device_group_id, now(), now()
                  FROM endpoint_platform.devices d
                  JOIN endpoint_platform.device_groups g ON g.id = d.device_group_id
                 WHERE NOT g.is_built_in;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_device_group_memberships_device_id",
                schema: "endpoint_platform",
                table: "device_group_memberships",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_group_memberships_group_device",
                schema: "endpoint_platform",
                table: "device_group_memberships",
                columns: new[] { "group_id", "device_id" },
                unique: true);

            migrationBuilder.DropForeignKey(
                name: "fk_devices_device_groups_device_group_id",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "ix_devices_device_group_id",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "device_group_id",
                schema: "endpoint_platform",
                table: "devices");

            migrationBuilder.Sql("DROP INDEX endpoint_platform.ux_device_groups_organization_id_lower_name;");

            migrationBuilder.DropIndex(
                name: "ux_device_groups_one_built_in_per_organization",
                schema: "endpoint_platform",
                table: "device_groups");

            // The built-in groups did not exist before. Scope rows naming them
            // cascade away with them, which is the pre-migration state: nobody could
            // have been scoped to a group that did not exist.
            migrationBuilder.Sql("DELETE FROM endpoint_platform.device_groups WHERE is_built_in;");

            migrationBuilder.DropColumn(
                name: "is_built_in",
                schema: "endpoint_platform",
                table: "device_groups");

            migrationBuilder.CreateIndex(
                name: "ix_device_groups_organization_id_name",
                schema: "endpoint_platform",
                table: "device_groups",
                columns: new[] { "organization_id", "name" },
                unique: true);
        }
    }
}
