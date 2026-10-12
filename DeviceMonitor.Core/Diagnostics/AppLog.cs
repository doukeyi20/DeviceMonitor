using System;

namespace DeviceMonitor.Core.Diagnostics;

/// <summary>
/// 全项目共用的日志入口。正式 App 启动时设置目录，其他类直接调用 Info/Warning/Error。
/// AppLog 内部保存了真正负责写日志文件的 FileLog 对象，并提供 Info、Warning、Error 这些方便调用的方法。
/// </summary>
public static class AppLog
{
    private static FileLog? _file;
    public static string? DirectoryPath => _file?.DirectoryPath;
    public static string? LastWriteError => _file?.LastWriteError;

    /// <summary>
    /// App.OnStartup 调用一次，指定日志目录并创建filelog对象，之后applog类提供的方法内部其实由这个对象执行。
    ///
    /// </summary>
    public static void Initialize(string directoryPath) => _file = new FileLog(directoryPath);

    /// <summary>
    /// 记录关键正常操作，例如启动、登录成功、任务开始/结束；不逐条记录正常读数。
    /// </summary>
    public static void Info(string operation, string context = "") => _file?.Write("INFO", operation, context);

    /// <summary>
    /// 记录可继续处理的异常或提醒，可带去重键限制重复错误频率；上下文只传固定说明和编号。
    /// </summary>
    public static void Warning(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => _file?.Write("WARN", operation, context, exception, throttleKey);

    /// <summary>
    /// 记录保存/查询等失败，附安全的异常类型和调用位置；不直接写密码或异常原始消息。
    /// </summary>
    public static void Error(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => _file?.Write("ERROR", operation, context, exception, throttleKey);
}
