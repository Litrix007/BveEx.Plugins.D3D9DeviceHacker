using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using HarmonyLib;
using MonoMod.RuntimeDetour;
using SlimDX.Direct3D9;

namespace BveEx.Plugins.D3D9DeviceHacker.Patches
{
    /// <summary>
    ///     精准 Patch 托管 SlimDX API：将 Pool.Managed 改写为 Pool.Default，
    ///     并清除 Mesh 的 Managed 标志位。D3D9Ex 下 DeviceEx 不会进入设备丢失状态，
    ///     Pool.Default 直接驻留显存性能更高。
    /// </summary>
    public static class TextureAndMeshPatcher
    {
        public static readonly ConcurrentDictionary<MethodBase, int> TexturePoolIndices =
            new ConcurrentDictionary<MethodBase, int>();

        public static readonly ConcurrentDictionary<MethodBase, int> MeshFlagsIndices =
            new ConcurrentDictionary<MethodBase, int>();

        private static Hook _textureCtorHook;
        private static Hook _meshCtorHook;
        private static Hook _meshCtorNoPoolHook;

        public static void ApplyHarmony(Harmony harmony)
        {
            var textureType = typeof(Texture);
            var meshType = typeof(Mesh);
            var baseMeshType = meshType.BaseType;

            var textureFixPrefix =
                new HarmonyMethod(AccessTools.Method(typeof(TextureAndMeshPatcher), nameof(FixTexturePoolPrefix)));
            var meshFixPrefix =
                new HarmonyMethod(AccessTools.Method(typeof(TextureAndMeshPatcher), nameof(FixMeshFlagsPrefix)));

            var fromFile1 = textureType.GetMethod("FromFile", new[] { typeof(Device), typeof(string) });
            var fromFile2 = textureType.GetMethod("FromFile",
                new[] { typeof(Device), typeof(string), typeof(Usage), typeof(Pool) });
            var fromFile3 = textureType.GetMethod("FromFile",
                new[]
                {
                    typeof(Device), typeof(string), typeof(int), typeof(int), typeof(int), typeof(Usage),
                    typeof(Format), typeof(Pool), typeof(Filter), typeof(Filter), typeof(int)
                });
            if (fromFile1 != null)
            {
                TexturePoolIndices[fromFile1] = -1;
                SafePatch(harmony, fromFile1, textureFixPrefix);
            }

            if (fromFile2 != null)
            {
                TexturePoolIndices[fromFile2] = 3;
                SafePatch(harmony, fromFile2, textureFixPrefix);
            }

            if (fromFile3 != null)
            {
                TexturePoolIndices[fromFile3] = 7;
                SafePatch(harmony, fromFile3, textureFixPrefix);
            }

            var fromStream1 = textureType.GetMethod("FromStream", new[] { typeof(Device), typeof(Stream) });
            var fromStream2 = textureType.GetMethod("FromStream",
                new[] { typeof(Device), typeof(Stream), typeof(Usage), typeof(Pool) });
            var fromStream3 = textureType.GetMethod("FromStream",
                new[]
                {
                    typeof(Device), typeof(Stream), typeof(int), typeof(int), typeof(int), typeof(Usage),
                    typeof(Format), typeof(Pool), typeof(Filter), typeof(Filter), typeof(int)
                });
            if (fromStream1 != null)
            {
                TexturePoolIndices[fromStream1] = -1;
                SafePatch(harmony, fromStream1, textureFixPrefix);
            }

            if (fromStream2 != null)
            {
                TexturePoolIndices[fromStream2] = 3;
                SafePatch(harmony, fromStream2, textureFixPrefix);
            }

            if (fromStream3 != null)
            {
                TexturePoolIndices[fromStream3] = 7;
                SafePatch(harmony, fromStream3, textureFixPrefix);
            }

            var meshFromFile =
                meshType.GetMethod("FromFile", new[] { typeof(Device), typeof(string), typeof(MeshFlags) });
            if (meshFromFile != null)
            {
                MeshFlagsIndices[meshFromFile] = 2;
                SafePatch(harmony, meshFromFile, meshFixPrefix);
            }

            if (baseMeshType != null)
            {
                var clone1 = baseMeshType.GetMethod("Clone",
                    new[] { typeof(Device), typeof(MeshFlags), typeof(VertexFormat) });
                var clone2 = baseMeshType.GetMethod("Clone",
                    new[] { typeof(Device), typeof(MeshFlags), typeof(VertexFormat), typeof(Pool) });
                if (clone1 != null)
                {
                    MeshFlagsIndices[clone1] = 1;
                    SafePatch(harmony, clone1, meshFixPrefix);
                }

                if (clone2 != null)
                {
                    MeshFlagsIndices[clone2] = 1;
                    SafePatch(harmony, clone2, meshFixPrefix);
                }
            }
        }

        private static void SafePatch(Harmony harmony, MethodBase method, HarmonyMethod prefix)
        {
            try
            {
                harmony.Patch(method, prefix);
                PluginLog.Debug($"[D3D9Ex] Harmony 成功: {method.DeclaringType.Name}.{method.Name}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"[D3D9Ex] Harmony 跳过: {method.DeclaringType.Name}.{method.Name}", ex);
            }
        }

        public static bool FixTexturePoolPrefix(MethodBase __originalMethod, object[] __args, ref object __result)
        {
            if (__args == null || __originalMethod == null) return true;
            if (__originalMethod.Name == "FromFile" && __args.Length == 2)
            {
                __result = Texture.FromFile((Device)__args[0], (string)__args[1], Usage.None, Pool.Default);
                return false;
            }

            if (__originalMethod.Name == "FromStream" && __args.Length == 2)
            {
                __result = Texture.FromStream((Device)__args[0], (Stream)__args[1], Usage.None, Pool.Default);
                return false;
            }

            if (TexturePoolIndices.TryGetValue(__originalMethod, out var index))
                if (index >= 0 && index < __args.Length && __args[index] is Pool pool && pool == Pool.Managed)
                    __args[index] = Pool.Default;

            return true;
        }

        public static bool FixMeshFlagsPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (__args == null || __originalMethod == null) return true;
            if (MeshFlagsIndices.TryGetValue(__originalMethod, out var index))
                if (index >= 0 && index < __args.Length && __args[index] is MeshFlags flags)
                {
                    var newFlags = flags & ~MeshFlags.Managed & ~MeshFlags.VertexBufferManaged &
                                   ~MeshFlags.IndexBufferManaged;
                    if (newFlags != flags) __args[index] = newFlags;
                }

            return true;
        }

        public static void ApplyConstructorHooks()
        {
            var texCtor = typeof(Texture).GetConstructor(new[]
            {
                typeof(Device), typeof(int), typeof(int), typeof(int),
                typeof(Usage), typeof(Format), typeof(Pool)
            });
            if (texCtor != null)
                try
                {
                    _textureCtorHook = new Hook(
                        texCtor,
                        new Action<Action<Texture, Device, int, int, int, Usage, Format, Pool>,
                            Texture, Device, int, int, int, Usage, Format, Pool>(TextureCtorDetour)
                    );
                    PluginLog.Debug("[D3D9Ex] MonoMod Hook 成功: Texture..ctor");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("[D3D9Ex] MonoMod Hook 失败: Texture..ctor", ex);
                }

            var meshCtor = typeof(Mesh).GetConstructor(new[]
            {
                typeof(Device), typeof(int), typeof(int),
                typeof(MeshFlags), typeof(VertexFormat), typeof(Pool)
            });
            if (meshCtor != null)
                try
                {
                    _meshCtorHook = new Hook(
                        meshCtor,
                        new Action<Action<Mesh, Device, int, int, MeshFlags, VertexFormat, Pool>,
                            Mesh, Device, int, int, MeshFlags, VertexFormat, Pool>(MeshCtorDetour)
                    );
                    PluginLog.Debug("[D3D9Ex] MonoMod Hook 成功: Mesh..ctor (带Pool)");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("[D3D9Ex] MonoMod Hook 失败: Mesh..ctor (带Pool)", ex);
                }

            var meshCtorNoPool = typeof(Mesh).GetConstructor(new[]
            {
                typeof(Device), typeof(int), typeof(int),
                typeof(MeshFlags), typeof(VertexFormat)
            });
            if (meshCtorNoPool != null)
                try
                {
                    _meshCtorNoPoolHook = new Hook(
                        meshCtorNoPool,
                        new Action<Action<Mesh, Device, int, int, MeshFlags, VertexFormat>,
                            Mesh, Device, int, int, MeshFlags, VertexFormat>(MeshCtorNoPoolDetour)
                    );
                    PluginLog.Debug("[D3D9Ex] MonoMod Hook 成功: Mesh..ctor (无Pool)");
                }
                catch (Exception ex)
                {
                    PluginLog.Error("[D3D9Ex] MonoMod Hook 失败: Mesh..ctor (无Pool)", ex);
                }
        }

        private static void TextureCtorDetour(
            Action<Texture, Device, int, int, int, Usage, Format, Pool> orig,
            Texture self,
            Device device, int width, int height, int levelCount,
            Usage usage, Format format, Pool pool)
        {
            if (pool == Pool.Managed) pool = Pool.Default;

            orig(self, device, width, height, levelCount, usage, format, pool);
        }

        private static void MeshCtorDetour(
            Action<Mesh, Device, int, int, MeshFlags, VertexFormat, Pool> orig,
            Mesh self,
            Device device, int faceCount, int vertexCount,
            MeshFlags flags, VertexFormat fvf, Pool pool)
        {
            flags = RemoveManagedFlags(flags);
            if (pool == Pool.Managed) pool = Pool.Default;

            orig(self, device, faceCount, vertexCount, flags, fvf, pool);
        }

        private static void MeshCtorNoPoolDetour(
            Action<Mesh, Device, int, int, MeshFlags, VertexFormat> orig,
            Mesh self,
            Device device, int faceCount, int vertexCount,
            MeshFlags flags, VertexFormat fvf)
        {
            flags = RemoveManagedFlags(flags);
            orig(self, device, faceCount, vertexCount, flags, fvf);
        }

        private static MeshFlags RemoveManagedFlags(MeshFlags flags)
        {
            return flags & ~(MeshFlags.Managed | MeshFlags.VertexBufferManaged | MeshFlags.IndexBufferManaged);
        }
    }
}