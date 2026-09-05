using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using BveEx.PluginHost.Plugins;
using BveEx.PluginHost.Plugins.Extensions;
using BveEx.Plugins.D3D9DeviceHacker.Patches;
using BveTypes.ClassWrappers;
using HarmonyLib;
using SlimDX.Direct3D9;

namespace BveEx.Plugins.D3D9DeviceHacker
{
    [Plugin(PluginType.Extension)]
    public class PluginMain : AssemblyPluginBase, IExtension
    {
        private readonly Harmony _harmony;

        public PluginMain(PluginBuilder builder) : base(builder)
        {
            // 配置与日志统一存放在 %LOCALAPPDATA%\BveEx.Plugins.D3D9DeviceHacker
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BveEx.Plugins.D3D9DeviceHacker");

            // 1. 加载配置（缺失时自动生成默认配置，失败也用默认值，不抛异常）
            PluginConfig.Load(dataDir);

            // 2. 初始化日志（按 PID 分文件，避免多实例互斥锁死；Init 内部自带容错）
            var logPath = Path.Combine(dataDir, "logs", $"D3D9DeviceHacker.{Process.GetCurrentProcess().Id}.log");
            PluginLog.Init(logPath, PluginConfig.LogLevel);

            // 3. 订阅首发异常
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

            // 4. 仅在用户显式请求时弹调试器（生产环境默认 false）
            if (PluginConfig.WaitForDebugger && !Debugger.IsAttached)
                Debugger.Launch();

            PluginLog.Info($"[D3D9Ex] 插件加载: 配置={PluginConfig.ConfigPath}, 日志={logPath}");
            PluginLog.Info(
                $"[D3D9Ex] 配置: EnableVSync={PluginConfig.EnableVSync}, "
                + $"MaxFrameLatency={PluginConfig.MaxFrameLatency}, UpgradeToD3D9Ex={PluginConfig.UpgradeToD3D9Ex}");

            // 5. 执行 Patch
            _harmony = new Harmony("BveEx.Plugins.D3D9DeviceHacker.Upgrade");
            ExecuteUpgrade();
        }

        private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            PluginLog.Error("检测到首发异常: ", e.Exception);
        }

        private void ExecuteUpgrade()
        {
            try
            {
                if (PluginConfig.UpgradeToD3D9Ex)
                {
                    // --- D3D9Ex 升级路径：应用全部补丁 ---
                    // 1. Harmony 常规资源补丁（静态工厂 + 实例方法）
                    TextureAndMeshPatcher.ApplyHarmony(_harmony);

                    // 2. MonoMod Hook 钩 SlimDX 构造函数（C++/CLI 无法用 Harmony）
                    TextureAndMeshPatcher.ApplyConstructorHooks();

                    // 3. 自适应多程序集动态扫描（DXDynamicTexture 可能延后加载）
                    ScanAndPatchDXDynamicTexture();

                    // 4. d9 初始化/Reset + SetDialogBoxMode + fp 加载等补丁
                    PatchD9AndFp();

                    PluginLog.Info("[D3D9Ex] D3D9Ex 升级部署完成");
                }
                else
                {
                    // --- D3D9 原生路径：仅应用设备初始化/Reset 补丁 ---
                    PatchD9InitAndReset();
                    PluginLog.Info("[D3D9Ex] D3D9 原生模式部署完成（仅 VSync）");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("部署 Hook 失败", ex);
            }
        }

        private void ScanAndPatchDXDynamicTexture()
        {
            try
            {
                var currentAsms = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.GetName().Name.Contains("DXDynamicTexture"));
                foreach (var asm in currentAsms) PatchDXDynamicTextureTarget(asm);

                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }
            catch (Exception ex)
            {
                PluginLog.Error("扫描并部署 DXDynamicTexture 拦截器失败", ex);
            }
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var asmName = args.LoadedAssembly.GetName().Name;
                if (asmName.Contains("DXDynamicTexture")) PatchDXDynamicTextureTarget(args.LoadedAssembly);
            }
            catch (Exception ex)
            {
                PluginLog.Error("动态装载拦截发生错误", ex);
            }
        }

        private void PatchDXDynamicTextureTarget(Assembly assembly)
        {
            try
            {
                var types = GetLoadableTypes(assembly);

                // 1. 拦截 TextureHandle.GetOrCreate 方法
                var handleType = types.FirstOrDefault(t => t.Name == "TextureHandle" &&
                                                           t.GetMethod("GetOrCreate",
                                                               BindingFlags.Public | BindingFlags.NonPublic |
                                                               BindingFlags.Instance) != null);

                if (handleType != null)
                {
                    var getOrCreateMethod = handleType.GetMethod("GetOrCreate",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (getOrCreateMethod != null)
                        _harmony.Patch(getOrCreateMethod,
                            new HarmonyMethod(AccessTools.Method(typeof(DXDynamicTexturePatch),
                                nameof(DXDynamicTexturePatch.GetOrCreatePrefix))));

                    // 2. 拦截 TextureHandle(Texture) 构造函数
                    var texCtors =
                        handleType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance);
                    foreach (var ctor in texCtors)
                    {
                        var parameters = ctor.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType.Name == "Texture")
                            _harmony.Patch(ctor,
                                new HarmonyMethod(AccessTools.Method(typeof(DXDynamicTexturePatch),
                                    nameof(DXDynamicTexturePatch.TextureHandleCtorPrefix))));
                    }
                }

                // 3. 拦截静态 Register 方法
                foreach (var type in types)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    foreach (var method in methods)
                        if (method.Name == "Register")
                        {
                            var parameters = method.GetParameters();
                            // Model.Register(Model model, string textureFileName)
                            if (parameters.Length == 2 && parameters[1].ParameterType == typeof(string) &&
                                parameters[0].ParameterType == typeof(Model))
                                _harmony.Patch(method,
                                    new HarmonyMethod(AccessTools.Method(typeof(DXDynamicTexturePatch),
                                        nameof(DXDynamicTexturePatch.ModelRegisterPrefix))));
                            // Texture.Register(Texture texture)
                            else if (parameters.Length == 1 && parameters[0].ParameterType.Name == "Texture")
                                _harmony.Patch(method,
                                    new HarmonyMethod(AccessTools.Method(typeof(DXDynamicTexturePatch),
                                        nameof(DXDynamicTexturePatch.TextureRegisterPrefix))));
                        }
                }

                PluginLog.Debug($"[D3D9Ex] 成功部署 DXDT 拦截器到程序集: {assembly.GetName().Name}");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"PatchDXDynamicTextureTarget ({assembly.GetName().Name}) 失败", ex);
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }

        private void PatchD9InitAndReset()
        {
            var initMethod = typeof(d9).GetMethod("a", new[] { typeof(Control), typeof(bool), typeof(Size) });
            if (initMethod != null)
                _harmony.Patch(initMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(D9Patch), nameof(D9Patch.InitPrefix))));

            var resetMethod = typeof(d9).GetMethod("h");
            if (resetMethod != null)
                _harmony.Patch(resetMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(D9Patch), nameof(D9Patch.ResetPrefix))));
        }

        private void PatchD9AndFp()
        {
            PatchD9InitAndReset();

            var setDialogBoxModeMethod = typeof(Device).GetMethod("SetDialogBoxMode");
            if (setDialogBoxModeMethod != null)
                _harmony.Patch(setDialogBoxModeMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(D9Patch),
                        nameof(D9Patch.SetDialogBoxModePrefix))));

            var fpAMethod = typeof(fp).GetMethod("a", new[] { typeof(RectangleF), typeof(float), typeof(Stream) });
            if (fpAMethod != null)
                _harmony.Patch(fpAMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(D9Patch), nameof(D9Patch.FpAPrefix))));

            var fpAStringMethod = typeof(fp).GetMethod("a", new[] { typeof(string) });
            if (fpAStringMethod != null)
                _harmony.Patch(fpAStringMethod,
                    new HarmonyMethod(AccessTools.Method(typeof(D9Patch), nameof(D9Patch.FpAStringPrefix))));
        }

        public override void Dispose()
        {
            PluginLog.Close();
        }

        public override void Tick(TimeSpan elapsed)
        {
        }
    }
}