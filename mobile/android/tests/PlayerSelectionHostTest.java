package com.local.lyricbridge;
import java.util.*;
public final class PlayerSelectionHostTest {
 public static void main(String[] args) {
  Map<String,Boolean> checks=new LinkedHashMap<>();
  checks.put("automaticSelectionAcceptsAnyNamedPlayer",PlayerSelection.matches("","dev.small.player")&&PlayerSelection.matches(null,"com.miui.player"));
  checks.put("automaticSelectionDoesNotAcceptAbsentPackage",!PlayerSelection.matches("",null)&&!PlayerSelection.matches("",""));
  checks.put("kugouPlatformAcceptsBothActualEditions",PlayerSelection.matches("platform:kugou","com.kugou.android")&&PlayerSelection.matches("platform:kugou","com.kugou.android.lite"));
  checks.put("kugouPlatformRejectsOtherPlatforms",!PlayerSelection.matches("platform:kugou","com.tencent.qqmusic")&&!PlayerSelection.matches("platform:kugou","com.luna.music"));
  checks.put("kugouPlatformDoesNotGuessUnlistedPrefix",!PlayerSelection.matches("platform:kugou","com.kugou.android.unverified")&&!PlayerSelection.matches("platform:kugou","com.kugou.androidlite"));
  checks.put("legacyBaseSelectionExpandsToConceptEdition",PlayerSelection.matches("com.kugou.android","com.kugou.android.lite"));
  checks.put("legacyConceptSelectionExpandsToBaseEdition",PlayerSelection.matches("com.kugou.android.lite","com.kugou.android"));
  checks.put("legacyPackageCanonicalizesToOnePlatform","platform:kugou".equals(PlayerSelection.canonical("com.kugou.android"))&&PlayerSelection.canonical("com.kugou.android").equals(PlayerSelection.canonical("com.kugou.android.lite")));
  checks.put("platformLabelDoesNotExposeEditionName","酷狗音乐".equals(PlayerSelection.title("com.kugou.android.lite"))&&"酷狗音乐".equals(PlayerSelection.title("platform:kugou")));
  checks.put("qqPlatformOnlyAcceptsQQMusic",PlayerSelection.matches("platform:qq","com.tencent.qqmusic")&&!PlayerSelection.matches("platform:qq","com.netease.cloudmusic"));
  checks.put("systemMusicSelectsExactResolvedPackage",PlayerSelection.matches("system:com.miui.player","com.miui.player")&&!PlayerSelection.matches("system:com.miui.player","com.kugou.android"));
  checks.put("systemSelectionDoesNotExpandKnownPlatform",PlayerSelection.matches("system:com.kugou.android","com.kugou.android")&&!PlayerSelection.matches("system:com.kugou.android","com.kugou.android.lite"));
  checks.put("systemMusicUsesGenericMusicLabel","音乐".equals(PlayerSelection.title("system:com.miui.player")));
  checks.put("customPlayerSelectionRemainsExact",PlayerSelection.matches("dev.small.player","dev.small.player")&&!PlayerSelection.matches("dev.small.player","dev.small.player.lite"));
  checks.put("customSelectionIdentityIsNotRewritten","dev.small.player".equals(PlayerSelection.canonical("dev.small.player"))&&"".equals(PlayerSelection.title("dev.small.player")));
  checks.put("nullSelectionCanonicalizesToAutomatic","".equals(PlayerSelection.canonical(null)));
  checks.put("unknownPlatformCannotMatchAnApp",!PlayerSelection.matches("platform:unknown","com.tencent.qqmusic")&&!PlayerSelection.matches("platform:unknown","platform:unknown"));
  StringBuilder out=new StringBuilder("{\"checks\":{");boolean first=true;for(Map.Entry<String,Boolean> item:checks.entrySet()){if(!first)out.append(',');first=false;out.append('"').append(item.getKey()).append("\":").append(item.getValue());}System.out.println(out.append("}}"));
 }
}
