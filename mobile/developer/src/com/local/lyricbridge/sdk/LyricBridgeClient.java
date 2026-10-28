package com.local.lyricbridge.sdk;

import android.content.Context;
import android.net.Uri;
import android.os.Bundle;
import android.os.SystemClock;

/** Optional Android-only bridge SDK. No networking, services or playback control. */
public final class LyricBridgeClient {
    public static final int PROTOCOL_VERSION=1,MAX_LYRICS=65536;
    public static final String AUTHORITY="com.local.lyricbridge.integration";
    private static final Uri ENDPOINT=Uri.parse("content://"+AUTHORITY);
    private final Context context;
    public LyricBridgeClient(Context context){if(context==null)throw new IllegalArgumentException("context");this.context=context.getApplicationContext();}
    public Result getStatus(){return call("status",new Bundle());}
    public Result requestAccess(){return call("requestAccess",new Bundle());}
    public Result clear(){return call("clear",new Bundle());}
    public Result publish(Snapshot snapshot){if(snapshot==null)throw new IllegalArgumentException("snapshot");return call("publish",snapshot.bundle());}
    private Result call(String method,Bundle payload) {
        payload.putString("packageName",context.getPackageName());
        try {
            Bundle reply=context.getContentResolver().call(ENDPOINT,method,null,payload);
            return reply==null?new Result("unavailable",false,false,false,"Helper returned no result"):new Result(reply);
        } catch(RuntimeException unavailable) {
            return new Result("unavailable",false,false,false,"Helper is unavailable or refused the caller");
        }
    }
    public static final class Result {
        public final String state,message;
        public final boolean accepted,enabled,approved;
        private Result(Bundle value){this(value.getString("state","unavailable"),value.getBoolean("accepted",false),value.getBoolean("enabled",false),value.getBoolean("approved",false),value.getString("message",""));}
        private Result(String state,boolean accepted,boolean enabled,boolean approved,String message){this.state=state;this.accepted=accepted;this.enabled=enabled;this.approved=approved;this.message=message;}
        public boolean isReady(){return approved&&enabled;}
    }
    /** Create a fresh builder when a new recording starts; IDs are caller-local, not platform IDs. */
    public static final class Snapshot {
        private final Bundle value;
        private Snapshot(Bundle value){this.value=new Bundle(value);}
        private Bundle bundle(){return new Bundle(value);}
        public static Builder builder(String sessionId,String trackId,String title){return new Builder(sessionId,trackId,title);}
        public static final class Builder {
            private final Bundle value=new Bundle();
            private final String trackId;
            private Builder(String sessionId,String trackId,String title) {
                this.trackId=text(trackId,256,true);
                value.putInt("protocolVersion",PROTOCOL_VERSION);
                value.putString("sessionId",text(sessionId,256,true));value.putString("trackId",this.trackId);
                value.putString("title",text(title,512,true));value.putLong("leaseMs",10000);
                value.putDouble("speed",1);value.putLong("positionMs",-1);
                value.putLong("sampledAtElapsedRealtimeMs",SystemClock.elapsedRealtime());
            }
            public Builder artist(String artist){value.putString("artist",text(artist,512,false));return this;}
            public Builder album(String album){value.putString("album",text(album,512,false));return this;}
            public Builder mediaId(String mediaId){value.putString("mediaId",text(mediaId,256,false));return this;}
            public Builder duration(long durationMs){if(durationMs<0||durationMs>604800000L)throw new IllegalArgumentException("durationMs");value.putLong("durationMs",durationMs);return this;}
            public Builder playback(long positionMs,boolean playing,double speed) {
                if(positionMs<0||positionMs>604800000L||Double.isNaN(speed)||Double.isInfinite(speed)||Math.abs(speed)>16)throw new IllegalArgumentException("playback");
                value.putLong("positionMs",positionMs);value.putBoolean("positionKnown",true);
                value.putBoolean("playing",playing);value.putDouble("speed",speed);
                value.putLong("sampledAtElapsedRealtimeMs",SystemClock.elapsedRealtime());return this;
            }
            public Builder metadataOnly(boolean playing){value.putBoolean("positionKnown",false);value.putBoolean("playing",playing);value.putLong("positionMs",-1);return this;}
            public Builder lyrics(String lrc){value.putString("providedLyrics",text(lrc,MAX_LYRICS,false));value.putString("providedLyricsTrackId",trackId);return this;}
            public Builder lease(long leaseMs){if(leaseMs<1000||leaseMs>30000)throw new IllegalArgumentException("leaseMs");value.putLong("leaseMs",leaseMs);return this;}
            public Snapshot build(){return new Snapshot(value);}
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

        private static String text(String value,int max,boolean required) {
            String result=trimText(value);
            if(result.length()>max||required&&result.isEmpty())throw new IllegalArgumentException("Invalid text field");
            return result;
        }
    }
}
