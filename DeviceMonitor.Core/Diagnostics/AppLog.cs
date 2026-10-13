using System;

namespace DeviceMonitor.Core.Diagnostics;

/// <summary>
/// 全项目共用的日志入口，内部交给 FileLog 真正写文件。
/// </summary>
public static class AppLog
{
    private static FileLog? _file; // 真正负责写日志文件的对象。
    public static string? DirectoryPath => _file?.DirectoryPath;
    public static string? LastWriteError => _file?.LastWriteError;

    /// <summary>
    /// 由 App.OnStartup 在程序启动时调用。
    /// 指定日志目录并创建 FileLog 对象，后面的日志都交给它写。
    /// </summary>
    public static void Initialize(string directoryPath) => _file = new FileLog(directoryPath);

    /// <summary>
    /// 写一条正常操作日志，例如登录成功、采集开始或结束。
    /// </summary>
    public static void Info(string operation, string context = "") => _file?.Write("INFO", operation, context);

    /// <summary>
    /// 写一条提醒或可继续处理的错误。throttleKey 用来限制同类日志的频率。
    /// </summary>
    public static void Warning(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => _file?.Write("WARN", operation, context, exception, throttleKey);

    /// <summary>
    /// 写一条失败日志，不记录密码或可能包含敏感数据的异常消息。
    /// </summary>
    public static void Error(string operation, string context = "", Exception? exception = null,
        string? throttleKey = null) => _file?.Write("ERROR", operation, context, exception, throttleKey);
}
