using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace GDShrapt.LSP;

/// <summary>
/// Opt-in file-based performance trace for diagnosing LSP hangs.
/// Writes only when a path is configured: env <c>GDSHRAPT_LSP_TRACE</c> (read once at startup) or
/// <see cref="Configure"/>. No default file, existing content is never truncated.
/// </summary>
internal static class GDLspPerformanceTrace
{
    private static string? _logPath = Environment.GetEnvironmentVariable("GDSHRAPT_LSP_TRACE");
    private static readonly object _lock = new();
    private static readonly Stopwatch _uptime = Stopwatch.StartNew();

    /// <summary>Sets (or clears with null) the trace file.</summary>
    internal static void Configure(string? path) => Volatile.Write(ref _logPath, string.IsNullOrEmpty(path) ? null : path);

    public static string? LogPath => Volatile.Read(ref _logPath);

    public static void Log(string tag, string message)
    {
        var path = Volatile.Read(ref _logPath);
        if (path == null)
            return;

        var line = $"[{_uptime.Elapsed:mm\\:ss\\.fff}] [{tag}] {message}";
        lock (_lock)
        {
            try { File.AppendAllText(path, line + "\n"); }
            catch { /* best effort */ }
        }
    }

    public static void LogSlow(string tag, long elapsedMs, string details)
    {
        Log(tag, $"{elapsedMs}ms — {details}");
    }
}
