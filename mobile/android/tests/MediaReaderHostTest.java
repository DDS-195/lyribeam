package com.local.lyricbridge;
import android.content.Context;
import android.content.SharedPreferences;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import java.lang.reflect.Method;
import java.util.*;
import org.json.JSONObject;
public final class MediaReaderHostTest {
 private static MediaMetadata meta(String title,String mediaId) { return new MediaMetadata(title,mediaId); }
 private static JSONObject snapshot(String title)throws Exception { return snapshot(title,"32112291"); }
 private static JSONObject snapshot(String title,String mediaId)throws Exception { return snapshot(meta(title,mediaId)); }
 private static JSONObject snapshot(MediaMetadata metadata)throws Exception {
  SharedPreferences.INSTANCE.player="";
  MediaSessionManager.sessions=Arrays.asList(new MediaController(metadata));
  MediaReader reader=new MediaReader(new Context());reader.start();try{return reader.snapshot();}finally{reader.stop();}
 }
 private static MediaController controller(String pkg,MediaMetadata metadata,boolean playing) {
  return new MediaController(pkg,metadata,new PlaybackState(playing?PlaybackState.STATE_PLAYING:PlaybackState.STATE_PAUSED));
 }
 public static void main(String[] args)throws Exception {
  Map<String,Boolean> checks=new LinkedHashMap<>();
  JSONObject empty=snapshot((String)null);checks.put("nullTitleIsIdle","idle".equals(empty.optString("type"))&&!empty.optString("diagnostic").isEmpty());
  empty=snapshot(" \t\r\n");checks.put("blankTitleIsIdle","idle".equals(empty.optString("type"))&&!empty.optString("diagnostic").isEmpty());
  empty=snapshot("\u00a0\u3000\u0085");checks.put("unicodeBlankTitleIsIdle","idle".equals(empty.optString("type"))&&!empty.optString("diagnostic").isEmpty());
  JSONObject normal=snapshot("山水之间");checks.put("validTitleRemainsMedia","media".equals(normal.optString("type"))&&"山水之间".equals(normal.optString("title")));
  checks.put("publicMediaIdentifierForwarded","32112291".equals(normal.optString("mediaId")));
  JSONObject noId=snapshot("山水之间",null);
  checks.put("nullMediaIdentifierFallsBackWithoutDroppingSong","media".equals(noId.optString("type"))&&"山水之间".equals(noId.optString("title"))&&noId.optString("mediaId").isEmpty());
  JSONObject blankId=snapshot("山水之间"," \t\u00a0\u3000\u0085");
  checks.put("blankMediaIdentifierFallsBackWithoutDroppingSong","media".equals(blankId.optString("type"))&&blankId.optString("mediaId").isEmpty());
  String boundaryId=new String(new char[256]).replace('\0','a');
  JSONObject boundary=snapshot("山水之间",boundaryId);
  checks.put("receiverBoundaryIdentifierPreservedIntact","media".equals(boundary.optString("type"))&&boundaryId.equals(boundary.optString("mediaId")));
  JSONObject oversized=snapshot("山水之间",boundaryId+"b");
  checks.put("oversizedIdentifierDiscardedInsteadOfTruncated","media".equals(oversized.optString("type"))&&oversized.optString("mediaId").isEmpty()&&"山水之间".equals(oversized.optString("title")));
  String opaqueId="local-player:album/item:42";
  JSONObject opaque=snapshot("山水之间",opaqueId);
  checks.put("boundedOpaqueIdentifierPreserved",opaqueId.equals(opaque.optString("mediaId")));
  Method limit=MediaReader.class.getDeclaredMethod("limit",String.class);limit.setAccessible(true);
  String longTitle=new String(new char[511]).replace('\0','a')+"\ud83d\ude03";
  String limited=(String)limit.invoke(null,longTitle);checks.put("limitDoesNotSplitSurrogatePair",limited.length()<=512&&!Character.isHighSurrogate(limited.charAt(limited.length()-1)));
  String fullPair=(String)limit.invoke(null,new String(new char[510]).replace('\0','a')+"\ud83d\ude03");checks.put("completeSurrogatePairIsPreserved",fullPair.length()==512&&fullPair.endsWith("\ud83d\ude03"));

  JSONObject display=snapshot(meta(" \u3000",null).set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲"));
  checks.put("blankTitleFallsBackToDisplayTitle","media".equals(display.optString("type"))&&"夜曲".equals(display.optString("title")));
  JSONObject primary=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"播放器品牌"));
  checks.put("primaryTitleWinsOverDecorativeDisplayTitle","夜曲".equals(primary.optString("title")));
  JSONObject albumArtist=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST," \u00a0").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"周杰伦"));
  checks.put("blankArtistFallsBackToAlbumArtist","周杰伦".equals(albumArtist.optString("artist")));
  MediaMetadata displayMetadata=meta(null,null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"周杰伦").set(MediaMetadata.METADATA_KEY_ALBUM,"十一月的萧邦");
  JSONObject displayArtist=snapshot(displayMetadata);
  checks.put("displayMetadataProvidesTitleAndArtist","夜曲".equals(displayArtist.optString("title"))&&"周杰伦".equals(displayArtist.optString("artist")));
  checks.put("albumForwardedSeparately","十一月的萧邦".equals(displayArtist.optString("album")));
  JSONObject albumAsSubtitle=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"十一月的萧邦").set(MediaMetadata.METADATA_KEY_ALBUM,"十一月的萧邦"));
  checks.put("knownAlbumIsNotUsedAsArtist",albumAsSubtitle.optString("artist").isEmpty());
  JSONObject wrongDisplay=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"另一首歌").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"其他歌手").set(MediaMetadata.METADATA_KEY_ALBUM,"专辑"));
  checks.put("unrelatedDisplaySubtitleIsNotArtist",wrongDisplay.optString("artist").isEmpty());
  JSONObject unknownAlbumSubtitle=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"无法判断是专辑还是歌手"));
  checks.put("unclassifiedSubtitleIsNotInventedAsArtist",unknownAlbumSubtitle.optString("artist").isEmpty());
  JSONObject titleAsSubtitle=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"夜曲").set(MediaMetadata.METADATA_KEY_ALBUM,"十一月的萧邦"));
  checks.put("songTitleIsNotUsedAsArtist",titleAsSubtitle.optString("artist").isEmpty());
  JSONObject primaryArtist=snapshot(meta("夜曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"周杰伦").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"群星")
   .set(MediaMetadata.METADATA_KEY_DISPLAY_TITLE,"夜曲").set(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE,"其他歌手"));
  checks.put("primaryArtistWinsOverFallbacks","周杰伦".equals(primaryArtist.optString("artist")));

  SharedPreferences.INSTANCE.player="";
  MediaController other=controller("com.other.player",meta("另一首", "other-id").set(MediaMetadata.METADATA_KEY_ARTIST,"其他歌手"),true);
  MediaController kugou=controller("com.kugou.android.lite",meta("酷狗歌曲","kugou-id"),true);
  MediaSessionManager.sessions=Arrays.asList(other,kugou);
  MediaReader reader=new MediaReader(new Context()); reader.start();
  checks.put("automaticSelectionDoesNotFavorKugou","com.other.player".equals(reader.snapshot().optString("packageName")));
  MediaSessionManager.sessions=Arrays.asList(kugou,other);reader.refresh();
  checks.put("equalPriorityKeepsCurrentValidSession","com.other.player".equals(reader.snapshot().optString("packageName")));
  other.state=new PlaybackState(PlaybackState.STATE_PAUSED);reader.refresh();
  checks.put("activePlayingSessionReplacesPausedCurrent","com.kugou.android.lite".equals(reader.snapshot().optString("packageName")));
  other.metadata=meta("切换歌曲","new-id").set(MediaMetadata.METADATA_KEY_ARTIST,"新歌手").set(MediaMetadata.METADATA_KEY_ALBUM,"新专辑");
  other.state=new PlaybackState(PlaybackState.STATE_PLAYING);kugou.metadata=meta(" ",null);reader.refresh();
  JSONObject switched=reader.snapshot();
  checks.put("playingSessionWithTitleBeatsIncompleteSession","com.other.player".equals(switched.optString("packageName")));
  checks.put("sessionSwitchKeepsAllFieldsFromSamePlayer","切换歌曲".equals(switched.optString("title"))&&"新歌手".equals(switched.optString("artist"))&&"new-id".equals(switched.optString("mediaId"))&&"新专辑".equals(switched.optString("album")));
  other.metadata=meta("后续歌曲",null).set(MediaMetadata.METADATA_KEY_ARTIST,"").set(MediaMetadata.METADATA_KEY_ALBUM_ARTIST,"");reader.refresh();
  JSONObject next=reader.snapshot();
  checks.put("nextSongDoesNotReusePreviousArtistAlbumOrId","后续歌曲".equals(next.optString("title"))&&next.optString("artist").isEmpty()&&next.optString("album").isEmpty()&&next.optString("mediaId").isEmpty());
  java.lang.reflect.Field callbackField=MediaReader.class.getDeclaredField("playbackChanged");callbackField.setAccessible(true);
  MediaController.Callback callback=(MediaController.Callback)callbackField.get(reader);
  other.state=new PlaybackState(PlaybackState.STATE_PAUSED);kugou.metadata=meta("恢复播放","kg-resume");
  callback.onPlaybackStateChanged(other.state);
  checks.put("playbackCallbackReevaluatesActivePlayerImmediately","com.kugou.android.lite".equals(reader.snapshot().optString("packageName")));
  other.state=new PlaybackState(PlaybackState.STATE_PLAYING);kugou.metadata=meta(" ",null);
  callback.onMetadataChanged(kugou.metadata);
  checks.put("metadataCallbackReevaluatesIncompleteCurrentSession","com.other.player".equals(reader.snapshot().optString("packageName")));
  boolean revokedHandled=false;MediaSessionManager.denyAccess=true;
  try {callback.onMetadataChanged(null);revokedHandled="idle".equals(reader.snapshot().optString("type"));}finally{MediaSessionManager.denyAccess=false;}
  checks.put("permissionRevocationDoesNotCrashMediaCallbackOrRetainSong",revokedHandled);
  reader.stop();

  SharedPreferences.INSTANCE.player="com.kugou.android.lite";
  MediaSessionManager.sessions=Arrays.asList(other,kugou);
  reader=new MediaReader(new Context());reader.start();
  checks.put("manualPlayerSelectionIsRespected","com.kugou.android.lite".equals(reader.snapshot().optString("packageName")));
  MediaSessionManager.sessions=Arrays.asList(other);reader.refresh();
  checks.put("missingManualPlayerDoesNotLeakAnotherSession","idle".equals(reader.snapshot().optString("type")));
  reader.stop();SharedPreferences.INSTANCE.player="";
  MediaSessionManager.sessions=new ArrayList<>();reader=new MediaReader(new Context());reader.start();
  checks.put("idleDiagnosticWorksForEveryPlayer",!reader.snapshot().optString("diagnostic").contains("酷狗"));
  reader.stop();
  MediaController unreliable=controller("com.other.player",meta("进度缺失",null),true);unreliable.state=null;
  MediaSessionManager.sessions=Arrays.asList(unreliable);reader=new MediaReader(new Context());reader.start();
  JSONObject noPosition=reader.snapshot();
  checks.put("nullPlaybackStateNeverPretendsToKnowPosition","false".equals(noPosition.optString("positionKnown"))&&"-1".equals(noPosition.optString("positionMs"))&&"media".equals(noPosition.optString("type")));reader.stop();
  MediaController baseFamily=controller("com.kugou.android",meta("普通版歌曲","base-id"),false);
  MediaController conceptFamily=controller("com.kugou.android.lite",meta("概念版歌曲","concept-id"),true);
  MediaController qqFamily=controller("com.tencent.qqmusic",meta("QQ歌曲","qq-id"),true);
  SharedPreferences.INSTANCE.player="platform:kugou";MediaSessionManager.sessions=Arrays.asList(qqFamily,baseFamily,conceptFamily);
  reader=new MediaReader(new Context());reader.start();JSONObject familyFrame=reader.snapshot();String familySession=familyFrame.optString("sessionId");
  checks.put("platformSelectionCanUsePlayingConceptEdition",familyFrame.optString("packageName").equals("com.kugou.android.lite")&&familyFrame.optString("mediaId").equals("concept-id"));
  conceptFamily.state=new PlaybackState(PlaybackState.STATE_PAUSED);baseFamily.state=new PlaybackState(PlaybackState.STATE_PLAYING);reader.refresh();familyFrame=reader.snapshot();
  checks.put("platformSessionSwitchRetainsRealPackageAndNewSession",familyFrame.optString("packageName").equals("com.kugou.android")&&familyFrame.optString("mediaId").equals("base-id")&&!familySession.equals(familyFrame.optString("sessionId")));reader.stop();
  SharedPreferences.INSTANCE.player="com.kugou.android.lite";MediaSessionManager.sessions=Arrays.asList(qqFamily,baseFamily);reader=new MediaReader(new Context());reader.start();
  checks.put("legacyConceptSelectionUsesItsPlatformWithoutLeakingQQ",reader.snapshot().optString("packageName").equals("com.kugou.android"));reader.stop();
  MediaController systemMusic=controller("com.miui.player",meta("系统音乐歌曲","system-id"),true);
  SharedPreferences.INSTANCE.player="system:com.miui.player";MediaSessionManager.sessions=Arrays.asList(qqFamily,baseFamily,systemMusic);reader=new MediaReader(new Context());reader.start();
  checks.put("systemMusicSelectionUsesOnlyResolvedSystemPackage",reader.snapshot().optString("packageName").equals("com.miui.player"));
  MediaSessionManager.sessions=Arrays.asList(qqFamily,baseFamily);reader.refresh();checks.put("absentSystemMusicCannotLeakAnotherPlatform",reader.snapshot().optString("type").equals("idle"));reader.stop();
  SharedPreferences.INSTANCE.player="com.other.player";MediaSessionManager.sessions=Arrays.asList(baseFamily,other);reader=new MediaReader(new Context());reader.start();
  checks.put("customPlayerExactSelectionStillWorks",reader.snapshot().optString("packageName").equals("com.other.player"));reader.stop();SharedPreferences.INSTANCE.player="";
  StringBuilder out=new StringBuilder("{\"checks\":{");boolean first=true;for(Map.Entry<String,Boolean> item:checks.entrySet()){if(!first)out.append(',');first=false;out.append('"').append(item.getKey()).append("\":").append(item.getValue());}out.append("}}");System.out.println(out);
 }
}
