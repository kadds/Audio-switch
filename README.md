# Audio Switch

研究目标：在 Windows 上检测 FPS 游戏进程，并尝试通过 Dolby Access 已安装的本地接口切换到 Game 预设。

当前版本：Dolby Access `3.27.11070.0`。

## 当前结论

- 已确认 `DAX.CDolbyDAX` 存在 `ActiveProfile`、`ActiveSubProfile` 等整数接口，但本机没有可用的 DAX RPC 服务，读取返回 `0x800706EF`。
- `CapxComponent.PropertyStoreProxy.SetAtmosProfile()` 只能改变 CAPX blob；它成功回读时，Dolby 运行时仍可能保持旧 preset。
- Dolby Access 的真实运行时入口是 `com.DolbyLaboratories.DolbyAccess.` AppService，调用顺序为 `SetProfile` → `SyncProfile` → `GetProfile`。
- 当前 Realtek 端点已通过脚本验证：AppService 从 `Movie` 切到 `Game` 后，`GetProfile` 实际返回 `Type: Game`。

## 目录

- `docs/REVERSE_ENGINEERING.md`：逆向过程、接口、日志和安全边界。
- `docs/CSHARP_RUNTIME.md`：C# 直接调用、Dolby 包身份限制和纯 C# 方案评估。
- `docs/WINUI3.md`：WinUI 3 项目、托盘和编译说明。
- `docs/HTTP_INTEGRATION.md`：本地 HTTP 状态接口、地址子规则和 Chrome 接入说明。
- `docs/DAX.generated.idl`：从本机 `DAX.winmd` 生成的接口定义。
- `src/DaxProbe.cs`：DAX WinRT/RPC 只读探测器。
- `src/CapxProbe.cs`：CAPX `GetAtmosProfile()` 只读探测器。
- `src/CapxSetProfile.cs`：CAPX `SetAtmosProfile()` C# setter。
- `tools/bin/`：已编译的探测器和 C# UI 程序。
- `src/AudioSwitch.WinUI/`：WinUI 3 + XAML 新版配置界面。
- `chrome-extension/`：可直接从 `chrome://extensions` 加载的 Manifest V3 Chrome 标签页状态插件。
- WinUI 3 使用 `dotnet run --project .\src\AudioSwitch.WinUI\AudioSwitch.WinUI.csproj -c Debug -p:Platform=x64` 启动。
- `%LOCALAPPDATA%\AudioSwitch\work\`：本机探测输出和临时材料。

## Dolby 运行时

当前 UI 和 profile 写入全部由 C# 实现。Dolby profile 通过 AppService 写入并用 Dolby 自己的 `GetProfile` 回读验证；CAPX 工具仅保留为逆向/诊断材料，不作为运行时 setter。详细记录见 `docs/CSHARP_RUNTIME.md`。

当前 UI 统一使用 WinUI 3，托盘、静默启动、进程选择器和输出设备/profile provider 配置均由 WinUI 项目维护。新版编译说明见 `docs/WINUI3.md`。

## 边界

项目只调用本机已安装组件暴露的接口，用于个人设备上的配置自动化；不修改 Dolby 二进制、不修改许可证或 Store receipt、不绕过购买和账户验证。AppService setter 仅在进程规则触发、无进程规则命中时应用全局默认，或用户点击 `Test active preset` 时执行。
