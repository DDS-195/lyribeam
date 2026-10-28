param([string]$OutputDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test-media'))
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
$base=Join-Path $OutputDirectory 'base-connection'
if(-not (Test-Path -LiteralPath (Join-Path $base 'fixtures'))){& (Join-Path $PSScriptRoot 'test.ps1') -OutputDirectory $base; if($LASTEXITCODE -ne 0){throw 'Base fixtures unavailable'}}
$fixtures=Join-Path $OutputDirectory 'fixtures';$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $fixtures,$classes | Out-Null
$copies=@('android/content/SharedPreferences.java','android/os/SystemClock.java','org/json/JSONObject.java')
foreach($relative in $copies){$target=Join-Path $fixtures $relative;New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null;Copy-Item -LiteralPath (Join-Path (Join-Path $base 'fixtures') $relative) -Destination $target -Force}
$sources=@{
'android/content/SharedPreferences.java'=@"
package android.content; public class SharedPreferences {public static final SharedPreferences INSTANCE=new SharedPreferences();public String player="";public String getString(String key,String fallback){return "player".equals(key)?player:fallback;}}
"@
'android/content/Context.java'=@"
package android.content; public class Context {public static final String MEDIA_SESSION_SERVICE="media_session"; public Object getSystemService(String key){return new android.media.session.MediaSessionManager();} public SharedPreferences getSharedPreferences(String name,int mode){return SharedPreferences.INSTANCE;}}
"@
'android/content/ComponentName.java'=@"
package android.content; public class ComponentName {public ComponentName(Context context,Class<?> cls){}}
"@
'android/os/Handler.java'=@"
package android.os; public class Handler {public Handler(Looper looper){}}
"@
'android/os/Looper.java'=@"
package android.os; public class Looper {public static Looper getMainLooper(){return new Looper();}}
"@
'android/media/MediaMetadata.java'=@"
package android.media; public class MediaMetadata {public static final String METADATA_KEY_MEDIA_ID="mediaId",METADATA_KEY_TITLE="title",METADATA_KEY_ARTIST="artist",METADATA_KEY_ALBUM_ARTIST="albumArtist",METADATA_KEY_DURATION="duration",METADATA_KEY_DISPLAY_TITLE="displayTitle",METADATA_KEY_DISPLAY_SUBTITLE="displaySubtitle",METADATA_KEY_ALBUM="album";private final java.util.Map<String,String> values=new java.util.HashMap<>();public MediaMetadata(String title){this(title,"32112291");}public MediaMetadata(String title,String mediaId){values.put(METADATA_KEY_TITLE,title);values.put(METADATA_KEY_MEDIA_ID,mediaId);values.put(METADATA_KEY_ARTIST,"许嵩");}public MediaMetadata set(String key,String value){values.put(key,value);return this;}public String getString(String key){return values.get(key);}public long getLong(String key){return 200000;}}
"@
'android/media/session/PlaybackState.java'=@"
package android.media.session; public class PlaybackState {public static final int STATE_PLAYING=3,STATE_PAUSED=2;private final int value;public PlaybackState(){this(STATE_PLAYING);}public PlaybackState(int state){value=state;}public int getState(){return value;}public long getPosition(){return 1000;}public long getLastPositionUpdateTime(){return android.os.SystemClock.elapsedRealtime()-1;}public float getPlaybackSpeed(){return 1;}}
"@
'android/media/session/MediaController.java'=@"
package android.media.session; public class MediaController {public android.media.MediaMetadata metadata;public PlaybackState state;private final String packageName;public MediaController(android.media.MediaMetadata m){this("com.kugou.android.lite",m,new PlaybackState());}public MediaController(String p,android.media.MediaMetadata m,PlaybackState s){packageName=p;metadata=m;state=s;}public String getPackageName(){return packageName;}public PlaybackState getPlaybackState(){return state;}public android.media.MediaMetadata getMetadata(){return metadata;}public Object getSessionToken(){return this;}public void registerCallback(Callback callback,android.os.Handler handler){}public void unregisterCallback(Callback callback){}public static class Callback {public void onMetadataChanged(android.media.MediaMetadata value){}public void onPlaybackStateChanged(PlaybackState value){}public void onSessionDestroyed(){}}}
"@
'android/media/session/MediaSessionManager.java'=@"
package android.media.session; public class MediaSessionManager {public static java.util.List<MediaController> sessions=new java.util.ArrayList<>();public static boolean denyAccess;public interface OnActiveSessionsChangedListener {void onActiveSessionsChanged(java.util.List<MediaController> sessions);}public void addOnActiveSessionsChangedListener(OnActiveSessionsChangedListener listener,android.content.ComponentName name,android.os.Handler handler){}public void removeOnActiveSessionsChangedListener(OnActiveSessionsChangedListener listener){}public java.util.List<MediaController> getActiveSessions(android.content.ComponentName name){if(denyAccess)throw new SecurityException("Access revoked");return sessions;}}
"@
'org/json/JSONObject.java'=@"
package org.json; public class JSONObject {private final java.util.Map<String,Object> values=new java.util.HashMap<>();public JSONObject put(String key,Object value){values.put(key,value);return this;}public String optString(String key){Object value=values.get(key);return value==null?"":value.toString();}}
"@
'com/local/lyricbridge/NotificationPlaybackStore.java'=@"
package com.local.lyricbridge;public final class NotificationPlaybackStore {public static java.util.List<String> packages(){return java.util.Collections.emptyList();}public static org.json.JSONObject snapshot(String selected,long sequence){return null;}public static org.json.JSONObject snapshotExact(String selected,long sequence){return null;}public static org.json.JSONObject snapshotNewerThan(String selected,String excluded,long activity,long sequence,String retained){return null;}public static String trackIdentifier(String session,String mediaId,String title,String artist,String album,long duration){return session+":"+mediaId+":"+title;}}
"@
'com/local/lyricbridge/MediaListener.java'=@"
package com.local.lyricbridge;public class MediaListener {}
"@
}
foreach($entry in $sources.GetEnumerator()){$path=Join-Path $fixtures $entry.Key;New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent)|Out-Null;[System.IO.File]::WriteAllText($path,$entry.Value,[System.Text.UTF8Encoding]::new($false))}
$files=@(Get-ChildItem -LiteralPath $fixtures -Recurse -Filter '*.java'|ForEach-Object{$_.FullName})
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\MediaReader.java';$files+=Join-Path $PSScriptRoot 'MediaReaderHostTest.java'
$files+=Join-Path $androidRoot 'src\com\local\lyricbridge\PlayerSelection.java'
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes @files
if($LASTEXITCODE -ne 0){throw 'MediaReader host compilation failed'}
$actual=@(& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.MediaReaderHostTest)|ConvertFrom-Json
if($LASTEXITCODE -ne 0){throw 'MediaReader host execution failed'}
$result=[ordered]@{completed=$true;success=(@($actual.checks.PSObject.Properties|Where-Object{-not $_.Value}).Count -eq 0);checkedAt=[DateTimeOffset]::Now.ToString('o');scope='Production MediaReader with fake Android media sessions on host JVM';checks=$actual.checks}
$json=$result|ConvertTo-Json -Depth 5;[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[System.Text.UTF8Encoding]::new($false));Write-Output $json
if(-not $result.success){exit 1}
