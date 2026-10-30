// 增加倍率、偏移、单位和小数读数的 EF 迁移；只在尚未执行时应用。
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceMonitor.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineeringValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 由启动时 Migrate 应用：整数列改为 REAL，原有数值保持不变。
            // 旧点位补倍率 1、偏移 0、空单位；不会自动除以 10，也不清空记录。
            migrationBuilder.AlterColumn<double>(
                name: "Value",
                table: "HistoryRecords",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "HistoryRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<double>(
                name: "UpperLimit",
                table: "DevicePoints",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "LowerLimit",
                table: "DevicePoints",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Offset",
                table: "DevicePoints",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "Scale",
                table: "DevicePoints",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "DevicePoints",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<double>(
                name: "Value",
                table: "AlarmRecords",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "LimitValue",
                table: "AlarmRecords",
                type: "REAL",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "AlarmRecords",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 这是回退步骤，不在正常启动时执行；回退会删除换算配置，不能保留完整的小数模型。
            migrationBuilder.DropColumn(
                name: "Unit",
                table: "HistoryRecords");

            migrationBuilder.DropColumn(
                name: "Offset",
                table: "DevicePoints");

            migrationBuilder.DropColumn(
                name: "Scale",
                table: "DevicePoints");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "DevicePoints");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "AlarmRecords");

            migrationBuilder.AlterColumn<int>(
                name: "Value",
                table: "HistoryRecords",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");

            migrationBuilder.AlterColumn<int>(
                name: "UpperLimit",
                table: "DevicePoints",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "LowerLimit",
                table: "DevicePoints",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Value",
                table: "AlarmRecords",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "LimitValue",
                table: "AlarmRecords",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "REAL",
                oldNullable: true);
        }
    }
}
