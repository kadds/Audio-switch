param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [ValidateSet('GetProfile', 'GetProfileParameters', 'GetGamingSubProfile', 'GetAvailableGameSubProfiles', 'GetLicenseInfo')]
    [string]$CommandName = 'GetProfile',
    [string]$DeviceId = '',
    [string]$MediaCodecName = ''
)

# This probe is intended to be launched with Invoke-CommandInDesktopPackage so
# it has Dolby Access package identity. It only sends a read/query command.
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$family = 'DolbyLaboratories.DolbyAccess_rz1tebttyb220'
$serviceName = 'com.DolbyLaboratories.DolbyAccess.'

function Get-WinRtAsyncResult {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)][type]$ResultType
    )

    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and
            $_.IsGenericMethodDefinition -and
            $_.GetGenericArguments().Count -eq 1 -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.IsGenericType -and
            $_.GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName -eq 'Windows.Foundation.IAsyncOperation`1'
        } |
        Select-Object -First 1

    if ($null -eq $method) {
        throw 'Could not locate WindowsRuntimeSystemExtensions.AsTask(IAsyncOperation<T>).'
    }

    $closed = $method.MakeGenericMethod($ResultType)
    $task = $closed.Invoke($null, [object[]]@($Operation))
    if (-not $task.Wait(5000)) {
        throw 'WinRT async operation timed out after 5 seconds.'
    }
    return $task.Result
}

function Add-ValueSetItem {
    param($ValueSet, [string]$Key, [object]$Value)
    $ValueSet.set_Item($Key, $Value)
}

$result = [ordered]@{
    command = $CommandName
    openStatus = $null
    responseStatus = $null
    response = $null
    error = $null
}

try {
    $connectionType = [type]::GetType('Windows.ApplicationModel.AppService.AppServiceConnection, Windows, ContentType=WindowsRuntime')
    $connection = [Activator]::CreateInstance($connectionType)
    $connection.PackageFamilyName = $family
    $connection.AppServiceName = $serviceName

    $openStatusType = [type]::GetType('Windows.ApplicationModel.AppService.AppServiceConnectionStatus, Windows, ContentType=WindowsRuntime')
    $openStatus = Get-WinRtAsyncResult -Operation $connection.OpenAsync() -ResultType $openStatusType
    $result.openStatus = $openStatus.ToString()

    if ($result.openStatus -eq 'Success') {
        $valueSetType = [type]::GetType('Windows.Foundation.Collections.ValueSet, Windows, ContentType=WindowsRuntime')
        $message = [Activator]::CreateInstance($valueSetType)
        Add-ValueSetItem -ValueSet $message -Key 'command' -Value $CommandName
        if ($DeviceId) { Add-ValueSetItem -ValueSet $message -Key 'deviceId' -Value $DeviceId }
        if ($MediaCodecName) { Add-ValueSetItem -ValueSet $message -Key 'mediaCodecName' -Value $MediaCodecName }

        $responseType = [type]::GetType('Windows.ApplicationModel.AppService.AppServiceResponse, Windows, ContentType=WindowsRuntime')
        $response = Get-WinRtAsyncResult -Operation $connection.SendMessageAsync($message) -ResultType $responseType
        $result.responseStatus = $response.Status.ToString()
        if ($null -ne $response.Message) {
            $result.response = @{}
            foreach ($item in $response.Message) {
                $result.response[$item.Key] = [string]$item.Value
            }
        }
    }
}
catch {
    $result.error = $_.Exception.ToString()
}
finally {
    if ($null -ne $connection) { $connection.Dispose() }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
