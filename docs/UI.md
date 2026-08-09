# C# UI 配置程序

当前主 UI 是 WinUI 3，项目不再保留旧 WinForms UI。

WinUI 3 项目：

- `src/DolbyAccessAutoSwitch.WinUI/DolbyAccessAutoSwitch.WinUI.csproj`
- 编译输出：`src/DolbyAccessAutoSwitch.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`

## 启动

```text
Set-Location G:\code\dolby-access-auto-switch
dotnet run --project .\src\DolbyAccessAutoSwitch.WinUI\DolbyAccessAutoSwitch.WinUI.csproj -c Debug -p:Platform=x64
```

第一次调试启动需要开启 Windows Developer Mode，以便 WinApp CLI 注册调试包身份；正常使用时关闭主窗口只会隐藏到托盘。静默启动通过 UI 中的 `Start hidden in tray` 配置控制。

界面顶部采用固定功能页签：`Process rules`、`Output devices`、`General`。`Process rules` 页维护多个进程名，例如 `cs2`、`VALORANT`、`r5apex`；左侧选择一个进程，右侧配置它自己的输出设备和 Dolby profile，不会为每个进程生成单独 Tab。进程名可以填写带或不带 `.exe` 后缀。

监控启动后：

- 任意名单进程出现：切换到 Game。
- 所有名单进程退出：恢复 Restore profile，默认是 Movie。
- 未勾选 Apply：只执行 dry-run，不改变 Dolby profile。
- 勾选 Apply：通过 WinUI 内置的 C# CAPX helper 调用 `SetAtmosProfile`。

UI 通过新的 XML 结构保存配置，不迁移旧配置，不需要 AHK，也不需要修改 Dolby Access 安装目录。

设备选择器会先立即显示所有输出设备，再在后台逐个运行 profile provider 匹配；不会在打开选择器时同步阻塞整个界面。`Matched profiles` 页在扫描完成后刷新。
## 托盘和启动

- 关闭主窗口会隐藏到系统托盘，不会退出监控。
- `Start with Windows` 会写入当前用户的 Run 启动项。
- `Start hidden` 会让开机启动时不显示主窗口。
- `Auto monitor` 会让程序启动后自动开始监控。
- 默认不勾选这三项，避免首次运行改变用户习惯。

## 进程和端点选择器

`Select running...` 会列出当前运行进程，并显示 PID、程序描述和 exe 图标；选中后加入名单，不需要手填进程名。WinUI 版本在后台读取进程信息，不阻塞主界面。

`Select audio device...` 会分成 `All output devices` 和 `Matched profiles` 两页。匹配页只显示被已注册 profile provider 实际确认的设备；选择后，程序自动生成内部 endpoint file，用户不需要理解 endpoint path。
