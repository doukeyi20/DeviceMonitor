using DeviceMonitor.Core.Models;
using DeviceMonitor.Core.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

// 只使用本次运行创建的临时数据库，不启动正式程序、不访问 AppData 的正式库。
internal static class Program
{
    private static int _checks;

    private static async Task<int> Main()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "ef-checks.db");
        try
        {
            Check(!File.Exists(path), "从空白路径开始首次迁移");
            new DatabaseInitializer(path).Initialize();
            Check(File.Exists(path), "Migrate 创建数据库文件");
            var users = new UserStore(path);
            var devices = new DeviceStore(path);
            var history = new HistoryStore(path);
            var alarms = new AlarmStore(path);
            Check(!users.HasAnyUser(), "新库无账户");
            Check(users.TryCreateFirstAdministrator("Admin", "Admin123"), "创建首次管理员");
            Check(!users.TryCreateFirstAdministrator("Other", "Admin123"), "已有账户不再创建首次管理员");
            var admin = users.CheckLogin(" admin ", "Admin123")!;
            Check(admin is not null && admin.Role == UserRole.Administrator, "登录保留 NOCASE 和 Trim");
            Check(users.CheckLogin("Admin", "wrong") is null, "错误密码拒绝登录");
            Check(users.TryCreateOperator(admin!, "Worker", "Worker123"), "创建操作员");
            Check(!users.TryCreateOperator(admin!, "worker", "Worker123"), "用户名大小写重复返回 false");
            var worker = users.CheckLogin("Worker", "Worker123")!;
            Check(users.TrySetOperatorEnabled(admin!, worker.Id, false), "停用操作员");
            Check(users.CheckLogin("Worker", "Worker123") is null, "停用账户不能登录");
            Check(users.TrySetOperatorEnabled(admin!, worker.Id, true), "重新启用操作员");
            Check(!users.TrySetOperatorEnabled(admin!, worker.Id, true), "重复启用返回 false");
            Check(!users.TrySetOperatorEnabled(admin!, admin!.Id, false), "操作员入口不能停用管理员");
            Check(users.TryResetOperatorPassword(admin!, worker.Id, "Reset123"), "EF UPDATE 重置密码");
            Check(users.CheckLogin("Worker", "Worker123") is null && users.CheckLogin("Worker", "Reset123") is not null,
                "重置后新旧密码正确生效");
            Check(users.TryChangePassword(worker, "Reset123", "Changed123"), "BLOB 条件匹配后修改自己的密码");
            Check(users.GetUsers(admin!).Count == 2, "账户列表投影返回原来的 UserAccount");
            Check(users.CheckLogin("Worker", "Changed123") is not null, "新密码可登录");
            Check(!users.TryChangePassword(worker, "Reset123", "Again123"), "旧密码已作废，不覆盖新密码");
            Throws<UnauthorizedAccessException>(() => users.GetUsers(worker), "用户管理仍检查权限");

            Check(devices.TryAddDevice(admin!, Input("Device'One")), "带单引号的设备名正确保存");
            Check(!devices.TryAddDevice(admin!, Input("device'one")), "设备名仍按 NOCASE 判重");
            var device = devices.GetDevices(admin!).Single();
            Check(!device.IsEnabled && devices.GetDevices(admin!, true).Count == 0, "新设备默认停用");
            Check(devices.TrySetDeviceEnabled(admin!, device.Id, true), "启用设备");
            Check(devices.GetDevice(admin!, device.Id, true) is not null, "按编号和启用条件查询");
            Check(devices.TryUpdateDevice(admin!, device.Id, Input("Device'One")), "未改变值的编辑仍表示找到并保存该设备");
            Throws<UnauthorizedAccessException>(() => devices.TryAddDevice(worker, Input("Denied")), "写设备仍检查权限");

            var pointInput = new PointInput { Name = "Humidity", RegisterType = RegisterType.Holding, DataType = PointDataType.UInt16 };
            Check(devices.TryAddPoint(admin!, device.Id, pointInput), "新增点位");
            Check(!devices.TryAddPoint(admin!, device.Id, new PointInput { Name = "humidity", RegisterType = RegisterType.Holding }),
                "同设备点位名仍忽略 ASCII 大小写判重");
            var point = devices.GetPoints(admin!, device.Id).Single();
            Check(point.LowerLimit is null && point.UpperLimit is null, "空上下限不是 0");
            Check(devices.TryUpdatePoint(admin!, device.Id, point.Id, pointInput), "点位同值编辑返回 true");
            Check(!devices.TryUpdatePoint(admin!, device.Id + 999, point.Id, pointInput), "修改点位同时核对所属设备");
            Check(devices.TryAddPoint(admin!, device.Id, new PointInput { Name = "Temporary", RegisterType = RegisterType.Holding }), "新增临时点");
            long removedPointId = devices.GetPoints(admin!, device.Id).Single(x => x.Name == "Temporary").Id;
            Check(devices.TryDeletePoint(admin!, device.Id, removedPointId), "删除临时点");
            Check(devices.TryAddPoint(admin!, device.Id, new PointInput { Name = "Next", RegisterType = RegisterType.Holding }), "删除后新增另一点");
            Check(devices.GetPoints(admin!, device.Id).Single(x => x.Name == "Next").Id > removedPointId, "读取 sqlite_sequence，空历史时也不复用点号");

            DateTimeOffset time = DateTimeOffset.Parse("2026-10-16T01:02:03.1234567+00:00", CultureInfo.InvariantCulture);
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // 模拟切换 EF 之前已保存的原版时间文字和历史记录。
                command.CommandText = "INSERT INTO HistoryRecords (Id, DeviceId, DeviceName, PointId, PointName, Value, ReadAtUtc) VALUES (50,$d,'OldName',$p,'Humidity',100,$t)";
                command.Parameters.AddWithValue("$d", device.Id);
                command.Parameters.AddWithValue("$p", point.Id);
                command.Parameters.AddWithValue("$t", time.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
            history.SaveRecord(worker, new(device.Id, "OldName", point.Id, "Humidity", 200, time.AddSeconds(1)));
            history.SaveRecord(worker, new(device.Id, "OldName", point.Id, "Humidity", 300, time.AddSeconds(1)));
            history.SaveRecord(worker, new(device.Id, "OldName", removedPointId, "Temperature", -97, time.AddSeconds(2)));
            var page = history.GetPage(admin!, new(), pageSize: 2);
            Check(page.TotalRecords == 4 && page.Records.Count == 2, "EF 在数据库统计并分页");
            Check(page.Records[0].Value == -97 && page.Records[1].Value == 300, "按时间和编号倒序，保留负数");
            history.SaveRecord(worker, new(device.Id, "NewName", point.Id, "Humidity", 400, time.AddSeconds(3)));
            var oldPage = history.GetPage(admin!, new(), 2, 2, page.SnapshotId);
            Check(oldPage.TotalRecords == 4 && oldPage.Records.Select(x => x.Value).SequenceEqual(new[] { 200, 100 }), "翻页排除后来新增读数");
            var range = history.GetPage(admin!, new HistoryFilter(startUtc: time.ToOffset(TimeSpan.FromHours(8)), endUtcExclusive: time.AddSeconds(2)));
            Check(range.TotalRecords == 3, "UTC 范围筛选，起点包含、终点不包含");
            Check(range.Records.Single(x => x.Value == 100).ReadAt == time, "EF 正确读取原有 O 格式时间，保留小数秒");
            history.SaveRecord(worker, new(device.Id, "NewName", point.Id, "Humidity%_A", 500, time.AddSeconds(4)));
            Check(history.GetPage(admin!, new HistoryFilter(pointName: "%_")).TotalRecords == 1, "Contains 仍是字面包含，不把百分号当通配符");
            Check(history.GetPage(admin!, new HistoryFilter(pointName: "humidity")).TotalRecords == 0, "历史点名包含保留大小写规则");
            Check(history.GetDeviceOptions(admin!).Single().DisplayName == $"NewName（#{device.Id}）", "历史设备选项取最后一条名称");
            var fresh = history.GetPage(admin!, new());
            var trend = history.GetPointTrend(admin!, device.Id, point.Id, new(), fresh.SnapshotId, 2);
            Check(trend.TotalRecords == 5 && trend.Records.Count == 2 && trend.IsTruncated, "趋势统计全部匹配，但仅取指定条数");
            Check(trend.Records[0].ReadAt <= trend.Records[1].ReadAt, "趋势按旧到新返回");
            var exported = new List<HistoryRecord>();
            while (true)
            {
                var batch = history.GetExportBatch(admin!, new(), fresh.SnapshotId, exported.LastOrDefault(), 2, default);
                if (batch.Count == 0) break;
                exported.AddRange(batch);
            }
            Check(exported.Count == 6 && exported.Select(x => x.Id).Distinct().Count() == 6, "历史导出分批，不漏同时间记录也不重复");

            var alarm = new AlarmRecord { DeviceId = device.Id, DeviceName = "AlarmOld", PointId = point.Id, PointName = "Humidity",
                Kind = AlarmKind.HighLimit, State = AlarmState.Active, Message = "超限", Value = 900, Limit = 500,
                StartedAt = time, LastSeenAt = time, Occurrences = 1 };
            alarms.SaveRecord(worker, alarm);
            long alarmId = alarms.GetRecentRecords(admin!).Single().Id;
            alarms.SaveRecord(worker, alarm with { DeviceName = "ShouldNotReplace", Value = 901, LastSeenAt = time.AddSeconds(1), Occurrences = 2 });
            var saved = alarms.GetRecentRecords(admin!).Single();
            Check(saved.Id == alarmId && saved.Value == 901 && saved.Occurrences == 2 && saved.DeviceName == "AlarmOld", "同事件只更新后续状态，保留编号与原名称");
            alarms.SaveRecord(worker, alarm with { State = AlarmState.Recovered, LastSeenAt = time.AddSeconds(1), EndedAt = time.AddSeconds(2), Occurrences = 2 });
            Check(alarms.GetRecentRecords(admin!).Single().EndedAt == time.AddSeconds(2), "报警恢复与可空结束时间还原");
            Throws<InvalidOperationException>(() => alarms.SaveRecord(worker, alarm with { PointId = point.Id + 999 }), "禁止复用事件编号覆盖其他点位");
            var failure = new AlarmRecord { DeviceId = device.Id, DeviceName = "AlarmNew", Kind = AlarmKind.AcquisitionFailure,
                State = AlarmState.Active, Message = "读取失败", StartedAt = time.AddSeconds(3), LastSeenAt = time.AddSeconds(3) };
            alarms.SaveRecord(worker, failure);
            Check(alarms.GetPage(admin!, new AlarmFilter(category: AlarmCategory.Value)).TotalRecords == 1, "报警数值类别筛选");
            Check(alarms.GetPage(admin!, new AlarmFilter(category: AlarmCategory.Acquisition)).Records.Single().PointId is null, "采集异常允许空点位、空数值");
            Check(alarms.GetPage(admin!, new AlarmFilter(startUtc: time, endUtcExclusive: time.AddSeconds(1))).TotalRecords == 1, "报警时间按首次发生筛选");
            Check(alarms.GetDeviceOptions(admin!).Single().DisplayName == $"AlarmNew（#{device.Id}）", "报警设备选项取最后一条名称");
            var alarmPage = alarms.GetPage(admin!, new(), pageSize: 1);
            alarms.SaveRecord(worker, failure with { EventId = Guid.NewGuid().ToString("N"), StartedAt = time.AddSeconds(5), LastSeenAt = time.AddSeconds(5) });
            Check(alarms.GetPage(admin!, new(), 2, 1, alarmPage.SnapshotId).TotalRecords == 2, "报警翻页保留查询批次");
            var alarmExport = new List<AlarmRecord>();
            while (true)
            {
                var batch = alarms.GetExportBatch(admin!, new(), alarmPage.SnapshotId, alarmExport.LastOrDefault(), 1, default);
                if (batch.Count == 0) break;
                alarmExport.AddRange(batch);
            }
            Check(alarmExport.Count == 2 && alarmExport.Select(x => x.Id).Distinct().Count() == 2, "报警分批导出保留批次且无重复");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Throws<OperationCanceledException>(() => history.GetExportBatch(admin!, new(), fresh.SnapshotId, null, 2, canceled.Token), "历史导出响应取消");
            Throws<OperationCanceledException>(() => alarms.GetExportBatch(admin!, new(), alarmPage.SnapshotId, null, 2, canceled.Token), "报警导出响应取消");

            var factory = new MonitorDbContextFactory(path);
            using (var db = factory.Create())
            {
                Check(db.Database.GetAppliedMigrations().Count() == 1 &&
                    db.Database.GetAppliedMigrations().Single().EndsWith("_InitialCreate"), "首次迁移已登记");
                Check(!db.Database.GetPendingMigrations().Any(), "没有尚未应用的迁移");
                Check(!db.Database.HasPendingModelChanges(), "模型快照与当前表配置一致");
                Check(db.Database.GenerateCreateScript().Contains("sqlite_sequence", StringComparison.OrdinalIgnoreCase) == false,
                    "迁移模型排除 SQLite 自增系统表，不手工创建它");
                DateTime start = time.UtcDateTime;
                string sql = db.History.Where(x => x.ReadAtUtc >= start).OrderByDescending(x => x.ReadAtUtc).Skip(1).Take(2).ToQueryString();
                Check(sql.Contains("WHERE") && sql.Contains("LIMIT") && sql.Contains("OFFSET") && !sql.Contains("julianday"),
                    "时间条件和分页留在 SQL，未转换为全表内存查询");
                string longOffsetSql = db.History.OrderBy(x => x.Id).SkipRows((long)int.MaxValue + 3).Take(1).ToQueryString();
                Check(longOffsetSql.Contains("OFFSET"), "长偏移分页可以翻译成 SQL");
                Check(!db.History.OrderBy(x => x.Id).SkipRows((long)int.MaxValue + 3).Take(1).Any(), "长偏移执行不溢出为负数");
                Check(db.History.AsNoTracking().Any(x => x.Value == 100), "新 Context 能继续查询已有数据");
            }
            bool[] duplicates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => devices.TryAddDevice(admin!, Input("RaceName")))));
            Check(duplicates.Count(x => x) == 1, "同时新增同名设备只有一个成功");
            var writers = Enumerable.Range(0, 4).Select(workerIndex => Task.Run(() =>
            {
                for (int i = 0; i < 10; i++) history.SaveRecord(worker, new(device.Id, "Parallel", point.Id, "Humidity", i, time.AddSeconds(10 + workerIndex * 10 + i)));
            })).ToList();
            writers.Add(Task.Run(() =>
            {
                for (int i = 0; i < 10; i++)
                {
                    history.GetPage(admin!, new(), pageSize: 2);
                    alarms.SaveRecord(worker, failure);
                    alarms.GetPage(admin!, new(), pageSize: 2);
                }
            }));
            await Task.WhenAll(writers);
            Check(history.GetPage(admin!, new()).TotalRecords == 46, "不同任务各用自己的 Context，40 次写入无遗漏");
            Check(devices.TryDeleteDevice(admin!, device.Id), "设备和点位在同一事务删除");
            Check(devices.GetPoints(admin!, device.Id).Count == 0, "删除设备清理所属点位");
            Check(history.GetPage(admin!, new()).TotalRecords == 46 && alarms.GetPage(admin!, new()).TotalRecords == 3, "删除配置不删除历史和报警");
            Check(devices.TryAddDevice(admin!, Input("AfterDelete")), "删除后可以新增设备");
            Check(devices.GetDevices(admin!).Single(x => x.Name == "AfterDelete").Id > device.Id, "新设备编号避开历史与报警旧编号");
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "不再使用旧的 user_version 结构版本");
                command.CommandText = "SELECT ReadAtUtc FROM HistoryRecords WHERE Value = 500";
                Check((string)command.ExecuteScalar()! == time.AddSeconds(4).ToString("O", CultureInfo.InvariantCulture), "EF 新写时间沿用原格式");
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "EF 迁移历史表已创建");
                command.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "首次迁移只登记一次");
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Users','Devices','DevicePoints','HistoryRecords','AlarmRecords')";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 5, "首次迁移一次创建五张业务表");
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_Devices_RtuStation','IX_HistoryRecords_DeviceId_ReadAtUtc','IX_HistoryRecords_ReadAtUtc_Id','IX_AlarmRecords_StartedAtUtc_Id','IX_AlarmRecords_DeviceId_StartedAtUtc_Id')";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 5, "RTU 唯一索引和历史分页索引齐全");
            }
            new DatabaseInitializer(path).Initialize();
            Check(users.CheckLogin("Admin", "Admin123") is not null && history.GetPage(admin!, new()).TotalRecords == 46,
                "重新初始化不清空已有账户和历史");
            using (var db = factory.Create())
                Check(db.Database.GetAppliedMigrations().Count() == 1, "重复启动不重复执行首次迁移");
            Console.WriteLine($"ALL {_checks} EF PERSISTENCE CHECKS PASSED\nTemporary database: {path}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static DeviceInput Input(string name) => new()
    {
        Name = name, Host = "127.0.0.1", Port = 502, UnitId = 1,
        SamplingIntervalMilliseconds = 100, ConnectTimeoutMilliseconds = 1000,
        ReadTimeoutMilliseconds = 1000, WriteTimeoutMilliseconds = 1000
    };

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {++_checks}: {description}");
    }

    private static void Throws<T>(Action action, string description) where T : Exception
    {
        try { action(); } catch (T) { Check(true, description); return; }
        throw new InvalidOperationException("没有出现预期异常：" + description);
    }
}
