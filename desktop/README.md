# LyriBeam Desktop · Windows

桌面端当前版本为 **0.8.15**。它接收手机通过蓝牙发来的歌曲和播放状态，查找并显示同步歌词，提供卡片/纯文字样式、颜色、置顶、鼠标穿透、托盘和按歌曲保存的歌词偏移。

此目录只包含 Windows 源码、离线回归和当前图标。手机应用与开发者接入 SDK 位于 [`../mobile/`](../mobile/README.md)。

## 环境

- Windows 10/11，以及可与安卓手机配对的蓝牙适配器。
- 构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。运行框架依赖版本需要 .NET 10 Windows Desktop Runtime；安装 SDK 的开发机通常已具备。
- PowerShell。脚本从当前目录定位源码，不依赖某台电脑的盘符或项目工具缓存。
- 安卓助手已完成通知读取授权、蓝牙配对和电脑选择。应用不接管手机音频。

## 构建和运行

在仓库根目录执行：

```powershell
& .\desktop\windows\build.ps1
& .\desktop\dist\LyricBridge.exe
```

默认输出到 `desktop/dist/`，也可指定其他目录：

```powershell
& .\desktop\windows\build.ps1 -OutputDirectory "$env:TEMP\lyribeam-desktop"
```

`dist/`、构建中间产物和运行生成的 `data/` 不应提交到仓库。当前程序集名仍为 `LyricBridge`，窗口中文名仍为“蓝牙歌词”；仓库名称 LyriBeam 不改变已有协议、数据文件或手机应用标识。

## 桌面入口

构建后可按需建立当前用户的桌面及开始菜单入口：

```powershell
& .\desktop\windows\install-desktop.ps1
```

默认入口名为“LyriBeam”，目标为 `desktop/dist/LyricBridge.exe`。自定义程序目录或入口名称：

```powershell
& .\desktop\windows\install-desktop.ps1 -AppDirectory "$env:TEMP\lyribeam-desktop" -ShortcutName 'LyriBeam'
```

安装脚本只建立快捷方式。遇到同名入口指向其他程序时会停止；图标刷新使用版本化 ICO 与定向通知，无需重启资源管理器。

## 检查

以下为离线用例和演示界面检查，不代表所有播放器、曲目均已实机验证。用新的输出目录保留报告：

```powershell
$checks=Join-Path $env:TEMP 'lyribeam-desktop-checks'
New-Item -ItemType Directory -Force -Path $checks | Out-Null
$app=Join-Path $PWD 'desktop\dist\LyricBridge.exe'
Start-Process -FilePath $app -ArgumentList '--self-test',"$checks\core.json" -Wait
Start-Process -FilePath $app -ArgumentList '--regression-test',"$checks\regression.json" -Wait
Start-Process -FilePath $app -ArgumentList '--ui-test',"$checks\ui" -Wait
```

`--self-test` 包含基本协议、时钟、LRC 和蓝牙结构检查；`--regression-test` 包含匹配、缓存、来源、取消与进度等离线回归。`--ui-test` 使用原创演示文字生成卡片、设置与托盘截图，不读取真实听歌记录。

## 图标

当前 B 柔和版母图位于 `design/app-icon/lyric-fold-b-soft/source.png`，示例为同目录 `icon-512.png`。图像由 AI 设计工具根据本项目的图标设计生成；Windows 原生遮罩负责干净圆角，素材不包含第三方播放器品牌。

需要重新导出 ICO 时，在仓库根目录执行：

```powershell
& .\desktop\design\app-icon\build-icon.ps1
& .\desktop\windows\build.ps1
```

脚本生成 16–256 px 的十种 ICO 帧，并更新 `windows/LyricBridge.ico`。尺寸预览由母图直接重采样，不增加锐化滤镜。

## 歌词和运行数据

当前播放器提供的有效歌词、私有缓存及严格验证的共享缓存优先；联网时使用原平台优先与多来源回退。歌词源的可访问性、覆盖率及版本信息会影响匹配；没有足够依据时不会强行显示疑似错误版本。

运行数据保存在应用输出目录旁的 `data/`。缓存、偏移、窗口偏好和诊断属于用户本机内容，仓库不附带这些数据。公开问题反馈中也请先删除设备信息、听歌记录和完整歌词。
