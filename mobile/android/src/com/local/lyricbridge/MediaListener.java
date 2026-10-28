package com.local.lyricbridge;

import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;

public final class MediaListener extends NotificationListenerService {
    @Override public void onListenerConnected() {
        try { NotificationPlaybackStore.replace(getActiveNotifications()); }
        catch (SecurityException unavailable) { NotificationPlaybackStore.clear(); }
        BridgeService.notice = "通知使用权已就绪";
        BridgeService.onPlaybackSourcesChanged();
    }
    @Override public void onNotificationPosted(StatusBarNotification notification) {
        if (NotificationPlaybackStore.update(notification)) BridgeService.onPlaybackSourcesChanged();
    }
    @Override public void onNotificationRemoved(StatusBarNotification notification) {
        if (NotificationPlaybackStore.remove(notification)) BridgeService.onPlaybackSourcesChanged();
    }
    @Override public void onListenerDisconnected() {
        NotificationPlaybackStore.clear();
        BridgeService.notice = "通知读取已断开，请重新开启通知使用权";
        BridgeService.onPlaybackSourcesChanged();
    }
    @Override public void onDestroy() {
        NotificationPlaybackStore.clear();
        BridgeService.onPlaybackSourcesChanged();
        super.onDestroy();
    }
}
