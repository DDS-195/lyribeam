package com.local.lyricbridge;

import android.app.Notification;
import android.media.session.MediaSession;
import android.os.Bundle;
import android.os.SystemClock;
import android.service.notification.StatusBarNotification;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.UUID;

// An optional, lower-confidence source. Only public music notifications are retained.
// Notification timestamps and progress bars are never treated as a playback clock.
public final class NotificationPlaybackStore {
    private static final int MAX_NOTIFICATIONS = 64;
    private static final LinkedHashMap<String, Entry> entries = new LinkedHashMap<>();
    private static long revision;
    private static final long TAKEOVER_FRESHNESS_MS = 120000L;
    private static final class Entry {
        final String packageName, title, artist, sessionId;
        final long order, changedAt;
        Entry(String packageName, String title, String artist, String sessionId, long order, long changedAt) {
            this.packageName=packageName; this.title=title; this.artist=artist; this.sessionId=sessionId; this.order=order; this.changedAt=changedAt;
        }
    }
    private NotificationPlaybackStore() {}
    public static synchronized boolean update(StatusBarNotification posted) {
        if (posted == null) return false;
        String key=posted.getKey(), packageName=posted.getPackageName();
        if (key == null || key.isEmpty() || key.length()>4096) return false;
        try {
            Notification notification=posted.getNotification();
            Bundle extras=notification == null ? null : notification.extras;
            boolean mediaToken=extras != null && extras.getParcelable(Notification.EXTRA_MEDIA_SESSION) instanceof MediaSession.Token;
            boolean music=notification != null && (Notification.CATEGORY_TRANSPORT.equals(notification.category) || mediaToken);
            if (!music || packageName == null || packageName.isEmpty() || packageName.length()>256) {
                return entries.remove(key) != null;
            }
            String title=extras == null ? "" : text(extras.getCharSequence(Notification.EXTRA_TITLE));
            if (title.isEmpty()) { return entries.remove(key) != null; }
            String subtitle=text(extras.getCharSequence(Notification.EXTRA_SUB_TEXT));
            String artist=text(extras.getCharSequence(Notification.EXTRA_TEXT));
            // Media notifications conventionally use TEXT for the artist. Reject
            // missing/multiline/duplicate text; SUB_TEXT can be a playlist or app label.
            if (artist.equalsIgnoreCase(title) || !subtitle.isEmpty() && artist.equalsIgnoreCase(subtitle)) artist="";
            Entry previous=entries.remove(key);
            String sessionId=previous != null && previous.packageName.equals(packageName)
                ? previous.sessionId : "notification:"+UUID.randomUUID().toString();
            boolean same = previous != null && previous.packageName.equals(packageName)
                && previous.title.equals(title) && previous.artist.equals(artist);
            // Progress-only notification updates must not make an old paused app
            // look newer than another player's real track/state change.
            long changedAt = same ? previous.changedAt
                : previous != null && previous.packageName.equals(packageName) ? SystemClock.elapsedRealtime() : postedElapsed(posted);
            long order = same ? previous.order : ++revision;
            entries.put(key,new Entry(packageName,title,artist,sessionId,order,changedAt));
            while(entries.size()>MAX_NOTIFICATIONS) entries.remove(entries.keySet().iterator().next());
            return true;
        } catch (RuntimeException malformed) { return entries.remove(key) != null; }
    }
    public static synchronized void replace(StatusBarNotification[] visible) {
        entries.clear();
        if (visible != null) for(StatusBarNotification posted:visible) update(posted);
    }
    public static synchronized boolean remove(StatusBarNotification removed) {
        return removed != null && entries.remove(removed.getKey()) != null;
    }
    public static synchronized void clear() { entries.clear(); }
    public static synchronized List<String> packages() {
        List<String> result=new ArrayList<>();
        for(Entry entry:entries.values()) if(!result.contains(entry.packageName)) result.add(entry.packageName);
        return result;
    }
    public static synchronized JSONObject snapshot(String selectedPackage,long sequence) {
        return snapshotMatching(selectedPackage,sequence,false);
    }
    public static synchronized JSONObject snapshotExact(String packageName,long sequence) {
        return snapshotMatching(packageName,sequence,true);
    }
    // Source arbitration only: these times never become a playback position.
    public static synchronized JSONObject snapshotNewerThan(String selectedPackage, String excludedPackage,
            long activityAt, long sequence, String retainedSession) {
        return snapshotMatching(selectedPackage, sequence, false, excludedPackage, activityAt, retainedSession);
    }
    private static long postedElapsed(StatusBarNotification posted) {
        long now = SystemClock.elapsedRealtime(), wallNow = System.currentTimeMillis(), time = posted.getPostTime();
        if (time <= 0 || time > wallNow) return 0;
        long age = wallNow - time;
        return age <= now ? now - age : 0;
    }
    private static JSONObject snapshotMatching(String selectedPackage,long sequence,boolean exactPackage) {
        return snapshotMatching(selectedPackage,sequence,exactPackage,null,0,"");
    }
    private static JSONObject snapshotMatching(String selectedPackage,long sequence,boolean exactPackage,
            String excludedPackage,long activityAt,String retainedSession) {
        Entry latest=null;
        for(Entry entry:entries.values()) {
            if(exactPackage ? !entry.packageName.equals(selectedPackage) : !PlayerSelection.matches(selectedPackage,entry.packageName)) continue;
            if (excludedPackage != null) {
                long age = SystemClock.elapsedRealtime() - entry.changedAt;
                if (entry.packageName.equals(excludedPackage) || entry.changedAt <= activityAt || age < 0
                    || age > TAKEOVER_FRESHNESS_MS && !entry.sessionId.equals(retainedSession)) continue;
            }
            if(latest == null || entry.order>latest.order) latest=entry;
        }
        if(latest == null) return null;
        JSONObject data=new JSONObject();
        try {
            data.put("version",1);data.put("sequence",sequence);data.put("type","media");
            data.put("source","notification");data.put("capability","metadata-only");
            data.put("sessionId",latest.sessionId);
            data.put("trackId",trackIdentifier(latest.sessionId,"",latest.title,latest.artist,"",0));
            data.put("packageName",latest.packageName);data.put("title",latest.title);data.put("artist",latest.artist);
            data.put("album","");data.put("mediaId","");data.put("durationMs",0);
            data.put("positionKnown",false);data.put("positionMs",-1);data.put("rawPositionMs",-1);
            data.put("positionUpdatedAtMs",0);data.put("playing",false);data.put("speed",1);
            data.put("diagnostic","已从音乐通知识别歌曲；播放器未提供可验证的播放进度");
            return data;
        } catch(Exception unavailable) { return null; }
    }
    public static String trackIdentifier(String sessionId,String mediaId,String title,String artist,String album,long duration) {
        try {
            MessageDigest digest=MessageDigest.getInstance("SHA-256");
            String[] fields=mediaId != null && !mediaId.isEmpty()
                ? new String[]{sessionId,"id",mediaId}
                : new String[]{sessionId,"metadata",title,artist,album,Long.toString(duration)};
            for(String field:fields) {
                String value=field == null ? "" : field;
                digest.update((value.length()+":"+value).getBytes(StandardCharsets.UTF_8));
            }
            StringBuilder result=new StringBuilder("track:");
            for(byte value:digest.digest()) result.append(Character.forDigit((value>>>4)&15,16)).append(Character.forDigit(value&15,16));
            return result.toString();
        } catch(java.security.NoSuchAlgorithmException impossible) { throw new IllegalStateException(impossible); }
    }
    private static String text(CharSequence raw) {
        if(raw == null) return "";
        String value=raw.toString();
        if(value.indexOf('\n')>=0 || value.indexOf('\r')>=0) return "";
        int start=0,end=value.length();
        while(start<end && space(value.charAt(start)))start++;
        while(end>start && space(value.charAt(end-1)))end--;
        end=Math.min(end,start+512);
        if(end<value.length() && end>start && Character.isHighSurrogate(value.charAt(end-1)) && Character.isLowSurrogate(value.charAt(end)))end--;
        return value.substring(start,end);
    }
    private static boolean space(char value) { return Character.isWhitespace(value)||Character.isSpaceChar(value)||value=='\u0085'; }
}
