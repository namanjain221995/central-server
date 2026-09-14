using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EndpointPlatform.Migrations.Schema
{
    /// <inheritdoc />
    public partial class SingleActiveRestartPerDevice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_device_tasks_active_restart_per_device",
                schema: "endpoint_platform",
                table: "device_tasks",
                column: "device_id",
                unique: true,
                filter: "type = 'RestartDevice' AND status IN ('Queued', 'Delivered')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_device_tasks_active_restart_per_device",
                schema: "endpoint_platform",
                table: "device_tasks");
        }
    }
}
