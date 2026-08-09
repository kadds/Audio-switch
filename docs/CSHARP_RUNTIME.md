# C# CAPX runtime path

The WinUI application and the CAPX operation are implemented in C#.

- `src/CapxProbe.cs` calls `CapxComponent.PropertyStoreProxy.GetAtmosProfile()`.
- `src/CapxSetProfile.cs` calls `SetAtmosProfile()` and verifies the readback.
- `DolbyCapxProfileProvider` launches the packaged helper through the Windows `DesktopAppXActivator` COM API. The helper receives the Dolby Access package identity, which is required for CAPX activation.
- The WinUI process does not start `powershell.exe` and does not open a terminal window.
- The `scripts/Invoke-Capx*.ps1` files are manual/reverse-engineering fallbacks only.

## Why the helper needs the Dolby package context

Calling `RoGetActivationFactory("CapxComponent.PropertyStoreProxy")` from an ordinary desktop process returns `0x80040154` on this machine. Loading the DLL directly is also blocked by the WindowsApps access boundary. A normal MSIX identity for Audio Switch does not become the Dolby Access identity, so the helper is launched with the system DesktopAppXActivator and the Dolby AUMID.

The provider passes the endpoint directly and creates only a short-lived JSON result file under the user's `%TEMP%` directory. It is deleted after each operation. A successful UI result requires both a successful CAPX HRESULT and a matching profile readback.
