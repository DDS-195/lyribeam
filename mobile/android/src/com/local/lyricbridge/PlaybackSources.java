package com.local.lyricbridge;
import org.json.JSONObject;

/** Chooses capability-complete snapshots; never combines fields from different players. */
public final class PlaybackSources {
    private PlaybackSources() {}
    private static JSONObject idle() { try { return new JSONObject().put("version",1).put("type","idle").put("capability","none").put("source","").put("diagnostic","等待播放器提供歌曲信息"); } catch(Exception error) { throw new IllegalStateException(error); } }
    private static boolean blank(String value) { if(value==null || value.isEmpty())return true; for(int i=0;i<value.length();i++){char c=value.charAt(i);if(!Character.isWhitespace(c) && !Character.isSpaceChar(c) && c != '\u0085')return false;}return true; }
    private static boolean media(JSONObject data) { return data != null && "media".equals(data.optString("type")) && !blank(data.optString("title")); }
    private static boolean playing(JSONObject data) { return media(data) && data.optBoolean("playing"); }
    private static boolean matches(JSONObject data,String selected) { return PlayerSelection.matches(selected,data.optString("packageName")); }
    private static boolean sameRecording(JSONObject a,JSONObject b) {
        String albumA=a.optString("album").trim(), albumB=b.optString("album").trim();
        String idA=a.optString("mediaId"), idB=b.optString("mediaId");
        if(!albumA.isEmpty() && !albumB.isEmpty() && !albumA.equalsIgnoreCase(albumB)) return false;
        if(!idA.isEmpty() && !idB.isEmpty() && !idA.equals(idB)) return false;
        long x=a.optLong("durationMs"), y=b.optLong("durationMs");
        return a.optString("title").trim().equalsIgnoreCase(b.optString("title").trim())
            && a.optString("artist").trim().equalsIgnoreCase(b.optString("artist").trim())
            && (x<=0 || y<=0 || Math.abs(x-y)<=3000);
    }
    public static JSONObject select(JSONObject standard,JSONObject supplied,String selected) {
        if(standard != null && "media".equals(standard.optString("type")) && (!media(standard) || !matches(standard,selected))) standard=null;
        if(standard != null && !"media".equals(standard.optString("type")) && !"idle".equals(standard.optString("type"))) standard=null;
        if(!media(supplied) || !matches(supplied,selected)) return standard == null ? idle() : standard;
        if(!media(standard) || !matches(standard,selected)) return supplied;
        boolean sameApp=standard.optString("packageName").equals(supplied.optString("packageName"));
        if(sameApp && playing(standard) && !sameRecording(standard,supplied)) return standard;
        if(playing(standard) && !playing(supplied)) return standard;
        if(playing(supplied) && !playing(standard)) return supplied;
        // A running system session is authoritative when two different apps both claim playback.
        if(!sameApp && playing(standard)) return standard;
        if(sameApp && sameRecording(standard,supplied)) {
            boolean lyrics=!supplied.optString("providedLyrics").isEmpty()
                && !supplied.optString("trackId").isEmpty()
                && supplied.optString("trackId").equals(supplied.optString("providedLyricsTrackId"));
            if(lyrics && (supplied.optBoolean("positionKnown") || !standard.optBoolean("positionKnown"))) return supplied;
            if(supplied.optBoolean("positionKnown") && !standard.optBoolean("positionKnown")) return supplied;
        }
        if("notification".equals(standard.optString("source")) && supplied.optBoolean("positionKnown")) return supplied;
        return standard;
    }
}
