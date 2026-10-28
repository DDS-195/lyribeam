[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
$desktopRoot=Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory=Join-Path $desktopRoot 'dist'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before building LyriBeam Desktop.'
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish (Join-Path $PSScriptRoot 'LyricBridge.csproj') -c $Configuration -o $OutputDirectory --self-contained false
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed' }
Write-Output ('Desktop application ready: ' + (Join-Path $OutputDirectory 'LyricBridge.exe'))
