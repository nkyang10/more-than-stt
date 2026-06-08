using System;
using System.IO;

namespace CantoneseDictation;

public static class AppLogger
{
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        $"dictation_{DateTime.Now:yyyyMMdd_HHmmss}.log"
    );

    private static readonly object _lock = new();

    static AppLogger()
    {
        try { File.WriteAllText(LogPath, $"=== Dictation Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n"); }
        catch { }
    }

    public static void Info(string msg)
    {
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] INFO  {msg}\r\n"); }
            catch { }
        }
    }

    public static void Warn(string msg)
    {
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] WARN  {msg}\r\n"); }
            catch { }
        }
    }

    public static void Error(string msg, Exception? ex = null)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] ERROR {msg}\r\n");
                if (ex != null)
                    File.AppendAllText(LogPath, $"  Exception: {ex.GetType().Name}: {ex.Message}\r\n" +
                                                 $"  StackTrace: {ex.StackTrace}\r\n");
            }
            catch { }
        }
    }

    public static string GetLogPath() => LogPath;
}
