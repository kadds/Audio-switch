# Dolby Access 逆向与自动切换记录

更新时间：2026-08-08

## 目标

确认 Dolby Access 是否存在可以切换音频预设的本地接口，并为 PowerShell 自动检测游戏进程、切换到 Game 预设保留可验证的实现路径。

## 本机环境

```text
Package:       DolbyLaboratories.DolbyAccess
Version:       3.27.11070.0
PackageFamily: DolbyLaboratories.DolbyAccess_rz1tebttyb220
AppService:    com.DolbyLaboratories.DolbyAccess.
```

安装包清单还确认了：

- `DAX.CDolbyDAX` -> `DAXRPCClient.dll`
- `CapxComponent.PropertyStoreProxy` -> `CapxComponent.dll`
- AppService 入口 -> `Dolby.SpatialCodecs.SpatialCodecsAppService`
- DAX 自定义能力 -> `DolbyLabs.dolbyDAX3ApiService_rz1tebttyb220`
- CAPX / 音频 codec 的自定义能力和 media codec 声明

## 路径一：DAX WinRT/RPC

从 `DAX.winmd` 生成了 `docs/DAX.generated.idl`。`DAX.IDolbyDAX` 暴露：

```text
OpenRPCEndpoint / CloseRPCEndpoint
Initialize / InitializeEx
ActiveProfile              INT32 get/set
ActiveSubProfile           INT32 get/set
AutoSwitchEnable           boolean get/set
GetProperty / SetProperty
GetPropertyEx / SetPropertyEx
RegisterCallback_Manager2
```

使用 `Invoke-CommandInDesktopPackage` 在 Dolby 包身份下运行 `DaxProbe.exe` 后，得到：

```json
{
  "openRpc": { "hr": "0x00000000", "value": 1722 },
  "version": { "hr": "0x800706EF" },
  "activeProfile": { "hr": "0x800706EF" },
  "activeSubProfile": { "hr": "0x800706EF" },
  "autoSwitch": { "hr": "0x800706EF" }
}
```

`0x800706EF` 是 `RPC_S_SERVER_UNAVAILABLE`。这不是 WinRT 激活失败，而是本机当前没有可用的 DAX RPC server。Dolby 日志同时多次记录 `No DAX driver detected`，所以不能把这条 OEM DAX 路径的整数当作本机 Dolby Access Game ID。

## 路径二：CAPX PropertyStoreProxy

从 `CapxComponent.winmd` 生成的接口定义可见：

```text
IPropertyStoreProxyFactory.CreateInstance(HSTRING endpointId)
IPropertyStoreProxy.GetEndpointId()
IPropertyStoreProxy.IsSupported()
IPropertyStoreProxy.GetAtmosProfile()
IPropertyStoreProxy.SetAtmosProfile()
IPropertyStoreProxy.GetInitParams()
IPropertyStoreProxy.GetRuntimeParams()
IPropertyStoreProxy.GetUpdateCounter()
```

当前默认端点：

```text
\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{deacc293-1866-4139-8dbc-749e990e0432}#{e6327cad-dcec-4949-ae8a-991e976a79d2}
```

只读探测结果：

```json
{
  "createInstance": { "hr": "0x00000000" },
  "queryInterface": { "hr": "0x00000000" },
  "isSupported": { "hr": "0x00000000", "value": true },
  "atmosProfile": { "hr": "0x00000000", "size": 5, "hex": "0102000000" }
}
```

这证明 Atmos 配置存在可读的本地 profile blob。通过同一 Realtek 端点上的 Dolby Access UI 对照，已确认 Movie 是 `01 02 00 00 00`，Game 是 `01 01 00 00 00`。

静态字符串还出现了以下顺序：

```text
PROFILE_DYNAMIC, PROFILE_GAME, PROFILE_MOVIE, PROFILE_MUSIC,
PROFILE_VOICE, PROFILE_CUSTOM1, PROFILE_CUSTOM2, PROFILE_CUSTOM3
```

在同一个 Realtek 端点上通过 Dolby Access UI 对照后，Movie 返回 `01 02 00 00 00`，Game 返回 `01 01 00 00 00`。因此当前版本已确认 `Game = 1`，对应 payload 为 `01 01 00 00 00`。

另外，在当前 Realtek 端点的 Windows 音频 Property Store 中发现了同一类 CAPX 记录：

```text
...\FxProperties\{45da5c30-2837-4ac4-b1e2-50acc3865974}\User
{e36464a1-2f4b-440b-a776-8b32b26a7f01},1
    41 00 00 00 01 00 00 00 01 02 00 00 00
```

另一个历史 AG275UXM 端点的同名记录为：

```text
41 00 00 00 01 00 00 00 01 01 00 00 00
```

去掉前 8 字节 Property Store 封装后，正好分别得到 `01 02 00 00 00` 和 `01 01 00 00 00`。点击 UI 的“游戏”后，当前 Realtek 端点和 CapxProbe 同时返回 `01 01 00 00 00`；恢复“电影”后同时回到 `01 02 00 00 00`。这已经完成 Game/Movie 的直接对照确认。

## 日志证据

日志目录：

```text
%LOCALAPPDATA%\Packages\DolbyLaboratories.DolbyAccess_rz1tebttyb220\LocalCache
```

已观察到：

```text
Selected profile: Game
Profile Game set or updated
Selected profile: Movie
CAPX supported
Init and runtime parameters for ASAR written to Property Store for endpoint ...
```

这表明 Dolby Access 的 Game/Movie 切换确实会写入端点 Property Store；这也是当前最有希望的自动化路线。

## PowerShell 复现

```powershell
Set-Location G:\code\dolby-access-auto-switch
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-CapxProbe.ps1 -Build
```

脚本使用 `Invoke-CommandInDesktopPackage`，因为普通 PowerShell 直接调用 `RoGetActivationFactory` 会遇到 `0x80040154 CLASS_NOT_REGISTERED`。脚本和探测器只读，不调用 `SetAtmosProfile`。

## Game 映射验证记录

1. 固定同一个端点，采集 Movie 的 blob：`01 02 00 00 00`。
2. 通过 Dolby Access UI 切换到 Game，采集同一个端点 blob：`01 01 00 00 00`。
3. 切回 Movie，确认同一个端点恢复为 `01 02 00 00 00`；本次已完成。
4. 下一阶段是设计默认 dry-run 的游戏进程监控脚本；写入功能另行实现并默认关闭。

## 安全边界

- 不修改 `WindowsApps` 中的 Dolby 文件。
- 不修改 `lic.dat`、Store receipt 或授权数据。
- 不注入 DolbyAccess 进程，不绕过自定义 capability。
+ CAPX `SetAtmosProfile` 已封装在独立 setter 中，并通过 Game/Movie 实际读回验证；C# UI 默认 dry-run，只有勾选 Apply 后才会执行写入。DAX profile setter 仍因本机没有 DAX RPC server 而不可用。
