param([string]$OutputDirectory=(Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-notification'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
$base=Join-Path $OutputDirectory 'base-media'
& (Join-Path $PSScriptRoot 'test-media.ps1') -OutputDirectory $base
if($LASTEXITCODE -ne 0){throw 'Existing media regression failed'}
$fixtures=Join-Path $OutputDirectory 'fixtures';$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $fixtures,$classes|Out-Null
$sourceFixtures=Join-Path $base 'fixtures'
foreach($item in Get-ChildItem -LiteralPath $sourceFixtures -Recurse -File) {
 $relative=$item.FullName.Substring($sourceFixtures.Length).TrimStart('\')
 $target=Join-Path $fixtures $relative
 New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null
 Copy-Item -LiteralPath $item.FullName -Destination $target -Force
}
$sources=@{
'android/os/Bundle.java'=@"
package android.os;public class Bundle {private final java.util.Map<String,Object> values=new java.util.HashMap<>();public CharSequence getCharSequence(String key){Object value=values.get(key);return value instanceof CharSequence?(CharSequence)value:null;}public Object getParcelable(String key){return values.get(key);}public void putCharSequence(String key,CharSequence value){values.put(key,value);}public void putParcelable(String key,Object value){values.put(key,value);}}
"@
'android/app/Notification.java'=@"
package android.app;public class Notification {public static final String CATEGORY_TRANSPORT="transport",EXTRA_TITLE="title",EXTRA_TEXT="text",EXTRA_SUB_TEXT="subText",EXTRA_MEDIA_SESSION="mediaSession";public String category;public android.os.Bundle extras=new android.os.Bundle();}
"@
'android/media/session/MediaSession.java'=@"
package android.media.session;public class MediaSession {public static class Token {}}
"@
'android/service/notification/StatusBarNotification.java'=@"
package android.service.notification;public class StatusBarNotification {private final String key,pkg;private final android.app.Notification notification;public StatusBarNotification(String k,String p,android.app.Notification n){key=k;pkg=p;notification=n;}public String getKey(){return key;}public String getPackageName(){return pkg;}public android.app.Notification getNotification(){return notification;}public long getPostTime(){return System.currentTimeMillis();}}
"@
'android/service/notification/NotificationListenerService.java'=@"
package android.service.notification;public class NotificationListenerService {public static StatusBarNotification[] visible=new StatusBarNotification[0];public void onListenerConnected(){}public void onListenerDisconnected(){}public void onNotificationPosted(StatusBarNotification n){}public void onNotificationRemoved(StatusBarNotification n){}public void onDestroy(){}public StatusBarNotification[] getActiveNotifications(){return visible;}}
"@
'com/local/lyricbridge/BridgeService.java'=@"
package com.local.lyricbridge;public class BridgeService {public static String notice="";public static int sourceChanges;public static void onPlaybackSourcesChanged(){sourceChanges++;}}
"@
}
foreach($entry in $sources.GetEnumerator()){$path=Join-Path $fixtures $entry.Key;New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent)|Out-Null;[IO.File]::WriteAllText($path,$entry.Value,[Text.UTF8Encoding]::new($false))}
$files=@(Get-ChildItem -LiteralPath $fixtures -Recurse -Filter '*.java'|Where-Object {$_.Name -notin @('MediaListener.java','NotificationPlaybackStore.java')}|ForEach-Object {$_.FullName})
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\MediaReader.java'
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\MediaListener.java'
$store=Join-Path $androidRoot 'src\com\local\lyricbridge\NotificationPlaybackStore.java'
if(Test-Path -LiteralPath $store){$files+=$store}
$files+=Join-Path $PSScriptRoot 'NotificationPlaybackHostTest.java'
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\PlayerSelection.java'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes @files
if($LASTEXITCODE -ne 0){throw 'Notification host compilation failed'}
$actual=@(& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.NotificationPlaybackHostTest)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'Notification host execution failed'}
$result=[ordered]@{completed=$true;success=(@($actual.checks.PSObject.Properties|Where-Object{-not $_.Value}).Count -eq 0);checkedAt=[DateTimeOffset]::Now.ToString('o');scope='Production reader and notification listener/store with fake Android media and music notifications';checks=$actual.checks}
$json=$result|ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[Text.UTF8Encoding]::new($false))
Write-Output $json
if(-not $result.success){exit 1}
