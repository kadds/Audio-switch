# Dolby Access 逆向与自动切换记录

更新时间：2026-08-08

## 目标

确认 Dolby Access 是否存在可以切换音频预设的本地接口，并为 C# 自动检测进程、切换到 Game 预设保留可验证的实现路径。

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

这表明 Dolby Access 的 Game/Movie 切换会写入端点 Property Store，但 Property Store 不是运行时 profile 的充分验证。

## 路径三：Dolby SpatialCodecs AppService

对安装包中的 `DolbyAccess.dll` 和 `Dolby.SpatialCodecs.winmd` 做字符串及元数据分析后，确认了 Dolby 自己的 AppService：

```text
Service: com.DolbyLaboratories.DolbyAccess.
Commands: SetProfile, SyncProfile, GetProfile
Codec: {8F3BBD02-6BBE-4B60-9F8B-406837CE466F}
```

请求包含 `Command`、`DeviceID`、`MediaCodecName`。`SetProfile` 和 `SyncProfile` 还需要 `ProfileParameters` JSON，例如：

```json
{"IntelligentEqualizerType":"Detailed","CustomEqualizerSettings":null,"IsPerformanceMode":null,"IsSurroundVirtualizerEnabled":null,"IsDialogueEnhancerEnabled":null,"IsVolumeLevelerEnabled":null,"GamingSubProfile":null,"Type":"Game"}
```

当前端点的无 UI 脚本验证结果：

```text
GetProfile 之前: Type = Movie
SetProfile: OK
SyncProfile: OK
GetProfile 之后: Type = Game
```

因此，原来的 CAPX setter 不是“调用成功但听感不明显”，而是只改了底层 CAPX 状态，没有走 Dolby Access 的运行时 profile 同步链路。WinUI provider 现已改为 `SetProfile → SyncProfile → GetProfile`，并以 Dolby AppService 回读作为成功条件。

## Dolby 自定义 EQ（2026-08-16）

继续对当前 Dolby Access 3.27 AppService 做无 UI 验证后，确认
`CustomEqualizerSettings` 不能使用自定义的 `BandGains` 字段。发送 10 段 EQ
后，Dolby 会将其规范化为以下结构：

```json
{
  "CustomGainRange": 12.0,
  "_dap20Gains": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
  "_user10Gains": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
}
```

`_user10Gains` 是用户编辑的 10 段曲线，AudioSwitch 将其限制在 `-12` 到
`+12 dB`，并通过相邻频段插值生成 20 段 `_dap20Gains`。`GetProfile` 会回读
这两个数组；仅发送 `CustomEqualizerSettings: null` 会使 Dolby 自定义预设的
EQ 模型拿到空值，Dolby Access 可能在打开 EQ 控件时抛出空引用异常。

Dolby AppService 的 `Type` 仍然只接受原生的 `Custom1/Custom2/Custom3` 槽位。
AudioSwitch 页面不把这些原生名称作为用户配置项，而是保存带有
`AudioSwitch EQ: ` 前缀的自定义 Profile。应用任意一个 AudioSwitch 自定义
Profile 时，AudioSwitch 都把当前选中的曲线写入 Dolby 的 `Custom3`，并以
`Type = Custom3` 完成 `SetProfile → SyncProfile → GetProfile`；`Custom1` 和
`Custom2` 不会被 AudioSwitch 自定义 EQ 使用。这样可以支持任意数量的
AudioSwitch Profile，同时 Dolby 端始终只有一个被实时更新的运行时槽位。

## Game 映射验证记录

1. 固定同一个端点，采集 Movie 的 blob：`01 02 00 00 00`。
2. 通过 Dolby Access UI 切换到 Game，采集同一个端点 blob：`01 01 00 00 00`。
3. 切回 Movie，确认同一个端点恢复为 `01 02 00 00 00`；本次已完成。
4. 下一阶段是完善 C# 游戏进程监控；写入功能由 WinUI provider 统一实现。

## 安全边界

- 不修改 `WindowsApps` 中的 Dolby 文件。
- 不修改 `lic.dat`、Store receipt 或授权数据。
- 不注入 DolbyAccess 进程，不绕过自定义 capability。
- CAPX `SetAtmosProfile` 仅保留为逆向/诊断接口，不再作为 Dolby 运行时 setter。C# UI 只在进程规则触发、全局默认生效或用户点击测试时调用 AppService。DAX profile setter 仍因本机没有 DAX RPC server 而不可用。

## DTS Sound Unbound 逆向修正（2026-08-16）

用户提供的 DTS Sound Unbound 页面显示 `DTS Headphone:X`“已授权”，设备为
`Generic Over-Ear Headphones`，空间模式为 `Balanced`。因此，
`DTSDsecProxy.DTSDsecProxyLicenseInfo` 探针返回的 `Unlicensed` 不能解释为
Headphone:X 未激活：它对应的是独立的 DSEC/DAP/Ultra 授权枚举。

当前通用端点的 `CapxSettingsInterface.CapxSettings.IsCAPxSupported` 也返回
`false`。CAPX 是 OEM/property-store 路径，不能作为通用 Headphone:X Store
授权或配置失败的依据。Sound Unbound 二进制中的有效逆向目标是
`DtsLicenseManager`、`DtsDeviceManager`、`SADOptionsManager`、
`SetSADBlob`、`SetDeviceProfileBlob` 和 `SetRuntimeParameterSAD`。

已通过包身份脚本确认 DTS AppService 能读取 `IsSpatialOutputHPX = TRUE`；
`GetLicenseInfo` 与 `GetRuntimeParameters` 当前仍返回错误，不能作为授权判定。
包的 `LICENSE_CACHE` 和 `ApplicationData` 中也存在加密授权缓存。进一步从
`resources.pri` 读到 DTS 的真实选项：通用 Headphone:X 是 `Balanced`（平衡）
和 `Spacious`（空阔），合作方目录是 `Gaming: Balanced/Neutral/Spacious/
Spacious 2` 与 `Movies: Balanced/Spacious`。`Game`、`Movie` 不是 DTS 原生
profile 名称，不能把它们硬映射成 DTS 选项。

当前 AudioSwitch 已接入这些真实名称，provider 为 `dts-sad`，默认
`Balanced`。通用端点的运行时 blob 对应关系为：

```text
Balanced -> 02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt
Spacious -> 04-SPAC-HqHeightAndHgNf_SD2_Hp_Normal_v4_RC2.SPAC.crypt
```

写入通过 DTS 包身份启动 `DtsSetProfile.exe` 完成：定位匹配
`AudioRendererId` 的 `RuntimeSettings` composite value，更新
`RuntimeSettingsBLOBName`，再回读同一入口确认。验证脚本已完成通用
`Balanced -> Spacious -> Balanced` 的可逆写入/回读，最终恢复为 Balanced；
整个路径不写注册表，也不依赖 DSEC license status、CAPX flag 或
AudioSwitch 配置文件作为当前 DTS 值。

完整 DTS 证据和命令输出见 [DTS_INTEGRATION.md](DTS_INTEGRATION.md)。
