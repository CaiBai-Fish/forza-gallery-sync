namespace ForzaGallerySync.Services;

/// <summary>
/// 简单但完整的日志模块：带级别、按天分文件、线程安全。
/// 日志目录：<c>%TEMP%\ForzaGallerySync\logs</c>（app-yyyyMMdd.log）。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string LogDirectory =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ForzaGallerySync", "logs");
    private static readonly string LogFile =
        System.IO.Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Debug(string message) => Write("DEBUG", message);

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    /// <summary>记录异常（上下文 + 完整堆栈）。</summary>
    public static void Exception(string context, Exception ex) =>
        Error($"{context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                System.IO.File.AppendAllText(
                    LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 写日志失败不影响主流程。
        }
    }
}
