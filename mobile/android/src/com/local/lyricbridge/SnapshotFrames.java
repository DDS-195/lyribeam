package com.local.lyricbridge;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Iterator;
import org.json.JSONObject;

/** Full lyrics on connection/change, then bounded periodic repair; metadata stays frequent. */
public final class SnapshotFrames {
    private String lastKey="", lastHash="";
    private long lastFull;
    public String encode(JSONObject snapshot,long now) throws Exception {
        JSONObject copy=new JSONObject();
        for(Iterator<String> keys=snapshot.keys();keys.hasNext();) {String key=keys.next();copy.put(key,snapshot.opt(key));}
        String content=snapshot.optString("providedLyrics"), track=snapshot.optString("trackId");
        String key=snapshot.optString("packageName")+"\n"+snapshot.optString("sessionId")+"\n"+track;
        if(!content.isEmpty() && content.length()<=65536 && !track.isEmpty()
            && track.equals(snapshot.optString("providedLyricsTrackId"))) {
            String hash=java.util.Base64.getEncoder().encodeToString(MessageDigest.getInstance("SHA-256").digest(content.getBytes(StandardCharsets.UTF_8)));
            if(key.equals(lastKey) && hash.equals(lastHash) && now-lastFull>=0 && now-lastFull<15000) copy.remove("providedLyrics");
            else {lastKey=key;lastHash=hash;lastFull=now;}
        } else {lastKey="";lastHash="";lastFull=0;}
        return copy.toString();
    }
}
