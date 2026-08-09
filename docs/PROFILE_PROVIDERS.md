# Profile provider 架构

UI 和进程监控不直接依赖 Dolby。核心抽象是 `IAudioProfileProvider`：

- `Id`：提供者稳定 ID，例如 `dolby-capx`。
- `DisplayName`：UI 展示名。
- `TryProbe`：对一个 Windows 音频输出端点做只读匹配和当前 profile 读取。
- `SupportsProfile`：判断 provider 是否支持要切换的 profile 名称。

当前实现：

- `DolbyCapxProfileProvider`：调用 Dolby 包身份下的 CAPX probe。

当前 provider 的 CAPX probe/setter 和包身份激活桥均为 C#。这样未来如果 DTS 提供普通 Win32/COM 接口，可以新增 provider，而不把包身份逻辑扩散到 UI。

未来支持 DTS 时，新增 `DtsProfileProvider : IAudioProfileProvider`，在 `AudioProfileProviderRegistry` 注册即可；进程名单、托盘、开机启动、配置文件和设备选择器不需要改写。
