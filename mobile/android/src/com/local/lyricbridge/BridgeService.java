package com.local.lyricbridge;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothSocket;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.IBinder;
import android.util.Log;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Timer;
import java.util.TimerTask;
import java.util.UUID;
import org.json.JSONObject;

public final class BridgeService extends Service {
    public static final UUID SERVICE_ID = UUID.fromString("8b5c1f34-4290-4b98-964e-ded6c66d4f90");
    public static final String ACTION_START = "com.local.lyricbridge.START_SYNC";
    public static final String ACTION_TARGET_CHANGED = "com.local.lyricbridge.TARGET_CHANGED";
    public static volatile boolean running;
    public static volatile String notice = "尚未启动";
    public static volatile String latestJson = "{}";
    private final Object connectionLock = new Object();
    private volatile boolean stopping;
    private volatile String connectionAddress = "";
    // connectionLock protects both the socket owner and target revision.
    private BluetoothSocket socket;
    private long targetRevision;
    private long snapshotSequence;
    private Thread worker;
    private MediaReader media;
    private boolean mediaStarted;
    private boolean foregroundStarted;
    private String mediaProblem = "";
    private void refreshStandardSource() {
        try {
            if (!mediaStarted) { media.start(); mediaStarted = true; }
            else media.refresh();
            mediaProblem = "";
        } catch (Exception error) {
            mediaStarted = false;
            try { media.stop(); } catch (Exception ignored) {}
            mediaProblem = "系统歌曲读取不可用，请检查通知使用权";
            Log.w("LyricBridge", mediaProblem + ": " + error.getClass().getSimpleName());
        }
    }
    private final UpdateSignal updates = new UpdateSignal();
    private static volatile BridgeService instance;
    private volatile boolean sourcesDirty = true;
    public static void onPlaybackSourcesChanged() {
        BridgeService active = instance;
        if (active != null) { active.sourcesDirty = true; active.updates.signal(); }
    }
    private JSONObject currentSnapshot() {
        String selected = getSharedPreferences("bridge",0).getString("player","");
        JSONObject standard = mediaStarted ? media.snapshot() : null;
        JSONObject supplied = running ? IntegrationStore.getApprovedSnapshot(this,selected) : null;
        JSONObject chosen = PlaybackSources.select(standard,supplied,selected);
        if (!mediaProblem.isEmpty() && !"media".equals(chosen.optString("type"))) {
            try { chosen.put("diagnostic",mediaProblem + "；也可使用已允许的主动接入播放器"); } catch (Exception ignored) {}
        }
        return chosen;
    }


    @Override public void onCreate() {
        super.onCreate();
        media = new MediaReader(this);
        media.setOnChanged(updates::signal);
        latestJson = "{}";
        instance = this;
        IntegrationStore.setOnChanged(BridgeService::onPlaybackSourcesChanged);
    }
    private boolean promoteForeground() {
        if (foregroundStarted) return true;
        try {
            NotificationManager nm = getSystemService(NotificationManager.class);
            nm.createNotificationChannel(new NotificationChannel("bridge", "歌词蓝牙连接", NotificationManager.IMPORTANCE_LOW));
            PendingIntent open = PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE);
            Notification notification = new Notification.Builder(this,"bridge")
                .setSmallIcon(android.R.drawable.ic_media_play).setContentTitle("蓝牙歌词助手")
                .setContentText("读取歌曲状态并直连电脑；可在应用内停止")
                .setContentIntent(open).setOngoing(true).build();
            if (Build.VERSION.SDK_INT >= 29) startForeground(1,notification,ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE);
            else startForeground(1,notification);
            foregroundStarted = true;
            return true;
        } catch (RuntimeException error) {
            notice = "同步服务未能恢复，请检查蓝牙权限后重新开始";
            Log.w("LyricBridge","foreground-start-failed: "+error.getClass().getSimpleName());
            return false;
        }
    }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        // Only the old internal, action-less command can migrate a missing flag.
        // Neither a delayed start command nor a system restart can override stop.
        if (!SyncState.hasDecision(this) && intent != null && intent.getAction() == null
            && !SyncState.migrateLegacyStart(this)) {
            notice = "未能保存同步状态，请重新开始";
            stopSelf(); return START_NOT_STICKY;
        }
        if (!SyncState.requested(this)) {
            notice = "已停止";
            stopSelf(); return START_NOT_STICKY;
        }
        if (selectedAddress().isEmpty()) {
            notice = "请选择电脑后重新开始同步";
            stopSelf(); return START_NOT_STICKY;
        }
        if (Build.VERSION.SDK_INT >= 31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            notice = "请允许蓝牙连接权限后重新开始同步";
            stopSelf(); return START_NOT_STICKY;
        }
        if (!promoteForeground()) { stopSelf(); return START_NOT_STICKY; }
        if (worker != null && worker.isAlive()) {
            if ((intent != null && ACTION_TARGET_CHANGED.equals(intent.getAction())) || !selectedAddress().equals(connectionAddress))
                requestReconnect();
            return START_STICKY;
        }
        refreshStandardSource();
        // A recreated process has no integration payloads yet. Keep the user's
        // session alive while readers reconnect or an approved source republishes.
        stopping = false;
        running = true;
        worker = new Thread(this::connectLoop,"LyricBridge-Bluetooth");
        worker.start();
        Log.i("LyricBridge",intent == null ? "sync-service-restored" : "sync-service-started");
        return START_STICKY;
    }
    private String selectedAddress() { return getSharedPreferences("bridge", 0).getString("device", ""); }
    private void requestReconnect() {
        BluetoothSocket previous;
        synchronized (connectionLock) {
            targetRevision++;
            previous = socket;
            socket = null;
            connectionLock.notifyAll();
        }
        updates.signal();
        close(previous);
    }
    private boolean currentAttempt(String address, long revision) {
        synchronized (connectionLock) { return !stopping && SyncState.requested(this) && revision == targetRevision && address.equals(selectedAddress()); }
    }
    private boolean ownSocket(BluetoothSocket candidate, String address, long revision) {
        synchronized (connectionLock) {
            if (stopping || revision != targetRevision || !address.equals(selectedAddress())) return false;
            socket = candidate;
            return true;
        }
    }
    private boolean currentConnection(BluetoothSocket candidate, String address, long revision) {
        synchronized (connectionLock) { return socket == candidate && currentAttempt(address, revision); }
    }
    private void releaseSocket(BluetoothSocket candidate) {
        synchronized (connectionLock) { if (socket == candidate) socket = null; }
        // A stale attempt must never close a newer attempt's socket.
        close(candidate);
    }
    private static void close(BluetoothSocket candidate) { if (candidate != null) try { candidate.close(); } catch (Exception ignored) {} }
    private void connectLoop() {
        while (!stopping && SyncState.requested(this)) {
            final String address;
            final long revision;
            synchronized (connectionLock) {
                if (stopping) break;
                address = selectedAddress();
                revision = targetRevision;
                connectionAddress = address;
            }
            BluetoothSocket candidate = null;
            try {
                if (address.isEmpty()) throw new IllegalStateException("尚未选择已配对电脑");
                BluetoothAdapter adapter = ((BluetoothManager)getSystemService(BLUETOOTH_SERVICE)).getAdapter();
                if (adapter == null || !adapter.isEnabled()) throw new IllegalStateException("请打开手机蓝牙");
                BluetoothDevice device = adapter.getRemoteDevice(address);
                if (!currentAttempt(address, revision)) continue;
                notice = "正在连接电脑…";
                candidate = device.createRfcommSocketToServiceRecord(SERVICE_ID);
                if (!ownSocket(candidate, address, revision)) continue;
                final BluetoothSocket connecting = candidate;
                Timer timeout = new Timer(true);
                timeout.schedule(new TimerTask() { @Override public void run() { close(connecting); } }, 15000);
                try { candidate.connect(); } finally { timeout.cancel(); }
                if (!currentConnection(candidate, address, revision)) continue;
                notice = "蓝牙已连接，正在发送歌曲状态";
                OutputStream output = candidate.getOutputStream();
                long lastRefresh = 0;
                SnapshotFrames frames = new SnapshotFrames();
                while (currentConnection(candidate, address, revision) && candidate.isConnected()) {
                    long now = android.os.SystemClock.elapsedRealtime();
                    if (sourcesDirty || now - lastRefresh >= 3000) { sourcesDirty = false; refreshStandardSource(); lastRefresh = now; }
                    long observed = updates.version();
                    JSONObject data = currentSnapshot();
                    notice = mediaProblem.isEmpty() ? "蓝牙已连接，正在发送歌曲状态"
                        : "integration".equals(data.optString("source")) ? "蓝牙已连接，正在使用播放器主动接入"
                        : "蓝牙已连接；请恢复歌曲读取权限或开启已允许的播放器";
                    data.put("sequence",++snapshotSequence);
                    if (!currentConnection(candidate, address, revision)) break;
                    latestJson = data.toString();
                    output.write((frames.encode(data,now) + "\n").getBytes(StandardCharsets.UTF_8));
                    output.flush();
                    updates.await(observed, 1000);
                }
            } catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); break; }
            catch (Exception error) {
                if (currentAttempt(address, revision)) { notice = "连接未成功：" + error.getMessage() + "（5 秒后重试）"; Log.w("LyricBridge", notice); }
            } finally { releaseSocket(candidate); }
            synchronized (connectionLock) {
                if (currentAttempt(address, revision)) try { connectionLock.wait(5000); }
                catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); break; }
            }
        }
    }
    @Override public void onDestroy() {
        synchronized (connectionLock) { stopping = true; }
        running = false;
        if (instance == this) { instance = null; IntegrationStore.setOnChanged(null); }
        requestReconnect();
        if (worker != null) worker.interrupt();
        if (media != null) try { media.stop(); } catch (Exception ignored) {}
        notice = "已停止";
        stopForeground(STOP_FOREGROUND_REMOVE);
        super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
