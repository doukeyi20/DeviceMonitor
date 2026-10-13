using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeviceMonitor.Core.Diagnostics;

/// <summary>
/// 把日志追加到文件，按日期和大小换文件，并清理多余旧日志。
/// </summary>
public sealed class FileLog
{
    private readonly object _gate = new(); // 同一时刻只让一个调用写文件。
    private readonly long _maxFileBytes; // 单个日志文件最多多大，超过就换新文件。
    private readonly int _retainedFiles; // 最多保留多少个日志文件。
    private readonly Dictionary<string, DateTimeOffset> _lastWarnings = new(); // 同类日志上次写入的时间。
    private string? _currentFile; // 当前正在追加的日志文件路径。
    private string? _currentDay; // 当前文件所属日期，换一天就换文件。
    private int _sequence; // 文件名里的递增序号，避免同一时刻生成同名文件。

    public string DirectoryPath { get; }
    public string? LastWriteError { get; private set; } // 最近一次写入失败的原因，供界面提醒。

    /// <summary>
    /// 记住日志目录、文件大小和保留数量，此时还不创建日志文件。
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
    /// 由 AppLog.Info、Warning 和 Error 调用。
    /// 追加一条日志。context 不要传密码、SQL 或连接字符串。
    /// 异常只记类型、错误码和位置，不记可能含敏感数据的消息。
    /// </summary>
    public void Write(string level, string operation, string context = "", Exception? exception = null,
        string? throttleKey = null)
    {
        // 多个地方同时写日志时，排队写，避免互相抢同一个文件。
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (throttleKey is not null && _lastWarnings.TryGetValue(throttleKey, out var last)
                && now - last < TimeSpan.FromSeconds(30))
                return; // 设备断线可能每秒报错，同一类错误最多每 30 秒写一次。

            // 把时间、级别、操作和错误信息拼成一行。
            string details = SafeExceptionDetails(exception);
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} [{OneLine(level, 12)}] " +
                $"{OneLine(operation, 160)} | {OneLine(context, 1024)}{details}{Environment.NewLine}";
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string day = now.ToString("yyyyMMdd");
                int bytes = Encoding.UTF8.GetByteCount(line);
                // 第一次写、换了一天或文件太大，都要准备新文件名。
                bool newFile = _currentFile is null || _currentDay != day
                    || (File.Exists(_currentFile) && new FileInfo(_currentFile).Length + bytes > _maxFileBytes);
                if (newFile)
                {
                    _currentDay = day;
                    // 时间、进程号、序号合起来避免重新启动时覆盖旧日志。
                    _currentFile = Path.Combine(DirectoryPath,
                        $"device-monitor-{day}-{now:HHmmssfff}-{Environment.ProcessId}-{++_sequence:D4}.log");
                }

                // 真正写入这一行；文件不存在时会创建，存在时追加。
                File.AppendAllText(_currentFile!, line, new UTF8Encoding(false));
                if (newFile)
                    RemoveOldFiles();
                LastWriteError = null;

                // 写成功后记下同类日志的时间，30 秒内不重复写。
                if (throttleKey is not null)
                {
                    if (_lastWarnings.Count >= 256)
                        _lastWarnings.Clear(); // 避免长期运行后，这份记录越积越多。
                    _lastWarnings[throttleKey] = now;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                LastWriteError = ex.GetType().Name; // 写日志失败就记下原因，不能再用写日志来报告这个错误。
            }
        }
    }

    /// <summary>
    /// 清理超出保留数量的旧日志，不碰用户的其他文件。
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
    /// 检查是不是本程序生成的日志文件名。
    /// </summary>
    private static bool IsOwnFileName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name,
            @"^device-monitor-\d{8}-\d{9}-\d+-\d{4,}\.log$");

    /// <summary>
    /// 提取可写入日志的异常信息，不读取异常消息中的敏感内容。
    /// </summary>
    private static string SafeExceptionDetails(Exception? exception)
    {
        var details = new StringBuilder();
        // 最多向里找四层错误；不读取 Message，避免写出密码或 SQL。
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
    /// 去掉换行并截短过长文字，让一条日志只占一行。
    /// </summary>
    private static string OneLine(string? value, int maxLength)
    {
        string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return safe.Length <= maxLength ? safe : safe[..maxLength];
    }
}
