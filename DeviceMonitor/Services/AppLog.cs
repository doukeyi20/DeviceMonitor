// 记录日志。文件轮换交给 Serilog，只做内容整理和重复错误限频。
using System.Net.Sockets;
using DeviceMonitor.Model;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;

namespace DeviceMonitor.Services;

public static class AppLog
{
    // 写入锁
    private static readonly object WriteLock = new();
    private static readonly Dictionary<string, (string Error, DateTimeOffset Time)> LastWarnings = new();
    
    // 真正处理日志的对象
    private static Serilog.Core.Logger? _logger;

    public static string? DirectoryPath { get; private set; }
    public static string? LastWriteError { get; private set; }

    /// <summary>启动时设置一次目录；按天或 2MB 分文件，保留最近 14 个。</summary>
    public static void Initialize(string directoryPath)
    {
        // 检查目录路径
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        
        
        lock (WriteLock)
        {
            // 先释放可能存在的旧日志对象
            _logger?.Dispose();
            _logger = null;
            DirectoryPath = Path.GetFullPath(directoryPath);

            LastWarnings.Clear();
            LastWriteError = null;

            // 日志自身失败不能再写日志，否则会递归；保留简短状态供界面提示。
            Serilog.Debugging.SelfLog.Enable(_ => LastWriteError = "日志文件写入失败");
            
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                // 创建日志对象，按天轮换，单文件最大 2MB，保留最近 14 个文件。
                _logger = new LoggerConfiguration()
                    .WriteTo.File(Path.Combine(DirectoryPath, "device-monitor-.log"),
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: 2 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 14,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}")
                    .CreateLogger();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LastWriteError = "日志目录不可写";
            }
        }
    }

    // 各个level在调用时已经确定
    public static void Info(string operation, string context = "") =>
        Write(LogEventLevel.Information, operation, context);

    public static void Warning(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => Write(LogEventLevel.Warning, operation, context, exception, throttleKey);

    public static void Error(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => Write(LogEventLevel.Error, operation, context, exception, throttleKey);

    /*
    operation    做什么操作失败了
    context 哪台设备、哪个用户、多少条记录等必要信息
    exception    用来提取错误类型和错误码
    throttleKey  哪一组重复错误需要限频
    */

    /// <summary>一行写清“什么操作、哪个对象、什么原因”；不写密码、SQL 和大段堆栈。</summary>
    private static void Write(LogEventLevel level, string operation, string context,
        Exception? exception = null, string? throttleKey = null)
    {
        lock (WriteLock)
        {
            if (_logger is null)
                return;

            DateTimeOffset now = DateTimeOffset.Now;

            string reason = DescribeError(exception);

            string signature = level + "|" + operation + "|" + reason;

            // 同标识、同原因 30 秒内不重复刷屏，原因变化仍立即记录。
            if (throttleKey is not null && LastWarnings.TryGetValue(throttleKey, out var last)
                && last.Error == signature && now - last.Time < TimeSpan.FromSeconds(30))
                return;

            string message = OneLine(operation, 80);

            if (!string.IsNullOrWhiteSpace(context))
                message += " | " + OneLine(context, 200);
            if (reason.Length > 0)
                message += " | " + reason;

            LastWriteError = null;

            // 真正写入的,level跟调用日志的方法有关，operation就是传入的操作，message就是传入的context做个隔断
            _logger.Write(level, "{Operation}", message);

            if (throttleKey is not null && LastWriteError is null)
            {
                if (LastWarnings.Count >= 256)
                    LastWarnings.Clear();
                LastWarnings[throttleKey] = (signature, now);
            }
        }
    }

    /// <summary>优先留下错误类型和错误码；不直接记录异常原文，避免泄露输入内容。</summary>
    private static string DescribeError(Exception? exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error is SocketException socket)
                return $"网络={socket.SocketErrorCode}";
            if (error is SqliteException sqlite)
                return $"数据库={sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}";
            if (error is ModbusDeviceException modbus)
                return $"Modbus=0x{modbus.Code:X2}";
        }

        // 其他异常类型转成简短文字
        return exception switch
        {
            null => "",
            TimeoutException => "等待超时",
            OperationCanceledException => "操作已取消",
            InvalidDataException or FormatException => "响应格式不符",
            UnauthorizedAccessException => "无访问权限",
            IOException => "读写失败",
            ArgumentException => "参数无效",
            _ => exception.GetType().Name
        };
    }

    private static string OneLine(string text, int limit)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= limit ? text : text[..limit];
    }

    /// <summary>程序退出时关闭文件；不在每次记录后重复创建日志对象。</summary>
    public static void Close()
    {
        lock (WriteLock)
        {
            _logger?.Dispose();
            _logger = null;
            Serilog.Debugging.SelfLog.Disable();
        }
    }
}
