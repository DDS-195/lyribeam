package com.local.lyricbridge;
import android.app.Notification;
import android.content.Context;
import android.content.SharedPreferences;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSession;
import android.media.session.MediaSessionManager;
import android.service.notification.StatusBarNotification;
import android.service.notification.NotificationListenerService;
import java.util.*;
import org.json.JSONObject;
public final class NotificationPlaybackHostTest {
 private static final MediaListener listener=new MediaListener();
 private static void reset() {
  listener.onListenerDisconnected();NotificationListenerService.visible=new StatusBarNotification[0];
  SharedPreferences.INSTANCE.player="";MediaSessionManager.sessions=new ArrayList<>();
 }
 private static Notification notice(String title,String artist,String subtext,boolean music) {
  Notification n=new Notification();n.category=music?Notification.CATEGORY_TRANSPORT:"msg";
  n.extras.putCharSequence(Notification.EXTRA_TITLE,title);n.extras.putCharSequence(Notification.EXTRA_TEXT,artist);n.extras.putCharSequence(Notification.EXTRA_SUB_TEXT,subtext);
  return n;
 }
 private static StatusBarNotification post(String key,String pkg,Notification n) {
  StatusBarNotification sbn=new StatusBarNotification(key,pkg,n);listener.onNotificationPosted(sbn);return sbn;
 }
 private static JSONObject read() {
  MediaReader reader=new MediaReader(new Context());reader.start();try{return reader.snapshot();}finally{reader.stop();}
 }
 private static boolean value(JSONObject data,String key,String expected){return expected.equals(data.optString(key));}
 public static void main(String[] args)throws Exception {
  Map<String,Boolean> checks=new LinkedHashMap<>();reset();
  MediaSessionManager.sessions=Arrays.asList(new MediaController(new MediaMetadata("标准歌曲","opaque:1")));
  MediaReader reader=new MediaReader(new Context());reader.start();
  JSONObject first=reader.snapshot(),second=reader.snapshot();String id=first.optString("sessionId");
  checks.put("standardSessionReportsSourceAndSyncCapability",value(first,"source","media-session")&&value(first,"capability","sync"));
  checks.put("sessionIdentifierIsBoundedAndStable",!id.isEmpty()&&id.length()<=256&&id.equals(second.optString("sessionId")));
  MediaSessionManager.sessions=Arrays.asList(new MediaController(new MediaMetadata("标准歌曲","opaque:1")));reader.refresh();
  checks.put("newSessionGetsANewIdentifier",!id.equals(reader.snapshot().optString("sessionId")));
  MediaController active=MediaSessionManager.sessions.get(0);
  String track=reader.snapshot().optString("trackId");
  checks.put("standardTrackIdentifierIsBoundedAndStable",!track.isEmpty()&&track.length()<=256&&track.equals(reader.snapshot().optString("trackId")));
  active.metadata=new MediaMetadata("下一首","opaque:2");
  checks.put("standardTrackIdentifierChangesWithSongIdentifier",!track.equals(reader.snapshot().optString("trackId")));
  active.metadata=new MediaMetadata("无编号曲甲",null);track=reader.snapshot().optString("trackId");
  active.metadata=new MediaMetadata("无编号曲乙",null);
  checks.put("standardTrackIdentifierChangesWithUnidentifiedSongMetadata",!track.equals(reader.snapshot().optString("trackId")));
  active.state=null;
  checks.put("sessionWithoutClockReportsMetadataOnly",value(reader.snapshot(),"capability","metadata-only")&&value(reader.snapshot(),"positionKnown","false"));
  reader.stop();
  reset();int beforeChat=BridgeService.sourceChanges;post("chat","chat.app",notice("朋友","来听歌吧","",false));
  checks.put("ordinaryChatNotificationIsIgnored",value(read(),"type","idle"));
  checks.put("chatNotificationDoesNotWakePlaybackPipeline",BridgeService.sourceChanges==beforeChat);
  int beforeMusic=BridgeService.sourceChanges;
  StatusBarNotification music=post("song","small.player",notice("通知歌曲","歌手甲","播放列表",true));
  JSONObject data=read();
  checks.put("musicNotificationImmediatelyWakesPlaybackPipeline",BridgeService.sourceChanges>beforeMusic);
  checks.put("musicNotificationProvidesConservativeMetadata",value(data,"type","media")&&value(data,"title","通知歌曲")&&value(data,"artist","歌手甲")&&value(data,"album","")&&value(data,"packageName","small.player"));
  checks.put("notificationReportsMetadataOnlyCapability",value(data,"source","notification")&&value(data,"capability","metadata-only"));
  checks.put("notificationNeverInventsPlaybackClockOrState",value(data,"positionKnown","false")&&value(data,"positionMs","-1")&&value(data,"rawPositionMs","-1")&&value(data,"positionUpdatedAtMs","0")&&value(data,"durationMs","0")&&value(data,"playing","false"));
  checks.put("notificationSessionIdentifierIsBounded",!data.optString("sessionId").isEmpty()&&data.optString("sessionId").length()<=256);
  checks.put("notificationUsesCompatibleVersionOne",value(data,"version","1"));
  String notificationId=data.optString("sessionId"),notificationTrack=data.optString("trackId");
  checks.put("notificationTrackIdentifierIsBoundedAndStable",!notificationTrack.isEmpty()&&notificationTrack.length()<=256&&notificationTrack.equals(read().optString("trackId")));post("song","small.player",notice("更新歌曲","歌手乙","播放列表",true));
  data=read();checks.put("notificationUpdateKeepsSessionButReplacesAllTrackFields",value(data,"title","更新歌曲")&&value(data,"artist","歌手乙")&&notificationId.equals(data.optString("sessionId"))&&value(data,"mediaId",""));
  checks.put("notificationTrackIdentifierChangesWhenSongChanges",!notificationTrack.equals(data.optString("trackId")));
  listener.onNotificationRemoved(music);checks.put("removedNotificationCannotLeaveStaleMetadata",value(read(),"type","idle"));
  Notification tokenNotice=notice("Token歌曲","歌手","",false);tokenNotice.extras.putParcelable(Notification.EXTRA_MEDIA_SESSION,new MediaSession.Token());
  post("token","token.player",tokenNotice);checks.put("realMediaSessionTokenQualifiesMusicNotification",value(read(),"title","Token歌曲"));
  reset();Notification fake=notice("不是音乐","人名","",false);fake.extras.putParcelable(Notification.EXTRA_MEDIA_SESSION,"not-a-token");
  post("fake","fake.app",fake);checks.put("arbitraryExtraDoesNotQualifyAsMediaSessionToken",value(read(),"type","idle"));
  post("blank","blank.player",notice("\u00a0\u3000\u0085","歌手","",true));checks.put("blankNotificationTitleIsIgnored",value(read(),"type","idle"));
  reset();post("same","small.player",notice("歌曲","专辑名","专辑名",true));
  checks.put("subtitleAlbumDuplicateIsNotUsedAsSinger",value(read(),"artist",""));
  post("same","small.player",notice("聊天","消息","",false));checks.put("notificationChangingToNonMusicRemovesPreviousTrack",value(read(),"type","idle"));
  reset();post("other","other.player",notice("另一播放器","其他歌手","",true));
  SharedPreferences.INSTANCE.player="wanted.player";checks.put("manualPlayerFilterAlsoRestrictsNotificationFallback",value(read(),"type","idle"));
  post("wanted","wanted.player",notice("指定播放器","歌手","",true));
  MediaSessionManager.sessions=Arrays.asList(new MediaController(new MediaMetadata("别的会话","1")));
  checks.put("manualPlayerWithoutSessionCanUseItsOwnMusicNotification",value(read(),"packageName","wanted.player")&&value(read(),"source","notification"));
  SharedPreferences.INSTANCE.player="";
  checks.put("availableMediaSessionAlwaysPrecedesNotificationFallback",value(read(),"title","别的会话")&&value(read(),"source","media-session"));
  MediaSessionManager.sessions=new ArrayList<>();listener.onListenerDisconnected();
  checks.put("listenerDisconnectClearsAllNotificationMetadata",value(read(),"type","idle"));
  NotificationListenerService.visible=new StatusBarNotification[]{new StatusBarNotification("initial","initial.player",notice("启动已有歌曲","歌手","",true)),new StatusBarNotification("initial-chat","chat.app",notice("聊天","消息","",false))};
  listener.onListenerConnected();checks.put("listenerConnectionSeedsExistingVisibleMusicNotifications",value(read(),"title","启动已有歌曲"));
  listener.onListenerDisconnected();data=read();checks.put("idleReportsNoCapability",value(data,"type","idle")&&value(data,"capability","none"));
  checks.put("notificationChangesWakePlaybackPipeline",BridgeService.sourceChanges>0);
  reset();
  reset();post("own","partial.player",notice("通知中的歌曲","歌手","",true));
  MediaSessionManager.sessions=Arrays.asList(new MediaController("partial.player",null,new android.media.session.PlaybackState()));
  checks.put("nullMetadataFallsBackToSamePlayerNotification",value(read(),"source","notification")&&value(read(),"title","通知中的歌曲"));
  MediaSessionManager.sessions=Arrays.asList(new MediaController("partial.player",new MediaMetadata(" ","id"),new android.media.session.PlaybackState()));
  checks.put("blankSessionTitleFallsBackToSamePlayerNotification",value(read(),"source","notification")&&value(read(),"title","通知中的歌曲"));
  post("other","another.player",notice("另一个播放器的歌曲","其他歌手","",true));
  checks.put("partialSessionDoesNotTakeLatestOtherPlayerNotification",value(read(),"packageName","partial.player"));
  NotificationPlaybackStore.remove(new StatusBarNotification("own","partial.player",notice("","","",true)));
  checks.put("partialSessionWithoutOwnNotificationDoesNotLeakOtherPlayer",value(read(),"type","idle"));
  SharedPreferences.INSTANCE.player="partial.player";
  checks.put("manualPartialSessionKeepsNotificationPackageBoundary",value(read(),"type","idle"));
  reset();
  reset();SharedPreferences.INSTANCE.player="platform:kugou";
  post("family-base","com.kugou.android",notice("普通版通知","歌手","",true));
  post("family-concept","com.kugou.android.lite",notice("概念版通知","歌手","",true));
  checks.put("platformNotificationFilterAcceptsConceptEdition",value(read(),"packageName","com.kugou.android.lite")&&value(read(),"source","notification"));
  post("family-qq","com.tencent.qqmusic",notice("较新的QQ通知","歌手","",true));
  checks.put("platformNotificationFilterRejectsNewerOtherPlatform",value(read(),"packageName","com.kugou.android.lite"));
  SharedPreferences.INSTANCE.player="com.kugou.android";
  checks.put("legacyBaseFilterCanReadConceptNotification",value(read(),"packageName","com.kugou.android.lite"));
  NotificationPlaybackStore.remove(new StatusBarNotification("family-base","com.kugou.android",notice("","","",true)));
  MediaSessionManager.sessions=Arrays.asList(new MediaController("com.kugou.android",null,new android.media.session.PlaybackState()));
  checks.put("partialSessionCannotUseDifferentEditionNotification",value(read(),"type","idle"));
  post("family-base","com.kugou.android",notice("普通版自身通知","歌手","",true));
  post("family-concept","com.kugou.android.lite",notice("最新概念版通知","歌手","",true));
  checks.put("partialSessionKeepsExactEditionNotificationBoundary",value(read(),"packageName","com.kugou.android")&&value(read(),"title","普通版自身通知"));
  reset();SharedPreferences.INSTANCE.player="system:com.miui.player";
  post("system","com.miui.player",notice("系统音乐通知","歌手","",true));
  post("other","com.kugou.android",notice("其他平台通知","歌手","",true));
  checks.put("systemMusicNotificationFilterKeepsExactPackage",value(read(),"packageName","com.miui.player"));
  reset();
  StringBuilder out=new StringBuilder("{\"checks\":{");boolean initial=true;for(Map.Entry<String,Boolean> item:checks.entrySet()){if(!initial)out.append(',');initial=false;out.append('"').append(item.getKey()).append("\":").append(item.getValue());}out.append("}}");System.out.println(out);
 }
}
