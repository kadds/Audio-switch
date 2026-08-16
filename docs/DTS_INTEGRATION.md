# DTS integration

Audio Switch now treats DTS as a separate spatial-audio profile provider.

## 2026-08-16 reverse-engineering correction

The attached DTS Sound Unbound screen shows `DTS Headphone:X` as `已授权`
(`Generic Over-Ear Headphones`, spatial mode `Balanced`). This is direct
evidence that the Headphone:X Store entitlement is active on this machine.

The earlier conclusion based on `DTSDsecProxy.DTSDsecProxyLicenseInfo` was
wrong. That API reports the separate DSEC/DAP/Ultra authorization chain:

- `Unlicensed = 0`
- `LicensedDAP2Plus = 1`
- `LicensedUltra = 2`
- `LicensedUltra2 = 3`

It is not the Headphone:X Store entitlement shown by the Sound Unbound UI.
Therefore `Unlicensed` from that probe must not be used to reject a generic
Headphone:X profile switch.

The current generic endpoint also reports `IsCAPxSupported = false` through
`CapxSettingsInterface.CapxSettings`. CAPX is the OEM/property-store path;
that result does not mean that the generic Headphone:X product is unlicensed.
The old `DtsCapxProfileProvider` implementation was consequently a
diagnostic/OEM path, not a valid setter for the screenshot's generic
Headphone:X device. It has been removed from the AudioSwitch provider
registry.

- Spatial mode: `DTS Headphone:X`
- Provider: `DTS Sound Unbound` generic Headphone:X runtime settings.
- The installed package is `DTSInc.DTSSoundUnbound` version `2026.3.0.0`.
- The package resource catalog exposes generic `Balanced`/`Spacious` labels
  and partner `Gaming: ...`/`Movies: ...` labels. These labels map to the
  installed SAD files listed below; `Game` and `Movie` are not DTS labels.
- Native strings in `DTSSoundUnbound2.dll` identify the consumer path as
  `DtsLicenseManager`, `DtsDeviceManager`, `SADOptionsManager`,
  `SetSADBlob`, `SetDeviceProfileBlob`, and `SetRuntimeParameterSAD`.
  These symbols led to the package-identity runtime-settings path used by
  the current helper.
- The same binary exposes generic HPX SAD names such as
  `06-SPAC-BasicSDContent_SD1_Hp_LowPower_v4_RC2.SPAC` and
  `08-SPAC-BasicSDContent_SD2_Hp_LowPower_v4_RC2.SPAC`; the active generic
  endpoint uses the encrypted `02-...SD1_Hp_Normal...` and
  `04-...SD2_Hp_Normal...` files.

## Current AudioSwitch implementation

`DtsSoundUnboundProfileProvider` is registered as `dts-sad`. Its profile list
matches the labels in the installed Sound Unbound resource catalog:

| DTS label | SAD file |
| --- | --- |
| `Balanced` | `02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt` |
| `Spacious` | `04-SPAC-HqHeightAndHgNf_SD2_Hp_Normal_v4_RC2.SPAC.crypt` |
| `Gaming: Balanced` | `2403-GamingBalanced.SPAC.crypt` |
| `Gaming: Neutral` | `2403-GamingNeutral.SPAC.crypt` |
| `Gaming: Spacious` | `2403-GamingSpacious.SPAC.crypt` |
| `Gaming: Spacious 2` | `2403-GamingSpacious2.SPAC.crypt` |
| `Movies: Balanced` | `2403-MoviesBalanced.SPAC.crypt` |
| `Movies: Spacious` | `2403-MoviesSpacious.SPAC.crypt` |

The default is `Balanced`, which is the option shown in the attached generic
Headphone:X screenshot. The helper is launched through the DTS package
identity and reads the `ApplicationData.Current.LocalSettings` `RuntimeSettings`
container. It finds the composite entry matching `AudioRendererId`, updates
`RuntimeSettingsBLOBName`, and reads the same composite back before reporting
success. This path does not write the Windows registry, does not use
`Game`/`Movie` aliases, and does not consult the AudioSwitch config as the
source of the current DTS value.

A package-identity script verified a reversible write/readback sequence for the
generic `Balanced` and `Spacious` blobs. The final runtime blob was restored
to `02-SPAC-HqHeightAndHgNf_SD1_Hp_Normal_v4_RC2.SPAC.crypt` (`Balanced`).

## Read-only reverse-engineering findings

The `DTS Audio Processing` package is not part of Audio Switch's runtime
implementation and must not be used as the DTS provider. The production path
uses DTS Sound Unbound only. `CapxSettingsInterface.CapxSettings` is not used
by the generic provider; the following section records the separate CAPX/OEM
package solely to document why it was rejected.

The DTS Sound Unbound package exposes `DTSDsecProxy.DTSDsecClientProxy` with
`GetDSECLicenseStatus`. The license enum is:

- `Unlicensed = 0`
- `LicensedDAP2Plus = 1`
- `LicensedUltra = 2`
- `LicensedUltra2 = 3`

The bare probe returned `Unlicensed`, but this is now understood to be the
wrong license model for the screenshot: it is the DSEC/DAP/Ultra layer, not
the Headphone:X Store entitlement. The screenshot and the package's encrypted
`LICENSE_CACHE` (`EncryptionTime` plus `EncryptedLicenseStream`) confirm that
the Sound Unbound license manager has a local entitlement cache. The encrypted
receipt was not modified or decrypted.

The separate `DTS Audio Processing` package exposes
`DTSApo4xWinRTComponent.DtsRpcInterface`. Its public control plane includes
`InitializeConnection(endpoint)`, `GetApoExistsOnEndpoint`,
`GetOperatingMode`/`SetOperatingMode`, `GetControlValue`, and device-change
events. The `OperatingMode` values include Music, Movie, Voice, Game1/2/3,
Custom, Off, and Automatic. This is the OEM DTS APO control API; it is not a
profile-file setter for the six encrypted `2403-*.SPAC.crypt` blobs.

Read-only activation against the two current render endpoints succeeded at the
RPC class level, but both endpoints reported no DTS APO (`GetApoExists... =
false`, `0x80004005`). No setter, transaction increment, reset, or device
mutation was called during probing.

## Unbound private service findings

The package manifest registers the following private AppService:

- Service name: `com.DTSInc.DTSSoundUnbound.t5j2fzb`
- Entry point: `LicenseService.LicenseBackgroundService`
- Request keys: `Command`, `DeviceID`, `MediaCodecName`, `CodecSKU`
- Response keys: `Status`, `Result`, `ERROR`, `Exception`, `LaunchUri`

Static analysis found these command names:

- `Ping` -> `Pong`
- `GetLicenseInfo`
- `GetRuntimeParameters`
- `GetSpatialOutputState`
- `IsSpatialOutputHPX`
- `IsSpatialOutputDtsxUltra`
- `IsSpatialOutputOff`
- `SetSadBlob` / `SetSadBlobBlobName`

The AppService transport can be reached under the Unbound package identity.
`IsSpatialOutputHPX` returned `Status=OK, Result=TRUE`, while
`GetLicenseInfo` returned only `Status=ERROR` and `GetRuntimeParameters`
reported that the local runtime-settings container could not be retrieved.
`GetDecoderLicenseInfo` returned `Status=OK`. These results are not a valid
Headphone:X entitlement verdict and no private write command was sent.

## Authentication and license diagnosis

The private service does not expose a caller-supplied bearer token in its
request contract. The package manifest identifies the service and grants the
`Xperi.dtsAudioSettings_t5j2fzbtdg37r` custom capability; the C# helper is
started through the DTS package identity (`DTSInc.DTSSoundUnbound_t5j2fzbtdg37r!App`).
That identity path was verified by opening the AppService and receiving a
valid `IsSpatialOutputHPX` response, so the transport is not failing because
Audio Switch forgot an HTTP token.

The native `DTSDsecProxy.dll` contains a separate device/license authorization
layer. Its diagnostics include key-file authorization, expiry, feature, and
signature checks. The current C# probe does not reproduce the full Sound
Unbound initialization: `Ping` and `GetDecoderLicenseInfo` return `OK`, while
`GetLicenseInfo` returns only `Status=ERROR` and `GetRuntimeParameters` reports
that the local runtime-settings container was not retrieved. The native binary
also contains a `SetLicenseInfo` handoff and
`CodecLicenseVerified`/`CodecLicenseNotVerified` callbacks. These findings do
not identify the Headphone:X Store entitlement and are not a reason to reject
the authorized generic endpoint.
The private `RequestLicenseRefresh`, `GetLicenseInfo`, and
`GetRuntimeParameters` commands were discovered, but no refresh or private
write command is invoked automatically. Forging customer tokens,
authorization headers, key files, or fake-license toggles is intentionally out
of scope.

The native app binary also contains internal profile-management symbols that
are not present in a public WinMD:

- `SADOptionsManager.GetBlobNamesForSADOption`
- `SADOptionsManager.GetBlobNameForSADOption`
- `SADOptionsManager.GetOptionForBlobName`
- `SADOptionsViewModel.GetBlobForPartner`
- `SADOptionsViewModel.GetCurrentlySetBlobNames`
- `SADOptionsViewModel.SetSADBlob`
- `RuntimeSettingsViewModel.SetDeviceProfileBlob`
- `RuntimeSettingsViewModel.SetRuntimeParameterSAD`
- `RuntimeSettingsViewModel.TryUpdateCapxPropertyStore`

These are internal C++/WinRT implementation symbols rather than stable
activatable classes. The `resources.pri` catalog provides the user-facing
labels for the generic and partner SAD files. A package-identity read-only
`ApplicationData` probe found:

- `LastSelectedSadBlob["Generic Over-Ear Headphones"] = ""`
- a `RuntimeSettings` container containing a composite
  `LicenseInfoCacheSWDMMDEVAPI...` value
- a `LICENSE_CACHE` container with an encrypted license stream

The empty `LastSelectedSadBlob` value is not the current-profile source for
this endpoint; the matching `RuntimeSettings` composite contains the current
encrypted SAD blob and is the source used by the helper.

Build the helper before building the WinUI app:

```text
dotnet build .\tools\DtsSetProfile.csproj --configuration Release -p:Platform=x64 -p:OutputPath=.\bin\
dotnet build .\src\AudioSwitch.WinUI\AudioSwitch.WinUI.csproj --configuration Debug -p:Platform=x64
```

The helper is a C# Windows-subsystem executable. The runtime does not launch
PowerShell or a terminal window.
