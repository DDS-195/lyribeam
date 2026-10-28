[CmdletBinding()]
param(
    [string]$AppDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'),
    [ValidatePattern('^[^\/:*?"<>|]+$')][string]$ShortcutName='LyriBeam'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$appPath = Join-Path $AppDirectory 'LyricBridge.exe'
$iconPath = Join-Path $AppDirectory 'LyricBridge.ico'
if (-not (Test-Path -LiteralPath $appPath)) { throw '请先构建电脑端应用。' }
if (Test-Path -LiteralPath $iconPath) {
    # A versioned icon path refreshes Explorer's shortcut cache without restarting it.
    $version = [regex]::Match([Diagnostics.FileVersionInfo]::GetVersionInfo($appPath).ProductVersion,'^[0-9]+\.[0-9]+\.[0-9]+').Value
    if ([string]::IsNullOrEmpty($version)) { throw '无法读取应用版本以更新图标。' }
    $versionedIcon = Join-Path (Split-Path $appPath -Parent) ('LyricBridge-' + $version + '.ico')
    Copy-Item -LiteralPath $iconPath -Destination $versionedIcon -Force
    $iconPath = $versionedIcon
} else { $iconPath = $appPath }
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
if ([string]::IsNullOrWhiteSpace($desktop) -or [string]::IsNullOrWhiteSpace($programs)) { throw '无法读取当前用户的桌面或开始菜单。' }
$shell = New-Object -ComObject WScript.Shell
function Write-AppShortcut([string] $destination) {
    if (Test-Path -LiteralPath $destination) {
        $existing = $shell.CreateShortcut($destination)
        if (-not [string]::Equals($existing.TargetPath, $appPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('已有同名入口指向其他程序，未覆盖：' + $destination)
        }
    }
    $shortcut = $shell.CreateShortcut($destination)
    $shortcut.TargetPath = $appPath
    $shortcut.WorkingDirectory = Split-Path $appPath -Parent
    $shortcut.Arguments = ''
    $shortcut.IconLocation = $iconPath + ',0'
    $shortcut.Description = '通过蓝牙显示手机正在播放歌曲的桌面歌词'
    $shortcut.WindowStyle = 1
    $shortcut.Save()
    $saved = $shell.CreateShortcut($destination)
    if (-not [string]::Equals($saved.TargetPath, $appPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('入口验证失败：' + $destination)
    }
    [pscustomobject]@{ path = $destination; target = $saved.TargetPath; workingDirectory = $saved.WorkingDirectory; icon = $saved.IconLocation; arguments = $saved.Arguments }
}
$shortcuts = @(
    Write-AppShortcut (Join-Path $desktop ($ShortcutName + '.lnk'))
    Write-AppShortcut (Join-Path $programs ($ShortcutName + '.lnk'))
)
$evidence = Join-Path $taskRoot 'verification\desktop-app'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$shortcuts | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidence 'shortcuts.json') -Encoding UTF8
$shortcuts | ConvertTo-Json -Depth 3

if (-not ('LyricBridgeShortcutRefresh' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class LyricBridgeShortcutRefresh {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern void SHChangeNotify(uint eventId, uint flags, string item1, string item2);
}
'@
}
foreach ($entry in $shortcuts) { [LyricBridgeShortcutRefresh]::SHChangeNotify(0x2000,0x0005,$entry.path,$null) }
[LyricBridgeShortcutRefresh]::SHChangeNotify(0x2000,0x0005,$appPath,$null)
[LyricBridgeShortcutRefresh]::SHChangeNotify(0x2000,0x0005,$iconPath,$null)
