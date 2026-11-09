using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeviceEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite 删除列会重建表，先记住已用过的自增编号。
            migrationBuilder.Sql("""
                CREATE TABLE __DeviceSequenceBackup AS
                SELECT COALESCE(MAX(seq), 0) AS LastId FROM sqlite_sequence WHERE name = 'Devices';
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Devices_IsEnabled",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "Devices");

            // EF 会在本次迁移最后重建 SQLite 表；下一次迁移再恢复自增编号。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Devices_IsEnabled",
                table: "Devices",
                sql: "IsEnabled IN (0, 1)");
        }
    }
}
