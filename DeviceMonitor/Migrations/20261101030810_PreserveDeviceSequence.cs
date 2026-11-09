using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class PreserveDeviceSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 上一次迁移的表重建已完成，此时恢复编号才不会被重建覆盖。
            migrationBuilder.Sql("""
                INSERT INTO sqlite_sequence (name, seq)
                SELECT 'Devices', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'Devices');
                UPDATE sqlite_sequence SET seq = MAX(seq,
                    (SELECT LastId FROM __DeviceSequenceBackup),
                    (SELECT COALESCE(MAX(DeviceId), 0) FROM HistoryRecords),
                    (SELECT COALESCE(MAX(DeviceId), 0) FROM AlarmRecords)) WHERE name = 'Devices';
                DROP TABLE __DeviceSequenceBackup;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
