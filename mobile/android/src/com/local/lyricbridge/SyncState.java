package com.local.lyricbridge;

import android.content.Context;
import android.content.SharedPreferences;

/** User intent survives process death; lifecycle cleanup must not revoke it. */
final class SyncState {
    static final String KEY = "sync_requested";
    private SyncState() {}
    private static SharedPreferences prefs(Context context) { return context.getSharedPreferences("bridge",0); }
    static boolean hasDecision(Context context) { return prefs(context).contains(KEY); }
    static boolean requested(Context context) { return prefs(context).getBoolean(KEY,false); }
    static synchronized boolean setRequested(Context context,boolean value) {
        SharedPreferences preferences = prefs(context);
        boolean saved = preferences.edit().putBoolean(KEY,value).commit();
        if (!saved && value) {
            // A failed commit may already have changed the in-memory preferences.
            // Do not authorize a new service after failing to save the start.
            preferences.edit().putBoolean(KEY,false).commit();
        }
        return saved;
    }
    static synchronized boolean migrateLegacyStart(Context context) {
        if (hasDecision(context)) return requested(context);
        return setRequested(context,true);
    }
}
