package com.local.lyricbridge;
import android.app.Notification;
import android.content.Context;
import android.content.SharedPreferences;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import android.os.SystemClock;
import android.service.notification.StatusBarNotification;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
public final class NotificationSelectionTests {
 private static final LinkedHashMap<String,Boolean> checks=new LinkedHashMap<>();
 private static void check(String name,boolean condition){checks.put(name,condition);System.out.println((condition?"PASS ":"FAIL ")+name);}
 private static void reset(){SystemClock.now=1000000L;NotificationPlaybackStore.clear();SharedPreferences.INSTANCE.player="";MediaSessionManager.sessions=new ArrayList<>();MediaSessionManager.denyAccess=false;}
 private static StatusBarNotification notice(String key,String pkg,String title,long age){Notification n=new Notification();n.category=Notification.CATEGORY_TRANSPORT;n.extras.putCharSequence(Notification.EXTRA_TITLE,title);n.extras.putCharSequence(Notification.EXTRA_TEXT,"New artist");return new StatusBarNotification(key,pkg,n,System.currentTimeMillis()-age);}
 private static MediaController session(String pkg,int state,long updated){return new MediaController(pkg,new MediaMetadata("Session song","session-id"),new PlaybackState(state,updated,1000));}
 private static MediaReader reader(MediaController controller){MediaSessionManager.sessions=Arrays.asList(controller);MediaReader reader=new MediaReader(new Context());reader.start();return reader;}
 private static boolean pkg(JSONObject data,String pkg){return data.optString("packageName").equals(pkg);}
 public static void main(String[] args)throws Exception{
  reset();NotificationPlaybackStore.update(notice("small","small.player","Fresh song",0));MediaReader r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));JSONObject data=r.snapshot();check("fresh_other_app_notification_overrides_old_paused_session",pkg(data,"small.player"));check("selected_notification_never_fakes_clock",data.optString("capability").equals("metadata-only")&&data.optString("positionKnown").equals("false")&&data.optString("positionMs").equals("-1"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Fresh song",0));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PLAYING,999000));check("playing_session_remains_preferred",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Old song",10000));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,999000));check("older_notification_does_not_override_new_pause",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Expired song",130000));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,700000));check("expired_notification_cannot_initiate_takeover",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Old song",0));SystemClock.now+=2000;r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,1001000));r.snapshot();SystemClock.now+=1000;NotificationPlaybackStore.update(notice("small","small.player","Old song",0));check("identical_notification_repost_does_not_become_new_activity",pkg(r.snapshot(),"com.tencent.qqmusic"));NotificationPlaybackStore.update(notice("small","small.player","Changed song",0));check("changed_notification_metadata_can_takeover",pkg(r.snapshot(),"small.player"));r.stop();
  reset();NotificationPlaybackStore.update(notice("same","com.tencent.qqmusic","Different notification song",0));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));check("same_app_notification_does_not_replace_trustworthy_metadata",r.snapshot().optString("title").equals("Session song"));r.stop();
  reset();SharedPreferences.INSTANCE.player="platform:qq";NotificationPlaybackStore.update(notice("small","small.player","Fresh song",0));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));check("manual_platform_selection_does_not_switch_to_unselected_notification",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();SharedPreferences.INSTANCE.player="small.player";NotificationPlaybackStore.update(notice("small","small.player","Fresh song",0));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));check("manual_custom_player_selection_uses_its_notification",pkg(r.snapshot(),"small.player"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Long song",0));MediaController c=session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000);r=reader(c);r.snapshot();SystemClock.now+=130000;check("accepted_long_song_does_not_fall_back_to_old_pause_after_freshness_window",pkg(r.snapshot(),"small.player"));c.state=new PlaybackState(PlaybackState.STATE_PLAYING,SystemClock.now,1000);r.refresh();check("resuming_session_reclaims_priority",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();StatusBarNotification selected=notice("small","small.player","Fresh song",0);NotificationPlaybackStore.update(selected);r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));r.snapshot();NotificationPlaybackStore.remove(selected);check("removed_notification_cannot_keep_selection",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Fresh song",0));c=session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000);r=reader(c);r.snapshot();SystemClock.now+=2000;c.metadata.set(MediaMetadata.METADATA_KEY_TITLE,"New native song");r.refresh();check("new_native_metadata_reclaims_priority_from_old_notification",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Future song",-60000));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,800000));check("future_wall_clock_post_time_cannot_manufacture_fresh_activity",pkg(r.snapshot(),"com.tencent.qqmusic"));r.stop();
  reset();NotificationPlaybackStore.update(notice("small","small.player","Recent song",45000));r=reader(session("com.tencent.qqmusic",PlaybackState.STATE_PAUSED,900000));check("recent_restored_notification_can_outlive_older_pause",pkg(r.snapshot(),"small.player"));r.stop();
  boolean success=!checks.containsValue(false);StringBuilder json=new StringBuilder("{\"success\":"+success+",\"checks\":{");boolean first=true;for(Map.Entry<String,Boolean> entry:checks.entrySet()){if(!first)json.append(',');first=false;json.append('"').append(entry.getKey()).append("\":").append(entry.getValue());}json.append("}}");Files.write(Paths.get(args[0]),json.toString().getBytes(StandardCharsets.UTF_8));if(!success)System.exit(1);
 }
}
