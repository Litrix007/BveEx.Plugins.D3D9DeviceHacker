using System;
using System.IO;
using System.Text;

namespace BveEx.Plugins.D3D9DeviceHacker
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        None
    }

    /// <summary>
    ///     轻量级日志器：
    ///     - 启动时清空日志文件（不滚动）
    ///     - 多线程安全（lock）
    ///     - 按级别过滤
    ///     - StreamWriter 复用 + AutoFlush，避免每次开关文件
    /// </summary>
    public static class PluginLog
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static LogLevel _minLevel = LogLevel.Info;
        private static string _logPath;

        // 重入守卫：FirstChanceException 会在 catch 之前触发，
        // 若 Write 内部抛异常 → OnFirstChanceException → PluginLog.Error → Write → 无限递归 → StackOverflow。
        // 用 ThreadStatic 标记当前线程是否已在 Write 内部，阻断递归。
        [ThreadStatic] private static bool _inWrite;

        public static void Init(string logPath, LogLevel minLevel)
        {
            lock (Gate)
            {
                _logPath = logPath;
                _minLevel = minLevel;

                try
                {
                    var dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    CleanupOldLogs(dir);

                    if (_minLevel != LogLevel.None)
                        _writer = new StreamWriter(logPath, false, Encoding.UTF8)
                        {
                            AutoFlush = true
                        };
                    else
                        _writer = null;
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public static void Debug(string msg)
        {
            Write(LogLevel.Debug, msg, null);
        }

        public static void Info(string msg)
        {
            Write(LogLevel.Info, msg, null);
        }

        public static void Warning(string msg, Exception ex = null)
        {
            Write(LogLevel.Warning, msg, ex);
        }

        public static void Error(string msg, Exception ex = null)
        {
            Write(LogLevel.Error, msg, ex);
        }

        private static void Write(LogLevel level, string msg, Exception ex)
        {
            if (_inWrite) return;
            if (_minLevel == LogLevel.None || level < _minLevel) return;

            _inWrite = true;
            try
            {
                var sb = new StringBuilder(256);
                sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("] ");
                sb.Append('[').Append(level.ToString().ToUpperInvariant()).Append("] ");
                sb.Append(msg);
                if (ex != null)
                {
                    sb.Append("\n  异常类型: ").Append(ex.GetType().FullName);
                    sb.Append("\n  消息: ").Append(ex.Message);
                    sb.Append("\n  堆栈:\n").Append(ex.StackTrace);
                }

                var line = sb.ToString();

                lock (Gate)
                {
                    if (_writer != null)
                        try
                        {
                            _writer.WriteLine(line);
                            return;
                        }
                        catch
                        {
                            /* fallthrough to fallback */
                        }

                    try
                    {
                        File.AppendAllText(_logPath, line + Environment.NewLine);
                    }
                    catch
                    {
                        /* 静默：日志失败不应阻断主流程 */
                    }
                }
            }
            finally
            {
                _inWrite = false;
            }
        }

        public static void Close()
        {
            lock (Gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        private static void CleanupOldLogs(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var pattern in new[] { "D3D9DeviceHacker.*.log", "D3D9DeviceHacker.log" })
            foreach (var file in Directory.GetFiles(dir, pattern))
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    /* 被其他实例占用，跳过 */
                }
        }
    }
}