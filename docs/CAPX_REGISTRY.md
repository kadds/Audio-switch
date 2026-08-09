# CAPX 注册表读取

scripts\Read-CapxProfileRegistry.ps1 对 Windows 音频 Render 端点做只读扫描，读取：

HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\
  {endpoint}\FxProperties\{45da5c30-2837-4ac4-b1e2-50acc3865974}\User

目标值为：

{e36464a1-2f4b-440b-a776-8b32b26a7f01},1

这个值前面有 Property Store 封装，末尾五个字节与 GetAtmosProfile() 返回值一致：

01 <profile-index> 00 00 00

当前已通过 Dolby Access UI 对照确认的 profile 映射是：

0 Dynamic
1 Game
2 Movie
3 Music
4 Voice
5 Custom1
6 Custom2
7 Custom3

运行：

Set-Location G:\code\dolby-access-auto-switch
.\scripts\Read-CapxProfileRegistry.ps1
.\scripts\Read-CapxProfileRegistry.ps1 -AsJson

脚本不会写注册表，不会调用 SetAtmosProfile，也不会改变当前 Dolby 配置。Game = 1 已通过同一个支持 CAPX 的端点上的 Dolby Access UI 切换对照确认。
