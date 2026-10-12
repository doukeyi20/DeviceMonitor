using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 采集历史的 CSV 导出：按表格查过的条件分批取记录，写临时文件，全部成功才替换目标文件。
/// 不修改数据库；取消或失败保留原目标文件。文字转义和防公式处理放在最后的辅助方法。
/// </summary>
public sealed class HistoryCsvExporter
{
    private readonly HistoryStore _store;

    /// <summary>
    /// HistoryViewModel 创建这里，传已有 HistoryStore；不另找数据库文件。
    /// </summary>
    public HistoryCsvExporter(HistoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// 先写同目录临时文件，全部成功后才替换目标。取消或失败不覆盖用户原来的 CSV。
    /// snapshotId 与表格一致，新采集的记录不混进这次导出。
    /// </summary>
    public long Export(UserAccount user, HistoryFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);
        // 页面隐藏入口还不够，直接调用导出器也要遵守与报警历史相同的管理员权限。
        if (!user.IsEnabled || user.Role != UserRole.Administrator)
            throw new UnauthorizedAccessException("只有已启用的管理员才能导出历史记录。");
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath)) throw new ArgumentException("请提供完整的导出路径。", nameof(filePath));
        string path = Path.GetFullPath(filePath);
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .csv 扩展名。", nameof(filePath));
        if (string.Equals(path, _store.DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能覆盖正在使用的数据库文件。", nameof(filePath));
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("导出文件夹不存在。");
        string temporary = Path.Combine(directory, $".history-export-{Guid.NewGuid():N}.tmp");
        long written = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            // UTF-8 加 BOM，方便 Excel 正确识别中文；行末统一 Windows 换行。
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)) { NewLine = "\r\n" })
            {
                writer.WriteLine("记录编号,设备编号,设备名称,点位编号,采集点名称,读取值,读取时间(本机含时区),读取时间(UTC)");
                HistoryRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0) break;
                    foreach (HistoryRecord record in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        writer.WriteLine(string.Join(",",
                            Field(record.Id.ToString(CultureInfo.InvariantCulture)),
                            Field(record.DeviceId.ToString(CultureInfo.InvariantCulture)), Field(SafeName(record.DeviceName)),
                            Field(record.PointId.ToString(CultureInfo.InvariantCulture)), Field(SafeName(record.PointName)),
                            Field(record.Value.ToString(CultureInfo.InvariantCulture)),
                            Field(record.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
                            Field(record.ReadAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))));
                        written++;
                    }
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
            // 只删除本次唯一名称的临时文件，不删除目标或其他用户文件。
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Export 写名称时调用。含逗号、引号、换行时加 CSV 引号保护，内部引号写两遍。
    /// </summary>
    private static string Field(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Export 写自由输入的名称时调用，避免 Excel 当公式执行；负数读数仍按数字写。
    /// </summary>
    private static string SafeName(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) ? "'" + value : value;
    }
}
