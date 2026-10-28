package com.local.lyricbridge;
import org.json.JSONObject;
import java.util.*;
public final class PlaybackSourcesHostTest {
 private static JSONObject media(String pkg,String source,String title,boolean known,boolean playing) {
  return new JSONObject().put("version",1).put("type","media").put("packageName",pkg).put("source",source)
   .put("capability",known?"sync":"metadata-only").put("sessionId",source+"-session").put("trackId",source+"-track")
   .put("title",title).put("artist","Singer").put("album","Album A").put("mediaId","42").put("durationMs",200000L)
   .put("positionKnown",known).put("positionMs",known?25000L:-1L).put("playing",playing).put("speed",1.0);
 }
 private static JSONObject standard(){return media("same.player","media-session","Current Song",true,true);}
 private static JSONObject supplied(){return media("same.player","integration","Current Song",true,true).put("providedLyrics","[00:00]provided").put("providedLyricsTrackId","integration-track");}
 private static JSONObject copy(JSONObject value){JSONObject result=new JSONObject();for(Iterator<String> keys=value.keys();keys.hasNext();){String key=keys.next();result.put(key,value.opt(key));}return result;}
 private static boolean idle(JSONObject value){return value!=null&&"1".equals(value.optString("version"))&&"idle".equals(value.optString("type"))&&"none".equals(value.optString("capability"));}
 private static boolean full(String frame){return frame.contains("\"providedLyrics\":");}
 public static void main(String[] args)throws Exception {
  Map<String,Boolean> checks=new LinkedHashMap<>();
  JSONObject empty=new JSONObject().put("version",1).put("type","idle").put("capability","none");
  checks.put("existingIdleFrameIsPreserved",PlaybackSources.select(empty,null,"")==empty);
  checks.put("absentSourcesProduceValidIdleFrame",idle(PlaybackSources.select(null,null,"")));
  JSONObject system=standard(),provided=supplied();
  checks.put("standardSessionWorksWithoutDeveloperIntegration",PlaybackSources.select(system,null,"")==system);
  checks.put("ideographicBlankSuppliedTitleFallsBackToStandard",PlaybackSources.select(system,copy(provided).put("title","\u3000"),"")==system);
  checks.put("nbspBlankSuppliedTitleFallsBackToStandard",PlaybackSources.select(system,copy(provided).put("title","\u00a0"),"")==system);
  checks.put("unicodeBlankSuppliedTitleWithoutStandardProducesIdle",idle(PlaybackSources.select(null,copy(provided).put("title","\u3000\u00a0"),"")));
  checks.put("unicodeBlankStandardDoesNotBlockValidSuppliedSource",PlaybackSources.select(copy(system).put("title","\u3000\u00a0"),provided,"")==provided);
  checks.put("unicodeBlankStandardWithoutOtherSourceProducesIdle",idle(PlaybackSources.select(copy(system).put("title","\u3000\u00a0"),null,"")));
  checks.put("approvedSuppliedSourceWorksWithoutSystemSession",PlaybackSources.select(empty,provided,"")==provided);
  checks.put("sameAppSameRecordingCanProvideLyricsAndClock",PlaybackSources.select(system,provided,"")==provided);
  checks.put("unboundSuppliedLyricsCannotDisplaceVerifiedStandardClock",PlaybackSources.select(system,copy(provided).put("providedLyricsTrackId","old-track"),"")==system);
  checks.put("providedLyricsWithoutClockDoNotDiscardKnownSystemClock",PlaybackSources.select(system,copy(provided).put("positionKnown",false),"")==system);
  JSONObject unknownClock=copy(system).put("positionKnown",false).put("capability","metadata-only").put("positionMs",-1);
  // Keep reference comparisons explicit rather than relying on equal JSON values.
  JSONObject clockOnly=copy(provided).put("providedLyrics","");
  checks.put("sameRecordingSuppliedClockUpgradesMissingStandardClock",PlaybackSources.select(unknownClock,clockOnly,"")==clockOnly);
  JSONObject other=copy(provided).put("packageName","other.player");
  checks.put("twoPlayingAppsPreferAuthoritativeSystemSession",PlaybackSources.select(system,other,"")==system);
  checks.put("manualSelectionCanChooseSuppliedDifferentApp",PlaybackSources.select(system,other,"other.player")==other);
  checks.put("manualSelectionRejectsOtherAppSuppliedSnapshot",PlaybackSources.select(system,other,"same.player")==system);
  checks.put("manualSelectionCannotLeakOnlyUnselectedStandard",idle(PlaybackSources.select(system,null,"missing.player")));
  checks.put("manualSelectionCannotLeakOnlyUnselectedSupplied",idle(PlaybackSources.select(null,other,"missing.player")));
  checks.put("newSongTitleCannotBeReplacedByOldSameAppLyrics",PlaybackSources.select(system,copy(provided).put("title","Old Song"),"")==system);
  checks.put("differentArtistCannotBeCombinedWithCurrentClock",PlaybackSources.select(system,copy(provided).put("artist","Other Singer"),"")==system);
  checks.put("differentDurationCannotBeCombinedWithCurrentClock",PlaybackSources.select(system,copy(provided).put("durationMs",220000L),"")==system);
  checks.put("knownAlbumConflictCannotAttachAnotherRecordingLyrics",PlaybackSources.select(system,copy(provided).put("album","Album B"),"")==system);
  checks.put("knownMediaIdentifierConflictCannotAttachAnotherRecordingLyrics",PlaybackSources.select(system,copy(provided).put("mediaId","99"),"")==system);
  JSONObject newSong=copy(provided).put("title","New Song").put("trackId","new-track").put("providedLyricsTrackId","new-track");
  checks.put("playingSuppliedNewSongCanReplacePausedOldSession",PlaybackSources.select(copy(system).put("playing",false),newSong,"")==newSong);
  JSONObject notification=media("notification.player","notification","Notification Song",false,false).put("album","").put("artist","").put("mediaId","").put("durationMs",0L);
  String beforeNotification=notification.toString();
  JSONObject selected=PlaybackSources.select(notification,null,"");
  checks.put("notificationFallbackDoesNotInventClockOrPlayingState",selected==notification&&!selected.optBoolean("positionKnown")&&!selected.optBoolean("playing")&&selected.optLong("positionMs")==-1);
  checks.put("notificationSelectionDoesNotBorrowStaleIntegrationFields",PlaybackSources.select(notification,copy(other).put("type","idle"),"")==notification&&notification.toString().equals(beforeNotification));
  checks.put("suppliedClockCanUpgradeNotificationFallback",PlaybackSources.select(notification,provided,"")==provided);
  String originalSystem=system.toString(),originalSupplied=provided.toString();PlaybackSources.select(system,provided,"");
  checks.put("sourceArbitrationNeverMutatesItsInputs",system.toString().equals(originalSystem)&&provided.toString().equals(originalSupplied));

  JSONObject frame=supplied().put("providedLyrics","[00:00]first中文\n[00:02]second");
  String original=frame.toString();SnapshotFrames encoder=new SnapshotFrames();
  String first=encoder.encode(frame,1000),heartbeat=encoder.encode(frame,1001);
  checks.put("firstFrameCarriesCompleteLyrics",full(first)&&first.contains("first中文")&&first.contains("\\n[00:02]second"));
  checks.put("heartbeatOmitsLyricsButKeepsBindingAndPlaybackMetadata",!full(heartbeat)&&heartbeat.contains("\"providedLyricsTrackId\":\"integration-track\"")&&heartbeat.contains("\"title\":\"Current Song\"")&&heartbeat.contains("\"positionMs\":25000"));
  checks.put("encoderNeverMutatesOriginalJSONObject",frame.toString().equals(original)&&frame.has("providedLyrics"));
  JSONObject changedLyrics=copy(frame).put("providedLyrics","[00:00]corrected");
  checks.put("sameTrackUpdatedLyricsAreSentImmediately",full(encoder.encode(changedLyrics,1002)));
  JSONObject newTrack=copy(changedLyrics).put("trackId","next-track").put("providedLyricsTrackId","next-track");
  checks.put("newTrackResendsEvenIdenticalLyricsContent",full(encoder.encode(newTrack,1003)));
  JSONObject newSession=copy(newTrack).put("sessionId","new-session");
  checks.put("newSessionResendsEvenIdenticalTrackAndLyrics",full(encoder.encode(newSession,1004)));
  checks.put("newPackageResendsEvenIdenticalSessionTrackAndLyrics",full(encoder.encode(copy(newSession).put("packageName","another.player"),1005)));
  SnapshotFrames periodic=new SnapshotFrames();periodic.encode(frame,1000);
  checks.put("unchangedLyricsRemainSuppressedBeforeRepairDeadline",!full(periodic.encode(frame,15999)));
  checks.put("lyricsRepairFrameIsSentAtFifteenSeconds",full(periodic.encode(frame,16000)));
  checks.put("repairTimerRestartsAfterFullFrame",!full(periodic.encode(frame,16001)));
  checks.put("reconnectWithNewEncoderAlwaysResendsLyrics",full(new SnapshotFrames().encode(frame,16001)));
  checks.put("backwardClockCannotSuppressLyricsIndefinitely",full(periodic.encode(frame,15999)));
  SnapshotFrames absent=new SnapshotFrames();absent.encode(frame,1000);absent.encode(copy(frame).put("providedLyrics",""),1001);
  checks.put("lyricsReturningAfterAnAbsentSnapshotAreResent",full(absent.encode(frame,1002)));
  checks.put("encodingChangedFramesDoesNotModifyOtherInputs",changedLyrics.optString("providedLyrics").equals("[00:00]corrected")&&newTrack.optString("trackId").equals("next-track")&&newSession.optString("sessionId").equals("new-session"));
  JSONObject familyStandard=copy(system).put("packageName","com.kugou.android.lite");
  JSONObject familySupplied=copy(provided).put("packageName","com.kugou.android");
  String familyStandardBefore=familyStandard.toString(),familySuppliedBefore=familySupplied.toString();
  checks.put("platformFilterAcceptsConceptStandardSource",PlaybackSources.select(familyStandard,null,"platform:kugou")==familyStandard);
  checks.put("platformFilterAcceptsBaseSuppliedSource",PlaybackSources.select(null,familySupplied,"platform:kugou")==familySupplied);
  checks.put("legacyBaseFilterAcceptsConceptSource",PlaybackSources.select(familyStandard,null,"com.kugou.android")==familyStandard);
  checks.put("samePlatformDifferentAppsNeverMergeClockAndLyrics",PlaybackSources.select(familyStandard,familySupplied,"platform:kugou")==familyStandard&&familyStandard.toString().equals(familyStandardBefore)&&familySupplied.toString().equals(familySuppliedBefore));
  checks.put("platformFilterRejectsOtherPlatformSources",idle(PlaybackSources.select(copy(system).put("packageName","com.tencent.qqmusic"),null,"platform:kugou")));
  JSONObject systemMusicFrame=copy(system).put("packageName","com.miui.player");
  checks.put("systemFilterAcceptsItsExactApp",PlaybackSources.select(systemMusicFrame,null,"system:com.miui.player")==systemMusicFrame);
  checks.put("systemFilterCannotSubstituteThirdPartyPlayer",idle(PlaybackSources.select(familyStandard,familySupplied,"system:com.miui.player")));
  StringBuilder out=new StringBuilder("{\"checks\":{");boolean initial=true;for(Map.Entry<String,Boolean> item:checks.entrySet()){if(!initial)out.append(',');initial=false;out.append('"').append(item.getKey()).append("\":").append(item.getValue());}out.append("}}");System.out.println(out);
 }
}
