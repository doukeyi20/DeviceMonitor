using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using DeviceMonitor.Core.Models;

namespace DeviceMonitor.Core.Persistence;

/// <summary>
/// 按上次查询条件分批导出读数历史，不修改数据库。
/// 先写临时文件，全部成功才替换目标 CSV。
/// </summary>
public sealed class HistoryCsvExporter
{
    private readonly HistoryStore _store;

    /// <summary>
    /// 记住已有的历史数据库对象，此时不导出。
    /// </summary>
    public HistoryCsvExporter(HistoryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// 由 HistoryViewModel.ExportRecordsAsync 选好导出路径后调用。
    /// 导出本次查询范围的记录，取消或失败时保留原文件。
    /// snapshotId 用来排除查询后新保存的读数。
    /// </summary>
    public long Export(UserAccount user, HistoryFilter filter, long snapshotId, string filePath,
        CancellationToken token = default, IProgress<long>? progress = null)
    {
        // 先检查账户、查询条件和文件路径，不能把数据库当作导出文件覆盖。
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(filter);
        // 真正导出前再检查管理员权限。
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
        // 先写同目录下的临时文件，全部成功后才替换用户选的 CSV。
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
                // after 记住上一批最后一条，每批最多查 1000 条，避免一次占用太多内存。
                HistoryRecord? after = null;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = _store.GetExportBatch(user, filter, snapshotId, after, 1000, token);
                    if (batch.Count == 0) break;
                    foreach (HistoryRecord record in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        // 一条 HistoryRecord 写成 CSV 的一行，名称特殊字符要处理后再写。
                        writer.WriteLine(string.Join(",",
                            Field(record.Id.ToString(CultureInfo.InvariantCulture)),
                            Field(record.DeviceId.ToString(CultureInfo.InvariantCulture)), Field(SafeName(record.DeviceName)),
                            Field(record.PointId.ToString(CultureInfo.InvariantCulture)), Field(SafeName(record.PointName)),
                            Field(record.Value.ToString(CultureInfo.InvariantCulture)),
                            Field(record.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
                            Field(record.ReadAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))));
                        written++;
                    }
                    // 记住本批末尾，并把已写条数报告给界面。
                    after = batch[^1];
                    progress?.Report(written);
                }
            }
            token.ThrowIfCancellationRequested();
            // 文件已写完且没有取消，现在才替换目标文件。
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
    /// 给文本加 CSV 引号，避免逗号、引号或换行造成错列。
    /// </summary>
    private static string Field(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// 防止名称被 Excel 当公式执行，不改变真正的负数读数。
    /// </summary>
    private static string SafeName(string value)
    {
        string trimmed = value.TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) ? "'" + value : value;
    }
}
