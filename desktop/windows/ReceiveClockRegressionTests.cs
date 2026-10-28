using System.Diagnostics;
using System.Reflection;
namespace LyricBridge;

internal static class ReceiveClockRegressionTests
{
    public static Task<Dictionary<string, bool>> RunAsync(string root)
    {
        var checks = new Dictionary<string, bool>();
        var song = new MediaSnapshot { Version=1, Type="media", Title="接收时钟审核", Artist="审核歌手", DurationMs=180000,
            PositionKnown=true, PositionMs=30000, Playing=true, Speed=1 };
        var clock = new PlaybackClock();
        Receive(clock, song, Ago(800));
        checks["Receiver timestamp includes time spent waiting in the UI queue"] = clock.PositionMs >= 30750 && clock.PositionMs < 32000;
        Receive(clock, song with { Playing=false, PositionMs=12345 }, Ago(800));
        checks["Queued paused frames retain their exact paused position"] = Math.Abs(clock.PositionMs-12345) < 0.1;
        Receive(clock, song with { PositionMs=50000, RawPositionMs=10000, PositionUpdatedAtMs=1 }, Stopwatch.GetTimestamp());
        checks["Already extrapolated phone position is never added twice"] = clock.PositionMs >= 50000 && clock.PositionMs < 50100;
        Receive(clock, song with { Speed=2 }, Ago(800));
        checks["Queue-time compensation respects playback speed"] = clock.PositionMs >= 31500 && clock.PositionMs < 33000;
        Receive(clock, song with { Speed=-2 }, Ago(800));
        checks["Queue-time compensation supports reverse playback without changing its direction"] = clock.PositionMs <= 28500 && clock.PositionMs > 27000;
        Receive(clock, song, Ago(6000));
        checks["An already stale queued frame stays stale after UI delivery"] = clock.Stale;
        checks["Long queued gaps retain the five-second extrapolation cap"] = clock.PositionMs >= 34999 && clock.PositionMs <= 35001;
        Receive(clock, song with { PositionMs=5000 }, Ago(800));
        checks["Seeking replaces the old position and retains receiver-time compensation"] = clock.PositionMs >= 5750 && clock.PositionMs < 7000;
        Receive(clock, song, Stopwatch.GetTimestamp()+Stopwatch.Frequency);
        checks["Future receiver timestamps cannot move a playing song backwards"] = clock.PositionMs >= 30000 && clock.PositionMs < 30100 && !clock.Stale;
        Receive(clock, song, 0);
        checks["Absent receiver timestamps fall back to the current monotonic time"] = clock.PositionMs >= 30000 && clock.PositionMs < 30100 && !clock.Stale;
        Receive(clock, song with { PositionKnown=false, PositionMs=-1 }, Ago(800));
        checks["Unknown playback clocks remain unknown even when queued"] = clock.PositionMs == -1;
        return Task.FromResult(checks);
    }

    internal static long Ago(double milliseconds) => Stopwatch.GetTimestamp()-(long)(Stopwatch.Frequency*milliseconds/1000);
    private static void Receive(PlaybackClock clock, MediaSnapshot snapshot, long receivedAt)
    {
        // The fallback lets this same regression suite demonstrate old-release failures.
        var method = typeof(PlaybackClock).GetMethod("Update", new[]{typeof(MediaSnapshot),typeof(long)});
        if (method == null) clock.Update(snapshot); else method.Invoke(clock,new object[]{snapshot,receivedAt});
    }
}
