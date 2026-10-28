package com.local.lyricbridge;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.SystemClock;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/** Bounded, process-local payloads. Approval belongs to the helper's settings. */
public final class IntegrationStore {
    public static final String AUTHORITY = "com.local.lyricbridge.integration";
    public static final int PROTOCOL_VERSION = 1, MAX_LYRICS = 65536;
    private static final long MAX_DURATION = 604800000L, DEFAULT_LEASE = 10000L, MAX_LEASE = 30000L;
    private static final String ENABLED="integration_enabled", APPROVED="integration_approved", SOURCES="integration_sources";
    private static final Map<String,Entry> entries = new LinkedHashMap<>();
    private static volatile Runnable changed = () -> {};
    private static String currentPackage = "";
    private IntegrationStore() {}
    private static SharedPreferences prefs(Context c) { return c.getSharedPreferences("bridge",0); }
    public static boolean isEnabled(Context c) { return prefs(c).getBoolean(ENABLED,false); }
    public static void setOnChanged(Runnable listener) { changed=listener==null?()->{}:listener; }
    private static void signal() { try {changed.run();} catch(RuntimeException ignored) {} }
    public static synchronized void setEnabled(Context c,boolean enabled) {
        prefs(c).edit().putBoolean(ENABLED,enabled).apply();
        if(!enabled){entries.clear();currentPackage="";} signal();
    }
    public static boolean isApproved(Context c,String pkg) {
        return prefs(c).getStringSet(APPROVED,Collections.<String>emptySet()).contains(pkg);
    }
    public static synchronized void setApproved(Context c,String pkg,boolean approved) {
        if(!validPackage(pkg)) throw new IllegalArgumentException("Invalid package");
        Set<String> allowed=new HashSet<>(prefs(c).getStringSet(APPROVED,Collections.<String>emptySet()));
        if(approved){if(allowed.size()>=32&&!allowed.contains(pkg))throw new IllegalArgumentException("Too many sources");allowed.add(pkg);}
        else {allowed.remove(pkg);entries.remove(pkg);if(currentPackage.equals(pkg))currentPackage="";}
        prefs(c).edit().putStringSet(APPROVED,allowed).apply(); noteSource(c,pkg);signal();
    }
    public static synchronized List<String> sourcePackages(Context c) {
        List<String> result=new ArrayList<>(prefs(c).getStringSet(SOURCES,Collections.<String>emptySet()));
        Collections.sort(result);return result;
    }
    public static synchronized void noteSource(Context c,String pkg) {
        if(!validPackage(pkg))return;
        Set<String> sources=new HashSet<>(prefs(c).getStringSet(SOURCES,Collections.<String>emptySet()));
        if(sources.contains(pkg))return;
        if(sources.size()>=32) {
            List<String> ordered=new ArrayList<>(sources);Collections.sort(ordered);
            for(String old:ordered)if(!isApproved(c,old)){sources.remove(old);break;}
        }
        if(sources.size()<32){sources.add(pkg);prefs(c).edit().putStringSet(SOURCES,sources).apply();}
    }
    static String accessState(Context c,String pkg) {
        if(!isEnabled(c))return "disabled";
        return isApproved(c,pkg)?"ready":"pending";
    }
    public static Bundle status(Context c,String pkg) {
        Bundle result=new Bundle();result.putInt("protocolVersion",PROTOCOL_VERSION);
        result.putString("packageName",pkg);result.putString("state",accessState(c,pkg));
        result.putBoolean("enabled",isEnabled(c));result.putBoolean("approved",isApproved(c,pkg));
        result.putBoolean("accepted",false);return result;
    }
    static synchronized Bundle publish(Context c,String pkg,Bundle input) {
        Bundle result=status(c,pkg);
        if(!"ready".equals(result.getString("state")))return result;
        try {
            Entry entry=new Entry(pkg,input,SystemClock.elapsedRealtime());
            entries.put(pkg,entry);result.putString("state","accepted");result.putBoolean("accepted",true);
            result.putLong("leaseMs",entry.deadline-entry.received);signal();
        } catch(RuntimeException invalid) {
            // An invalid update invalidates that sender's old song, avoiding stale lyrics.
            entries.remove(pkg);result.putString("state","invalid");
            result.putString("message",invalid.getMessage()==null?"Invalid snapshot":invalid.getMessage());signal();
        }
        return result;
    }
    static synchronized Bundle clear(Context c,String pkg) {
        entries.remove(pkg);if(currentPackage.equals(pkg))currentPackage="";
        Bundle result=status(c,pkg);result.putBoolean("accepted",true);result.putString("state","cleared");signal();return result;
    }
    public static synchronized JSONObject getApprovedSnapshot(Context c,String selectedPackage) {
        if(!isEnabled(c))return null;
        long now=SystemClock.elapsedRealtime();Entry best=null;
        for(java.util.Iterator<Map.Entry<String,Entry>> it=entries.entrySet().iterator();it.hasNext();) {
            Map.Entry<String,Entry> item=it.next();Entry e=item.getValue();
            if(now<e.received||now>=e.deadline||!isApproved(c,e.pkg)){it.remove();continue;}
            if(!PlayerSelection.matches(selectedPackage,e.pkg))continue;
            if(best==null||e.playing&&!best.playing
                ||e.playing==best.playing&&(e.pkg.equals(currentPackage)&&!best.pkg.equals(currentPackage)
                    ||!best.pkg.equals(currentPackage)&&e.received>best.received))best=e;
        }
        if(best==null){currentPackage="";return null;}
        currentPackage=best.pkg;return best.snapshot(now);
    }
    private static boolean validPackage(String value) {
        return value!=null&&value.length()<=256&&value.matches("[A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)+");
    }
    private static String text(Bundle b,String key,int limit,boolean required) {
        Object value=b.get(key);
        if(value!=null&&!(value instanceof String))throw new IllegalArgumentException(key+" must be text");
        String s=trimText(value==null?"":(String)value);
        if(s.length()>limit||required&&s.isEmpty())throw new IllegalArgumentException("Invalid "+key);
        return s;
    }
    private static String trimText(String value) {
        if(value==null)return "";
        int start=0,end=value.length();
        while(start<end&&space(value.charAt(start)))start++;
        while(end>start&&space(value.charAt(end-1)))end--;
        return value.substring(start,end);
    }
    private static boolean space(char value) {
        return Character.isWhitespace(value)||Character.isSpaceChar(value)||value=='\u0085';
    }

    private static long number(Bundle b,String key,long fallback) {
        Object value=b.get(key);
        if(value==null)return fallback;
        if(!(value instanceof Long)&&!(value instanceof Integer))throw new IllegalArgumentException(key+" must be an integer");
        return ((Number)value).longValue();
    }
    private static boolean flag(Bundle b,String key,boolean fallback) {
        Object value=b.get(key);if(value==null)return fallback;
        if(!(value instanceof Boolean))throw new IllegalArgumentException(key+" must be boolean");
        return (Boolean)value;
    }
    private static final class Entry {
        final String pkg,title,artist,album,mediaId,sessionId,trackId,lyrics,lyricsTrackId;
        final long duration,position,sampled,received,deadline;
        final boolean known,playing;final double speed;
        Entry(String pkg,Bundle b,long now) {
            if(b==null||number(b,"protocolVersion",0)!=PROTOCOL_VERSION)throw new IllegalArgumentException("Unsupported protocolVersion");
            this.pkg=pkg;title=text(b,"title",512,true);artist=text(b,"artist",512,false);album=text(b,"album",512,false);
            mediaId=text(b,"mediaId",256,false);sessionId=text(b,"sessionId",256,true);trackId=text(b,"trackId",256,true);
            duration=number(b,"durationMs",0);position=number(b,"positionMs",-1);known=flag(b,"positionKnown",false);playing=flag(b,"playing",false);
            if(duration<0||duration>MAX_DURATION||position< -1||position>MAX_DURATION||known&&position<0
                ||known&&duration>0&&position>duration+10000)throw new IllegalArgumentException("Invalid playback time");
            Object rawSpeed=b.get("speed");
            if(rawSpeed!=null&&!(rawSpeed instanceof Double)&&!(rawSpeed instanceof Float)&&!(rawSpeed instanceof Integer))throw new IllegalArgumentException("Invalid speed");
            speed=rawSpeed==null?1:((Number)rawSpeed).doubleValue();
            if(Double.isNaN(speed)||Double.isInfinite(speed)||Math.abs(speed)>16)throw new IllegalArgumentException("Invalid speed");
            sampled=number(b,"sampledAtElapsedRealtimeMs",now);
            if(sampled<0||sampled>now||now-sampled>MAX_LEASE)throw new IllegalArgumentException("Stale playback sample");
            long lease=number(b,"leaseMs",DEFAULT_LEASE);
            if(lease<1000||lease>MAX_LEASE)throw new IllegalArgumentException("Invalid leaseMs");
            received=now;deadline=now+lease;
            lyrics=text(b,"providedLyrics",MAX_LYRICS,false);lyricsTrackId=text(b,"providedLyricsTrackId",256,false);
            if(!lyrics.isEmpty()&&!trackId.equals(lyricsTrackId))throw new IllegalArgumentException("Lyrics must belong to trackId");
        }
        JSONObject snapshot(long now) {
            JSONObject result=new JSONObject();
            try {
                long effective=known?Math.max(0,position+(playing?(long)((now-sampled)*speed):0)):-1;
                if(known&&duration>0)effective=Math.min(effective,duration);
                result.put("version",1).put("type","media").put("packageName",pkg).put("title",title).put("artist",artist).put("album",album)
                    .put("mediaId",mediaId).put("sessionId",sessionId).put("trackId",trackId).put("source","integration")
                    .put("capability",known?"sync":"metadata-only").put("durationMs",duration).put("positionMs",effective)
                    .put("positionKnown",known).put("rawPositionMs",position).put("positionUpdatedAtMs",sampled)
                    .put("playing",playing).put("speed",speed).put("providedLyrics",lyrics).put("providedLyricsTrackId",lyricsTrackId)
                    .put("diagnostic",known?"播放器接入已提供歌曲与进度":"已识别歌曲，播放器尚未提供进度");
                return result;
            } catch(Exception invalid){return null;}
        }
    }
}
