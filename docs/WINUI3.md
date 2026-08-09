# WinUI 3 UI

当前 UI 项目位于：

`src/DolbyAccessAutoSwitch.WinUI/DolbyAccessAutoSwitch.WinUI.csproj`

技术栈：

- C# / .NET 10
- WinUI 3 / Windows App SDK 2.3.1
- XAML 页面和 Fluent 控件
- `H.NotifyIcon.WinUI` 托盘图标
- 单项目 MSIX 模板
- 顶部固定功能页签：`Process rules`、`Output devices`、`General`；为避开当前运行时的 `TabView` 原生崩溃，页签栏使用轻量按钮切换实现。

## 已迁移能力

- 输出设备与 profile provider 配置。
- `Process rules` 页左侧选择进程，右侧独立配置该进程的输出设备、provider、Game/Restore profile 和 Apply 行为；不是一个进程生成一个 Tab。
- `All output devices` / `Matched profiles` 双页选择器。
- 后台 profile provider 扫描，打开设备选择器不会同步卡住 UI。
- 运行进程列表、带图标的进程选择器和进程名单维护。
- Game / Restore profile、Apply dry-run、开机启动、启动后隐藏、自动监控。
- 关闭主窗口隐藏到托盘；托盘菜单可以打开设置、启动监控或退出。
- 新配置保存到 `%LOCALAPPDATA%\DolbySwitch\dolby-switch-config.xml`；当前版本不迁移旧配置，程序不会修改 Dolby 安装目录。

## 编译

在项目根目录执行：

```text
dotnet restore .\src\DolbyAccessAutoSwitch.WinUI\DolbyAccessAutoSwitch.WinUI.csproj -r win-x64
dotnet build .\src\DolbyAccessAutoSwitch.WinUI\DolbyAccessAutoSwitch.WinUI.csproj -c Debug -p:Platform=x64
```

编译输出位于：

`src/DolbyAccessAutoSwitch.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`

当前版本只维护 WinUI 3；核心音频逻辑在 `Core/SwitchCore.cs`。

WinUI 3 属于 Windows App SDK，打包和未打包应用的运行时部署方式不同；正式分发时需要选择 MSIX 或自包含运行时方案，不能只复制单个 DLL。[Windows App SDK 部署说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
