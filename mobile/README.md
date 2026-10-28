# LyriBeam · 移动端（Android）

当前版本 **0.4.6**，最低 Android 8 / API 26，目标 API 35。手机照常播放音乐；助手从标准媒体会话、音乐通知或用户批准的主动接入读取歌曲信息，通过蓝牙发送到桌面端。

## 目录

- `android/src/`：应用实现，保留包名 `com.local.lyricbridge`。
- `android/res/`：当前 Android 图标及界面资源。
- `android/tests/`：主机回归与 Android API 替身，不操作真实音乐播放器。
- `developer/`：可选播放器主动接入 SDK、协议和检查。
- `scripts/build-env.ps1`：JDK 与 Android SDK 环境发现。

本源码导出不包含 SDK 工具链、安装包、用户数据、日志或开发签名。桌面新 B 图标尚未应用到此 Android 版本，当前资源与 0.4.6 保持一致。

## 构建

构建脚本面向 Windows PowerShell。准备 JDK 17、Android SDK Platform 35 和 Build Tools 35.0.1（可使用已安装的更新稳定版）。通过 Android SDK Manager 安装组件，工具链不随源码分发。

```powershell
$env:JAVA_HOME = 'C:\Program Files\Java\jdk-17'
$env:ANDROID_SDK_ROOT = 'C:\Android\Sdk'
./android/build.ps1
```

上述路径只是示例，可按实际安装位置设置。未设 `JAVA_HOME` 时使用 `PATH` 中的 `javac.exe`；`ANDROID_HOME` 可作为 SDK 根目录备选。自定义 SDK 布局可另设 `ANDROID_PLATFORM_JAR` 与 `ANDROID_BUILD_TOOLS`，分别指向 `android.jar` 和完整构建工具目录。

输出：`dist/LyriBeam-0.4.6-debug.apk`。构建使用 Java 8 字节码和 Android 公共 API；版本名、版本号取自 `android/AndroidManifest.xml`。

脚本首次构建会在 `.local/debug.keystore` 生成当前开发者自己的调试签名，后续本地构建复用它。`.local/`、构建输出和签名均不进入仓库。此调试密钥与原开发环境签名不同，自行构建的 APK 不能直接覆盖使用另一签名安装的应用。项目原签名私钥不在公开源码中；正式发布应单独配置并安全保管发布签名。

## 安装和使用

将自己构建的 APK 安装到 Android 手机。首次按引导开启歌曲读取、授予蓝牙连接权限并选择已配对电脑；电脑启动桌面端后，在手机点击“开始同步”。后续打开直接进入日常首页，音乐播放器默认自动识别，仍可手动选择。

同步被系统结束后可点击“恢复同步”。后台自动恢复受手机系统策略影响，不保证所有设备都能自动恢复；应用不替用户开启后台自启动设置。

手机申请通知读取、蓝牙连接、前台连接服务及系统通知所需权限；不申请手机互联网或录音权限。仅有音乐通知但没有可信进度时，明确作为歌曲信息使用，不伪造同步时钟。

## 检查

主机回归只需要 JDK 17，不需要连接手机或安装 Android SDK：

```powershell
./android/tests/test-notification-selection.ps1
./android/tests/test-sources.ps1
./android/tests/test-player-selection.ps1
./android/tests/test-integration.ps1
./android/tests/test-setup.ps1
```

通知选择检查会同时运行其媒体读取、通知及连接测试依赖。默认结果放在 `.local/test-results/`；可通过 `-OutputDirectory` 指定其他位置。主机检查不能替代真机熄屏、切歌和进度跟随验证。

## 自建播放器

优先实现 Android 标准媒体会话。需要主动上报歌曲或合法拥有的同步歌词时，可查看 [主动接入文档](developer/README.md)；授权由用户逐个批准，外部调用不能开启助手同步服务或读取其他播放器的数据。

```powershell
./developer/build-sdk.ps1
```

构建得到 `developer/dist/lyricbridge-sdk-1.0.0.jar`。公开项目使用 LyriBeam 名称，Java 包名、ContentProvider authority 和协议版本保留以兼容已有集成。
