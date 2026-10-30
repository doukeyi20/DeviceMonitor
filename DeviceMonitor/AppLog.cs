// 记录运行日志，限制重复错误，并按大小和日期轮换文件。
using System.IO;
using System.Net.Sockets;
using System.Text;
using DeviceMonitor.Model;
using Microsoft.Data.Sqlite;

namespace DeviceMonitor;

public static class AppLog
{
    private const long MaximumFileBytes = 2 * 1024 * 1024;

    private const int RetainedFiles = 14;

    // 串行写入，保护文件轮换状态和重复错误登记。
    private static readonly object WriteLock = new();

    // 按调用方标识记录最近一次成功写入的内容与时间。
    private static readonly Dictionary<string, (string Error, DateTimeOffset Time)> LastWarnings = new();

    private static string? _currentFile;

    private static string? _currentDay;

    private static int _sequence;

    public static string? DirectoryPath { get; private set; }

    public static string? LastWriteError { get; private set; }

    /// <summary>设置日志目录并重置轮换状态。</summary>
    public static void Initialize(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        lock (WriteLock)
        {
            DirectoryPath = Path.GetFullPath(directoryPath);
            _currentFile = null;
            _currentDay = null;
            _sequence = 0;
            LastWarnings.Clear();
            LastWriteError = null;
        }
    }

    /// <summary>记录正常操作。</summary>
    public static void Info(string operation, string context = "") => Write("信息", operation, context);

    /// <summary>记录警告，可按标识限制重复频率。</summary>
    public static void Warning(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => Write("警告", operation, context, exception, throttleKey);

    /// <summary>记录失败，可按标识限制重复频率。</summary>
    public static void Error(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => Write("错误", operation, context, exception, throttleKey);

    /// <summary>限频检查后追加日志，写入失败时保留错误状态。</summary>
    private static void Write(string level, string operation, string context,
        Exception? exception = null, string? throttleKey = null)
    {
        lock (WriteLock)
        {
            if (DirectoryPath is null)
                return;

            DateTimeOffset now = DateTimeOffset.Now;

            // 从异常类型或错误码提取短原因，避免把敏感异常原文直接写入文件。
            string reason = DescribeError(exception);

            string signature = level + "|" + operation + "|" + reason;

            // 同标识、同内容在 30 秒内跳过；错误原因变化仍立即记录。
            if (throttleKey is not null && LastWarnings.TryGetValue(throttleKey, out var last)
                && last.Error == signature && now - last.Time < TimeSpan.FromSeconds(30))
                return;

            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {OneLine(operation, 80)}";
            if (!string.IsNullOrWhiteSpace(context))
                line += " | " + OneLine(context, 200);
            if (reason.Length > 0)
                line += " | " + reason;
            line += Environment.NewLine;

            try
            {
                Directory.CreateDirectory(DirectoryPath);

                string day = now.ToString("yyyyMMdd");

                // 首次写入、跨天或达到大小限制时创建新的日志文件。
                bool newFile = _currentFile is null || _currentDay != day
                    || (File.Exists(_currentFile)
                        && new FileInfo(_currentFile).Length + Encoding.UTF8.GetByteCount(line) > MaximumFileBytes);

                if (newFile)
                {
                    _currentDay = day;
                    _currentFile = Path.Combine(DirectoryPath,
                        $"device-monitor-{day}-{now:HHmmssfff}-{Environment.ProcessId}-{++_sequence:D4}.log");
                }

                // 文件不存在时创建，存在时追加；只有写入成功才更新限频状态。
                File.AppendAllText(_currentFile!, line, new UTF8Encoding(false));

                LastWriteError = null;

                // 写入成功后才更新限频状态，失败记录不算已写入。
                if (throttleKey is not null)
                {
                    if (LastWarnings.Count >= 256)
                        LastWarnings.Clear();

                    LastWarnings[throttleKey] = (signature, now);
                }

                // 只在换文件时清理；清理失败不影响已成功写入的日志。
                if (newFile)
                    RemoveOldFiles();
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                // 记录写入失败状态，避免递归调用日志。
                LastWriteError = DescribeError(ex);
            }
        }
    }

    /// <summary>保留最近 14 个日志文件，跳过暂时无法删除的文件。</summary>
    private static void RemoveOldFiles()
    {
        try
        {
            var oldFiles = new DirectoryInfo(DirectoryPath!).GetFiles("device-monitor-*.log")
                .Where(file => System.Text.RegularExpressions.Regex.IsMatch(file.Name,
                    @"^device-monitor-\d{8}-\d{9}-\d+-\d{4,}\.log$"))
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name)
                .Skip(RetainedFiles);

            foreach (var file in oldFiles)
            {
                if (file.FullName == _currentFile)
                    continue;
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    /* 文件被占用或无权限，下次换文件时再清理。 */
                }
            }
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            /* 暂时列不出旧文件，不影响已成功写入的日志。 */
        }
    }

    // 只从异常类型和错误码得到简短原因，不直接写 Message，避免泄露密码或 SQL。
    private static string DescribeError(Exception? exception)
    {
        // 例如普通日志没有传异常参数，这里就直接返回 ""
        if (exception is null) return "";

        // TCP/数据库错误常被外层异常包着，优先留下有用的底层错误码。
        Exception? current = exception;
        for (int i = 0; current is not null && i < 4; i++, current = current.InnerException)
        {
            // 找到网络异常，立即返回对应原因
            // swtich语法，对比socket.SocketErrorCode和SocketError.xxx ，返回对应的中文描述；没有匹配的就返回网络错误码。
            if (current is SocketException socket) return socket.SocketErrorCode switch
            {
                SocketError.TimedOut => "网络超时",
                SocketError.ConnectionReset => "连接被重置",
                SocketError.ConnectionRefused => "连接被拒绝",
                SocketError.NetworkUnreachable => "网络不可达",
                SocketError.HostUnreachable => "主机不可达",
                SocketError.HostNotFound => "主机名无效",
                _ => $"网络={socket.SocketErrorCode}"
            };

            //找到数据库异常，也立即返回
            if (current is SqliteException sqlite) return $"数据库={sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}";
        }
        return exception switch
        {
            // 嵌套swtich ，如果 exception 是 ModbusDeviceException 类型，就用变量 modbus 引用它，供右边读取属性
            ModbusDeviceException modbus => $"Modbus=0x{modbus.Code:X2} " + (modbus.Code switch
            {
                1 => "功能不支持",
                2 => "地址不可用",
                3 => "参数不正确",
                6 => "设备忙",
                _ => "设备执行失败"
            }),
            TimeoutException => "等待超时",
            InvalidDataException or FormatException => "响应格式不符",
            UnauthorizedAccessException => "无访问权限",
            IOException => $"读写失败(0x{exception.HResult:X8})",
            OperationCanceledException => "操作已取消",
            ArgumentException => "参数无效",
            _ => exception.TargetSite is null ? exception.GetType().Name
                : OneLine($"{exception.GetType().Name} @{exception.TargetSite.DeclaringType?.Name}.{exception.TargetSite.Name}", 150)
        };
    }


    private static bool IsFileError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string OneLine(string value, int maximumLength)
    {
        string text = value.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= maximumLength ? text : text[..maximumLength];
    }
}
