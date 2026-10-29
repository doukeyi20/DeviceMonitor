// 首次建库建表的 EF 迁移；只在未执行过时应用，不清空已迁移的库。
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeviceMonitor.Core.Migrations
{
    /// <summary>
    /// 首次完整建库迁移，由 EF 根据实体和映射生成。
    /// 以后增加表或字段要生成新迁移，不直接修改已经应用过的这份文件。
    /// </summary>
    public partial class InitialCreate : Migration
    {
        /// <summary>应用首次迁移时执行：建五张业务表和索引，不预置账户或设备。</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 报警历史保存当时名称和事件状态，不与配置表建立删除关联。
            migrationBuilder.CreateTable(
                name: "AlarmRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    PointId = table.Column<long>(type: "INTEGER", nullable: true),
                    PointName = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: true),
                    LimitValue = table.Column<int>(type: "INTEGER", nullable: true),
                    StartedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Occurrences = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlarmRecords", x => x.Id);
                    table.CheckConstraint("CK_AlarmRecords_DeviceId", "DeviceId > 0");
                    table.CheckConstraint("CK_AlarmRecords_Kind", "Kind IN (1, 2, 3)");
                    table.CheckConstraint("CK_AlarmRecords_Occurrences", "Occurrences > 0");
                    table.CheckConstraint("CK_AlarmRecords_PointId", "PointId IS NULL OR PointId > 0");
                    table.CheckConstraint("CK_AlarmRecords_State", "State IN (1, 2, 3)");
                });

            // 设备连接配置，一次包含 TCP / RTU 和全部采集参数，不再逐版补列。
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitId = table.Column<int>(type: "INTEGER", nullable: false),
                    SamplingIntervalMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectTimeoutMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadTimeoutMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    WriteTimeoutMilliseconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CommunicationMode = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SerialPortName = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "", collation: "NOCASE"),
                    BaudRate = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 4800),
                    Parity = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    StopBits = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.CheckConstraint("CK_Devices_BaudRate", "BaudRate > 0");
                    table.CheckConstraint("CK_Devices_CommunicationMode", "CommunicationMode IN (0, 1)");
                    table.CheckConstraint("CK_Devices_ConnectTimeout", "ConnectTimeoutMilliseconds > 0");
                    table.CheckConstraint("CK_Devices_IsEnabled", "IsEnabled IN (0, 1)");
                    table.CheckConstraint("CK_Devices_Parity", "Parity IN (0, 1, 2)");
                    table.CheckConstraint("CK_Devices_Port", "Port BETWEEN 1 AND 65535");
                    table.CheckConstraint("CK_Devices_ReadTimeout", "ReadTimeoutMilliseconds > 0");
                    table.CheckConstraint("CK_Devices_SamplingInterval", "SamplingIntervalMilliseconds > 0");
                    table.CheckConstraint("CK_Devices_StopBits", "StopBits IN (1, 2)");
                    table.CheckConstraint("CK_Devices_UnitId", "UnitId BETWEEN 0 AND 255");
                    table.CheckConstraint("CK_Devices_WriteTimeout", "WriteTimeoutMilliseconds > 0");
                });

            // 成功读数历史；时间按统一的 UTC 文字格式保存。
            migrationBuilder.CreateTable(
                name: "HistoryRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<long>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                    PointId = table.Column<long>(type: "INTEGER", nullable: false),
                    PointName = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryRecords", x => x.Id);
                    table.CheckConstraint("CK_HistoryRecords_DeviceId", "DeviceId > 0");
                    table.CheckConstraint("CK_HistoryRecords_PointId", "PointId > 0");
                });

            // 只建账户表，不写默认密码；首次管理员仍由登录页创建。
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PasswordSalt = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PasswordIterations = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_Users_IsEnabled", "IsEnabled IN (0, 1)");
                    table.CheckConstraint("CK_Users_PasswordIterations", "PasswordIterations > 0");
                    table.CheckConstraint("CK_Users_Role", "Role IN (0, 1)");
                });

            // 每个点必须属于已有设备；点号使用自增，设备删除前先由 Store 删除点位。
            migrationBuilder.CreateTable(
                name: "DevicePoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    RegisterType = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<int>(type: "INTEGER", nullable: false),
                    DataType = table.Column<int>(type: "INTEGER", nullable: false),
                    LowerLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    UpperLimit = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePoints", x => x.Id);
                    table.CheckConstraint("CK_DevicePoints_Address", "Address BETWEEN 0 AND 65535");
                    table.CheckConstraint("CK_DevicePoints_DataType", "DataType IN (0, 1)");
                    table.CheckConstraint("CK_DevicePoints_RegisterType", "RegisterType IN (3, 4)");
                    table.ForeignKey(
                        name: "FK_DevicePoints_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // 下面建立查询/分页索引和唯一约束；RTU 唯一约束只作用于 RTU 设备。
            migrationBuilder.CreateIndex(
                name: "IX_AlarmRecords_DeviceId_StartedAtUtc_Id",
                table: "AlarmRecords",
                columns: new[] { "DeviceId", "StartedAtUtc", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRecords_EventId",
                table: "AlarmRecords",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlarmRecords_StartedAtUtc_Id",
                table: "AlarmRecords",
                columns: new[] { "StartedAtUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePoints_DeviceId_Name",
                table: "DevicePoints",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Name",
                table: "Devices",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_RtuStation",
                table: "Devices",
                columns: new[] { "SerialPortName", "UnitId" },
                unique: true,
                filter: "CommunicationMode = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryRecords_DeviceId_ReadAtUtc",
                table: "HistoryRecords",
                columns: new[] { "DeviceId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryRecords_ReadAtUtc_Id",
                table: "HistoryRecords",
                columns: new[] { "ReadAtUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <summary>回退首次迁移会删除这些表及其数据；正常启动的 Migrate 不调用这里。</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlarmRecords");

            migrationBuilder.DropTable(
                name: "DevicePoints");

            migrationBuilder.DropTable(
                name: "HistoryRecords");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Devices");
        }
    }
}
