using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SE2VR;

/// <summary>
/// Minimal thread-safe logger. Writes to SE2VR.log next to the plugin and mirrors every line into the game's own log
/// so problems can be correlated with engine messages.
/// </summary>
internal static class Log
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;

    public static bool Verbose { get; set; }

    public static void Initialize(string path)
    {
        lock (_lock)
        {
            try
            {
                _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception == null ? message : $"{message}{Environment.NewLine}{exception}");

    public static void Debug(string message)
    {
        if (Verbose)
        {
            Write("DEBUG", message);
        }
    }

    public static void Close()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [T{Thread.CurrentThread.ManagedThreadId}] {message}";
        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // Logging must never take the game down.
            }
        }
        try
        {
            Keen.VRage.Library.Diagnostics.Log.Default?.WriteLine("[SE2VR] " + message);
        }
        catch
        {
            // The engine log may not exist yet (very early start-up) or already be gone (shutdown).
        }
    }
}
