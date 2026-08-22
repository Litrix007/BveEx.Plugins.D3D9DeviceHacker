using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using HarmonyLib;
using SlimDX;
using SlimDX.Direct3D9;

namespace BveEx.Plugins.D3D9DeviceHacker.Patches
{
    /// <summary>
    /// BveTs 内部 d9（D3D9 包装）与 fp（Mesh/Material 包装）补丁。
    /// 把 Direct3D9/Device 替换为 Direct3D9Ex/DeviceEx，接管 Reset、SetDialogBoxMode、fp.a 资源构建。
    /// </summary>
    public static class D9Patch
    {
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BveVertex
        {
            public float X, Y, Z;
            public float Nx, Ny, Nz;
            public float U, V;
            public const VertexFormat Format = VertexFormat.Position | VertexFormat.Normal | VertexFormat.Texture1;
        }

        private static IntPtr ResolveWindowHandle(object handleObj)
        {
            if (handleObj == null)
            {
                var mainHwnd = Process.GetCurrentProcess().MainWindowHandle;
                return mainHwnd;
            }

            if (handleObj is IntPtr ptr && ptr != IntPtr.Zero) return ptr;
            if (handleObj is IWin32Window win && win.Handle != IntPtr.Zero) return win.Handle;
            if (handleObj is Control ctrl && ctrl.Handle != IntPtr.Zero) return ctrl.Handle;

            var prop = handleObj.GetType().GetProperty("Handle",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                var val = prop.GetValue(handleObj);
                if (val is IntPtr p && p != IntPtr.Zero) return p;
            }

            return Process.GetCurrentProcess().MainWindowHandle;
        }

        public static bool InitPrefix(object __instance, object A_0, bool A_1, Size A_2)
        {
            try
            {
                var d9Type = __instance.GetType();
                var fields = d9Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var direct3DField = fields.FirstOrDefault(f => typeof(Direct3D).IsAssignableFrom(f.FieldType));
                var deviceField = fields.FirstOrDefault(f => typeof(Device).IsAssignableFrom(f.FieldType));
                var ppField = fields.FirstOrDefault(f => f.FieldType == typeof(PresentParameters));

                if (direct3DField == null || deviceField == null || ppField == null)
                {
                    PluginLog.Error("InitPrefix 无法定位 D3D9 内部字段！");
                    return true;
                }

                var hwnd = ResolveWindowHandle(A_0);

                if (PluginConfig.UpgradeToD3D9Ex)
                {
                    // --- D3D9Ex 升级路径 ---
                    var d3d9Ex = new Direct3DEx();
                    direct3DField.SetValue(__instance, d3d9Ex);
                    var adapter = d3d9Ex.Adapters.DefaultAdapter.Adapter;
                    var deviceCaps = d3d9Ex.GetDeviceCaps(adapter, DeviceType.Hardware);
                    var createFlags = deviceCaps.VertexShaderVersion >= new Version(2, 0)
                        ? CreateFlags.HardwareVertexProcessing
                        : CreateFlags.SoftwareVertexProcessing;

                    var pp = new PresentParameters
                    {
                        SwapEffect = SwapEffect.Discard,
                        PresentFlags = PresentFlags.None,
                        DeviceWindowHandle = hwnd // 无论窗口还是全屏，均显式指定
                    };

                    var currentDisplayMode = d3d9Ex.Adapters[0].CurrentDisplayMode;
                    pp.Windowed = A_1 || !d3d9Ex.CheckDeviceType(adapter, DeviceType.Hardware,
                        currentDisplayMode.Format, currentDisplayMode.Format, false);

                    if (pp.Windowed)
                    {
                        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT rect) && rect.Width > 0 &&
                            rect.Height > 0)
                        {
                            pp.BackBufferWidth = rect.Width;
                            pp.BackBufferHeight = rect.Height;
                        }
                        else
                        {
                            pp.BackBufferWidth = A_2.Width > 0 ? A_2.Width : 800;
                            pp.BackBufferHeight = A_2.Height > 0 ? A_2.Height : 600;
                        }
                    }
                    else
                    {
                        pp.BackBufferFormat = currentDisplayMode.Format;
                        pp.BackBufferCount = 1;
                        pp.BackBufferWidth = A_2.Width != 0 ? A_2.Width : currentDisplayMode.Width;
                        pp.BackBufferHeight = A_2.Height != 0 ? A_2.Height : currentDisplayMode.Height;
                        pp.FullScreenRefreshRateInHertz = currentDisplayMode.RefreshRate;
                    }

                    if (d3d9Ex.CheckDeviceFormat(adapter, DeviceType.Hardware, currentDisplayMode.Format,
                            Usage.DepthStencil, ResourceType.Surface, Format.D24S8))
                    {
                        pp.EnableAutoDepthStencil = true;
                        pp.AutoDepthStencilFormat = Format.D24S8;
                    }

                    pp.PresentationInterval =
                        PluginConfig.EnableVSync ? PresentInterval.One : PresentInterval.Immediate;

                    DeviceEx deviceEx;
                    if (pp.Windowed)
                    {
                        // 窗口模式：DisplayModeEx 必须为 null
                        deviceEx = new DeviceEx(d3d9Ex, adapter, DeviceType.Hardware, hwnd, createFlags, pp);
                    }
                    else
                    {
                        // 全屏模式：必须提供 DisplayModeEx
                        var displayModeEx = new DisplayModeEx
                        {
                            Width = pp.BackBufferWidth,
                            Height = pp.BackBufferHeight,
                            Format = pp.BackBufferFormat,
                            RefreshRate = pp.FullScreenRefreshRateInHertz,
                            ScanlineOrdering = ScanlineOrdering.Progressive
                        };
                        deviceEx = new DeviceEx(d3d9Ex, adapter, DeviceType.Hardware, hwnd, createFlags, pp,
                            displayModeEx);
                    }

                    try
                    {
                        deviceEx.MaximumFrameLatency = PluginConfig.MaxFrameLatency;
                    }
                    catch
                    {
                    }

                    ppField.SetValue(__instance, pp);
                    deviceField.SetValue(__instance, deviceEx);
                }
                else
                {
                    // --- D3D9 原生路径（仅应用 VSync） ---
                    var d3d = (Direct3D)direct3DField.GetValue(__instance);
                    if (d3d == null) d3d = new Direct3D();

                    var adapter = d3d.Adapters.DefaultAdapter.Adapter;
                    var deviceCaps = d3d.GetDeviceCaps(adapter, DeviceType.Hardware);
                    var createFlags = deviceCaps.VertexShaderVersion >= new Version(2, 0)
                        ? CreateFlags.HardwareVertexProcessing
                        : CreateFlags.SoftwareVertexProcessing;

                    var pp = new PresentParameters
                    {
                        SwapEffect = SwapEffect.Discard,
                        DeviceWindowHandle = hwnd
                    };

                    var currentDisplayMode = d3d.Adapters[0].CurrentDisplayMode;
                    pp.Windowed = A_1 || !d3d.CheckDeviceType(adapter, DeviceType.Hardware,
                        currentDisplayMode.Format, currentDisplayMode.Format, false);

                    if (pp.Windowed)
                    {
                        pp.BackBufferWidth = 0;
                        pp.BackBufferHeight = 0;
                    }
                    else
                    {
                        pp.BackBufferFormat = currentDisplayMode.Format;
                        pp.BackBufferCount = 1;
                        pp.BackBufferWidth = A_2.Width != 0 ? A_2.Width : currentDisplayMode.Width;
                        pp.BackBufferHeight = A_2.Height != 0 ? A_2.Height : currentDisplayMode.Height;
                        pp.FullScreenRefreshRateInHertz = currentDisplayMode.RefreshRate;
                        pp.PresentFlags = PresentFlags.LockableBackBuffer;
                    }

                    if (d3d.CheckDeviceFormat(adapter, DeviceType.Hardware, currentDisplayMode.Format,
                            Usage.DepthStencil, ResourceType.Surface, Format.D24S8))
                    {
                        pp.EnableAutoDepthStencil = true;
                        pp.AutoDepthStencilFormat = Format.D24S8;
                    }

                    pp.PresentationInterval =
                        PluginConfig.EnableVSync ? PresentInterval.One : PresentInterval.Immediate;
                    var device = new Device(d3d, adapter, DeviceType.Hardware, hwnd, createFlags, pp);

                    ppField.SetValue(__instance, pp);
                    deviceField.SetValue(__instance, device);
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("InitPrefix 异常", ex);
                return true;
            }
        }

        public static bool ResetPrefix(object __instance)
        {
            try
            {
                var d9Type = __instance.GetType();
                var fields = d9Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var deviceField = fields.FirstOrDefault(f => typeof(Device).IsAssignableFrom(f.FieldType));
                var ppField = fields.FirstOrDefault(f => f.FieldType == typeof(PresentParameters));

                if (deviceField == null || ppField == null) return true;

                var device = deviceField.GetValue(__instance) as Device;
                var pp = ppField.GetValue(__instance) as PresentParameters;

                if (device == null || pp == null) return true;

                if (pp.Windowed)
                {
                    var hwnd = pp.DeviceWindowHandle;
                    if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT rect) && rect.Width > 0 && rect.Height > 0)
                    {
                        pp.BackBufferWidth = rect.Width;
                        pp.BackBufferHeight = rect.Height;
                    }
                }

                pp.PresentationInterval =
                    PluginConfig.EnableVSync ? PresentInterval.One : PresentInterval.Immediate;

                if (device is DeviceEx deviceEx)
                {
                    // D3D9Ex 使用 ResetEx
                    if (pp.Windowed)
                    {
                        deviceEx.ResetEx(pp);
                    }
                    else
                    {
                        var displayModeEx = new DisplayModeEx
                        {
                            Width = pp.BackBufferWidth,
                            Height = pp.BackBufferHeight,
                            Format = pp.BackBufferFormat,
                            RefreshRate = pp.FullScreenRefreshRateInHertz,
                            ScanlineOrdering = ScanlineOrdering.Progressive
                        };
                        deviceEx.ResetEx(pp, displayModeEx);
                    }

                    try
                    {
                        deviceEx.MaximumFrameLatency = PluginConfig.MaxFrameLatency;
                    }
                    catch
                    {
                        // ignored
                    }
                }
                else
                {
                    var coop = device.TestCooperativeLevel();
                    if (coop != ResultCode.DeviceLost)
                    {
                        device.Reset(pp);
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("ResetPrefix 异常", ex);
            }

            return false;
        }

        public static bool SetDialogBoxModePrefix(Device __instance, bool enableDialogs) => false;

        public static bool FpAPrefix(ref object __result, RectangleF A_0, float A_1, Stream A_2)
        {
            try
            {
                if (A_2 == null)
                    A_2 = typeof(d9).Assembly.GetManifestResourceStream("Mackoy.Bvets.Resources.Title.png");

                var fpObj = new fp();
                var data = new short[6] { 0, 1, 2, 1, 3, 2 };

                var device = d9.d().e();

                var mesh = new Mesh(device, data.Length / 3, 4, 0, BveVertex.Format);
                fpObj.b(mesh);

                using (var vertexBuffer = mesh.VertexBuffer)
                {
                    var dataStream = vertexBuffer.Lock(0, 0, LockFlags.None);
                    var vertices = new BveVertex[4]
                    {
                        new BveVertex { X = A_0.Left, Y = A_0.Top, Z = A_1, Nx = 0, Ny = 0, Nz = -1, U = 0, V = 0 },
                        new BveVertex { X = A_0.Right, Y = A_0.Top, Z = A_1, Nx = 0, Ny = 0, Nz = -1, U = 1, V = 0 },
                        new BveVertex { X = A_0.Left, Y = A_0.Bottom, Z = A_1, Nx = 0, Ny = 0, Nz = -1, U = 0, V = 1 },
                        new BveVertex { X = A_0.Right, Y = A_0.Bottom, Z = A_1, Nx = 0, Ny = 0, Nz = -1, U = 1, V = 1 }
                    };
                    dataStream.WriteRange(vertices);
                    vertexBuffer.Unlock();
                }

                using (var indexBuffer = mesh.IndexBuffer)
                {
                    var dataStream = indexBuffer.Lock(0, 0, LockFlags.None);
                    dataStream.WriteRange(data);
                    indexBuffer.Unlock();
                }

                var e0Array = new e0[1];
                var e0Instance = new e0(Color.White);
                e0Array[0] = e0Instance;
                fpObj.a(e0Array);

                if (A_2 != null)
                {
                    var tex = Texture.FromStream(device, A_2, 0, 0, 0, Usage.None, Format.Unknown, Pool.Default,
                        Filter.None, Filter.None, 0);
                    e0Instance.a(tex);
                }

                __result = fpObj;
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("FpAPrefix 异常", ex);
                return true;
            }
        }

        public static bool FpAStringPrefix(ref object __result, string A_0)
        {
            try
            {
                var fpObj = new fp();
                var device = d9.d().e();

                var loadedMesh = Mesh.FromFile(device, A_0, 0);
                var materials = loadedMesh.GetMaterials();

                var e0Array = new e0[materials.Length];

                for (var i = 0; i < materials.Length; ++i)
                {
                    var materialD3D = materials[i].MaterialD3D;
                    materialD3D.Ambient = materialD3D.Diffuse;
                    var e0Inst = new e0(materialD3D);
                    e0Inst.a((double)materialD3D.Diffuse.Alpha < 1.0);

                    if (!string.IsNullOrEmpty(materials[i].TextureFileName))
                    {
                        var texPath = materials[i].TextureFileName;
                        if (string.IsNullOrEmpty(Path.GetDirectoryName(texPath)))
                            texPath = Path.Combine(Path.GetDirectoryName(A_0), texPath);

                        if (File.Exists(texPath))
                        {
                            var colorKey = Path.GetExtension(texPath).ToLower() == ".bmp" ? Color.Black.ToArgb() : 0;
                            var tex = Texture.FromFile(device, texPath, 0, 0, 0, Usage.None, Format.Unknown,
                                Pool.Default, Filter.None, Filter.None, colorKey);
                            e0Inst.a(tex);
                        }
                    }

                    e0Array[i] = e0Inst;
                }

                if ((loadedMesh.VertexFormat & VertexFormat.Normal) == VertexFormat.None ||
                    (loadedMesh.VertexFormat & VertexFormat.Texture1) == VertexFormat.None)
                {
                    var newFvf = loadedMesh.VertexFormat | VertexFormat.Normal | VertexFormat.Texture1;

                    var cloneFlags = (MeshFlags)0;
                    if (loadedMesh.VertexCount > 65535 || loadedMesh.FaceCount > 65535 ||
                        (loadedMesh.CreationOptions & MeshFlags.Use32Bit) != 0)
                    {
                        cloneFlags |= MeshFlags.Use32Bit;
                    }

                    var clonedMesh = loadedMesh.Clone(device, cloneFlags, newFvf);

                    if ((loadedMesh.VertexFormat & VertexFormat.Normal) == VertexFormat.None)
                        clonedMesh.ComputeNormals();

                    loadedMesh.Dispose();
                    loadedMesh = clonedMesh;
                }

                fpObj.b(loadedMesh);
                fpObj.a(e0Array);
                fpObj.b();

                __result = fpObj;
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("FpAStringPrefix 异常", ex);
                return true;
            }
        }
    }
}