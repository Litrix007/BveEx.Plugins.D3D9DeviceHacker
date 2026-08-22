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
    /// 轻量级日志器：
    /// - 启动时清空日志文件（不滚动）
    /// - 多线程安全（lock）
    /// - 按级别过滤
    /// - StreamWriter 复用 + AutoFlush，避免每次开关文件
    /// </summary>
    public static class PluginLog
    {
        private static readonly object _gate = new object();
        private static StreamWriter _writer;
        private static LogLevel _minLevel = LogLevel.Info;
        private static string _logPath;

        public static void Init(string logPath, LogLevel minLevel)
        {
            lock (_gate)
            {
                _logPath = logPath;
                _minLevel = minLevel;

                try
                {
                    if (_minLevel != LogLevel.None)
                    {
                        var dir = Path.GetDirectoryName(logPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);

                        // 插件加载时清空日志，不滚动
                        _writer = new StreamWriter(logPath, append: false, Encoding.UTF8)
                        {
                            AutoFlush = true
                        };
                    }
                    else
                    {
                        // None：不创建日志文件，也不输出任何日志
                        _writer = null;
                    }
                }
                catch
                {
                    _writer = null;
                }
            }
        }

        public static void Debug(string msg) => Write(LogLevel.Debug, msg, null);
        public static void Info(string msg) => Write(LogLevel.Info, msg, null);
        public static void Warning(string msg, Exception ex = null) => Write(LogLevel.Warning, msg, ex);
        public static void Error(string msg, Exception ex = null) => Write(LogLevel.Error, msg, ex);

        private static void Write(LogLevel level, string msg, Exception ex)
        {
            if (_minLevel == LogLevel.None || level < _minLevel) return;

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

            lock (_gate)
            {
                if (_writer != null)
                {
                    try
                    {
                        _writer.WriteLine(line);
                        return;
                    }
                    catch
                    {
                        /* fallthrough to fallback */
                    }
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

        public static void Close()
        {
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}