using System.Globalization;
using System.Text;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 报警历史的 CSV 导出：分批从 AlarmStore 取出已查询范围的记录，成功后才替换目标文件。
/// 只固定新记录的最大编号，原事件仍可能更新恢复状态；不是整个文件同一瞬间的状态快照。
/// </summary>
public sealed class AlarmCsvExporter
{
    private readonly AlarmStore _store;

    /// <summary>
    /// AlarmHistoryViewModel 创建这里并传已有 AlarmStore；只保存对象，不立即导出。
    /// </summary>
    public AlarmCsvExporter(AlarmStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// 先写本次独有的临时文件，全部成功才替换目标；取消或失败保留用户原来的文件。
    /// 沿用表格上次查询的条件和最大编号，不混入后来新增的报警。
    /// 老报警仍可更新恢复状态：每批读到当时的状态，不把整份导出当作同一瞬间的快照。
    /// </summary>
    public long Export(UserAccount user, AlarmFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);
        // 不只靠页面隐藏按钮；即使从其他代码直接调用，也只允许已启用管理员导出。
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

        // 和目标放在同一文件夹；最后移动成功，用户才会看到完整的导出结果。
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
                AlarmRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0) break;
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
            // 最后一批写完后也检查取消，避免点击取消后仍覆盖原文件。
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return written;
        }
        finally
        {
            // 只清理这次自己创建的临时文件；不删除用户的目标文件或其他导出文件。
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // 一条报警对应 CSV 的一行；数据库 NULL 保持空白，不能写成“读数 0”或假的结束时间。
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

    // 名称和说明都可能来自输入或设备。以公式符号开头就补单引号，避免 Excel 把文字当公式。
    // 只处理这些自由文本，真正的负数读数和限值仍按原数字写出。
    private static string SafeText(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) ? "'" + value : value;
    }

    /// <summary>
    /// 组装 CSV 一行时调用，给人查看的时间转成本机格式；空结束时间保持空白。
    /// </summary>
    private static string LocalTime(DateTimeOffset time) =>
        time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// 组装 CSV 一行时调用，同时保留统一 UTC 时间，便于后续处理；空时间不伪造数值。
    /// </summary>
    private static string UtcTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
