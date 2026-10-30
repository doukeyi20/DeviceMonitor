using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class UseDatabaseGeneratedIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER")
                .Annotation("Sqlite:Autoincrement", true);

            // 只在升级时衔接旧编号。以后新增直接用自增，不再逐次扫描历史表。
            // 旧配置可能已删除，但历史和报警中的设备、点位编号不能再分给新配置。
            migrationBuilder.Sql("""
                INSERT INTO sqlite_sequence (name, seq)
                SELECT 'Devices', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'Devices');
                UPDATE sqlite_sequence SET seq = MAX(seq,
                    (SELECT COALESCE(MAX(Id), 0) FROM Devices),
                    (SELECT COALESCE(MAX(DeviceId), 0) FROM HistoryRecords),
                    (SELECT COALESCE(MAX(DeviceId), 0) FROM AlarmRecords)) WHERE name = 'Devices';

                INSERT INTO sqlite_sequence (name, seq)
                SELECT 'DevicePoints', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'DevicePoints');
                UPDATE sqlite_sequence SET seq = MAX(seq,
                    (SELECT COALESCE(MAX(Id), 0) FROM DevicePoints),
                    (SELECT COALESCE(MAX(PointId), 0) FROM HistoryRecords),
                    (SELECT COALESCE(MAX(PointId), 0) FROM AlarmRecords)) WHERE name = 'DevicePoints';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER")
                .OldAnnotation("Sqlite:Autoincrement", true);
        }
    }
}
