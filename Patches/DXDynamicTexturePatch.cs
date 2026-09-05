using System;
using System.Reflection;
using BveTypes.ClassWrappers;
using SlimDX.Direct3D9;

namespace BveEx.Plugins.D3D9DeviceHacker.Patches
{
    /// <summary>
    ///     DXDynamicTexture 靶向按需转换补丁（支持动态刷新与模型属性写回）。
    ///     将静态 Pool.Default 纹理转存为 Usage.Dynamic + Pool.Default，绕过显存无法直接 Lock 的限制。
    /// </summary>
    public static class DXDynamicTexturePatch
    {
        // 1. 拦截 GetOrCreate
        public static bool GetOrCreatePrefix(object __instance, Device device, ref Texture __result)
        {
            try
            {
                var type = __instance.GetType();

                var dxTextureProp = type.GetProperty("DXTexture",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dxTextureProp == null) return true;

                var isCreatedProp = type.GetProperty("IsCreated", BindingFlags.Public | BindingFlags.Instance);
                var isCreated = isCreatedProp != null && (bool)isCreatedProp.GetValue(__instance, null);
                if (isCreated)
                {
                    __result = dxTextureProp.GetValue(__instance, null) as Texture;
                    return false;
                }

                var width = (int)type.GetField("Width", BindingFlags.Public | BindingFlags.Instance)
                    .GetValue(__instance);
                var height = (int)type.GetField("Height", BindingFlags.Public | BindingFlags.Instance)
                    .GetValue(__instance);

                var texInstance = new Texture(device, width, height, 1, Usage.Dynamic, Format.A8R8G8B8, Pool.Default);

                dxTextureProp.SetValue(__instance, texInstance, null);

                var createdField = type.GetField("Created", BindingFlags.NonPublic | BindingFlags.Instance);
                if (createdField != null)
                {
                    var createdDel = (MulticastDelegate)createdField.GetValue(__instance);
                    if (createdDel != null)
                        foreach (var handler in createdDel.GetInvocationList())
                            try
                            {
                                handler.Method.Invoke(handler.Target, new[] { __instance, EventArgs.Empty });
                            }
                            catch
                            {
                                // ignored
                            }
                }

                __result = texInstance;
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error("GetOrCreatePrefix 拦截异常", ex);
                return true;
            }
        }

        // 2. 拦截 TextureHandle(Texture) 构造函数
        public static bool TextureHandleCtorPrefix(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length == 1 && __args[0] is Texture staticTex)
                {
                    var device = staticTex.Device;
                    if (device != null)
                    {
                        var dynamicTex = CreateDynamicCopy(staticTex, device);
                        if (dynamicTex != null) __args[0] = dynamicTex;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("TextureHandleCtorPrefix 异常", ex);
            }

            return true;
        }

        // 3. 拦截 Texture.Register(this Texture texture)
        public static bool TextureRegisterPrefix(ref Texture texture)
        {
            try
            {
                if (texture != null)
                {
                    var device = texture.Device;
                    if (device != null)
                    {
                        var dynamicTex = CreateDynamicCopy(texture, device);
                        if (dynamicTex != null) texture = dynamicTex;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("TextureRegisterPrefix 异常", ex);
            }

            return true;
        }

        // 4. 拦截 Model.Register(this Model model, string textureFileName)
        public static bool ModelRegisterPrefix(Model model, string textureFileName)
        {
            try
            {
                if (model == null || string.IsNullOrEmpty(textureFileName)) return true;

                var mesh = model.Mesh;
                if (mesh == null) return true;

                var extMaterials = mesh.GetMaterials();
                if (extMaterials == null) return true;
                var materials = model.Materials;
                if (materials == null) return true;
                for (var i = 0; i < extMaterials.Length && i < materials.Length; i++)
                {
                    var extMat = extMaterials[i];
                    if (extMat.TextureFileName == textureFileName)
                    {
                        var matInfo = materials[i];
                        if (matInfo == null) continue;

                        var staticTex = matInfo.Texture;
                        if (staticTex != null)
                        {
                            var device = staticTex.Device;
                            if (device != null)
                            {
                                var dynamicTex = CreateDynamicCopy(staticTex, device);
                                if (dynamicTex != null)
                                {
                                    matInfo.Texture = dynamicTex;
                                    PluginLog.Debug(
                                        $"[D3D9Ex] 成功将 Model [{textureFileName}] 材质[{i}] 转换为 Dynamic Texture!");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("ModelRegisterPrefix 异常", ex);
            }

            return true;
        }

        /// <summary>
        ///     将 Pool.Default 静态纹理转存并创建为 Usage.Dynamic + Pool.Default 纹理的核心函数。
        ///     使用 D3DX (Texture.ToStream) 绕过显存无法直接 Lock 的限制。
        /// </summary>
        public static Texture CreateDynamicCopy(Texture staticTex, Device device)
        {
            if (staticTex == null || device == null) return null;

            try
            {
                var desc = staticTex.GetLevelDescription(0);
                if ((desc.Usage & Usage.Dynamic) != 0) return staticTex;

                using (var imageStream = BaseTexture.ToStream(staticTex, ImageFileFormat.Bmp))
                {
                    if (imageStream == null) return null;

                    var dynamicTex = Texture.FromStream(
                        device, imageStream, desc.Width, desc.Height, 1,
                        Usage.Dynamic, desc.Format, Pool.Default,
                        Filter.None, Filter.None, 0
                    );

                    return dynamicTex;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("CreateDynamicCopy 转换异常", ex);
                return null;
            }
        }
    }
}