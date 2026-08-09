# WinUI runtime path

The WinUI application does not start PowerShell or a terminal process.

- Dolby CAPX support detection reads the CAPX profile record from the Windows audio endpoint registry in C#.
- Profile changes are performed by the packaged C# `CapxSetProfile.exe` helper through Windows' `DesktopAppXActivator`, which gives that helper the Dolby Access package identity required by `CapxComponent.PropertyStoreProxy`. The helper is compiled as a Windows-subsystem executable, so the package-identity bridge does not create a console window.
- The helper result is checked for a successful `SetAtmosProfile` HRESULT and a matching CAPX readback before the UI reports success.
- No PowerShell script is included in the runtime project.

The lower-level `CapxComponent.PropertyStoreProxy` activation still requires the Dolby package identity on this machine. A normal desktop C# process receives `CLASS_NOT_REGISTERED` when activating that class directly, so the runtime provider uses a GUI-subsystem C# helper in the Dolby package context rather than writing the registry record and claiming that the live preset changed.
