[CmdletBinding()]
param(
    [switch]$IncludeUnsupported,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'

$renderRoot = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render'
$fxPropertiesGuid = '{45da5c30-2837-4ac4-b1e2-50acc3865974}'
$profilePropertyName = '{e36464a1-2f4b-440b-a776-8b32b26a7f01},1'

$profileNames = @(
    'Dynamic',
    'Game',
    'Movie',
    'Music',
    'Voice',
    'Custom1',
    'Custom2',
    'Custom3'
)

function Get-PropertyValue {
    param(
        [Parameter(Mandatory = $true)]
        [Microsoft.Win32.RegistryKey]$Key,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = $Key.GetValue(
        $Name,
        $null,
        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames
    )
    return ,$value
}

function Convert-ProfileBlob {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    # The registry value is a property-store wrapper. The final five bytes are
    # the payload returned by CapxComponent.GetAtmosProfile():
    #   01 <profile-index> 00 00 00
    $payload = if ($Bytes.Length -ge 5) {
        [byte[]]$Bytes[($Bytes.Length - 5)..($Bytes.Length - 1)]
    } else {
        [byte[]]@()
    }

    $index = $null
    $name = $null
    if ($payload.Length -eq 5 -and
        $payload[0] -eq 1 -and
        $payload[2] -eq 0 -and
        $payload[3] -eq 0 -and
        $payload[4] -eq 0) {
        $index = [int]$payload[1]
        if ($index -lt $profileNames.Count) {
            $name = $profileNames[$index]
        } else {
            $name = "Unknown($index)"
        }
    }

    [pscustomobject]@{
        RawHex = (($Bytes | ForEach-Object { $_.ToString('x2') }) -join '')
        PayloadHex = (($payload | ForEach-Object { $_.ToString('x2') }) -join '')
        Index = $index
        Profile = $name
    }
}

$rows = foreach ($endpointKey in Get-ChildItem -LiteralPath "Registry::HKEY_LOCAL_MACHINE\$renderRoot") {
    $endpointId = $endpointKey.PSChildName
    $endpointRoot = "$renderRoot\$endpointId"
    $propertiesKey = $null
    $profileKey = $null

    try {
        $propertiesKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$endpointRoot\Properties")
        $profileKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
            "$endpointRoot\FxProperties\$fxPropertiesGuid\User"
        )
        if ($null -eq $profileKey) {
            continue
        }

        $bytes = Get-PropertyValue -Key $profileKey -Name $profilePropertyName
        if ($bytes -isnot [byte[]]) {
            continue
        }

        $decoded = Convert-ProfileBlob -Bytes $bytes
        $friendlyName = if ($null -ne $propertiesKey) {
            Get-PropertyValue -Key $propertiesKey -Name '{a45c254e-df1c-4efd-8020-67d146a850e0},2'
        }
        $driverName = if ($null -ne $propertiesKey) {
            Get-PropertyValue -Key $propertiesKey -Name '{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
        }

        [pscustomobject]@{
            EndpointId = $endpointId
            FriendlyName = $friendlyName
            Driver = $driverName
            Profile = $decoded.Profile
            ProfileIndex = $decoded.Index
            PayloadHex = $decoded.PayloadHex
            RawHex = $decoded.RawHex
        }
    }
    finally {
        if ($null -ne $profileKey) { $profileKey.Dispose() }
        if ($null -ne $propertiesKey) { $propertiesKey.Dispose() }
    }
}

if (-not $IncludeUnsupported) {
    $rows = $rows | Where-Object { $null -ne $_.ProfileIndex }
}

if ($AsJson) {
    $rows | ConvertTo-Json -Depth 4
} else {
    $rows | Format-Table -AutoSize
}
