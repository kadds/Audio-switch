# Game 预设映射验证

## 结论

在同一个 Realtek 音频端点上，通过 Dolby Access UI 依次选择 Movie 和 Game，并在每次选择后读取 CAPX：

Movie -> 01 02 00 00 00
Game  -> 01 01 00 00 00

对应的 Windows Property Store 外层值分别是：

Movie -> 41 00 00 00 01 00 00 00 01 02 00 00 00
Game  -> 41 00 00 00 01 00 00 00 01 01 00 00 00

因此，在 Dolby Access 3.27.11070.0 这台机器上，Game 预设的 CAPX profile 是：

profile index = 1
payload       = 01 01 00 00 00

## 实际验证过程

1. 初始 UI 显示“电影”；Realtek 的 GetAtmosProfile() 返回 0102000000。
2. 点击 UI 左侧“游戏”；Realtek 的注册表 payload 变成 0101000000。
3. 同一次读取中，CapxProbe 的 GetAtmosProfile() 也返回 0101000000。
4. 点击 UI 左侧“电影”恢复；Realtek 的 payload 和 CapxProbe 都回到 0102000000。

## 可复现命令

Set-Location G:\code\dolby-access-auto-switch
.\scripts\Read-CapxProfileRegistry.ps1 -AsJson
.\scripts\Invoke-CapxProbe.ps1

其中 Read-CapxProfileRegistry.ps1 和 Invoke-CapxProbe.ps1 都是只读脚本。本次验证没有调用 SetAtmosProfile，UI 恢复后当前端点仍为 Movie。
