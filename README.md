# BveEx.Plugins.D3D9DeviceHacker

将 BVE Trainsim 5.8/6 的图形API从 Direct3D9 升级为 Direct3D9Ex。

目前主要配合[JREMonitors](https://github.com/Litrix007/JREMonitors)插件使用。

此外，本插件提供了一些其他的可配置选项。

## 安装方法

将所有文件直接移动到`C:\Users\Public\Documents\BveEx\2.0\Extensions`目录下。

## 配置说明

第一次运行时，插件会在**BVE程序安装目录**下自动生成配置文件`D3D9DeviceHacker.ini`，之后手动编辑后重启游戏生效。所有配置均为
`key=value`格式，`#`、`;`或`//`开头的行为注释，非法或解析失败的值会自动回退到默认值。

| 配置项               | 默认值     | 取值范围                                    | 说明                                                                                  |
|-------------------|---------|-----------------------------------------|-------------------------------------------------------------------------------------|
| `UpgradeToD3D9Ex` | `true`  | `true`/`false`                          | 是否将图形API升级为D3D9Ex。为`true`时升级并应用帧延迟、纹理、DXDT补丁；为`false`时保持原生D3D9，仅应用VSync和初始化/Reset补丁 |
| `EnableVSync`     | `true`  | `true`/`false`                          | 是否开启垂直同步                                                                            |
| `MaxFrameLatency` | `3`     | `1`~`16`                                | 最大帧延迟缓冲（帧队列），仅在D3D9Ex模式下生效                                                          |
| `EnableDebug`     | `false` | `true`/`false`                          | 是否输出调试日志（仅影响日志输出量，与是否弹出调试器无关）                                                       |
| `WaitForDebugger` | `false` | `true`/`false`                          | 是否在启动时等待调试器附加。用于开发者调试，正常情况下请保持`false`                                               |
| `LogLevel`        | `Info`  | `Debug`/`Info`/`Warning`/`Error`/`None` | 日志级别。`None`表示不输出任何日志                                                                |

### 示例

配置文件生成后内容如下，可按需修改：

```ini
# BveEx D3D9Ex 插件配置
# 是否升级到 D3D9Ex (true=升级到D3D9Ex并应用帧延迟/纹理/DXDT补丁, false=保持D3D9仅应用VSync)
UpgradeToD3D9Ex = true
# 开启垂直同步 (默认为true)
EnableVSync = true
# 最大帧延迟缓冲 (1-16)，默认为3，仅在D3D9Ex下生效
MaxFrameLatency = 3
# 是否输出调试日志（不影响是否弹调试器）
EnableDebug = false
# 是否在启动时等待调试器附加（正常情况下请保持false）
WaitForDebugger = false
# 日志级别: Debug / Info / Warning / Error / None（None 表示不输出任何日志）
LogLevel = Info
```

### 日志文件

插件运行日志输出到**BVE程序安装目录**下的`D3D9DeviceHacker.log`，级别由`LogLevel`控制，每次启动时会清空旧日志。

## 兼容性

本插件对[DXDynamicTexture](https://github.com/zbx1425/DXDynamicTexture)进行了特殊处理，在多条线路上测试通过，但对于其他自行添加纹理的插件可能遇到兼容性问题。

## 依赖

### [Harmony](https://github.com/pardeike/Harmony) (MIT)

### [MonoMod](https://github.com/MonoMod/MonoMod) (MIT)

### [BveEx](https://github.com/automatic9045/BveEX) (PolyForm Noncommercial License 1.0.0)

