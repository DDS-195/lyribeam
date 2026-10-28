param([string]$OutputDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.local\test-results\test'), [string]$BridgeServiceSource = '')
$ErrorActionPreference='Stop'
$androidRoot=Split-Path $PSScriptRoot -Parent
. (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts\build-env.ps1')
$javaBin=Get-LyriBeamJavaBin
$fixtureRoot=Join-Path $OutputDirectory 'fixtures'
$classes=Join-Path $OutputDirectory 'classes'
New-Item -ItemType Directory -Force -Path $fixtureRoot,$classes | Out-Null
$fixtures=@{
'android/content/Context.java'=@"
package android.content;
public class Context {
 public static final String BLUETOOTH_SERVICE="bluetooth";
 public <T> T getSystemService(Class<T> kind) { try{return kind.newInstance();}catch(Exception e){throw new RuntimeException(e);} }
 public Object getSystemService(String name){return new android.bluetooth.BluetoothManager();}
 public static volatile boolean bluetoothPermission=true;
 public SharedPreferences getSharedPreferences(String name,int mode){return SharedPreferences.INSTANCE;}
 public int checkSelfPermission(String permission){return bluetoothPermission?android.content.pm.PackageManager.PERMISSION_GRANTED:android.content.pm.PackageManager.PERMISSION_DENIED;}
}
"@
'android/content/SharedPreferences.java'=@"
package android.content;
import java.util.*;
public class SharedPreferences {
 public static final SharedPreferences INSTANCE=new SharedPreferences();
 public volatile String address="A"; public volatile boolean failCommit;
 private final Map<String,Object> values=new HashMap<>(),disk=new HashMap<>();
 public String getString(String key,String fallback){return "device".equals(key)?address:fallback;}
 public synchronized boolean contains(String key){return values.containsKey(key);}
 public synchronized boolean getBoolean(String key,boolean fallback){Object value=values.get(key);return value instanceof Boolean?(Boolean)value:fallback;}
 public Editor edit(){return new Editor();}
 public synchronized void reset(){values.clear();disk.clear();address="A";failCommit=false;}
 public synchronized void recreateProcess(){values.clear();values.putAll(disk);failCommit=false;}
 public final class Editor {
  private final Map<String,Object> pending=new HashMap<>();private final Set<String> removed=new HashSet<>();
  public Editor putBoolean(String key,boolean value){pending.put(key,value);return this;}
  public Editor remove(String key){removed.add(key);return this;}
  public boolean commit(){synchronized(SharedPreferences.this){for(String key:removed)values.remove(key);values.putAll(pending);if(failCommit)return false;for(String key:removed)disk.remove(key);disk.putAll(pending);return true;}}
  public void apply(){commit();}
 }
}
"@
'android/Manifest.java'=@"
package android; public final class Manifest {public static final class permission {public static final String BLUETOOTH_CONNECT="android.permission.BLUETOOTH_CONNECT";}}
"@
'android/content/pm/PackageManager.java'=@"
package android.content.pm;public class PackageManager {public static final int PERMISSION_GRANTED=0,PERMISSION_DENIED=-1;}
"@
'android/content/Intent.java'=@"
package android.content;
public class Intent { private String action; public Intent(){} public Intent(Context c,Class<?> target){} public Intent setAction(String value){action=value;return this;} public String getAction(){return action;} }
"@
'android/app/Service.java'=@"
package android.app;
public class Service extends android.content.Context {
 public static final int START_STICKY=1,START_NOT_STICKY=2,STOP_FOREGROUND_REMOVE=1;
 public static volatile boolean failForeground;public static volatile int foregroundCalls;public boolean stoppedSelf;
 public void onCreate(){} public int onStartCommand(android.content.Intent i,int f,int id){return 0;}
 public void onDestroy(){} public android.os.IBinder onBind(android.content.Intent i){return null;}
 public void startForeground(int id,Notification n,int type){promote();} public void startForeground(int id,Notification n){promote();}
 private void promote(){foregroundCalls++;if(failForeground)throw new SecurityException("Foreground permission denied");}
 public void stopForeground(int flags){} public void stopSelf(){stoppedSelf=true;}
}
"@
'android/app/Notification.java'=@"
package android.app;
public class Notification {public static class Builder { public Builder(android.content.Context c,String channel){} public Builder setSmallIcon(int i){return this;} public Builder setContentTitle(String s){return this;} public Builder setContentText(String s){return this;} public Builder setContentIntent(PendingIntent p){return this;} public Builder setOngoing(boolean b){return this;} public Notification build(){return new Notification();} }}
"@
'android/app/NotificationChannel.java'=@"
package android.app; public class NotificationChannel {public NotificationChannel(String a,String b,int c){} }
"@
'android/app/NotificationManager.java'=@"
package android.app; public class NotificationManager {public static final int IMPORTANCE_LOW=2; public void createNotificationChannel(NotificationChannel c){} }
"@
'android/app/PendingIntent.java'=@"
package android.app; public class PendingIntent {public static final int FLAG_IMMUTABLE=1; public static PendingIntent getActivity(android.content.Context c,int request,android.content.Intent i,int flags){return new PendingIntent();} }
"@
'android/content/pm/ServiceInfo.java'=@"
package android.content.pm; public class ServiceInfo {public static final int FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE=16;}
"@
'android/os/Build.java'=@"
package android.os; public class Build {public static class VERSION {public static volatile int SDK_INT=35;} }
"@
'android/os/IBinder.java'=@"
package android.os; public interface IBinder {}
"@
'android/os/SystemClock.java'=@"
package android.os; public class SystemClock {public static long elapsedRealtime(){return System.nanoTime()/1000000;}}
"@
'android/util/Log.java'=@"
package android.util; public class Log {public static int w(String tag,String message){return 0;}public static int i(String tag,String message){return 0;}}
"@
'android/R.java'=@"
package android; public class R {public static class drawable {public static final int ic_media_play=1;}}
"@
'android/bluetooth/BluetoothManager.java'=@"
package android.bluetooth; public class BluetoothManager {public BluetoothAdapter getAdapter(){return new BluetoothAdapter();}}
"@
'android/bluetooth/BluetoothAdapter.java'=@"
package android.bluetooth; public class BluetoothAdapter {public boolean isEnabled(){return true;} public BluetoothDevice getRemoteDevice(String address){return new BluetoothDevice(address);}}
"@
'android/bluetooth/BluetoothDevice.java'=@"
package android.bluetooth; public class BluetoothDevice {private final String address; public BluetoothDevice(String a){address=a;} public BluetoothSocket createRfcommSocketToServiceRecord(java.util.UUID id){return new BluetoothSocket(address);}}
"@
'android/bluetooth/BluetoothSocket.java'=@"
package android.bluetooth;
import java.io.*; import java.util.*;
public class BluetoothSocket {
 public static final List<BluetoothSocket> CREATED=Collections.synchronizedList(new ArrayList<BluetoothSocket>());
 public static volatile boolean blockConnectA,failConnectA,blockWriteA;
 public final String address; public volatile boolean connected,closed,writeEntered; public volatile int writes;
 public BluetoothSocket(String a){address=a;CREATED.add(this);}
 public synchronized void connect() throws IOException {while("A".equals(address)&&blockConnectA&&!closed)try{wait();}catch(InterruptedException e){throw new IOException(e);} if(closed||("A".equals(address)&&failConnectA))throw new IOException("test connection closed");connected=true;}
 public boolean isConnected(){return connected&&!closed;}
 public OutputStream getOutputStream(){return new OutputStream(){public void write(int value)throws IOException{write(new byte[]{(byte)value});} public void write(byte[] value)throws IOException{synchronized(BluetoothSocket.this){writeEntered=true;while("A".equals(address)&&blockWriteA&&!closed)try{BluetoothSocket.this.wait();}catch(InterruptedException e){throw new IOException(e);}if(closed)throw new IOException("closed");writes++;}}};}
 public synchronized void close(){closed=true;connected=false;notifyAll();}
 public static void reset(){CREATED.clear();blockConnectA=failConnectA=blockWriteA=false;}
 public static BluetoothSocket first(String address){synchronized(CREATED){for(BluetoothSocket s:CREATED)if(address.equals(s.address))return s;}return null;}
 public static boolean wrote(String address){synchronized(CREATED){for(BluetoothSocket s:CREATED)if(address.equals(s.address)&&s.writes>0)return true;}return false;}
}
"@
'org/json/JSONObject.java'=@"
package org.json; public class JSONObject {public String toString(){return "{}";}}
"@
'com/local/lyricbridge/IntegrationStore.java'=@"
package com.local.lyricbridge;public final class IntegrationStore {public static volatile org.json.JSONObject supplied;public static void setOnChanged(Runnable callback){}public static org.json.JSONObject getApprovedSnapshot(android.content.Context context,String selected){return supplied;}}
"@
'com/local/lyricbridge/PlaybackSources.java'=@"
package com.local.lyricbridge;public final class PlaybackSources {public static org.json.JSONObject select(org.json.JSONObject standard,org.json.JSONObject supplied,String selected){return supplied!=null?supplied:standard!=null?standard:new org.json.JSONObject().put("version",1).put("type","idle");}}
"@
'com/local/lyricbridge/SnapshotFrames.java'=@"
package com.local.lyricbridge;public final class SnapshotFrames {public String encode(org.json.JSONObject snapshot,long now){return snapshot.toString();}}
"@
'com/local/lyricbridge/MainActivity.java'=@"
package com.local.lyricbridge; public class MainActivity {}
"@
'com/local/lyricbridge/MediaReader.java'=@"
package com.local.lyricbridge; public class MediaReader {public static volatile boolean startDenied,refreshDenied;public static volatile String snapshotTitle="";public MediaReader(android.content.Context c){} public void setOnChanged(Runnable callback){} public void start(){if(startDenied)throw new SecurityException("Notification access missing");} public void stop(){} public void refresh(){if(refreshDenied)throw new SecurityException("Notification access revoked");} public org.json.JSONObject snapshot(){return snapshotTitle.isEmpty()?new org.json.JSONObject().put("type","idle"):new org.json.JSONObject().put("type","media").put("title",snapshotTitle);}}
"@
}
foreach($entry in $fixtures.GetEnumerator()){
 $path=Join-Path $fixtureRoot $entry.Key
 New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent)|Out-Null
 [System.IO.File]::WriteAllText($path,$entry.Value,[System.Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\org\json\JSONObject.java') -Destination (Join-Path $fixtureRoot 'org\json\JSONObject.java') -Force
$sources=@(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Filter '*.java'|ForEach-Object {$_.FullName})
if([string]::IsNullOrWhiteSpace($BridgeServiceSource)){$BridgeServiceSource=Join-Path $androidRoot 'src\com\local\lyricbridge\BridgeService.java'}
$sources+=$BridgeServiceSource
$sources+=Join-Path $androidRoot 'src\com\local\lyricbridge\SyncState.java'
$sources+=Join-Path $androidRoot 'src\com\local\lyricbridge\UpdateSignal.java'
$sources+=Join-Path $PSScriptRoot 'BridgeServiceHostTest.java'
& (Join-Path $javaBin 'javac.exe') -encoding UTF-8 --release 8 -d $classes @sources
if($LASTEXITCODE -ne 0){throw 'Host regression test compilation failed'}
$runtime=@(& (Join-Path $javaBin 'java.exe') -cp $classes com.local.lyricbridge.BridgeServiceHostTest)
$runtimeExit=$LASTEXITCODE
$activity=Get-Content -LiteralPath (Join-Path $androidRoot 'src\com\local\lyricbridge\MainActivity.java') -Raw
$reader=Get-Content -LiteralPath (Join-Path $androidRoot 'src\com\local\lyricbridge\MediaReader.java') -Raw
$manifest=[xml](Get-Content -LiteralPath (Join-Path $androidRoot 'AndroidManifest.xml') -Raw)
$permissions=@($manifest.manifest.'uses-permission'|ForEach-Object {$_.GetAttribute('name','http://schemas.android.com/apk/res/android')})
$expected=@('android.permission.BLUETOOTH','android.permission.BLUETOOTH_ADMIN','android.permission.BLUETOOTH_CONNECT','android.permission.POST_NOTIFICATIONS','android.permission.FOREGROUND_SERVICE','android.permission.FOREGROUND_SERVICE_CONNECTED_DEVICE')
$hostResult=$runtime -join "`n" | ConvertFrom-Json
$checks=[ordered]@{
 noExternalAutomaticIntentActions=($activity -notmatch 'probeLyrics|resumeSync')
 noProbeCode=($reader -notmatch 'probeLyrics|lyric-probe:|cacheAccess|bundleText')
 unchangedPermissions=(@(Compare-Object $expected $permissions).Count -eq 0)
 noPhoneInternetAudioOrMediaControl=($permissions -notcontains 'android.permission.INTERNET' -and $permissions -notcontains 'android.permission.RECORD_AUDIO' -and $reader -notmatch 'getTransportControls|dispatchMediaButtonEvent')
}
foreach($item in $hostResult.checks.PSObject.Properties){$checks[$item.Name]=[bool]$item.Value}
$result=[ordered]@{completed=$true;success=(@($checks.Values|Where-Object {-not $_}).Count -eq 0 -and $runtimeExit -eq 0);checkedAt=[DateTimeOffset]::Now.ToString('o');scope='Host JVM using production BridgeService and UpdateSignal with fake Android and Bluetooth; no phone controlled';checks=$checks;events=$hostResult.events}
$json=$result|ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'),$json,[System.Text.UTF8Encoding]::new($false))
Write-Output $json
if(-not $result.success){exit 1}
