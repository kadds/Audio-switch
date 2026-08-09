# Audio Switch

研究目标：在 Windows 上检测 FPS 游戏进程，并尝试通过 Dolby Access 已安装的本地接口切换到 Game 预设。

当前版本：Dolby Access `3.27.11070.0`。

## 当前结论

- 已确认 `DAX.CDolbyDAX` 存在 `ActiveProfile`、`ActiveSubProfile` 等整数接口，但本机没有可用的 DAX RPC 服务，读取返回 `0x800706EF`。
- 已确认更接近 Dolby Access / Atmos 配置的 `CapxComponent.PropertyStoreProxy` 接口。
- 当前 Realtek 端点通过 CAPX 探测为 `IsSupported=True`，`GetAtmosProfile()` 返回 `0102000000`。
- 静态 profile 顺序为 Dynamic、Game、Movie、Music、Voice、Custom1-3；已通过 Dolby Access UI 对照确认 Game 的索引是 `1`，payload 是 `0101000000`。

## 目录

- `docs/REVERSE_ENGINEERING.md`：逆向过程、接口、日志和安全边界。
- `docs/CSHARP_RUNTIME.md`：C# 直接调用、Dolby 包身份限制和纯 C# 方案评估。
- `docs/WINUI3.md`：WinUI 3 项目、托盘和编译说明。
- `docs/DAX.generated.idl`：从本机 `DAX.winmd` 生成的接口定义。
- `src/DaxProbe.cs`：DAX WinRT/RPC 只读探测器。
- `src/CapxProbe.cs`：CAPX `GetAtmosProfile()` 只读探测器。
- `src/CapxSetProfile.cs`：CAPX `SetAtmosProfile()` C# setter。
- `tools/bin/`：已编译的探测器和 C# UI 程序。
- `src/DolbyAccessAutoSwitch.WinUI/`：WinUI 3 + XAML 新版配置界面。
- WinUI 3 使用 `dotnet run --project .\src\DolbyAccessAutoSwitch.WinUI\DolbyAccessAutoSwitch.WinUI.csproj -c Debug -p:Platform=x64` 启动。
- `work/`：本机探测输出和临时材料。

## CAPX 运行时

当前 UI、CAPX 探测和 profile 写入全部由 C# 实现。WinUI 通过 C# Desktop AppX 激活桥调用 Dolby CAPX helper，并验证 `SetAtmosProfile` 的 HRESULT 和读回值。详细记录见 `docs/CSHARP_RUNTIME.md`。

当前 UI 统一使用 WinUI 3，托盘、静默启动、进程选择器和输出设备/profile provider 配置均由 WinUI 项目维护。新版编译说明见 `docs/WINUI3.md`。

## 边界

项目只调用本机已安装组件暴露的接口，用于个人设备上的配置自动化；不修改 Dolby 二进制、不修改许可证或 Store receipt、不绕过购买和账户验证。CAPX setter 仅在 UI 勾选 Apply 或脚本传入 `-Apply` 时执行。
