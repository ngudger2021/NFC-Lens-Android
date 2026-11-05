[CmdletBinding()]
param(
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [switch]$InstallDependencies
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'NfcLens.Android.csproj'

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    $candidates = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Android/Sdk') }
    $candidates += (Join-Path $PSScriptRoot '.toolchain/android-sdk')
    $AndroidSdkDirectory = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $AndroidSdkDirectory) { $AndroidSdkDirectory = Join-Path $PSScriptRoot '.toolchain/android-sdk' }
}
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
    if ($env:JAVA_HOME -and (Test-Path -LiteralPath (Join-Path $env:JAVA_HOME 'bin/java.exe'))) {
        $JavaSdkDirectory = $env:JAVA_HOME
    } else {
        $JavaSdkDirectory = Join-Path $PSScriptRoot '.toolchain/jdk'
    }
}
$AndroidSdkDirectory = [IO.Path]::GetFullPath($AndroidSdkDirectory)
$JavaSdkDirectory = [IO.Path]::GetFullPath($JavaSdkDirectory)
$paths = @("-p:AndroidSdkDirectory=$AndroidSdkDirectory", "-p:JavaSdkDirectory=$JavaSdkDirectory")

Write-Host "Android SDK: $AndroidSdkDirectory"
Write-Host "Java SDK:    $JavaSdkDirectory"
if ($InstallDependencies) {
    Write-Host 'Installing SDK/JDK components. This accepts the Android SDK licences.'
    & dotnet build $project -c Debug -t:InstallAndroidDependencies @paths '-p:AcceptAndroidSdkLicenses=True'
    if ($LASTEXITCODE -ne 0) { throw 'Android dependency installation failed. See the error above.' }
} elseif (-not (Test-Path -LiteralPath $AndroidSdkDirectory) -or -not (Test-Path -LiteralPath (Join-Path $JavaSdkDirectory 'bin/java.exe'))) {
    throw 'SDK/JDK not found. Run .\android\build.ps1 -InstallDependencies, or pass -AndroidSdkDirectory and -JavaSdkDirectory with the installed paths.'
}

& dotnet build $project -c Debug @paths
if ($LASTEXITCODE -ne 0) { throw 'Android APK build failed. See the error above.' }
$apk = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'bin/Debug') -Recurse -Filter '*-Signed.apk' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw 'Build completed but no signed APK was found in android/bin/Debug.' }
Write-Host "APK ready: $($apk.FullName)"
