// 验证项目功能及异常处理的回归检查。
using System.IO;
using DeviceMonitor.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

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
            CheckEngineeringValues(folder);
            CheckBatchPersistence(folder);
            CheckGeneratedIdsUpgrade(folder);
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
            foreach (string password in new[] { null!, "", "   ", "12345" })
            {
                Throws<ArgumentException>(() => users.TryCreateFirstAdministrator("InvalidAdmin", password),
                    "首次管理员的空白或过短密码由统一入口拒绝");
                Throws<ArgumentException>(() => users.TryCreateOperator(admin!, "InvalidWorker", password),
                    "操作员的空白或过短密码由统一入口拒绝");
            }
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
            Check(devices.GetPoints(admin!, device.Id).Single(x => x.Name == "Next").Id > removedPointId, "数据库自增，空历史时也不复用点号");

            DateTimeOffset time = DateTimeOffset.Parse("2026-10-16T01:02:03.1234567+00:00", CultureInfo.InvariantCulture);
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();

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
            Check(oldPage.TotalRecords == 4 && oldPage.Records.Select(x => x.Value).SequenceEqual(new double[] { 200, 100 }), "翻页排除后来新增读数");
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

            var alarm = new AlarmRecord
            {
                DeviceId = device.Id,
                DeviceName = "AlarmOld",
                PointId = point.Id,
                PointName = "Humidity",
                Kind = AlarmKind.HighLimit,
                State = AlarmState.Active,
                Message = "超限",
                Value = 900,
                Limit = 500,
                StartedAt = time,
                LastSeenAt = time,
                Occurrences = 1
            };
            alarms.SaveRecord(worker, alarm);
            long alarmId = alarms.GetRecentRecords(admin!).Single().Id;
            alarms.SaveRecord(worker, alarm with { DeviceName = "ShouldNotReplace", Value = 901, LastSeenAt = time.AddSeconds(1), Occurrences = 2 });
            var saved = alarms.GetRecentRecords(admin!).Single();
            Check(saved.Id == alarmId && saved.Value == 901 && saved.Occurrences == 2 && saved.DeviceName == "AlarmOld", "同事件只更新后续状态，保留编号与原名称");
            alarms.SaveRecord(worker, alarm with { State = AlarmState.Recovered, LastSeenAt = time.AddSeconds(1), EndedAt = time.AddSeconds(2), Occurrences = 2 });
            Check(alarms.GetRecentRecords(admin!).Single().EndedAt == time.AddSeconds(2), "报警恢复与可空结束时间还原");
            Throws<InvalidOperationException>(() => alarms.SaveRecord(worker, alarm with { PointId = point.Id + 999 }), "禁止复用事件编号覆盖其他点位");
            var failure = new AlarmRecord
            {
                DeviceId = device.Id,
                DeviceName = "AlarmNew",
                Kind = AlarmKind.AcquisitionFailure,
                State = AlarmState.Active,
                Message = "读取失败",
                StartedAt = time.AddSeconds(3),
                LastSeenAt = time.AddSeconds(3)
            };
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
                Check(db.Database.GetAppliedMigrations().Count() == 3 &&
                    db.Database.GetAppliedMigrations().First().EndsWith("_InitialCreate"), "建表、数值换算和自动编号迁移均已登记");
                Check(!db.Database.GetPendingMigrations().Any(), "没有尚未应用的迁移");
                Check(!db.Database.HasPendingModelChanges(), "模型快照与当前表配置一致");
                Check(db.Database.GenerateCreateScript().Contains("sqlite_sequence", StringComparison.OrdinalIgnoreCase) == false,
                    "迁移模型排除 SQLite 自增系统表，不手工创建它");
                DateTime start = time.UtcDateTime;
                string sql = db.History.Where(x => x.ReadAtUtc >= start).OrderByDescending(x => x.ReadAtUtc).Skip(1).Take(2).ToQueryString();
                Check(sql.Contains("WHERE") && sql.Contains("LIMIT") && sql.Contains("OFFSET") && !sql.Contains("julianday"),
                    "时间条件和分页留在 SQL，未转换为全表内存查询");
                Check(db.History.OrderBy(x => x.Id).Skip(1).Take(1).Count() == 1, "使用标准 LINQ Skip/Take 读取一页");
                bool overflowRejected = false;
                try { int offset = checked(int.MaxValue * db.History.Count()); }
                catch (OverflowException) { overflowRejected = true; }
                Check(overflowRejected, "超出普通分页整数范围时明确拒绝，不生成负偏移");
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
                Check(Convert.ToInt32(command.ExecuteScalar()) == 3, "三次迁移各登记一次");
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Users','Devices','DevicePoints','HistoryRecords','AlarmRecords')";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 5, "首次迁移一次创建五张业务表");
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('IX_Devices_RtuStation','IX_HistoryRecords_DeviceId_ReadAtUtc','IX_HistoryRecords_ReadAtUtc_Id','IX_AlarmRecords_StartedAtUtc_Id','IX_AlarmRecords_DeviceId_StartedAtUtc_Id')";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 5, "RTU 唯一索引和历史分页索引齐全");
            }
            new DatabaseInitializer(path).Initialize();
            Check(users.CheckLogin("Admin", "Admin123") is not null && history.GetPage(admin!, new()).TotalRecords == 46,
                "重新初始化不清空已有账户和历史");
            using (var db = factory.Create())
                Check(db.Database.GetAppliedMigrations().Count() == 3, "重复启动不重复执行已有迁移");
            Console.WriteLine($"ALL {_checks} EF PERSISTENCE CHECKS PASSED\nTemporary database: {path}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static void CheckGeneratedIdsUpgrade(string folder)
    {
        string path = Path.Combine(folder, "generated-ids-upgrade.db");
        var options = new DbContextOptionsBuilder<MonitorDbContext>()
            .UseSqlite(DatabaseInitializer.CreateConnectionString(path)).Options;
        using (var db = new MonitorDbContext(options))
        {
            // 先建立改动前的数据库，再用真实旧数据验证升级，而不是只测空库。
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            migrator.Migrate("20261017072905_AddEngineeringValues");
        }

        var users = new UserStore(path);
        users.TryCreateFirstAdministrator("ids-admin", "Admin123");
        var user = users.CheckLogin("ids-admin", "Admin123")!;
        var devices = new DeviceStore(path);
        devices.TryAddDevice(user, Input("保留设备"));
        var device = devices.GetDevices(user).Single();
        devices.TrySetDeviceEnabled(user, device.Id, true);
        devices.TryAddPoint(user, device.Id, new PointInput
        {
            Name = "保留点位", RegisterType = RegisterType.Holding, DataType = PointDataType.UInt16
        });

        var history = new HistoryStore(path);
        history.SaveRecord(user, new(700, "已删除设备", 900, "已删除点位", 23.5, DateTimeOffset.UtcNow));
        var alarms = new AlarmStore(path);
        alarms.SaveRecord(user, new AlarmRecord
        {
            DeviceId = 800, DeviceName = "旧报警设备", PointId = 1000, PointName = "旧报警点位",
            Kind = AlarmKind.HighLimit, State = AlarmState.Active, Message = "超限",
            Value = 30, Limit = 20, StartedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        });

        new DatabaseInitializer(path).Initialize();
        Check(users.CheckLogin("ids-admin", "Admin123") is not null
            && devices.GetPoints(user, device.Id).Single().Name == "保留点位",
            "自动编号升级保留原账户、设备和点位关系");
        Check(history.GetRecentRecords(user).Single().Value == 23.5
            && alarms.GetRecentRecords(user).Single().PointId == 1000,
            "自动编号升级不修改旧历史和报警");
        devices.TryAddDevice(user, Input("升级后设备"));
        devices.TryAddPoint(user, device.Id, new PointInput
        {
            Name = "升级后点位", RegisterType = RegisterType.Holding, DataType = PointDataType.UInt16
        });
        long addedDevice = devices.GetDevices(user).Single(x => x.Name == "升级后设备").Id;
        long addedPoint = devices.GetPoints(user, device.Id).Single(x => x.Name == "升级后点位").Id;
        Check(addedDevice > 800 && addedPoint > 1000, "升级后的新编号避开已删除配置的历史和报警编号");
        devices.TryDeleteDevice(user, addedDevice);
        devices.TryDeletePoint(user, device.Id, addedPoint);
        devices.TryAddDevice(user, Input("再次新增"));
        devices.TryAddPoint(user, device.Id, new PointInput
        {
            Name = "再次新增点位", RegisterType = RegisterType.Holding, DataType = PointDataType.UInt16
        });
        Check(devices.GetDevices(user).Single(x => x.Name == "再次新增").Id > addedDevice
            && devices.GetPoints(user, device.Id).Single(x => x.Name == "再次新增点位").Id > addedPoint,
            "自动编号不复用刚删除且没有历史的配置编号");
    }

    private static void CheckBatchPersistence(string folder)
    {
        string path = Path.Combine(folder, "batch-atomic.db");
        new DatabaseInitializer(path).Initialize();
        var user = new UserAccount(1, "batch-check", UserRole.Administrator, true);
        var history = new HistoryStore(path);
        var alarms = new AlarmStore(path);
        var now = DateTimeOffset.UtcNow;
        HistoryRecord[] records = [new(1, "设备", 1, "点1", 10, now), new(1, "设备", 2, "点2", 20, now)];
        Execute("CREATE TRIGGER fail_history BEFORE INSERT ON HistoryRecords WHEN NEW.PointId=2 BEGIN SELECT RAISE(ABORT,'test'); END;");
        Throws<DbUpdateException>(() => history.SaveRecords(user, records), "批量历史中途数据库失败会抛出");
        Check(history.GetPage(user, new()).TotalRecords == 0, "历史整批失败回滚，第一条也不会遗留");
        Execute("DROP TRIGGER fail_history;");
        history.SaveRecords(user, records);
        Check(history.GetPage(user, new()).TotalRecords == 2, "一次提交保存全部读数历史");
        AlarmRecord[] events = [Event(1), Event(2)];
        Execute("CREATE TRIGGER fail_alarm BEFORE INSERT ON AlarmRecords WHEN NEW.PointId=2 BEGIN SELECT RAISE(ABORT,'test'); END;");
        Throws<DbUpdateException>(() => alarms.SaveRecords(user, events), "批量报警中途数据库失败会抛出");
        Check(alarms.GetPage(user, new()).TotalRecords == 0, "报警整批失败回滚，不留下半批记录");
        Execute("DROP TRIGGER fail_alarm;");
        alarms.SaveRecords(user, events);
        alarms.SaveRecords(user, events.Select(e => e with
        {
            State = AlarmState.Recovered,
            LastSeenAt = now.AddSeconds(1),
            EndedAt = now.AddSeconds(1)
        }).ToArray());
        Check(alarms.GetPage(user, new()).Records.All(e => e.State == AlarmState.Recovered)
            && alarms.GetPage(user, new()).TotalRecords == 2, "批量更新同事件不重复新增，正确记录恢复");
        Throws<ArgumentException>(() => alarms.SaveRecords(user, new[] { events[0], events[0] }), "同批重复事件编号明确拒绝");

        AlarmRecord changed = events[0] with
        {
            State = AlarmState.Recovered,
            LastSeenAt = now.AddSeconds(2),
            EndedAt = now.AddSeconds(2),
            Value = 12,
            Occurrences = 2
        };
        alarms.SaveRecords(user, new[] { changed, Event(3) });
        var mixed = alarms.GetRecentRecords(user);
        Check(mixed.Count == 3 && mixed.Single(e => e.EventId == changed.EventId).Value == 12,
            "同批既更新旧报警又新增报警，一次查询后仍正确区分");
        Execute("CREATE TRIGGER fail_mixed_alarm BEFORE INSERT ON AlarmRecords WHEN NEW.PointId=4 BEGIN SELECT RAISE(ABORT,'test'); END;");
        Throws<DbUpdateException>(() => alarms.SaveRecords(user, new[] { changed with { Value = 99 }, Event(4) }),
            "混合批次新增失败仍抛出保存异常");
        mixed = alarms.GetRecentRecords(user);
        Check(mixed.Count == 3 && mixed.Single(e => e.EventId == changed.EventId).Value == 12,
            "混合批次失败同时回滚旧报警更新和新报警插入");
        Execute("DROP TRIGGER fail_mixed_alarm;");

        AlarmRecord Event(long point) => new()
        {
            EventId = Guid.NewGuid().ToString("N"),
            DeviceId = 1,
            DeviceName = "设备",
            PointId = point,
            PointName = "点" + point,
            Kind = AlarmKind.HighLimit,
            State = AlarmState.Active,
            Value = 10,
            Limit = 5,
            Message = "超限",
            StartedAt = now,
            LastSeenAt = now,
            Occurrences = 1
        };
        void Execute(string sql)
        {
            using var db = new SqliteConnection($"Data Source={path}");
            db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
    }

    private static void CheckEngineeringValues(string folder)
    {
        string path = Path.Combine(folder, "conversion-upgrade.db");
        var options = new DbContextOptionsBuilder<MonitorDbContext>().UseSqlite(DatabaseInitializer.CreateConnectionString(path)).Options;
        using (var db = new MonitorDbContext(options))
        {
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            migrator.Migrate("20261016085148_InitialCreate");
        }
        var users = new UserStore(path);
        users.TryCreateFirstAdministrator("conversion", "Admin123");
        var user = users.CheckLogin("conversion", "Admin123")!;
        var store = new DeviceStore(path);
        store.TryAddDevice(user, Input("旧设备"));
        var device = store.GetDevices(user).Single();
        store.TrySetDeviceEnabled(user, device.Id, true);
        string time = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using (var db = new MonitorDbContext(options))
        {
            db.Database.ExecuteSqlInterpolated($"INSERT INTO DevicePoints (Id, DeviceId, Name, RegisterType, Address, DataType, LowerLimit, UpperLimit) VALUES (1,{device.Id},'旧点位',3,0,0,NULL,600)");
            db.Database.ExecuteSqlInterpolated($"INSERT INTO HistoryRecords (DeviceId,DeviceName,PointId,PointName,Value,ReadAtUtc) VALUES ({device.Id},'旧设备',1,'旧点位',486,{time})");
            db.Database.ExecuteSqlInterpolated($"INSERT INTO AlarmRecords (EventId,DeviceId,DeviceName,PointId,PointName,Kind,State,Message,Value,LimitValue,StartedAtUtc,LastSeenAtUtc,Occurrences) VALUES ({Guid.NewGuid().ToString("N")},{device.Id},'旧设备',1,'旧点位',1,1,'旧报警',700,600,{time},{time},1)");
        }
        new DatabaseInitializer(path).Initialize();
        var old = store.GetPoints(user, device.Id).Single();
        Check(old.Scale == 1 && old.Offset == 0 && old.Unit == "" && old.UpperLimit == 600, "升级保留旧点位，默认不换算");
        var history = new HistoryStore(path);
        Check(history.GetRecentRecords(user).Single().Value == 486, "INTEGER 改 REAL 后旧历史数值不丢失");
        Check(new AlarmStore(path).GetRecentRecords(user).Single().Value == 700, "升级保留旧报警数值");
        Check(users.CheckLogin("conversion", "Admin123") is not null, "升级保留账户和设备");

        var input = new PointInput
        {
            Name = "湿度",
            RegisterType = RegisterType.Holding,
            DataType = PointDataType.UInt16,
            Scale = 0.1,
            Offset = 0,
            Unit = "%RH",
            UpperLimit = 60.5
        };
        store.TryUpdatePoint(user, device.Id, old.Id, input);
        var humidity = store.GetPoints(user, device.Id).Single();
        var result = new PointReadResult(humidity, 486, 486, DateTimeOffset.UtcNow);
        Check(result.DecodedValue == 486 && Math.Abs(result.Value - 48.6) < 1e-10 && result.ValueText == "48.6 %RH", "湿度原始整数与实际值分开，配置可往返保存");
        var temperature = new PointInfo(2, device.Id, "温度", RegisterType.Holding, 1, PointDataType.Int16,
            scale: 0.1, unit: "℃");
        var negative = new PointReadResult(temperature, 0xFF9F, unchecked((short)0xFF9F), DateTimeOffset.UtcNow);
        Check(negative.DecodedValue == -97 && negative.ValueText == "-9.7 ℃", "负温度先解释补码，再换算");
        var offset = new PointInfo(3, device.Id, "偏移", RegisterType.Input, 2, PointDataType.UInt16, scale: -0.5, offset: 10);
        Check(new PointReadResult(offset, 30, 30, result.ReadAt).Value == -5, "支持非零偏移和负倍率");
        Throws<ArgumentException>(() => new PointInfo(4, device.Id, "无效", RegisterType.Holding, 0, PointDataType.UInt16, scale: double.NaN), "拒绝 NaN 倍率");
        Throws<ArgumentException>(() => new PointInfo(4, device.Id, "无效", RegisterType.Holding, 0, PointDataType.UInt16, upperLimit: double.PositiveInfinity), "拒绝无穷大限值");
        history.SaveRecord(user, new(device.Id, device.Name, humidity.Id, humidity.Name, result.Value, result.ReadAt, unit: humidity.Unit));
        input.Scale = 1; input.Unit = "新单位"; store.TryUpdatePoint(user, device.Id, humidity.Id, input);
        var saved = history.GetRecentRecords(user).First();
        Check(saved.ValueText == "48.6 %RH", "修改点位后，旧历史仍保留当时的值和单位");
        var page = history.GetPage(user, new());
        Check(history.GetPointTrend(user, device.Id, humidity.Id, new(), page.SnapshotId, unit: "%RH").Records.Count == 1, "历史趋势不混画不同单位");
        string csv = Path.Combine(folder, "engineering-values.csv");
        new HistoryCsvExporter(history).Export(user, new(), page.SnapshotId, csv);
        Check(File.ReadAllText(csv).Contains("\"48.6\",\"%RH\""), "CSV 分列导出实际值和单位");
    }

    private static DeviceInput Input(string name) => new()
    {
        Name = name,
        Host = "127.0.0.1",
        Port = 502,
        UnitId = 1,
        SamplingIntervalMilliseconds = 100,
        ConnectTimeoutMilliseconds = 1000,
        ReadTimeoutMilliseconds = 1000,
        WriteTimeoutMilliseconds = 1000
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
