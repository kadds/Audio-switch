# WinUI runtime path

The WinUI application does not start PowerShell or a terminal process.

- Dolby CAPX support detection reads the CAPX profile record from the Windows audio endpoint registry in C#.
- Profile changes are performed by the packaged C# `CapxSetProfile.exe` helper through Windows' `DesktopAppXActivator`, which gives that helper the Dolby Access package identity required by `CapxComponent.PropertyStoreProxy`.
- The helper result is checked for a successful `SetAtmosProfile` HRESULT and a matching CAPX readback before the UI reports success.
- The `scripts/Invoke-Capx*.ps1` files remain only as reverse-engineering/manual fallback tools; the WinUI provider does not call them.

The lower-level `CapxComponent.PropertyStoreProxy` activation still requires the Dolby package identity on this machine. A normal desktop C# process receives `CLASS_NOT_REGISTERED` when activating that class directly, so the runtime provider uses a hidden C# helper in the Dolby package context rather than writing the registry record and claiming that the live preset changed.
