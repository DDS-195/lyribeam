package com.local.lyricbridge;

import java.util.Collection;
import java.util.Locale;

/** Resolves the manufacturer's music app without replacing it with a user default. */
final class SystemMusic {
    static final String[][] OEM_PACKAGES = {
        {"xiaomi|redmi|poco","com.miui.player"},
        {"samsung","com.sec.android.app.music"},
        {"oppo|realme","com.oppo.music"},
        {"huawei","com.android.mediacenter"},
        {"vivo|iqoo","com.android.bbkmusic"}
    };
    private SystemMusic() {}
    static String choose(String manufacturer,Collection<String> installed,Collection<String> systemMusicCandidates) {
        String brand = manufacturer == null ? "" : manufacturer.toLowerCase(Locale.ROOT);
        for (String[] app : OEM_PACKAGES) {
            for (String alias : app[0].split("\\|")) {
                if (brand.contains(alias) && installed.contains(app[1])) return app[1];
            }
        }
        String selected = "";
        for (String candidate : systemMusicCandidates) {
            if (candidate == null || candidate.isEmpty()) continue;
            if (!selected.isEmpty() && !selected.equals(candidate)) return "";
            selected = candidate;
        }
        return selected;
    }
}
