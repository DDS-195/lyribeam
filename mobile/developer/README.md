# 播放器主动接入（协议 1 / SDK 1.0.0）

此接口用于自己开发的 Android 播放器。标准 MediaSession 能提供完整歌曲状态时，优先使用标准接口，无需集成本 SDK。主动接入可补充歌曲状态和播放器拥有的带时间轴歌词。SDK 不播放音乐、不联网、不建立蓝牙连接，也不会启动助手同步服务。

本目录提供 Java 源码、协议与单元检查；运行构建脚本后生成轻量 JAR。没有示例播放器或测试 APK。

## 用户操作与访问边界

1. 播放器调用 requestAccess() 或 getStatus()，助手记录经 Android Binder 核实的来源包名。
2. 用户进入助手“设置”，开启“扩展播放器接入”，并允许对应播放器。
3. 用户在助手首页点击“开始同步”。外部播放器调用接口不能替用户开启同步。
4. 播放器发布状态；停止后调用 clear()。未获准时不保存歌曲或歌词载荷。

总开关默认关闭。允许设置按来源包名保存，允许列表不代表播放器能够读取其他应用的数据。接口只返回当前调用方的接入状态，不提供全局歌曲查询、文件读写、网络代理或任意指令执行。声明包名必须与真实 Binder UID 对应；无法区分来源的共享 UID 会被拒绝。

## 引入

先执行 `./build-sdk.ps1` 生成 `dist/lyricbridge-sdk-1.0.0.jar`，再加入播放器工程；也可使用 `src/` 中的 Java 源码。仅依赖 Android 公共 API；最低按助手要求为 Android 8 / API 26。JAR 采用 Java 8 字节码。

Android 11 及之后，如播放器的包可见性配置限制发现助手，可在播放器自己的 manifest 里添加指定 authority 的查询项。不要申请 QUERY_ALL_PACKAGES：

~~~xml
<queries>
    <provider android:authorities="com.local.lyricbridge.integration" />
</queries>
~~~

在后台工作线程调用，避免在音频实时线程或界面主线程同步传输较大歌词。助手不存在或拒绝调用时，SDK 返回 unavailable，不影响播放器继续播放。

## 使用片段

以下只是 SDK 调用片段，不是额外的播放器工程：

~~~java
LyricBridgeClient bridge = new LyricBridgeClient(context);
LyricBridgeClient.Result permission = bridge.requestAccess();
// disabled / pending：提示用户自行到助手设置开启并允许。
// ready：可以开始发布。每次更新提供当前真实进度。
LyricBridgeClient.Snapshot frame = LyricBridgeClient.Snapshot
    .builder(sessionId, trackId, title)
    .artist(artist)
    .album(album)
    .duration(durationMs)
    .playback(positionMs, playing, speed)
    .lyrics(lrcText) // 可选；必须是当前 trackId 的歌词
    .build();
LyricBridgeClient.Result result = bridge.publish(frame);
// 播放源停止或退出：bridge.clear();
~~~

sessionId 标识这次播放会话；trackId 是播放器自有且稳定的录音标识。切换另一份录音必须更换 trackId，重开会话应更换 sessionId。不能把每次心跳序号当 trackId；也不能给不同录音复用同一 trackId。

每 1–2 秒发布一份完整新快照，并在切歌、暂停、继续、拖动进度时立即更新。SDK playback() 在调用时使用同一台安卓设备的 elapsedRealtime 记录采样时刻。不要反复发送很久以前构建的 Snapshot；超过 30 秒的旧采样会被拒绝。纯歌曲信息可调用 metadataOnly(playing)，此时助手明确标记没有可靠进度。

每份快照独立，不继承上一份的歌词。若持续提供自带歌词，需要在当前歌曲的更新快照中继续传入相同 LRC；换歌后未传歌词即为空。歌词可在内存保留复用字符串，不需要重新读取文件。不同歌曲、不同会话、clear、允许撤销、总开关关闭或租约到期后均不会保留该来源的有效快照。

## 原始接口

地址：content://com.local.lyricbridge.integration

使用 ContentResolver.call(uri, method, null, extras)：

| method | 行为 |
| --- | --- |
| status | 记录来源请求并返回本调用方的允许状态 |
| requestAccess | 同 status，不弹窗、不打开页面、不启动服务 |
| publish | 校验并存储已批准调用方的短期完整快照 |
| clear | 只清除当前调用方的快照 |

extras 的 packageName 是调用方自身包名；SDK 自动添加。服务端不会信任或转发伪造的包名。

publish 的字段：

| 字段 | 类型与边界 |
| --- | --- |
| protocolVersion | int，必须为 1 |
| sessionId / trackId | 非空字符串，各不超过 256 字符 |
| title | 非空字符串，不超过 512 字符 |
| artist / album | 可选字符串，各不超过 512 字符 |
| mediaId | 可选、不超过 256 字符，只表示播放器自行声明的编号，不保证能直接查询其他平台 |
| durationMs | long，0 表示未知，范围 0–604800000 |
| positionKnown | boolean，默认 false |
| positionMs | long，未知为 -1；已知时必须非负，并符合歌曲时长边界 |
| playing | boolean，默认 false |
| speed | double，默认 1，有限值且绝对值不超过 16 |
| sampledAtElapsedRealtimeMs | long，采样时刻，不能来自未来或早于当前时刻 30 秒；SDK 自动设置 |
| leaseMs | long，默认 10000，范围 1000–30000 |
| providedLyrics | 可选 LRC，不超过 65536 字符 |
| providedLyricsTrackId | 提供 LRC 时必须与当前 trackId 完全相同，SDK 自动绑定 |

返回 Bundle 含 protocolVersion、packageName（已核实的本调用方）、state、enabled、approved、accepted，以及可选 message / leaseMs。state 为 disabled、pending、ready、accepted、invalid、cleared、unsupported；SDK 还可能返回 unavailable。不要把 ready 理解为蓝牙已经连接，只表示接口已获用户允许。应用不会收到其他播放器的歌曲信息。

提供歌词不等于必定被显示：电脑还会验证时间轴、歌曲名和歌手等内容。无可靠进度时不会假装从零开始滚动；错误歌词会继续使用其他可用来源。外部联网曲库由电脑查询，手机接入不需要 INTERNET 权限。

## 构建与检查

设置 `JAVA_HOME` 和 `ANDROID_SDK_ROOT` 后，执行 `./build-sdk.ps1` 生成 JAR。接收端实现位于 `../android/src/com/local/lyricbridge/IntegrationProvider.java` 和 `IntegrationStore.java`。

执行 ../android/tests/test-integration.ps1 对真实 Provider、Store 和 SDK 进行主机单元检查。测试替身只模拟 Binder 身份、设置和系统时钟，不包含可安装播放器，也不操作用户音乐。

当前接收端从内存读取短期快照，进程退出即丢弃歌曲载荷；仅来源请求名单和允许设置持久保存。总开关与逐来源设置使用助手 bridge preferences，不修改安卓系统权限。

Android 一手接口参考：
- https://developer.android.com/reference/android/content/ContentProvider#call(java.lang.String,%20java.lang.String,%20android.os.Bundle)
- https://developer.android.com/reference/android/os/Binder#getCallingUid()
- https://developer.android.com/training/package-visibility/declaring
