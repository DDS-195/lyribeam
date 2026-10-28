package com.local.lyricbridge;

import android.content.ComponentName;
import android.content.Context;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.List;

public final class MediaReader {
    private final Context context;
    private final MediaSessionManager manager;
    private final ComponentName listener;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private volatile MediaController current;
    private String currentSessionId = "";
    private String currentMetadataKey = "", retainedNotificationSession = "";
    private int currentState = -1;
    private long currentActivityAt, currentRawPosition = -1;
    private volatile Runnable onChanged = () -> {};
    private final MediaController.Callback playbackChanged = new MediaController.Callback() {
        @Override public void onMetadataChanged(MediaMetadata metadata) { refreshAfterChange(); }
        @Override public void onPlaybackStateChanged(PlaybackState state) { refreshAfterChange(); }
        @Override public void onSessionDestroyed() { refreshAfterChange(); }
    };
    private void refreshAfterChange() {
        try { refresh(); }
        catch (SecurityException denied) { choose(null); }
        onChanged.run();
    }
    public void setOnChanged(Runnable callback) { onChanged = callback; }
    private long sequence;
    private String selected = "";
    private final MediaSessionManager.OnActiveSessionsChangedListener changed = sessions -> choose(sessions);

    public MediaReader(Context value) {
        context = value;
        manager = (MediaSessionManager) context.getSystemService(Context.MEDIA_SESSION_SERVICE);
        listener = new ComponentName(context, MediaListener.class);
    }
    public void start() {
        selected = context.getSharedPreferences("bridge", 0).getString("player", "");
        manager.addOnActiveSessionsChangedListener(changed, listener, handler);
        refresh();
    }
    public void refresh() { choose(manager.getActiveSessions(listener)); }
    public synchronized void stop() {
        try { manager.removeOnActiveSessionsChangedListener(changed); }
        finally {
            MediaController previous = current;
            current = null;
            currentSessionId = "";
            currentMetadataKey = retainedNotificationSession = "";
            currentActivityAt = 0; currentState = -1; currentRawPosition = -1;
            if (previous != null) previous.unregisterCallback(playbackChanged);
        }
    }
    private synchronized void choose(List<MediaController> sessions) {
        MediaController best = null;
        int bestScore = -1;
        if (sessions != null) for (MediaController controller : sessions) {
            String name = controller.getPackageName();
            if (!PlayerSelection.matches(selected,name)) continue;
            PlaybackState state = controller.getPlaybackState();
            // Prefer a playing session with a usable song name, independent of its app.
            // Keep the current session on equal scores so list reordering cannot cause flapping.
            boolean hasTitle = !blank(songTitle(controller.getMetadata()));
            int score = (state != null && state.getState() == PlaybackState.STATE_PLAYING ? 200 : 0)
                + (hasTitle ? 100 : 0)
                + (current != null && current.getSessionToken().equals(controller.getSessionToken()) ? 1 : 0);
            if (score > bestScore) { best = controller; bestScore = score; }
        }
        if (current != null && best != null && current.getSessionToken().equals(best.getSessionToken())) {
            observeActivity(best, false);
            return;
        }
        if (current != null) current.unregisterCallback(playbackChanged);
        current = best;
        currentSessionId = best == null ? "" : "media-session:" + java.util.UUID.randomUUID().toString();
        retainedNotificationSession = "";
        if (best != null) {
            observeActivity(best, true);
            best.registerCallback(playbackChanged, handler);
        } else {
            currentMetadataKey = ""; currentActivityAt = 0; currentState = -1; currentRawPosition = -1;
        }
        onChanged.run();
    }
    private void observeActivity(MediaController controller, boolean firstObservation) {
        MediaMetadata metadata = controller.getMetadata();
        PlaybackState state = controller.getPlaybackState();
        long now = SystemClock.elapsedRealtime();
        String title = songTitle(metadata);
        String album = metadata == null ? "" : firstText(metadata.getString(MediaMetadata.METADATA_KEY_ALBUM));
        String artist = metadata == null ? "" : songArtist(metadata, title, album);
        String mediaId = metadata == null ? "" : mediaIdentifier(metadata.getString(MediaMetadata.METADATA_KEY_MEDIA_ID));
        long duration = metadata == null ? 0 : metadata.getLong(MediaMetadata.METADATA_KEY_DURATION);
        String key = limit(title) + "\n" + mediaId + "\n" + limit(artist) + "\n" + limit(album) + "\n" + duration;
        int value = state == null ? -1 : state.getState();
        long raw = state == null ? -1 : state.getPosition();
        long updated = state == null ? 0 : state.getLastPositionUpdateTime();
        if (firstObservation) {
            // An already-paused session is not newly active merely because this
            // reader starts. Android's update time uses the same monotonic clock.
            currentActivityAt = updated > 0 && updated <= now ? updated : 0;
        } else if (!key.equals(currentMetadataKey) || value != currentState
                || value != PlaybackState.STATE_PLAYING && raw >= 0 && raw != currentRawPosition) {
            currentActivityAt = now;
        }
        currentMetadataKey = key; currentState = value; currentRawPosition = raw;
    }
    public static List<String> activePackages(Context context) {
        List<String> packages = new ArrayList<>();
        MediaSessionManager mgr = (MediaSessionManager) context.getSystemService(Context.MEDIA_SESSION_SERVICE);
        try {
            for (MediaController controller : mgr.getActiveSessions(new ComponentName(context, MediaListener.class)))
                if (!packages.contains(controller.getPackageName())) packages.add(controller.getPackageName());
        } catch (SecurityException denied) { /* Approved integration sources remain independently available. */ }
        for (String packageName : NotificationPlaybackStore.packages())
            if (!packages.contains(packageName)) packages.add(packageName);
        return packages;
    }
    public synchronized JSONObject snapshot() {
        JSONObject data = new JSONObject();
        try {
            data.put("version", 1);
            data.put("sequence", ++sequence);
            data.put("type", "idle");
            data.put("source", "media-session");
            data.put("capability", "none");
            data.put("sessionId", currentSessionId);
            data.put("trackId", "");
            MediaController controller = current;
            if (controller == null) {
                JSONObject fallback = NotificationPlaybackStore.snapshot(selected, sequence);
                if (fallback != null) return fallback;
                data.put("diagnostic", "没有活动音乐会话。请在音乐播放器中开始播放，并开启通知栏播放控制。");
                return data;
            }
            observeActivity(controller, false);
            MediaMetadata metadata = controller.getMetadata();
            PlaybackState state = controller.getPlaybackState();
            boolean usablePlaying = state != null && state.getState() == PlaybackState.STATE_PLAYING;
            if (!usablePlaying) {
                JSONObject newer = NotificationPlaybackStore.snapshotNewerThan(selected, controller.getPackageName(),
                    currentActivityAt, sequence, retainedNotificationSession);
                if (newer != null) {
                    retainedNotificationSession = newer.optString("sessionId");
                    return newer;
                }
            }
            retainedNotificationSession = "";
            data.put("packageName", controller.getPackageName());
            if (metadata == null) {
                JSONObject fallback = NotificationPlaybackStore.snapshotExact(controller.getPackageName(), sequence);
                if (fallback != null) return fallback;
                data.put("diagnostic", "播放器未提供歌曲信息"); return data;
            }
            String title = songTitle(metadata);
            if (blank(title)) {
                JSONObject fallback = NotificationPlaybackStore.snapshotExact(controller.getPackageName(), sequence);
                if (fallback != null) return fallback;
                data.put("diagnostic", "播放器暂未提供歌曲名称");
                return data;
            }
            String album = firstText(metadata.getString(MediaMetadata.METADATA_KEY_ALBUM));
            String artist = songArtist(metadata, title, album);
            long duration = metadata.getLong(MediaMetadata.METADATA_KEY_DURATION);
            if (duration < 0 || duration > 604800000L) duration = 0;
            long raw = state == null ? -1 : state.getPosition();
            long updated = state == null ? 0 : state.getLastPositionUpdateTime();
            long now = SystemClock.elapsedRealtime();
            boolean playing = state != null && state.getState() == PlaybackState.STATE_PLAYING;
            float speed = state == null ? 1f : state.getPlaybackSpeed();
            if (Float.isNaN(speed) || Float.isInfinite(speed) || Math.abs(speed) > 16) speed = 1f;
            boolean known = state != null && raw >= 0 && raw <= 604800000L && updated > 0 && updated <= now
                && (duration <= 0 || raw <= duration + 10000L);
            long position = -1;
            if (known) {
                position = raw + (playing ? (long)((now - updated) * (double)speed) : 0);
                position = Math.max(0, Math.min(position, duration > 0 ? duration : 604800000L));
            }
            data.put("type", "media");
            data.put("capability", known ? "sync" : "metadata-only");
            data.put("title", limit(title));
            String mediaId = mediaIdentifier(metadata.getString(MediaMetadata.METADATA_KEY_MEDIA_ID));
            data.put("mediaId", mediaId);
            data.put("trackId", NotificationPlaybackStore.trackIdentifier(currentSessionId, mediaId, limit(title), limit(artist), limit(album), duration));
            data.put("artist", limit(artist));
            data.put("album", limit(album));
            data.put("durationMs", duration);
            data.put("rawPositionMs", raw);
            data.put("positionUpdatedAtMs", updated);
            data.put("positionKnown", known);
            data.put("positionMs", position);
            data.put("playing", playing);
            data.put("speed", speed);
            data.put("diagnostic", known ? "系统进度可读取" : "播放器没有提供可验证的进度；不会假装从零同步");
        } catch (Exception error) {
            try { data.put("type", "idle"); data.put("capability", "none"); data.put("trackId", ""); data.put("diagnostic", "媒体读取失败：" + error.getMessage()); } catch (Exception ignored) {}
        }
        return data;
    }
    private static String songTitle(MediaMetadata metadata) {
        if (metadata == null) return "";
        return firstText(metadata.getString(MediaMetadata.METADATA_KEY_TITLE),
            metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE));
    }
    private static String songArtist(MediaMetadata metadata, String title, String album) {
        String artist = firstText(metadata.getString(MediaMetadata.METADATA_KEY_ARTIST),
            metadata.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST));
        if (!blank(artist)) return artist;
        // A display subtitle can contain an album or arbitrary UI text. Use it only
        // when its display title identifies this song and a known album rules out
        // the common album-as-subtitle layout. Never infer artists from descriptions.
        String subtitle = firstText(metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE));
        String displayTitle = firstText(metadata.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE));
        if (!blank(album) && sameText(displayTitle, title) && !blank(subtitle)
            && !sameText(subtitle, album) && !sameText(subtitle, title)) return subtitle;
        return "";
    }
    private static String firstText(String... values) {
        for (String value : values) {
            if (blank(value)) continue;
            int start = 0, end = value.length();
            while (start < end && whitespace(value.charAt(start))) start++;
            while (end > start && whitespace(value.charAt(end - 1))) end--;
            return value.substring(start, end);
        }
        return "";
    }
    private static boolean sameText(String left, String right) {
        return !blank(left) && !blank(right) && firstText(left).equalsIgnoreCase(firstText(right));
    }
    private static boolean whitespace(char character) {
        return Character.isWhitespace(character) || Character.isSpaceChar(character) || character == '\u0085';
    }

    private static boolean blank(String value) {
        if (value == null || value.isEmpty()) return true;
        for (int i = 0; i < value.length(); i++) {
            char character = value.charAt(i);
            if (!Character.isWhitespace(character) && !Character.isSpaceChar(character) && character != '\u0085') return false;
        }
        return true;
    }
    private static String mediaIdentifier(String value) {
        // An optional identifier must fit the receiver's bound intact; truncation could merge recordings.
        if (blank(value) || value.length() > 256) return "";
        return value;
    }
    private static String limit(String value) {
        if (value == null) return "";
        int end = Math.min(512, value.length());
        if (end < value.length() && end > 0 && Character.isHighSurrogate(value.charAt(end - 1)) && Character.isLowSurrogate(value.charAt(end))) end--;
        return value.substring(0, end);
    }
}
