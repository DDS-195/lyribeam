package com.local.lyricbridge;

/** First-run routing; permission repairs stay accessible from the daily home screen. */
final class SetupFlow {
    private SetupFlow() {}
    static boolean initiallyCompleted(Boolean saved, boolean hasComputer) {
        return saved != null ? saved : hasComputer;
    }
    static boolean canFinish(boolean songsGranted, boolean bluetoothGranted, boolean hasComputer) {
        return songsGranted && bluetoothGranted && hasComputer;
    }
    static boolean canFinish(boolean standardAccess, boolean activeApprovedIntegration, boolean bluetoothGranted, boolean hasComputer) {
        return canFinish(standardAccess || activeApprovedIntegration, bluetoothGranted, hasComputer);
    }
}
