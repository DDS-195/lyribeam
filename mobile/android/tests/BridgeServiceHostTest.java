package com.local.lyricbridge;
import android.app.Service;
import android.bluetooth.BluetoothSocket;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import java.util.*;
import java.util.function.BooleanSupplier;

public final class BridgeServiceHostTest {
 private static final Map<String,Boolean> checks=new LinkedHashMap<>();
 private static final List<String> events=new ArrayList<>();
 private static boolean waitFor(BooleanSupplier condition,long timeout)throws Exception{long end=System.nanoTime()+timeout*1000000;while(System.nanoTime()<end){if(condition.getAsBoolean())return true;Thread.sleep(10);}return condition.getAsBoolean();}
 private static BridgeService create(Boolean wanted){
  BluetoothSocket.reset();SharedPreferences.INSTANCE.reset();Context.bluetoothPermission=true;Service.failForeground=false;Service.foregroundCalls=0;android.os.Build.VERSION.SDK_INT=35;
  MediaReader.startDenied=MediaReader.refreshDenied=false;MediaReader.snapshotTitle="";IntegrationStore.supplied=null;
  BridgeService service=new BridgeService();if(wanted!=null)SyncState.setRequested(service,wanted);service.onCreate();return service;
 }
 private static BridgeService create(){return create(Boolean.TRUE);}
 private static Intent change(){return new Intent().setAction("com.local.lyricbridge.TARGET_CHANGED");}
 private static Intent startIntent(){return new Intent().setAction("com.local.lyricbridge.START_SYNC");}
 private static int start(BridgeService service){return service.onStartCommand(startIntent(),0,1);}
 private static void stop(BridgeService service)throws Exception{service.onDestroy();waitFor(()->workers()==0,1500);}
 private static int workers(){int count=0;for(Thread t:Thread.getAllStackTraces().keySet())if(t.isAlive()&&t.getName().equals("LyricBridge-Bluetooth"))count++;return count;}
 public static void main(String[] args)throws Exception{
  BridgeService s=create();checks.put("foregroundPromotionWaitsUntilWantedAndPrerequisitesChecked",Service.foregroundCalls==0);
  int policy=start(s);checks.put("requestedStartReturnsSticky",policy==Service.START_STICKY);checks.put("initialConnection",waitFor(()->BluetoothSocket.wrote("A"),1000));
  BluetoothSocket old=BluetoothSocket.first("A");SharedPreferences.INSTANCE.address="B";int targetPolicy=s.onStartCommand(change(),0,2);
  boolean changed=waitFor(()->BluetoothSocket.wrote("B"),1300);checks.put("switchConnectedTargetImmediately",changed&&old.closed);checks.put("targetChangeKeepsStickyRecoveryPolicy",targetPolicy==Service.START_STICKY);events.add("switchConnectedTargetImmediately="+changed);
  for(int i=0;i<20;i++){SharedPreferences.INSTANCE.address="T"+i;s.onStartCommand(change(),0,3+i);}
  checks.put("rapidChangesReachLastTarget",waitFor(()->BluetoothSocket.wrote("T19"),1300));
  checks.put("singleWorkerAfterRepeatedStarts",workers()==1);stop(s);checks.put("stopClosesConnectionAndWorker",workers()==0&&!BridgeService.running);
  checks.put("ordinaryDestroyPreservesRequestedDecision",SyncState.hasDecision(s)&&SyncState.requested(s));
  s=create();BluetoothSocket.blockConnectA=true;start(s);waitFor(()->BluetoothSocket.first("A")!=null,1000);
  SharedPreferences.INSTANCE.address="B";s.onStartCommand(change(),0,2);checks.put("switchDuringConnectClosesPendingSocket",waitFor(()->BluetoothSocket.wrote("B"),1300)&&BluetoothSocket.first("A").closed);stop(s);
  s=create();BluetoothSocket.blockWriteA=true;start(s);waitFor(()->BluetoothSocket.first("A")!=null&&BluetoothSocket.first("A").writeEntered,1000);
  SharedPreferences.INSTANCE.address="B";s.onStartCommand(change(),0,2);checks.put("switchUnblocksOldSocketWrite",waitFor(()->BluetoothSocket.wrote("B"),1300)&&BluetoothSocket.first("A").closed);stop(s);
  s=create();BluetoothSocket.failConnectA=true;start(s);waitFor(()->BluetoothSocket.first("A")!=null&&BluetoothSocket.first("A").closed,1000);
  SharedPreferences.INSTANCE.address="B";checks.put("retryReadsNewestSavedTarget",waitFor(()->BluetoothSocket.wrote("B"),6200));stop(s);
  s=create();MediaReader.startDenied=true;IntegrationStore.supplied=new org.json.JSONObject().put("type","media").put("source","integration").put("title","SDK歌曲");
  start(s);checks.put("approvedSdkConnectsWithoutStandardReadPermission",waitFor(()->BluetoothSocket.wrote("A"),1300)&&BridgeService.latestJson.contains("SDK歌曲"));
  checks.put("missingStandardPermissionIsNotConnectionFailure",!BridgeService.notice.contains("连接未成功"));stop(s);
  s=create();MediaReader.startDenied=true;policy=start(s);
  checks.put("requestedRecoveryWaitsForTemporarilyUnavailableSources",policy==Service.START_STICKY&&waitFor(()->BluetoothSocket.wrote("A"),1300)&&BridgeService.running);
  BluetoothSocket waiting=BluetoothSocket.first("A");MediaReader.startDenied=false;MediaReader.snapshotTitle="权限恢复后的歌曲";BridgeService.onPlaybackSourcesChanged();
  checks.put("standardSourceRecoveryReusesTheLiveConnection",waitFor(()->BridgeService.latestJson.contains("权限恢复后的歌曲"),1500)&&waiting!=null&&!waiting.closed&&BluetoothSocket.first("A")==waiting);stop(s);
  s=create();MediaReader.startDenied=true;start(s);waitFor(()->BluetoothSocket.wrote("A"),1300);waiting=BluetoothSocket.first("A");
  IntegrationStore.supplied=new org.json.JSONObject().put("type","media").put("source","integration").put("title","进程恢复后重新提供的歌曲");BridgeService.onPlaybackSourcesChanged();
  checks.put("lostProcessLocalSdkPayloadCanBePublishedAgainWithoutRestart",waitFor(()->BridgeService.latestJson.contains("进程恢复后重新提供的歌曲"),1500)&&waiting!=null&&!waiting.closed&&BluetoothSocket.first("A")==waiting);stop(s);
  s=create();start(s);waitFor(()->BluetoothSocket.wrote("A"),1000);BluetoothSocket stable=BluetoothSocket.first("A");MediaReader.refreshDenied=true;
  IntegrationStore.supplied=new org.json.JSONObject().put("type","media").put("source","integration").put("title","仍在同步");BridgeService.onPlaybackSourcesChanged();
  checks.put("revokedStandardPermissionKeepsApprovedSdkStreaming",waitFor(()->BridgeService.latestJson.contains("仍在同步"),1500)&&stable!=null&&!stable.closed&&BluetoothSocket.first("A")==stable);
  checks.put("revokedStandardPermissionDoesNotBecomeBluetoothRetry",!BridgeService.notice.contains("连接未成功"));stop(s);

  s=create();policy=start(s);waitFor(()->BluetoothSocket.wrote("A"),1000);old=BluetoothSocket.first("A");stop(s);SharedPreferences.INSTANCE.address="B";SharedPreferences.INSTANCE.recreateProcess();
  BridgeService restarted=new BridgeService();restarted.onCreate();int restartPolicy=restarted.onStartCommand(null,0,9);
  checks.put("savedRequestSurvivesProcessRecreation",SyncState.requested(restarted));
  checks.put("systemNullRestartCreatesOneFreshWorkerAndReconnectsLatestTarget",policy==Service.START_STICKY&&restartPolicy==Service.START_STICKY&&waitFor(()->BluetoothSocket.wrote("B"),1300)&&old.closed&&workers()==1);stop(restarted);
  s=create(null);policy=s.onStartCommand(null,0,1);checks.put("nullRestartWithoutAnyUserDecisionCannotStart",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&!BridgeService.running&&workers()==0&&!SyncState.hasDecision(s));stop(s);
  s=create(Boolean.FALSE);policy=s.onStartCommand(null,0,1);checks.put("explicitFalseNullRestartCannotResurrectSync",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&!BridgeService.running&&workers()==0);stop(s);
  s=create(Boolean.FALSE);policy=start(s);checks.put("lateStartIntentNeverOverridesExplicitStop",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&!SyncState.requested(s)&&workers()==0);stop(s);
  s=create(Boolean.FALSE);policy=s.onStartCommand(change(),0,1);checks.put("targetChangeIntentNeverOverridesExplicitStop",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&!SyncState.requested(s)&&workers()==0);stop(s);
  s=create(null);policy=s.onStartCommand(new Intent(),0,1);checks.put("legacyInternalStartMigratesExactlyOnce",policy==Service.START_STICKY&&SyncState.hasDecision(s)&&SyncState.requested(s)&&waitFor(()->BluetoothSocket.wrote("A"),1000));stop(s);
  s=create(Boolean.FALSE);policy=s.onStartCommand(new Intent(),0,1);checks.put("lateLegacyStartCannotReplaceAnExistingFalseDecision",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&!SyncState.requested(s)&&workers()==0);stop(s);
  s=create();start(s);waitFor(()->BluetoothSocket.wrote("A"),1000);boolean stopSaved=SyncState.setRequested(s,false);stop(s);SharedPreferences.INSTANCE.recreateProcess();restarted=new BridgeService();restarted.onCreate();restartPolicy=restarted.onStartCommand(null,0,2);
  checks.put("persistedUserStopSurvivesFreshProcessAndBlocksRecovery",stopSaved&&!SyncState.requested(restarted)&&restartPolicy==Service.START_NOT_STICKY&&restarted.stoppedSelf&&workers()==0);stop(restarted);
  s=create(Boolean.FALSE);SharedPreferences.INSTANCE.failCommit=true;boolean failedSave=SyncState.setRequested(s,true);policy=start(s);
  checks.put("failedStartCommitDoesNotLeaveInMemoryAuthorization",!failedSave&&!SyncState.requested(s));
  checks.put("failedStartCommitCannotAuthorizeAQueuedStart",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&workers()==0);SharedPreferences.INSTANCE.failCommit=false;stop(s);
  s=create();start(s);waitFor(()->BluetoothSocket.wrote("A"),1000);SharedPreferences.INSTANCE.failCommit=true;boolean failedStop=SyncState.setRequested(s,false);stop(s);policy=s.onStartCommand(startIntent(),0,7);
  checks.put("failedStopCommitStillBlocksLateStartsInCurrentProcess",!failedStop&&!SyncState.requested(s)&&policy==Service.START_NOT_STICKY&&workers()==0);SharedPreferences.INSTANCE.failCommit=false;stop(s);
  s=create();Context.bluetoothPermission=false;policy=start(s);checks.put("missingBluetoothPermissionStopsSafelyWithoutStickyLoop",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&workers()==0&&Service.foregroundCalls==0&&SyncState.requested(s));stop(s);
  s=create();SharedPreferences.INSTANCE.address="";policy=start(s);checks.put("missingComputerStopsSafelyWithoutStickyLoop",policy==Service.START_NOT_STICKY&&s.stoppedSelf&&workers()==0&&Service.foregroundCalls==0);stop(s);
  s=create();Service.failForeground=true;boolean escaped=false;policy=-1;try{policy=start(s);}catch(RuntimeException failure){escaped=true;}
  checks.put("foregroundPermissionFailureDoesNotCrashOrCreateWorker",!escaped&&policy==Service.START_NOT_STICKY&&s.stoppedSelf&&workers()==0&&SyncState.requested(s));Service.failForeground=false;stop(s);
  s=create();android.os.Build.VERSION.SDK_INT=30;Context.bluetoothPermission=false;policy=start(s);checks.put("preAndroid12DoesNotRequireNewBluetoothRuntimePermission",policy==Service.START_STICKY&&waitFor(()->BluetoothSocket.wrote("A"),1000));stop(s);android.os.Build.VERSION.SDK_INT=35;

  StringBuilder out=new StringBuilder("{\"checks\":{");boolean first=true;for(Map.Entry<String,Boolean> e:checks.entrySet()){if(!first)out.append(',');first=false;out.append('"').append(e.getKey()).append("\":").append(e.getValue());}out.append("},\"events\":[");first=true;for(String e:events){if(!first)out.append(',');first=false;out.append('"').append(e).append('"');}out.append("]}");System.out.println(out);if(checks.containsValue(false))System.exit(1);
 }
}
