package com.local.lyricbridge;
import android.content.*;import android.content.pm.PackageManager;import android.net.Uri;import android.os.*;import java.util.*;import org.json.JSONObject;import com.local.lyricbridge.sdk.LyricBridgeClient;
public final class IntegrationHostTest {
 private static final Context c=Context.HELPER;private static final IntegrationProvider p=new IntegrationProvider();
 private static final Map<String,Boolean> checks=new LinkedHashMap<>();
 private static void check(String name,boolean value){checks.put(name,value);}
 private static void caller(int uid,String name){Binder.uid=uid;PackageManager.packages.put(uid,new String[]{name});ContentProvider.attributed=name;}
 private static Bundle snapshot(String track){Bundle b=new Bundle();b.putInt("protocolVersion",1);b.putString("sessionId","session-1");b.putString("trackId",track);b.putString("title","原创歌曲");b.putString("artist","独立作者");b.putString("album","自制专辑");b.putBoolean("positionKnown",true);b.putBoolean("playing",true);b.putLong("durationMs",180000);b.putLong("positionMs",12000);b.putDouble("speed",1);return b;}
 private static Bundle call(String method,Bundle b){return p.call(method,null,b);}
 private static void reset(){IntegrationStore.setEnabled(c,false);IntegrationStore.setEnabled(c,true);IntegrationStore.setApproved(c,"dev.small.player",true);caller(1001,"dev.small.player");}
 public static void main(String[] args)throws Exception{
  caller(1001,"dev.small.player");ContentResolver.provider=p;p.onCreate();
  check("disabledByDefault","disabled".equals(call("status",null).getString("state")));
  check("disabledPublicationDoesNotStorePayload",!call("publish",snapshot("one")).getBoolean("accepted",false)&&IntegrationStore.getApprovedSnapshot(c,"")==null);
  check("requestRegistersVerifiedSource",IntegrationStore.sourcePackages(c).contains("dev.small.player"));
  IntegrationStore.setEnabled(c,true);
  check("unapprovedPublicationIsPending","pending".equals(call("publish",snapshot("one")).getString("state"))&&IntegrationStore.getApprovedSnapshot(c,"")==null);
  IntegrationStore.setApproved(c,"dev.small.player",true);
  check("approvalAllowsPublication",call("publish",snapshot("one")).getBoolean("accepted",false));
  JSONObject s=IntegrationStore.getApprovedSnapshot(c,"");
  check("normalizedIntegrationSource","integration".equals(s.optString("source"))&&"sync".equals(s.optString("capability"))&&"one".equals(s.optString("trackId")));
  check("providerSuppliesVerifiedPackage","dev.small.player".equals(s.optString("packageName")));
  SystemClock.now+=1500;s=IntegrationStore.getApprovedSnapshot(c,"");
  check("playingPositionExtrapolatesWithinLease",s.optLong("positionMs",-1)==13500);
  Bundle lyric=snapshot("one");lyric.putString("providedLyrics","[00:00]原创歌词");lyric.putString("providedLyricsTrackId","one");call("publish",lyric);
  check("matchingTrackLyricsAvailable",IntegrationStore.getApprovedSnapshot(c,"").optString("providedLyrics").contains("原创歌词"));
  call("publish",snapshot("two"));
  check("newSongCannotInheritOldLyrics",IntegrationStore.getApprovedSnapshot(c,"").optString("providedLyrics").isEmpty());
  lyric=snapshot("two");lyric.putString("providedLyrics","[00:00]错歌");lyric.putString("providedLyricsTrackId","one");
  check("wrongTrackBindingInvalidatesSender","invalid".equals(call("publish",lyric).getString("state"))&&IntegrationStore.getApprovedSnapshot(c,"")==null);
  reset();Bundle metadata=snapshot("meta");metadata.putBoolean("positionKnown",false);metadata.putLong("positionMs",-1);call("publish",metadata);
  check("metadataOnlyNeverInventsProgress","metadata-only".equals(IntegrationStore.getApprovedSnapshot(c,"").optString("capability"))&&!IntegrationStore.getApprovedSnapshot(c,"").optBoolean("positionKnown"));
  check("manualSelectionCannotLeakAnotherSource",IntegrationStore.getApprovedSnapshot(c,"different.player")==null);
  reset();call("publish",snapshot("lease"));SystemClock.now+=10001;
  check("expiredLeaseDropsSongAndLyrics",IntegrationStore.getApprovedSnapshot(c,"")==null);
  reset();Bundle tooLong=snapshot("lease");tooLong.putLong("leaseMs",30001);
  check("leasesAreBounded","invalid".equals(call("publish",tooLong).getString("state")));
  reset();Bundle stale=snapshot("old");stale.putLong("sampledAtElapsedRealtimeMs",SystemClock.now-30001);
  check("staleSamplesAreRejected","invalid".equals(call("publish",stale).getString("state")));
  Bundle future=snapshot("future");future.putLong("sampledAtElapsedRealtimeMs",SystemClock.now+1);
  check("futureClockSamplesAreRejected","invalid".equals(call("publish",future).getString("state")));
  for(String mode:new String[]{"longTitle","longId","hugeLyrics","negativeDuration","unknownVersion","nanSpeed","negativeKnownPosition"}){
   reset();Bundle b=snapshot("bad");
   if(mode.equals("longTitle"))b.putString("title",String.join("",Collections.nCopies(513,"a")));
   if(mode.equals("longId"))b.putString("trackId",String.join("",Collections.nCopies(257,"a")));
   if(mode.equals("hugeLyrics")){b.putString("providedLyrics",String.join("",Collections.nCopies(65537,"a")));b.putString("providedLyricsTrackId","bad");}
   if(mode.equals("negativeDuration"))b.putLong("durationMs",-1);
   if(mode.equals("unknownVersion"))b.putInt("protocolVersion",2);
   if(mode.equals("nanSpeed"))b.putDouble("speed",Double.NaN);
   if(mode.equals("negativeKnownPosition"))b.putLong("positionMs",-1);
   check("rejects_"+mode,"invalid".equals(call("publish",b).getString("state")));
  }
  for(String field:new String[]{"title","sessionId","trackId"}) {
   reset();Bundle b=snapshot("unicode");b.putString(field,"\u00a0\u3000\u0085");
   check("unicodeBlankRequiredFieldRejected_"+field,"invalid".equals(call("publish",b).getString("state")));
  }
  reset();Bundle bounded=snapshot("surrogate");String pair="\ud83d\ude03";String exact=String.join("",Collections.nCopies(510,"a"))+pair;
  bounded.putString("title","\u3000"+exact+"\u00a0");bounded.putString("artist","\u00a0\u3000\u0085");
  Bundle acceptedBounded=call("publish",bounded);JSONObject boundedFrame=IntegrationStore.getApprovedSnapshot(c,"");
  check("unicodeEdgeTrimPreservesSurrogateBoundary",acceptedBounded.getBoolean("accepted",false)&&boundedFrame!=null&&exact.equals(boundedFrame.optString("title")));
  check("unicodeBlankOptionalFieldNormalizesToEmpty",boundedFrame!=null&&boundedFrame.optString("artist").isEmpty());
  for(String field:new String[]{"title","sessionId","trackId"}) {
   boolean sdkRejected=false;
   try {LyricBridgeClient.Snapshot.builder(field.equals("sessionId")?"\u00a0\u3000\u0085":"s",field.equals("trackId")?"\u00a0\u3000\u0085":"t",field.equals("title")?"\u00a0\u3000\u0085":"歌").build();}
   catch(IllegalArgumentException invalid){sdkRejected=true;}
   check("sdkUnicodeBlankRequiredFieldRejected_"+field,sdkRejected);
  }

  reset();Bundle spoof=snapshot("spoof");spoof.putString("packageName","com.tencent.qqmusic");boolean denied=false;try{call("publish",spoof);}catch(SecurityException ex){denied=true;}
  check("claimedPackageCannotSpoofAnotherPlayer",denied);
  PackageManager.packages.put(1001,new String[]{"dev.small.player","dev.sibling.player"});denied=false;try{call("status",null);}catch(SecurityException ex){denied=true;}
  check("sharedUidCannotChooseAnUnverifiableSibling",denied);
  caller(1001,"dev.small.player");ContentProvider.attributed="dev.sibling.player";denied=false;try{call("status",null);}catch(SecurityException ex){denied=true;}
  check("attributionMustMatchUniqueUidPackage",denied);
  reset();call("publish",snapshot("clear"));call("clear",null);
  check("clearDropsCallerPayload",IntegrationStore.getApprovedSnapshot(c,"")==null);
  call("publish",snapshot("disable"));IntegrationStore.setEnabled(c,false);IntegrationStore.setEnabled(c,true);
  check("disableCannotResurrectPayloadWhenReenabled",IntegrationStore.getApprovedSnapshot(c,"")==null);
  call("publish",snapshot("revoke"));IntegrationStore.setApproved(c,"dev.small.player",false);
  check("revocationDropsPayload",IntegrationStore.getApprovedSnapshot(c,"")==null);
  reset();call("publish",snapshot("private"));caller(1002,"dev.other.player");Bundle status=call("status",null);
  check("statusDisclosesOnlyCallerApproval","dev.other.player".equals(status.getString("packageName"))&&!status.containsKey("title")&&!status.containsKey("providedLyrics")&&"pending".equals(status.getString("state")));
  boolean blocked=false;try{p.query(Uri.parse("content://x"),null,null,null,null);}catch(UnsupportedOperationException e){blocked=true;}
  check("providerDoesNotExposeSongQueries",blocked);
  reset();LyricBridgeClient client=new LyricBridgeClient(new Context("dev.small.player"));
  check("sdkStatusUsesSameProtocol",client.getStatus().isReady());
  LyricBridgeClient.Snapshot snap=LyricBridgeClient.Snapshot.builder("sdk-session","sdk-song","SDK原创").artist("作者").album("专辑").duration(90000).playback(3000,true,1).lyrics("[00:00]SDK歌词").build();
  check("sdkSnapshotPublishesThroughAuthenticatedProvider",client.publish(snap).accepted&&"SDK原创".equals(IntegrationStore.getApprovedSnapshot(c,"").optString("title")));
  check("sdkLyricsBindToCurrentTrack","sdk-song".equals(IntegrationStore.getApprovedSnapshot(c,"").optString("providedLyricsTrackId")));
  client.clear();check("sdkClearDropsItsSong",IntegrationStore.getApprovedSnapshot(c,"")==null);
  ContentResolver.provider=null;check("sdkHandlesMissingHelper","unavailable".equals(client.getStatus().state));ContentResolver.provider=p;
  for(int i=0;i<40;i++)IntegrationStore.noteSource(c,"dev.extra"+i+".player");
  check("sourceRequestListIsBounded",IntegrationStore.sourcePackages(c).size()<=32);
  reset();IntegrationStore.setApproved(c,"com.kugou.android",true);caller(2001,"com.kugou.android");call("publish",snapshot("family-base"));
  JSONObject family=IntegrationStore.getApprovedSnapshot(c,"platform:kugou");
  check("platformFilterAcceptsApprovedBaseIntegration",family!=null&&"com.kugou.android".equals(family.optString("packageName")));
  call("clear",null);caller(2002,"com.kugou.android.lite");Bundle unapprovedEdition=call("publish",snapshot("family-concept"));
  check("platformGroupingCannotBypassEditionApproval","pending".equals(unapprovedEdition.getString("state"))&&IntegrationStore.getApprovedSnapshot(c,"platform:kugou")==null);
  IntegrationStore.setApproved(c,"com.kugou.android.lite",true);call("publish",snapshot("family-concept"));family=IntegrationStore.getApprovedSnapshot(c,"platform:kugou");
  check("platformFilterAcceptsApprovedConceptIntegration",family!=null&&"com.kugou.android.lite".equals(family.optString("packageName"))&&"family-concept".equals(family.optString("trackId")));
  check("legacyBaseSelectorAcceptsApprovedConceptIntegration",IntegrationStore.getApprovedSnapshot(c,"com.kugou.android")!=null);
  check("integrationPlatformFilterRejectsOtherPlatform",IntegrationStore.getApprovedSnapshot(c,"platform:qq")==null);
  check("integrationSystemFilterCannotBroadenToPlatform",IntegrationStore.getApprovedSnapshot(c,"system:com.miui.player")==null);
  IntegrationStore.setApproved(c,"com.kugou.android.lite",false);
  check("revokingEditionApprovalImmediatelyDropsGroupedSource",IntegrationStore.getApprovedSnapshot(c,"platform:kugou")==null);
  StringBuilder out=new StringBuilder("{\"checks\":{");boolean first=true;
  for(Map.Entry<String,Boolean> e:checks.entrySet()){if(!first)out.append(',');first=false;out.append('\"').append(e.getKey()).append("\":").append(e.getValue());}
  System.out.println(out.append("}}"));
 }
}
