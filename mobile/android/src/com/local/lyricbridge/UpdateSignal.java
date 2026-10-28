package com.local.lyricbridge;

// A state change wakes the sender immediately; the periodic heartbeat remains a fallback.
public final class UpdateSignal {
    private long revision;
    public synchronized long version() { return revision; }
    public synchronized void signal() { revision++; notifyAll(); }
    public synchronized void await(long observed, long maxWaitMs) throws InterruptedException {
        if (revision == observed) wait(maxWaitMs);
    }
}
