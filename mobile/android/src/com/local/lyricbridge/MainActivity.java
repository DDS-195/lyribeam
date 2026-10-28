package com.local.lyricbridge;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.content.ComponentName;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.TextUtils;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private enum Screen { SETUP, HOME, SETTINGS, DETAILS }
    private static final int BG = Color.rgb(247,245,252), INK = Color.rgb(43,34,57),
        MUTED = Color.rgb(133,122,149), PURPLE = Color.rgb(133,71,236), PALE = Color.rgb(238,230,255);
    private static final String COMPLETED = "onboarding_complete";
    private static final int REQUEST_BLUETOOTH = 1, REQUEST_NOTIFICATIONS = 2, REQUEST_ENABLE_BLUETOOTH = 3;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private MediaReader preview;
    private boolean previewStarted, pendingStart, resumed;
    private Screen screen;
    private int renderedConfiguration;
    private LinearLayout page;
    private TextView connectionTitle, connectionDetail, songState, songTitle, songArtist, songTime, diagnostic;
    private View statusDot, playerSettingRow;
    private TextView playerSettingValue;
    private ProgressBar songProgress;
    private Button syncButton, cancelSyncButton, finishButton;
    private final Runnable refresh = new Runnable() {
        @Override public void run() { update(); if (resumed) handler.postDelayed(this,1000); }
    };

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = getSharedPreferences("bridge",0);
        // Existing installations with a saved computer go straight to the daily home page.
        Boolean saved = prefs.contains(COMPLETED) ? prefs.getBoolean(COMPLETED,false) : null;
        boolean completed = SetupFlow.initiallyCompleted(saved,hasComputer());
        if (saved == null) prefs.edit().putBoolean(COMPLETED,completed).apply();
        preview = new MediaReader(this);
        screen = completed ? Screen.HOME : Screen.SETUP;
        if (state != null) {
            pendingStart = state.getBoolean("pendingStart",false);
            try {
                Screen previous = Screen.valueOf(state.getString("screen",screen.name()));
                if (completed && previous != Screen.SETUP) screen = previous;
            } catch (IllegalArgumentException ignored) {}
        }
        getWindow().setStatusBarColor(BG);
        getWindow().setNavigationBarColor(BG);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        if (Build.VERSION.SDK_INT >= 33)
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT,this::navigateBack);
        show(screen);
    }
    private void show(Screen next) {
        screen = next;
        connectionTitle = connectionDetail = songState = songTitle = songArtist = songTime = diagnostic = null;
        statusDot = playerSettingRow = null; playerSettingValue = null; songProgress = null; syncButton = cancelSyncButton = finishButton = null;
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true); scroll.setBackgroundColor(BG); scroll.setClipToPadding(false);
        scroll.setOnApplyWindowInsetsListener((view,insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                view.setPadding(bars.left,bars.top,bars.right,bars.bottom);
            } else view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),
                insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());
            return insets;
        });
        page = column(); page.setPadding(dp(24),dp(20),dp(24),dp(28));
        scroll.addView(page,new ScrollView.LayoutParams(-1,-2)); setContentView(scroll); scroll.requestApplyInsets();
        renderedConfiguration = configuration();
        switch (screen) {
            case SETUP: renderSetup(); break;
            case HOME: renderHome(); break;
            case SETTINGS: renderSettings(); break;
            case DETAILS: renderDetails(); break;
        }
    }
    private void renderSetup() {
        addBrand(false);
        space(page,30);
        page.addView(label("让歌词，跟着音乐走",29,INK,true));
        addCopy(page,"只需完成首次设置，之后打开就是同步首页。",15,MUTED,12);
        space(page,28);
        boolean songs = hasSongSource(), computer = hasComputer() && hasBluetoothPermission() && bluetoothEnabled();
        setupStep("01","读取正在播放的歌曲","开启「通知使用权」，或使用已允许且正在提供歌曲的主动接入播放器。",
            hasNotificationAccess() ? "已开启" : songs ? "主动接入可用" : "去开启",songs,() -> {
                if (!hasNotificationAccess() && hasActiveIntegrationSource()) chooseIntegrationSources(); else openSongAccess();
            });
        setupStep("02","连接你的电脑","先在电脑打开蓝牙歌词，再选择已经配对的电脑。",
            computer ? "已选择 " + computerName() : "选择电脑",computer,this::chooseComputer);
        addCopy(page,"播放器默认自动识别，跟随正在播放的音乐。",13,MUTED,4);
        action(page,"使用支持主动接入的播放器",false,() -> show(Screen.SETTINGS));
        space(page,26);
        finishButton = action(page,"完成设置，开始同步",true,this::finishSetup);
        finishButton.setEnabled(ready());
        finishButton.setAlpha(ready() ? 1f : .42f);
        addCopy(page,"手机照常听歌，歌词会显示在电脑上。",13,MUTED,14);
    }
    private void setupStep(String number,String title,String detail,String actionText,boolean done,Runnable action) {
        LinearLayout card = card(page,false);
        TextView badge = label(done ? "已完成" : number,12,done ? Color.rgb(52,145,108) : PURPLE,true);
        card.addView(badge); space(card,10);
        card.addView(label(title,19,INK,true)); addCopy(card,detail,14,MUTED,8); space(card,16);
        action(card,actionText,false,action);
    }
    private void renderHome() {
        addBrand(true); space(page,30);
        page.addView(label("音乐在手机，歌词在桌面",23,INK,true));
        addCopy(page,"享受音乐，连接交给我们。",14,MUTED,10); space(page,24);
        String issue = repairMessage();
        if (!issue.isEmpty()) {
            LinearLayout warning = card(page,false);
            warning.addView(label(issue,16,INK,true));
            addCopy(warning,"之前的设置已保留，恢复后即可继续同步。",13,MUTED,8);
            space(warning,12); action(warning,"检查设置",false,() -> show(Screen.SETTINGS));
        }
        LinearLayout connection = card(page,true);
        LinearLayout statusRow = row();
        statusDot = new View(this);
        LinearLayout.LayoutParams dotParams = new LinearLayout.LayoutParams(dp(8),dp(8)); dotParams.rightMargin = dp(8);
        statusRow.addView(statusDot,dotParams);
        connectionTitle = label("准备同步",13,PURPLE,true); statusRow.addView(connectionTitle); connection.addView(statusRow);
        space(connection,16);
        connection.addView(label(computerName(),26,INK,true));
        connectionDetail = label("",14,MUTED,false); connectionDetail.setLineSpacing(dp(3),1f);
        LinearLayout.LayoutParams detailParams = new LinearLayout.LayoutParams(-1,-2); detailParams.topMargin = dp(8);
        connection.addView(connectionDetail,detailParams); space(connection,22);
        syncButton = action(connection,"开始同步",true,() -> {
            if (BridgeService.running) { stopBridge(); syncButton.setEnabled(false); }
            else startBridge();
        });
        cancelSyncButton = action(connection,"停止同步",false,() -> { stopBridge(); update(); });
        cancelSyncButton.setVisibility(View.GONE);
        LinearLayout music = card(page,false);
        songState = label("当前音乐",12,PURPLE,true); music.addView(songState); space(music,18);
        songTitle = label("等待手机播放",25,INK,true); songTitle.setMaxLines(2); songTitle.setEllipsize(TextUtils.TruncateAt.END); music.addView(songTitle);
        songArtist = label("打开音乐播放器，播放喜欢的音乐",14,MUTED,false); addWithTop(music,songArtist,10);
        space(music,24);
        songProgress = new ProgressBar(this,null,android.R.attr.progressBarStyleHorizontal);
        songProgress.setMax(1000); songProgress.setProgressTintList(ColorStateList.valueOf(PURPLE));
        songProgress.setProgressBackgroundTintList(ColorStateList.valueOf(PALE));
        music.addView(songProgress,new LinearLayout.LayoutParams(-1,dp(5)));
        songTime = label("—  /  —",12,MUTED,false); songTime.setGravity(Gravity.END); addWithTop(music,songTime,12);
        TextView footer = label("通过蓝牙同步 · 不影响手机播放",12,MUTED,false);
        footer.setGravity(Gravity.CENTER); addWithTop(page,footer,10);
    }
    private void renderSettings() {
        topBar("设置",prefs.getBoolean(COMPLETED,false) ? Screen.HOME : Screen.SETUP);
        addCopy(page,prefs.getBoolean(COMPLETED,false) ? "首次配置已经保存，日常使用无需重复设置。" : "选择歌曲读取方式并连接电脑，完成后返回首次设置。",14,MUTED,10); space(page,22);
        LinearLayout connection = card(page,false);
        connection.addView(label("连接与授权",12,PURPLE,true)); space(connection,8);
        settingRow(connection,"连接电脑",hasComputer() ? computerName() : "尚未选择",this::chooseComputer);
        settingRow(connection,"歌曲读取",hasNotificationAccess() ? "已开启通知使用权" : hasActiveIntegrationSource() ? "主动接入可用 · 无需通知读取权限" : "开启通知使用权，或使用主动接入播放器",this::openSongAccess);
        settingRow(connection,"蓝牙",!hasBluetoothPermission() ? "需要允许附近设备权限" : bluetoothEnabled() ? "已开启" : "已关闭",this::openBluetooth);
        settingRow(connection,"同步通知",notificationsEnabled() ? "已允许" : "未允许，可能无法显示状态通知",() ->
            startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName())));
        LinearLayout player = card(page,false);
        player.addView(label("音乐",12,PURPLE,true)); space(player,8);
        playerSettingValue = settingRow(player,"音乐播放器","自动识别 · 等待播放",this::choosePlayer);
        playerSettingRow = (View)playerSettingValue.getParent().getParent();
        settingRow(player,"扩展播放器接入",IntegrationStore.isEnabled(this) ? "已开启 · 仅允许的播放器可接入" : "未开启",this::configureIntegration);
        settingRow(player,"允许的播放器","管理请求主动接入的播放器",this::chooseIntegrationSources);
        LinearLayout more = card(page,false);
        more.addView(label("更多",12,PURPLE,true)); space(more,8);
        settingRow(more,"连接详情","连接异常时查看详细状态",() -> show(Screen.DETAILS));
        addCopy(page,"同步歌曲与播放进度；支持接入的播放器也可提供自带歌词。手机无需联网，其余歌词由电脑匹配。",13,MUTED,4);
        TextView version = label("蓝牙歌词助手  " + versionName(),12,MUTED,false);
        version.setGravity(Gravity.CENTER); addWithTop(page,version,22);
    }
    private void renderDetails() {
        topBar("连接详情",Screen.SETTINGS);
        addCopy(page,"这里保留详细信息，方便连接异常时排查。",14,MUTED,10); space(page,24);
        LinearLayout card = card(page,false);
        diagnostic = label("",13,INK,false); diagnostic.setTextIsSelectable(true); diagnostic.setLineSpacing(dp(5),1f); card.addView(diagnostic);
    }
    private void addBrand(boolean settings) {
        LinearLayout row = row();
        ImageView icon = new ImageView(this);
        icon.setImageResource(getResources().getIdentifier("ic_launcher","mipmap",getPackageName()));
        icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        LinearLayout.LayoutParams iconParams = new LinearLayout.LayoutParams(dp(38),dp(38)); iconParams.rightMargin = dp(10);
        row.addView(icon,iconParams);
        TextView name = label("蓝牙歌词",21,INK,true); row.addView(name,new LinearLayout.LayoutParams(0,-2,1f));
        if (settings) {
            Button button = button("设置",false,() -> show(Screen.SETTINGS));
            row.addView(button,new LinearLayout.LayoutParams(dp(66),dp(42)));
        }
        page.addView(row);
    }
    private void topBar(String title,Screen back) {
        Button button = button("‹  返回",false,() -> show(back));
        page.addView(button,new LinearLayout.LayoutParams(dp(86),dp(42))); space(page,24);
        page.addView(label(title,29,INK,true));
    }
    private TextView settingRow(LinearLayout parent,String title,String value,Runnable action) {
        LinearLayout row = row(); row.setPadding(0,dp(16),0,dp(16));
        LinearLayout content = column();
        content.addView(label(title,16,INK,true));
        TextView detail = label(value,13,MUTED,false); detail.setLineSpacing(dp(3),1f); addWithTop(content,detail,5);
        row.addView(content,new LinearLayout.LayoutParams(0,-2,1f));
        TextView arrow = label("›",24,MUTED,false); arrow.setPadding(dp(12),0,0,0); row.addView(arrow);
        row.setBackground(new RippleDrawable(ColorStateList.valueOf(0x158547EC),null,rounded(Color.WHITE,12)));
        row.setFocusable(true); row.setContentDescription(title + "，" + value);
        row.setOnClickListener(view -> run(action)); parent.addView(row); return detail;
    }
    private void finishSetup() {
        if (!ready()) { toast(repairMessage()); return; }
        prefs.edit().putBoolean(COMPLETED,true).apply(); show(Screen.HOME); startBridge();
    }
    private void startBridge() {
        if (!ready()) {
            if (screen == Screen.HOME) show(Screen.SETTINGS);
            toast(repairMessage()); return;
        }
        if (Build.VERSION.SDK_INT >= 33 && !notificationsEnabled() && !prefs.getBoolean("notification_prompted",false)) {
            pendingStart = true; prefs.edit().putBoolean("notification_prompted",true).apply();
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},REQUEST_NOTIFICATIONS); return;
        }
        if (!SyncState.setRequested(this,true)) { toast("未能保存同步状态，请重试"); return; }
        try { startForegroundService(new Intent(this,BridgeService.class).setAction(BridgeService.ACTION_START)); }
        catch (RuntimeException error) { SyncState.setRequested(this,false); throw error; }
        update();
    }
    private void stopBridge() {
        boolean saved = SyncState.setRequested(this,false);
        stopService(new Intent(this,BridgeService.class));
        if (!saved) toast("同步已停止，但停止状态未能保存，请稍后重试");
    }
    private boolean hasActiveIntegrationSource() { return IntegrationStore.getApprovedSnapshot(this,prefs.getString("player","")) != null; }
    private boolean hasSongSource() { return hasNotificationAccess() || hasActiveIntegrationSource(); }
    private boolean ready() { return SetupFlow.canFinish(hasNotificationAccess(),hasActiveIntegrationSource(),hasBluetoothPermission() && bluetoothEnabled(),hasComputer()); }
    private boolean hasComputer() { return !prefs.getString("device","").isEmpty(); }
    private String computerName() {
        String value = prefs.getString("deviceName","");
        return value == null || value.trim().isEmpty() ? hasComputer() ? "已选择的电脑" : "尚未选择电脑" : value;
    }
    private boolean hasBluetoothPermission() { return Build.VERSION.SDK_INT < 31 || checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED; }
    private BluetoothAdapter adapter() {
        BluetoothManager manager = (BluetoothManager)getSystemService(BLUETOOTH_SERVICE);
        return manager == null ? null : manager.getAdapter();
    }
    private boolean bluetoothEnabled() {
        try { BluetoothAdapter adapter = adapter(); return hasBluetoothPermission() && adapter != null && adapter.isEnabled(); }
        catch (SecurityException ignored) { return false; }
    }
    private boolean notificationsEnabled() {
        return Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED;
    }
    private boolean hasNotificationAccess() {
        String enabled = Settings.Secure.getString(getContentResolver(),"enabled_notification_listeners");
        if (enabled == null) return false;
        String expected = new ComponentName(this,MediaListener.class).flattenToString();
        for (String item : enabled.split(":")) if (expected.equals(item)) return true;
        return false;
    }
    private void openSongAccess() { startActivity(new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)); }
    private void openBluetooth() {
        if (!hasBluetoothPermission()) { requestPermissions(new String[]{Manifest.permission.BLUETOOTH_CONNECT},REQUEST_BLUETOOTH); return; }
        BluetoothAdapter adapter = adapter();
        if (adapter == null) { toast("这台手机没有可用的蓝牙"); return; }
        if (!adapter.isEnabled()) startActivityForResult(new Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE),REQUEST_ENABLE_BLUETOOTH);
        else startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS));
    }
    private void chooseComputer() {
        if (!hasBluetoothPermission()) { requestPermissions(new String[]{Manifest.permission.BLUETOOTH_CONNECT},REQUEST_BLUETOOTH); return; }
        BluetoothAdapter adapter = adapter();
        if (adapter == null) { toast("这台手机没有可用的蓝牙"); return; }
        if (!adapter.isEnabled()) { startActivityForResult(new Intent(BluetoothAdapter.ACTION_REQUEST_ENABLE),REQUEST_ENABLE_BLUETOOTH); return; }
        List<BluetoothDevice> devices = new ArrayList<>(adapter.getBondedDevices());
        if (devices.isEmpty()) {
            new AlertDialog.Builder(this).setTitle("先配对你的电脑").setMessage("在系统蓝牙设置中与电脑配对，完成后返回这里选择。")
                .setPositiveButton("打开蓝牙设置",(dialog,which) -> startActivity(new Intent(Settings.ACTION_BLUETOOTH_SETTINGS)))
                .setNegativeButton("稍后",null).show(); return;
        }
        String[] names = new String[devices.size()]; int selected = -1;
        for (int i = 0; i < names.length; i++) {
            BluetoothDevice device = devices.get(i);
            String name = device.getName();
            names[i] = (name == null || name.trim().isEmpty() ? "未命名设备" : name) + "\n" + device.getAddress();
            if (device.getAddress().equals(prefs.getString("device",""))) selected = i;
        }
        new AlertDialog.Builder(this).setTitle("选择已配对的电脑")
            .setSingleChoiceItems(names,selected,(dialog,which) -> {
                BluetoothDevice chosen = devices.get(which);
                String previous = prefs.getString("device","");
                prefs.edit().putString("device",chosen.getAddress()).putString("deviceName",chosen.getName()).apply();
                if (BridgeService.running && !chosen.getAddress().equals(previous))
                    startForegroundService(new Intent(this,BridgeService.class).setAction(BridgeService.ACTION_TARGET_CHANGED));
                dialog.dismiss(); show(screen); update();
            }).setNegativeButton("取消",null).show();
    }
    private void configureIntegration() {
        boolean enabled=IntegrationStore.isEnabled(this);
        new AlertDialog.Builder(this).setTitle("扩展播放器接入")
            .setMessage("允许支持此功能的播放器提供歌曲、进度和自带歌词。开启后，还需在“允许的播放器”中逐个允许；不会自动开始蓝牙同步。")
            .setPositiveButton(enabled ? "关闭接入" : "开启接入",(dialog,which)->{
                IntegrationStore.setEnabled(this,!enabled); show(Screen.SETTINGS); update();
            }).setNegativeButton("取消",null).show();
    }
    private void chooseIntegrationSources() {
        List<String> packages=IntegrationStore.sourcePackages(this);
        if(packages.isEmpty()) {
            new AlertDialog.Builder(this).setTitle("允许的播放器")
                .setMessage("暂时没有播放器请求接入。请先在支持此功能的播放器中发起接入，再回到这里允许。")
                .setPositiveButton("知道了",null).show(); return;
        }
        String[] names=new String[packages.size()];boolean[] checked=new boolean[packages.size()];
        for(int i=0;i<names.length;i++) {
            names[i]=playerName(packages.get(i));checked[i]=IntegrationStore.isApproved(this,packages.get(i));
        }
        new AlertDialog.Builder(this).setTitle("允许的播放器")
            .setMultiChoiceItems(names,checked,(dialog,which,allowed)->IntegrationStore.setApproved(this,packages.get(which),allowed))
            .setPositiveButton("完成",(dialog,which)->{show(Screen.SETTINGS);update();}).show();
    }
    private String sourceName(String source) {
        if("integration".equals(source))return "播放器主动接入";
        if("notification".equals(source))return "音乐通知";
        if("media-session".equals(source))return "系统播放信息";
        return "等待播放信息";
    }

    private boolean playerInstalled(String packageName) {
        try { return getPackageManager().getApplicationInfo(packageName,0).enabled; }
        catch (PackageManager.NameNotFoundException ignored) { return false; }
    }
    private String systemMusicPackage() {
        List<String> installed = new ArrayList<>(), systemCandidates = new ArrayList<>();
        for (String[] app : SystemMusic.OEM_PACKAGES) if (playerInstalled(app[1])) installed.add(app[1]);
        Intent music = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_APP_MUSIC);
        for (ResolveInfo candidate : getPackageManager().queryIntentActivities(music,0)) {
            if (candidate.activityInfo == null || candidate.activityInfo.applicationInfo == null) continue;
            ApplicationInfo app = candidate.activityInfo.applicationInfo;
            if (app.enabled && (app.flags & (ApplicationInfo.FLAG_SYSTEM | ApplicationInfo.FLAG_UPDATED_SYSTEM_APP)) != 0)
                systemCandidates.add(app.packageName);
        }
        try {
            ApplicationInfo app = getPackageManager().getApplicationInfo("com.android.music",0);
            if (app.enabled && (app.flags & (ApplicationInfo.FLAG_SYSTEM | ApplicationInfo.FLAG_UPDATED_SYSTEM_APP)) != 0)
                systemCandidates.add(app.packageName);
        } catch (PackageManager.NameNotFoundException ignored) { }
        return SystemMusic.choose(Build.MANUFACTURER + " " + Build.BRAND,installed,systemCandidates);
    }
    private String playerChoice(String value,String systemPackage) {
        if (value != null && !systemPackage.isEmpty() && value.equals(systemPackage)) return "system:" + systemPackage;
        return PlayerSelection.canonical(value);
    }
    private void addPlayerChoice(List<String> choices,String packageName,String systemPackage) {
        String choice = playerChoice(packageName,systemPackage);
        if (!choice.isEmpty() && !choices.contains(choice)) choices.add(choice);
    }
    private void choosePlayer() {
        if (!hasNotificationAccess() && !IntegrationStore.isEnabled(this)) { toast("请开启歌曲读取权限，或先启用扩展播放器接入"); return; }
        List<String> choices = new ArrayList<>();
        final String systemPackage = systemMusicPackage();
        if (!systemPackage.isEmpty()) choices.add("system:" + systemPackage);
        for (String[] platform : PlayerSelection.PLATFORMS) {
            for (int i = 2; i < platform.length; i++) {
                if (playerInstalled(platform[i])) { choices.add(platform[0]); break; }
            }
        }
        if (hasNotificationAccess()) for (String pkg : MediaReader.activePackages(this)) addPlayerChoice(choices,pkg,systemPackage);
        for (String pkg : IntegrationStore.sourcePackages(this))
            if (IntegrationStore.isApproved(this,pkg)) addPlayerChoice(choices,pkg,systemPackage);
        String current = playerChoice(prefs.getString("player",""),systemPackage);
        if (!current.isEmpty() && !choices.contains(current)) choices.add(current);
        choices.add(0,"");
        String[] names = new String[choices.size()]; int selected = 0;
        for (int i = 0; i < names.length; i++) {
            names[i] = playerName(choices.get(i));
            if (choices.get(i).equals(current)) selected = i;
        }
        new AlertDialog.Builder(this).setTitle("选择音乐播放器")
            .setSingleChoiceItems(names,selected,(dialog,which) -> {
                String chosen = choices.get(which);
                if (!chosen.equals(playerChoice(prefs.getString("player",""),systemPackage))) {
                    boolean wasSyncing = BridgeService.running || SyncState.requested(this);
                    stopBridge();
                    prefs.edit().putString("player",chosen).apply(); stopPreview();
                    if (wasSyncing) toast("已保存播放器，回首页重新开始同步即可");
                }
                dialog.dismiss(); show(Screen.SETTINGS); update();
            }).setNegativeButton("取消",null).show();
    }
    private String playerName(String selection) {
        if (selection == null || selection.isEmpty()) return "自动识别 · 跟随正在播放";
        String title = PlayerSelection.title(selection);
        if (!title.isEmpty()) return title;
        try { ApplicationInfo app = getPackageManager().getApplicationInfo(selection,0); return getPackageManager().getApplicationLabel(app).toString(); }
        catch (PackageManager.NameNotFoundException ignored) { return selection; }
    }
    private void updatePlayerReadout(JSONObject data) {
        if (playerSettingValue == null) return;
        String selected = prefs.getString("player","");
        String mode = selected.isEmpty() ? "自动识别" : "手动选择：" + playerName(selected);
        String actual = data.optString("packageName","");
        String current;
        if (!hasSongSource()) current = "等待歌曲读取";
        else if (!"media".equals(data.optString("type")) || actual.isEmpty()) current = "等待播放";
        else {
            String name = playerName(playerChoice(actual,systemMusicPackage()));
            current = "当前：" + (name.equals(actual) ? "其他播放器" : name);
        }
        String description = mode + (selected.isEmpty() ? " · " : "\n") + current;
        playerSettingValue.setText(description);
        if (playerSettingRow != null) playerSettingRow.setContentDescription("音乐播放器，" + description.replace('\n','，'));
    }
    private String repairMessage() {
        if (!hasSongSource()) return "请开启歌曲读取权限，或让已允许的播放器提供歌曲";
        if (!hasBluetoothPermission()) return "需要允许蓝牙连接权限";
        if (!bluetoothEnabled()) return "手机蓝牙已关闭";
        if (!hasComputer()) return "请选择要连接的电脑";
        return "";
    }
    private int configuration() {
        return (hasNotificationAccess() ? 1 : 0) | (hasBluetoothPermission() ? 2 : 0)
            | (hasComputer() ? 4 : 0) | (bluetoothEnabled() ? 8 : 0) | (notificationsEnabled() ? 16 : 0)
            | (hasActiveIntegrationSource() ? 32 : 0) | (IntegrationStore.isEnabled(this) ? 64 : 0);
    }
    private JSONObject snapshot() {
        if (BridgeService.running && BridgeService.notice != null && BridgeService.notice.startsWith("蓝牙已连接")) {
            try {
                JSONObject active=new JSONObject(BridgeService.latestJson);
                String type = active.optString("type");
                if("media".equals(type) || "idle".equals(type)) return active;
            } catch(Exception ignored) {}
        }
        JSONObject supplied = IntegrationStore.getApprovedSnapshot(this,prefs.getString("player",""));
        JSONObject standard = new JSONObject();
        if (!hasNotificationAccess()) {
            stopPreview();
            try { standard.put("diagnostic","请开启通知使用权，或让已允许的播放器提供歌曲信息"); } catch (Exception ignored) {}
        } else try {
            if (!previewStarted) { preview.start(); previewStarted = true; }
            preview.refresh(); standard = preview.snapshot();
        } catch (Exception error) {
            stopPreview();
            try { standard.put("diagnostic","系统歌曲读取不可用，请检查通知使用权"); } catch (Exception ignored) {}
        }
        return PlaybackSources.select(standard,supplied,prefs.getString("player",""));
    }
    private void update() {
        if (page == null || isFinishing()) return;
        if (configuration() != renderedConfiguration) show(screen);
        JSONObject data = snapshot();
        updatePlayerReadout(data);
        if (screen == Screen.HOME) {
            boolean running = BridgeService.running;
            String notice = BridgeService.notice == null ? "" : BridgeService.notice;
            boolean connected = running && notice.startsWith("蓝牙已连接");
            boolean requested = SyncState.requested(this);
            connectionTitle.setText(connected ? "已连接电脑" : running ? "正在连接" : requested ? "同步已中断" : "同步未开启");
            statusDot.setBackground(rounded(connected ? Color.rgb(48,166,122) : running || requested ? Color.rgb(225,166,57) : MUTED,4));
            connectionDetail.setText(connected ? "歌曲状态正在同步" : running ? "请保持电脑端打开，连接会自动重试" : requested ? "点「恢复同步」继续，之前的设置已保留" : "点一下开始，歌词就会跟随音乐");
            syncButton.setEnabled(true); syncButton.setText(running ? "停止同步" : requested ? "恢复同步" : "开始同步");
            cancelSyncButton.setVisibility(!running && requested ? View.VISIBLE : View.GONE);
            boolean media = "media".equals(data.optString("type"));
            songState.setText(!media ? "当前音乐" : "notification".equals(data.optString("source")) ? "已识别歌曲" : data.optBoolean("playing") ? "正在播放" : "已暂停");
            songTitle.setText(media ? data.optString("title") : "等待手机播放");
            String artist = data.optString("artist","");
            songArtist.setText(media ? artist.isEmpty() ? playerName(data.optString("packageName")) : artist : "打开音乐播放器，播放喜欢的音乐");
            long duration = data.optLong("durationMs"), position = data.optLong("positionMs",-1);
            boolean known = media && data.optBoolean("positionKnown");
            songProgress.setProgress(known && duration > 0 ? (int)Math.min(1000,Math.max(0,position * 1000 / duration)) : 0);
            songTime.setText(media && !known ? "该播放器暂未提供播放进度" : (known ? time(position) : "—") + "  /  " + (duration > 0 ? time(duration) : "—"));
        } else if (screen == Screen.DETAILS) {
            diagnostic.setText("连接电脑：" + computerName() + "\n连接状态：" + (BridgeService.running ? BridgeService.notice : SyncState.requested(this) ? "同步已中断，请回首页恢复" : "同步未开启")
                + "\n歌曲读取：" + (hasNotificationAccess() ? "通知读取已开启" : hasActiveIntegrationSource() ? "正在使用已允许的主动接入" : "暂无可用来源")
                + "\n\n歌曲：" + data.optString("title","—") + "\n歌手：" + data.optString("artist","—")
                + "\n播放器：" + data.optString("packageName","—")
                + "\n播放进度：" + (data.optBoolean("positionKnown") ? time(data.optLong("positionMs")) : "未知")
                + "\n读取来源：" + sourceName(data.optString("source"))
                + "\n同步能力：" + (!"media".equals(data.optString("type")) ? "等待播放信息" : data.optBoolean("positionKnown") ? "可跟随播放" : "已识别歌曲，缺少进度")
                + "\n自带歌词：" + (!data.optString("providedLyrics").isEmpty() ? "已提供" : "未提供，由电脑匹配")
                + "\n原始进度：" + data.optLong("rawPositionMs",-1) + " ms"
                + "\n\n" + data.optString("diagnostic","请先开启歌曲读取权限。"));
        }
    }
    private String time(long millis) { long seconds = Math.max(0,millis) / 1000; return String.format(Locale.ROOT,"%d:%02d",seconds / 60,seconds % 60); }
    private String versionName() {
        try { return getPackageManager().getPackageInfo(getPackageName(),0).versionName; }
        catch (PackageManager.NameNotFoundException ignored) { return ""; }
    }
    private LinearLayout column() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private LinearLayout row() { LinearLayout view = new LinearLayout(this); view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView label(String value,int size,int color,boolean bold) {
        TextView view = new TextView(this); view.setText(value); view.setTextSize(size); view.setTextColor(color);
        view.setFontFeatureSettings("kern"); view.setIncludeFontPadding(false);
        if (bold) view.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));
        return view;
    }
    private void addCopy(LinearLayout parent,String value,int size,int color,int top) {
        TextView view = label(value,size,color,false); view.setLineSpacing(dp(4),1f); addWithTop(parent,view,top);
    }
    private void addWithTop(LinearLayout parent,View view,int top) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1,-2); params.topMargin = dp(top); parent.addView(view,params);
    }
    private GradientDrawable rounded(int color,int radius) { GradientDrawable drawable = new GradientDrawable(); drawable.setColor(color); drawable.setCornerRadius(dp(radius)); return drawable; }
    private LinearLayout card(LinearLayout parent,boolean tinted) {
        LinearLayout card = column(); card.setPadding(dp(22),dp(22),dp(22),dp(22));
        GradientDrawable background = tinted ? new GradientDrawable(GradientDrawable.Orientation.TL_BR,new int[]{0xFFF0E8FF,0xFFF9F5FF}) : rounded(Color.WHITE,24);
        background.setCornerRadius(dp(24)); background.setStroke(dp(1),tinted ? 0xFFE9DDFB : 0xFFF0EAF7); card.setBackground(background);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1,-2); params.bottomMargin = dp(16); parent.addView(card,params); return card;
    }
    private Button button(String value,boolean primary,Runnable action) {
        Button button = new Button(this); button.setText(value); button.setTextSize(primary ? 16 : 14); button.setAllCaps(false);
        button.setTextColor(primary ? Color.WHITE : PURPLE); button.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));
        button.setMinHeight(0); button.setMinimumHeight(0); button.setMinWidth(0); button.setMinimumWidth(0);
        button.setPadding(dp(12),dp(8),dp(12),dp(8)); button.setStateListAnimator(null);
        GradientDrawable background = primary ? new GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT,new int[]{0xFF965FF3,0xFF793CE0}) : rounded(PALE,14);
        background.setCornerRadius(dp(14));
        button.setBackground(new RippleDrawable(ColorStateList.valueOf(primary ? 0x30FFFFFF : 0x158547EC),background,null));
        button.setOnClickListener(view -> run(action)); return button;
    }
    private Button action(LinearLayout parent,String value,boolean primary,Runnable action) {
        Button button = button(value,primary,action); parent.addView(button,new LinearLayout.LayoutParams(-1,dp(54))); return button;
    }
    private void space(LinearLayout parent,int height) { View spacer = new View(this); parent.addView(spacer,new LinearLayout.LayoutParams(1,dp(height))); }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private void run(Runnable action) { try { action.run(); } catch (Exception error) { toast(error.getMessage()); } }
    private void toast(String message) { Toast.makeText(this,message == null ? "操作未完成，请重试" : message,Toast.LENGTH_LONG).show(); }
    private void stopPreview() { if (preview != null) try { preview.stop(); } catch (Exception ignored) {} previewStarted = false; }
    private void navigateBack() { if (screen == Screen.DETAILS) show(Screen.SETTINGS); else if (screen == Screen.SETTINGS) show(prefs.getBoolean(COMPLETED,false) ? Screen.HOME : Screen.SETUP); else finish(); }
    @Override public void onBackPressed() { navigateBack(); }
    @Override protected void onResume() { super.onResume(); resumed = true; handler.removeCallbacks(refresh); handler.post(refresh); }
    @Override protected void onPause() { resumed = false; handler.removeCallbacks(refresh); stopPreview(); super.onPause(); }
    @Override protected void onSaveInstanceState(Bundle state) { state.putString("screen",screen.name()); state.putBoolean("pendingStart",pendingStart); super.onSaveInstanceState(state); }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data);
        if (request == REQUEST_ENABLE_BLUETOOTH && result == RESULT_OK) run(this::chooseComputer);
    }
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results) {
        super.onRequestPermissionsResult(request,permissions,results);
        if (request == REQUEST_NOTIFICATIONS) {
            boolean start = pendingStart; pendingStart = false;
            if (start) run(this::startBridge);
        } else if (request == REQUEST_BLUETOOTH && results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) run(this::chooseComputer);
        else toast("未授予权限，可以稍后在设置中开启");
        show(screen); update();
    }
}
