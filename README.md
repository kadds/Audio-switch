# AudioSwitch

AudioSwitch 是一个运行在 Windows 上的音频配置自动切换工具。

它可以根据当前使用的应用、游戏或浏览器网页，自动切换输出设备、空间音频格式和对应的音频预设。程序常驻托盘后台运行，适合为游戏、影音软件和不同网页设置独立的音频方案。

AudioSwitch 同时支持：

- Dolby Atmos for Headphones（通过 Dolby Access）
- DTS Headphone:X（通过 DTS Sound Unbound）
- 按前台进程匹配的音频规则
- 浏览器网页地址、标题和状态匹配
- 本地 HTTP 状态接口与 Chrome 浏览器插件
- 输出设备切换、开机启动和托盘后台运行

## 工作方式

在“进程规则”中为应用配置目标输出设备和空间音频设置。AudioSwitch 检测到对应进程成为前台窗口后，就会应用该规则；没有匹配规则时，可以使用全局默认设置。

浏览器场景可以启用 HTTP 状态监听。Chrome 插件会把当前标签页的地址、标题和状态发送给 AudioSwitch，然后由网页地址子规则决定是否切换音频配置。

Dolby 和 DTS 使用各自软件实际提供的预设名称。Dolby 常见预设包括 `Game`、`Movie`、`Music` 和 `Voice`；DTS Sound Unbound 使用 `Balanced`、`Spacious`、`Gaming: ...` 和 `Movies: ...` 等名称，不将它们强行映射成 Dolby 的 `Game` 或 `Movie`。

## 快速开始

### 使用已编译程序

1. 确保系统已安装并激活需要使用的 Dolby Access 或 DTS Sound Unbound。
2. 启动 AudioSwitch，打开“进程规则”。
3. 添加或选择一个进程，配置输出设备、空间音频格式和预设。
4. 启用自动监控。程序会在匹配到进程时自动应用规则。

如果要按浏览器网页切换：

1. 在 AudioSwitch 的“常规”设置中启用 HTTP 状态监听。
2. 从 `chrome-extension` 目录加载 Chrome 插件，或打开程序输出目录中的插件目录。
3. 在插件设置中填写 AudioSwitch HTTP 地址和密码（如果启用了密码）。
4. 在浏览器进程下添加地址规则，例如 `bilibili.com` 或某个具体网页地址。

HTTP 接口和插件的完整说明见 [`docs/HTTP_INTEGRATION.md`](docs/HTTP_INTEGRATION.md)。

## 从源码运行

要求：Windows 10/11、.NET 10 SDK，以及对应的 Windows App SDK 运行环境。

在项目根目录执行：

```powershell
dotnet restore .\src\AudioSwitch.WinUI\AudioSwitch.WinUI.csproj -r win-x64
dotnet run --project .\src\AudioSwitch.WinUI\AudioSwitch.WinUI.csproj -c Debug -p:Platform=x64
```

构建后的输出位于：

`src/AudioSwitch.WinUI/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`

构建时 Chrome 插件会自动复制到输出目录的 `AppX/chrome-extension/` 下。

## 配置和日志

AudioSwitch 使用本地文件保存配置，不把应用配置写入 Windows 注册表：

- 配置：`%LOCALAPPDATA%\AudioSwitch\audio-switch-config.xml`
- 日志：`%LOCALAPPDATA%\AudioSwitch\audio-switch.log`
- 工作文件：`%LOCALAPPDATA%\AudioSwitch\work\`

程序只调用系统音频接口以及本机已安装的 Dolby/DTS 组件，不修改 Dolby 或 DTS 的安装文件、许可证和账户状态。

## 项目文档

- [`docs/WINUI3.md`](docs/WINUI3.md)：界面、托盘和构建说明
- [`docs/HTTP_INTEGRATION.md`](docs/HTTP_INTEGRATION.md)：HTTP 接口、浏览器插件和地址规则
- [`docs/DTS_INTEGRATION.md`](docs/DTS_INTEGRATION.md)：DTS Sound Unbound 支持说明
- [`docs/REVERSE_ENGINEERING.md`](docs/REVERSE_ENGINEERING.md)：Dolby/DTS 接口研究和验证记录

## 项目结构

- `src/AudioSwitch.WinUI/`：AudioSwitch 主程序和 WinUI 3 界面
- `chrome-extension/`：Chrome Manifest V3 浏览器插件
- `docs/`：使用说明和技术记录
- `assets/`：项目图标及源素材
