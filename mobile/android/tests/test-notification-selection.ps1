param([string]$OutputDirectory=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-notification-selection'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
$base=Join-Path $OutputDirectory 'base-notification'
& (Join-Path $PSScriptRoot 'test-notification.ps1') -OutputDirectory $base
if($LASTEXITCODE -ne 0){throw 'Existing notification regression failed'}
$fixtures=Join-Path $OutputDirectory 'fixtures';$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Path $fixtures,$classes -Force|Out-Null
$sourceFixtures=Join-Path $base 'fixtures'
foreach($item in Get-ChildItem -LiteralPath $sourceFixtures -Recurse -File){
 $relative=$item.FullName.Substring($sourceFixtures.Length).TrimStart('\')
 $target=Join-Path $fixtures $relative
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
 Copy-Item -LiteralPath $item.FullName -Destination $target -Force
}
$overrides=@{
'android/os/SystemClock.java'='package android.os; public class SystemClock {public static long now=1000000L;public static long elapsedRealtime(){return now;}}'
'android/media/session/PlaybackState.java'='package android.media.session; public class PlaybackState {public static final int STATE_PLAYING=3,STATE_PAUSED=2;private final int value;private final long updated,position;public PlaybackState(){this(STATE_PLAYING);}public PlaybackState(int state){this(state,android.os.SystemClock.elapsedRealtime()-1000,1000);}public PlaybackState(int state,long time,long pos){value=state;updated=time;position=pos;}public int getState(){return value;}public long getPosition(){return position;}public long getLastPositionUpdateTime(){return updated;}public float getPlaybackSpeed(){return 1;}}'
'android/service/notification/StatusBarNotification.java'='package android.service.notification;public class StatusBarNotification {private final String key,pkg;private final android.app.Notification notification;private final long posted;public StatusBarNotification(String k,String p,android.app.Notification n){this(k,p,n,System.currentTimeMillis());}public StatusBarNotification(String k,String p,android.app.Notification n,long time){key=k;pkg=p;notification=n;posted=time;}public String getKey(){return key;}public String getPackageName(){return pkg;}public android.app.Notification getNotification(){return notification;}public long getPostTime(){return posted;}}'
}
foreach($entry in $overrides.GetEnumerator()){[IO.File]::WriteAllText((Join-Path $fixtures $entry.Key),$entry.Value,[Text.UTF8Encoding]::new($false))}
$files=@(Get-ChildItem -LiteralPath $fixtures -Recurse -Filter '*.java'|Where-Object {$_.Name -ne 'NotificationPlaybackStore.java'}|ForEach-Object {$_.FullName})
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\MediaReader.java'
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\NotificationPlaybackStore.java'
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\PlayerSelection.java'
$files+=Join-Path $PSScriptRoot 'NotificationSelectionTests.java'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes @files
if($LASTEXITCODE -ne 0){throw 'Notification selection host compile failed'}
$resultPath=Join-Path $OutputDirectory 'result.json'
& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.NotificationSelectionTests $resultPath
if($LASTEXITCODE -ne 0){throw 'Notification selection host regression failed'}
$result=Get-Content -LiteralPath $resultPath -Raw|ConvertFrom-Json
$result|Add-Member -NotePropertyName completed -NotePropertyValue $true
$result|Add-Member -NotePropertyName checkedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o'))
$result|Add-Member -NotePropertyName scope -NotePropertyValue 'Production MediaReader and notification store, deterministic Android host fixtures; no physical phone controlled'
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $resultPath -Encoding utf8
if(-not $result.success){throw 'Notification selection checks were not all successful'}
