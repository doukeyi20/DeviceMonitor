using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeviceMonitor.Core.Diagnostics;

/// <summary>
/// 把 AppLog 交来的关键信息追加到文件，按日期/大小换文件并限制保留数量。
/// 锁只保护短暂文件追加；日志写失败记状态，不把正常业务一起弄失败。
/// </summary>
public sealed class FileLog
{
    private readonly object _gate = new();
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private readonly Dictionary<string, DateTimeOffset> _lastWarnings = new();
    private string? _currentFile;
    private string? _currentDay;
    private int _sequence;

    public string DirectoryPath { get; }
    public string? LastWriteError { get; private set; }

    /// <summary>
    /// AppLog 初始化时创建。检查并保存日志目录与文件大小/数量限制，此时还不写文件。
    /// </summary>
    public FileLog(string directoryPath, long maxFileBytes = 2 * 1024 * 1024, int retainedFiles = 14)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (maxFileBytes < 256 || retainedFiles < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        DirectoryPath = Path.GetFullPath(directoryPath);
        _maxFileBytes = maxFileBytes;
        _retainedFiles = retainedFiles;
    }

    /// <summary>
    /// AppLog 调用这里。context 只传固定说明和编号，不传密码、SQL、连接字符串。
    /// 异常只记类型、错误码和调用位置，不记可能夹带输入内容的 Message / ToString。
    /// </summary>
    public void Write(string level, string operation, string context = "", Exception? exception = null,
        string? throttleKey = null)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (throttleKey is not null && _lastWarnings.TryGetValue(throttleKey, out var last)
                && now - last < TimeSpan.FromSeconds(30))
                return; // 设备断线可能每秒报错，同一类错误最多每 30 秒写一次。

            string details = SafeExceptionDetails(exception);
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{OneLine(level, 12)}] " +
                $"{OneLine(operation, 160)} | {OneLine(context, 1024)}{details}{Environment.NewLine}";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string day = now.ToString("yyyyMMdd");
                int bytes = Encoding.UTF8.GetByteCount(line);
                bool newFile = _currentFile is null || _currentDay != day
                    || (File.Exists(_currentFile) && new FileInfo(_currentFile).Length + bytes > _maxFileBytes);
                if (newFile)
                {
                    _currentDay = day;
                    // 时间、进程号、序号合起来避免重新启动时覆盖旧日志。
                    _currentFile = Path.Combine(DirectoryPath,
                        $"device-monitor-{day}-{now:HHmmssfff}-{Environment.ProcessId}-{++_sequence:D4}.log");
                }

                File.AppendAllText(_currentFile!, line, new UTF8Encoding(false));
                if (newFile)
                    RemoveOldFiles();
                LastWriteError = null;
                if (throttleKey is not null)
                {
                    if (_lastWarnings.Count >= 256)
                        _lastWarnings.Clear(); // 不让长期运行时的去重记录无限增多。
                    _lastWarnings[throttleKey] = now;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LastWriteError = ex.GetType().Name; // 不能在日志失败后再递归写一条日志。
            }
        }
    }

    /// <summary>
    /// 开始一个新日志文件后调用。只清理本程序命名格式的旧日志，保留最近有限个，不碰无关文件。
    /// </summary>
    private void RemoveOldFiles()
    {
        // 只清理本程序生成的日志，不碰这个目录里用户自己放的文件。
        var ownFiles = new DirectoryInfo(DirectoryPath).GetFiles("device-monitor-*.log")
            .Where(file => IsOwnFileName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name)
            .ToArray();
        foreach (var file in ownFiles.Skip(_retainedFiles))
            if (file.FullName != _currentFile)
                file.Delete();
    }

    /// <summary>
    /// 清理旧日志前调用，确认名字符合本程序生成格式，不因后缀碰巧是 .log 就删除。
    /// </summary>
    private static bool IsOwnFileName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^device-monitor-\d{8}-\d{9}-\d+-\d{4,}\.log$");

    /// <summary>
    /// Write 调用。只整理异常类型、错误码和调用位置，不读可能夹带密码或 SQL 的 Message。
    /// </summary>
    private static string SafeExceptionDetails(Exception? exception)
    {
        var details = new StringBuilder();
        // 外层可能只是“任务失败”。往里找几层才能看到真正的 SQLite / 网络错误。
        // 不读 Message，避免把调用方传进异常里的密码、SQL 等内容带到文件中。
        for (int depth = 0; exception is not null && depth < 4; depth++, exception = exception.InnerException)
        {
            details.Append($" | {exception.GetType().FullName} HResult={exception.HResult}");
            if (exception is Microsoft.Data.Sqlite.SqliteException sqlite)
                details.Append($" SqliteCode={sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}");
            if (exception is System.Net.Sockets.SocketException socket)
                details.Append($" SocketError={socket.SocketErrorCode}");
            details.Append(" | ").Append(OneLine(exception.StackTrace, 1024));
        }
        return details.ToString();
    }

    /// <summary>
    /// 写日志前共用，把换行折成空格并限制文字长度，防止一条消息伪造多条日志。
    /// </summary>
    private static string OneLine(string? value, int maxLength)
    {
        string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= maxLength ? safe : safe[..maxLength];
    }
}
