package com.local.lyricbridge;

/** Display choices may group applications; snapshots always retain their actual package. */
final class PlayerSelection {
    static final String[][] PLATFORMS = {
        {"platform:qq","QQ音乐","com.tencent.qqmusic"},
        {"platform:netease","网易云音乐","com.netease.cloudmusic"},
        {"platform:kugou","酷狗音乐","com.kugou.android","com.kugou.android.lite"},
        {"platform:qishui","汽水音乐","com.luna.music"},
        {"platform:kuwo","酷我音乐","cn.kuwo.player"},
        {"platform:migu","咪咕音乐","cmccwm.mobilemusic"}
    };
    private PlayerSelection() {}
    static String canonical(String value) {
        if (value == null) return "";
        for (String[] platform : PLATFORMS) {
            if (value.equals(platform[0])) return platform[0];
            for (int i=2;i<platform.length;i++) if (value.equals(platform[i])) return platform[0];
        }
        return value;
    }
    static String title(String value) {
        String selected=canonical(value);
        for (String[] platform : PLATFORMS) if (selected.equals(platform[0])) return platform[1];
        return selected.startsWith("system:") && selected.length()>7 ? "音乐" : "";
    }
    static boolean matches(String selection,String actualPackage) {
        if (actualPackage == null || actualPackage.isEmpty()) return false;
        String selected=canonical(selection);
        if (selected.isEmpty()) return true;
        for (String[] platform : PLATFORMS) if (selected.equals(platform[0])) {
            for (int i=2;i<platform.length;i++) if (actualPackage.equals(platform[i])) return true;
            return false;
        }
        if (selected.startsWith("system:")) return actualPackage.equals(selected.substring(7));
        if (selected.startsWith("platform:")) return false;
        return actualPackage.equals(selected);
    }
}
