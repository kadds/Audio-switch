# C# Dolby runtime path

The WinUI application and the Dolby runtime operation are implemented in C#.

- `DolbyCapxProfileProvider` connects to `com.DolbyLaboratories.DolbyAccess.` through `Windows.ApplicationModel.AppService.AppServiceConnection`.
- A profile change sends `SetProfile` and `SyncProfile`, then calls `GetProfile` and verifies the returned `ProfileParameters.Type`.
- The request uses Dolby Atmos for Headphones codec GUID `{8F3BBD02-6BBE-4B60-9F8B-406837CE466F}` and the endpoint interface path as `DeviceID`.
- The WinUI process does not start `powershell.exe` and does not open a terminal window.

## Why CAPX readback was misleading

`CapxComponent.PropertyStoreProxy.SetAtmosProfile()` returned `S_OK` and its `GetAtmosProfile()` readback changed from Movie to Game, but Dolby's AppService `GetProfile` still returned Movie. That proves the CAPX blob is not a sufficient runtime-application signal for this installation. The AppService path performs Dolby's own profile synchronization and is therefore the production setter.

The provider verifies the state through Dolby's own runtime readback instead of treating a registry or CAPX blob readback as proof of an applied preset.
