using System;
using System.IO;

namespace BveEx.Plugins.D3D9DeviceHacker
{
    /// <summary>
    ///     轻量级 key=value 文本配置。启动时一次性加载，文件缺失时自动生成示例。
    ///     解析失败/字段非法时回退到编译期默认值，不抛异常。
    /// </summary>
    public static class PluginConfig
    {
        public const bool WaitForDebuggerDefault = false;
        public const bool EnableVSyncDefault = true;
        public const int MaxFrameLatencyDefault = 3;
        public const LogLevel LogLevelDefault = LogLevel.Info;
        public const bool UpgradeToD3D9ExDefault = true;

        private const string DefaultConfigText =
            @"# BveEx D3D9Ex 插件配置
# 是否升级到 D3D9Ex (true=升级到D3D9Ex并应用帧延迟/纹理/DXDT补丁, false=保持D3D9仅应用VSync)
UpgradeToD3D9Ex=true
# 开启垂直同步 (默认为true)
EnableVSync=true
# 最大帧延迟缓冲 (1-5)，默认为3，仅在D3D9Ex下生效
MaxFrameLatency=3
# 是否在启动时等待调试器附加（正常情况下请保持false）
WaitForDebugger=false
# 日志级别: Debug / Info / Warning / Error / None（None 表示不输出任何日志）
LogLevel=Info
";

        public static bool WaitForDebugger { get; private set; }
        public static bool EnableVSync { get; private set; }
        public static int MaxFrameLatency { get; private set; }
        public static LogLevel LogLevel { get; private set; }
        public static bool UpgradeToD3D9Ex { get; private set; }
        public static string ConfigPath { get; private set; }

        public static void Load(string configDirectory)
        {
            ConfigPath = Path.Combine(configDirectory, "D3D9DeviceHacker.ini");

            WaitForDebugger = WaitForDebuggerDefault;
            EnableVSync = EnableVSyncDefault;
            MaxFrameLatency = MaxFrameLatencyDefault;
            LogLevel = LogLevelDefault;
            UpgradeToD3D9Ex = UpgradeToD3D9ExDefault;

            if (File.Exists(ConfigPath))
            {
                LoadConfig(ConfigPath);
                return;
            }

            TryWriteDefaultConfig();
        }

        private static void LoadConfig(string path)
        {
            try
            {
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line.StartsWith("//"))
                        continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "UpgradeToD3D9Ex": UpgradeToD3D9Ex = ParseBool(val, UpgradeToD3D9ExDefault); break;
                        case "EnableVSync": EnableVSync = ParseBool(val, EnableVSyncDefault); break;
                        case "MaxFrameLatency": MaxFrameLatency = ParseInt(val, MaxFrameLatencyDefault, 1, 5); break;
                        case "WaitForDebugger": WaitForDebugger = ParseBool(val, WaitForDebuggerDefault); break;
                        case "LogLevel": LogLevel = ParseEnum(val, LogLevelDefault); break;
                    }
                }
            }
            catch
            {
                // 解析失败保留默认值
            }
        }

        private static bool ParseBool(string s, bool def)
        {
            return bool.TryParse(s, out var v) ? v : def;
        }

        private static int ParseInt(string s, int def, int min, int max)
        {
            if (!int.TryParse(s, out var v)) return def;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static T ParseEnum<T>(string s, T def) where T : struct
        {
            return Enum.TryParse(s, true, out T v) ? v : def;
        }

        private static void TryWriteDefaultConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, DefaultConfigText);
            }
            catch
            {
                /* 静默 */
            }
        }
    }
}