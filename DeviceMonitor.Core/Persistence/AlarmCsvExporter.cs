using System.Globalization;
using System.Text;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 分批导出报警历史，全部写完后才替换目标 CSV。
/// 不包含后来新增的报警，但已有报警的状态仍可能更新。
/// </summary>
public sealed class AlarmCsvExporter
{
    private readonly AlarmStore _store;

    /// <summary>
    /// 记住已有的报警数据库对象，此时不导出。
    /// </summary>
    public AlarmCsvExporter(AlarmStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// 由 AlarmHistoryViewModel.ExportRecordsAsync 选好导出路径后调用。
    /// 按上次查询条件导出。先写临时文件，成功才替换目标；失败或取消保留原文件。
    /// </summary>
    public long Export(UserAccount user, AlarmFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        // 先检查权限、查询条件和路径，不能覆盖数据库文件。
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);
        // 真正导出前再检查管理员权限。
        if (!user.IsEnabled || user.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有已启用的管理员才能导出报警历史。");
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("请提供完整的导出路径。", nameof(filePath));
        string path = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .csv 扩展名。", nameof(filePath));
        if (string.Equals(path, _store.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能覆盖正在使用的数据库文件。", nameof(filePath));
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("导出文件夹不存在。");

        // 临时文件放在目标旁边，全部写完才换成最终文件。
        string temporary = Path.Combine(directory, $".alarm-export-{Guid.NewGuid():N}.tmp");
        long written = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            // UTF-8 加 BOM，让 Excel 正确识别中文；每行统一使用 Windows 换行。
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)) { NewLine = "\r\n" })
            {
                writer.WriteLine("记录编号,报警事件编号,设备编号,设备名称,采集点编号,采集点名称,报警类别,报警类型,报警说明,报警状态,读取值,限值,发生次数,首次发生时间(本机含时区),首次发生时间(UTC),最后发生时间(本机含时区),最后发生时间(UTC),结束时间(本机含时区),结束时间(UTC)");
                // after 记住上一批最后一条，每批最多查 1000 条，不一次加载全部报警。
                AlarmRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0) break;
                    foreach (AlarmRecord record in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        // 一条 AlarmRecord 写成 CSV 的一行。
                        writer.WriteLine(ToCsvLine(record));
                        written++;
                    }
                    // 记录本批末尾，再报告已写条数。
                    after = batch[^1];
                    progress?.Report(written);
                }
            }
            // 最后一批写完后也检查取消，避免点击取消后仍覆盖原文件。
            token.ThrowIfCancellationRequested();
            // 全部写完才替换目标文件，前面失败或取消都不会覆盖原文件。
            File.Move(temporary, path, overwrite: true);
            return written;
        }
        finally
        {
            // 只清理这次自己创建的临时文件；不删除用户的目标文件或其他导出文件。
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // 一条报警写一行，未知数值和结束时间留空，不填假数据。
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
        Field(record.EndedAt.HasValue ? UtcTime(record.EndedAt.Value) : string.Empty));

    // 文字里有逗号、双引号、换行也能保存：整格加引号，内部双引号再写一遍。
    private static string Field(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    // 文本像公式时补单引号，避免 Excel 执行；真正的负数仍按数字写出。
    private static string SafeText(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) ? "'" + value : value;
    }

    /// <summary>
    /// 转成本机时间文字，供 CSV 显示。
    /// </summary>
    private static string LocalTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// 转成 UTC 时间文字，供 CSV 保存统一时间。
    /// </summary>
    private static string UtcTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
