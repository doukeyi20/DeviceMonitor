// 分批导出读数、报警 CSV，成功后替换目标文件。
using System.Globalization;
using System.Text;
using static DeviceMonitor.Model.CsvText;

namespace DeviceMonitor.Model;

/// <summary>分批导出读数历史；每批最多查 1000 条，不修改数据库。</summary>
public sealed class HistoryCsvExporter
{
    private readonly HistoryStore _store;

    public HistoryCsvExporter(HistoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>导出本次查询范围的记录，取消或失败时保留原文件。</summary>
    public long Export(UserAccount user, HistoryFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        // 导出目标不得覆盖数据库文件。
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);

        if (!user.IsEnabled || user.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有已启用的管理员才能导出历史记录。");
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        string path = CheckPath(filePath, _store.DatabasePath);
        string directory = Path.GetDirectoryName(path)!;

        // 先写同目录临时文件，全部成功后才替换目标，失败保留旧文件。
        string temporary = Path.Combine(directory, $".history-export-{Guid.NewGuid():N}.tmp");
        long written = 0;
        try
        {
            token.ThrowIfCancellationRequested();

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)) { NewLine = "\r\n" })
            {
                writer.WriteLine("记录编号,设备编号,设备名称,点位编号,采集点名称,读取值,单位,读取时间(本机含时区),读取时间(UTC)");

                HistoryRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0)
                        break;
                    foreach (HistoryRecord record in batch)
                    {
                        token.ThrowIfCancellationRequested();

                        writer.WriteLine(string.Join(",",
                            Field(record.Id.ToString(CultureInfo.InvariantCulture)),
                            Field(record.DeviceId.ToString(CultureInfo.InvariantCulture)), Field(SafeText(record.DeviceName)),
                            Field(record.PointId.ToString(CultureInfo.InvariantCulture)), Field(SafeText(record.PointName)),
                            Field(record.Value.ToString(CultureInfo.InvariantCulture)),
                            Field(SafeText(record.Unit)),
                            Field(LocalTime(record.ReadAt)),
                            Field(UtcTime(record.ReadAt))));
                        written++;
                    }

                    // 按上一批末项继续读取，避免一次加载全部历史。
                    after = batch[^1];
                    progress?.Report(written);
                }
            }
            token.ThrowIfCancellationRequested();

            File.Move(temporary, path, overwrite: true);
            return written;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

/// <summary>分批导出报警历史；排除查询后新增的事件，但事件状态仍可能更新。</summary>
public sealed class AlarmCsvExporter
{
    private readonly AlarmStore _store;

    public AlarmCsvExporter(AlarmStore store) =>
    _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>按上次查询条件导出。先写临时文件，成功才替换目标；失败或取消保留原文件。</summary>
    public long Export(UserAccount user, AlarmFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        // 导出目标不得覆盖数据库文件。
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);

        if (!user.IsEnabled || user.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有已启用的管理员才能导出报警历史。");
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        string path = CheckPath(filePath, _store.DatabasePath);
        string directory = Path.GetDirectoryName(path)!;

        // 固定本次导出范围；已有报警的状态仍可能在导出期间变化。
        string temporary = Path.Combine(directory, $".alarm-export-{Guid.NewGuid():N}.tmp");
        long written = 0;
        try
        {
            token.ThrowIfCancellationRequested();

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)) { NewLine = "\r\n" })
            {
                writer.WriteLine("记录编号,报警事件编号,设备编号,设备名称,采集点编号,采集点名称,报警类别,报警类型,报警说明,报警状态,读取值,限值,发生次数,首次发生时间(本机含时区),首次发生时间(UTC),最后发生时间(本机含时区),最后发生时间(UTC),结束时间(本机含时区),结束时间(UTC),单位");

                AlarmRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0)
                        break;
                    foreach (AlarmRecord record in batch)
                    {
                        token.ThrowIfCancellationRequested();

                        writer.WriteLine(ToCsvLine(record));
                        written++;
                    }

                    after = batch[^1];
                    progress?.Report(written);
                }
            }
            // 最终替换前再次检查取消，保留原目标文件。
            token.ThrowIfCancellationRequested();

            File.Move(temporary, path, overwrite: true);
            return written;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string ToCsvLine(AlarmRecord record) => string.Join(",",
        Field(record.Id.ToString(CultureInfo.InvariantCulture)),
        Field(record.EventId),
        Field(record.DeviceId.ToString(CultureInfo.InvariantCulture)),
        Field(SafeText(record.DeviceName)),
        Field(record.PointId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        Field(SafeText(record.PointName)),
        Field(record.CategoryText),
        Field(record.KindText),
        Field(SafeText(record.Message)),
        Field(record.StateText),
        Field(record.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        Field(record.Limit?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
        Field(record.Occurrences.ToString(CultureInfo.InvariantCulture)),
        Field(LocalTime(record.StartedAt)), Field(UtcTime(record.StartedAt)),
        Field(LocalTime(record.LastSeenAt)), Field(UtcTime(record.LastSeenAt)),
        Field(record.EndedAt.HasValue ? LocalTime(record.EndedAt.Value) : string.Empty),
        Field(record.EndedAt.HasValue ? UtcTime(record.EndedAt.Value) : string.Empty),
        Field(SafeText(record.Unit)));
}

internal static class CsvText
{
    internal static string CheckPath(string filePath, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("请提供完整的导出路径。", nameof(filePath));
        string path = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .csv 扩展名。", nameof(filePath));
        if (string.Equals(path, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能覆盖正在使用的数据库文件。", nameof(filePath));
        if (!Directory.Exists(Path.GetDirectoryName(path)!))
            throw new DirectoryNotFoundException("导出文件夹不存在。");
        return path;
    }

    internal static string Field(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    internal static string SafeText(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) ? "'" + value : value;
    }

    internal static string LocalTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    internal static string UtcTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
