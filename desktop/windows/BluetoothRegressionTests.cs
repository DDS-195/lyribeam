using System.Collections.Concurrent;
using System.Text;

namespace LyricBridge;

// Fault injection only: these checks never create a native socket or register a Bluetooth service.
public static class BluetoothRegressionTests
{
    private const string Valid = "{\"version\":1,\"type\":\"media\",\"title\":\"测试\",\"artist\":\"本地\",\"positionKnown\":true,\"positionMs\":10}\n";
    public static async Task<Dictionary<string, bool>> RunAsync(string? root = null)
    {
        // Keep first-use JSON/JIT initialization outside the intentionally short simulated deadlines.
        _ = MediaSnapshot.Parse(Valid);
        var checks = new Dictionary<string, bool>();
        async Task Check(string name, Func<Task<bool>> test)
        { try { checks[name] = await test(); } catch { checks[name] = false; } }
        await Check("Bluetooth retries initialization failures", () => Recovery(true));
        await Check("Bluetooth rebuilds listener after accept failure", () => Recovery(false));
        await Check("Silent connection releases receiver for reconnection", () => TimeoutConnection(false));
        await Check("Invalid bytes cannot extend connection lifetime", () => TimeoutConnection(true));
        await Check("Valid heartbeat keeps connection alive", ValidHeartbeat);
        await Check("Stopping cancels retry without restarting", StopRetry);
        await Check("Stopping unblocks receive and permits explicit restart", StopReceiveAndRestart);
        await Check("Stopping unblocks accept", StopAccept);
        await Check("Invalid null frame does not discard following valid frame", InvalidFrameThenValid);
        await Check("Protocol rejects invalid mandatory values", () => Task.FromResult(ProtocolRejects()));
        await Check("Protocol normalizes optional nulls and bounds metadata", () => Task.FromResult(ProtocolBounds()));
        return checks;
    }
    private static BluetoothServer Server(Func<IBluetoothTransport> factory, int retryMs = 15) =>
        new(factory, TimeSpan.FromMilliseconds(120), _ => TimeSpan.FromMilliseconds(retryMs));
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<bool> Wait(Task<bool> signal, int ms = 900)
    { try { return await signal.WaitAsync(TimeSpan.FromMilliseconds(ms)); } catch (TimeoutException) { return false; } }
    private static async Task Stop(BluetoothServer server)
    { server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(1)); }

    private static async Task<bool> Recovery(bool failOpen)
    {
        int attempts = 0; var received = Signal();
        using var server = Server(() => Interlocked.Increment(ref attempts) == 1
            ? new FakeTransport(failOpen, !failOpen) : new FakeTransport(new FakeConnection(Valid)));
        server.SnapshotReceived += _ => received.TrySetResult(true);
        try { server.Start(); return await Wait(received.Task) && attempts >= 2; }
        finally { await Stop(server); }
    }
    private static async Task<bool> TimeoutConnection(bool junk)
    {
        var first = junk ? new FakeConnection("{\"version\":99,\"type\":\"bad\"}\n", true, 15) : new FakeConnection();
        var native = new FakeTransport(first, new FakeConnection(Valid));
        var received = Signal(); using var server = Server(() => native);
        server.SnapshotReceived += _ => received.TrySetResult(true);
        try { server.Start(); return await Wait(received.Task) && first.Closed && native.AcceptCount >= 2; }
        finally { await Stop(server); }
    }
    private static async Task<bool> ValidHeartbeat()
    {
        var connection = new FakeConnection(Valid, true, 20); var native = new FakeTransport(connection);
        int count = 0; var enough = Signal(); using var server = Server(() => native);
        server.SnapshotReceived += _ => { if (Interlocked.Increment(ref count) >= 8) enough.TrySetResult(true); };
        try
        {
            server.Start(); if (!await Wait(enough.Task)) return false;
            await Task.Delay(180); return !connection.Closed && native.AcceptCount == 1 && count >= 8;
        }
        finally { await Stop(server); }
    }
    private static async Task<bool> StopRetry()
    {
        int attempts = 0; var failed = Signal();
        using var server = Server(() => { Interlocked.Increment(ref attempts); return new FakeTransport(true, false); }, 500);
        server.Status += value => { if (value.Contains("失败") || value.Contains("重试")) failed.TrySetResult(true); };
        try
        {
            server.Start(); if (!await Wait(failed.Task)) return false;
            await Task.Delay(20); bool wasWaiting = !server.Completion.IsCompleted;
            await Stop(server); int afterStop = attempts; await Task.Delay(100);
            return wasWaiting && server.Completion.IsCompleted && attempts == afterStop;
        }
        finally { await Stop(server); }
    }
    private static async Task<bool> StopReceiveAndRestart()
    {
        int attempts = 0, count = 0; var first = Signal(); var second = Signal();
        using var server = Server(() => { Interlocked.Increment(ref attempts); return new FakeTransport(new FakeConnection(Valid)); });
        server.SnapshotReceived += _ => { if (Interlocked.Increment(ref count) == 1) first.TrySetResult(true); else second.TrySetResult(true); };
        try
        {
            server.Start(); if (!await Wait(first.Task)) return false;
            await Stop(server); await Task.Delay(60); if (attempts != 1) return false;
            server.Start(); return await Wait(second.Task) && attempts == 2;
        }
        finally { await Stop(server); }
    }
    private static async Task<bool> StopAccept()
    {
        var native = new FakeTransport(); using var server = Server(() => native);
        try
        {
            server.Start(); if (!await Wait(native.AcceptEntered.Task)) return false;
            await Stop(server); return server.Completion.IsCompleted && native.Closed;
        }
        finally { await Stop(server); }
    }
    private static async Task<bool> InvalidFrameThenValid()
    {
        const string invalid = "{\"version\":1,\"type\":\"media\",\"title\":null,\"artist\":\"本地\"}\n";
        var connection = new FakeConnection(invalid + Valid); var received = Signal();
        using var server = Server(() => new FakeTransport(connection));
        server.SnapshotReceived += _ => received.TrySetResult(true);
        try { server.Start(); return await Wait(received.Task) && !connection.Closed; }
        finally { await Stop(server); }
    }
    private static bool ProtocolRejects()
    {
        string[] invalid = {
            "{\"version\":1,\"type\":\"media\",\"title\":null}",
            "{\"version\":1,\"type\":\"media\",\"title\":\"测试\",\"artist\":null}",
            "{\"version\":1,\"type\":\"media\",\"title\":\"  \"}",
            "{\"version\":1,\"type\":null}",
            "{\"version\":1,\"type\":\"media\",\"title\":\"测试\",\"durationMs\":-1}" };
        foreach (string value in invalid) { try { MediaSnapshot.Parse(value); return false; } catch (FormatException) { } }
        return MediaSnapshot.Parse("{\"version\":1,\"type\":\"idle\"}").Type == "idle";
    }
    private static bool ProtocolBounds()
    {
        var value = MediaSnapshot.Parse("{\"version\":1,\"type\":\"idle\",\"packageName\":null,\"diagnostic\":null}");
        if (value.PackageName != "" || value.Diagnostic != "") return false;
        foreach (string json in new[] {
            System.Text.Json.JsonSerializer.Serialize(new MediaSnapshot { Version = 1, Type = "idle", PackageName = new string('p', 257) }),
            System.Text.Json.JsonSerializer.Serialize(new MediaSnapshot { Version = 1, Type = "idle", Diagnostic = new string('d', 8193) }) })
        { try { MediaSnapshot.Parse(json); return false; } catch (FormatException) { } }
        return true;
    }
    private sealed class FakeConnection : IBluetoothConnection
    {
        private readonly ManualResetEventSlim closed = new();
        private readonly byte[]? packet;
        private readonly bool repeat;
        private readonly int delay;
        private bool sent;
        public bool Closed => closed.IsSet;
        public FakeConnection(string? text = null, bool repeat = false, int delay = 0)
        { packet = text == null ? null : Encoding.UTF8.GetBytes(text); this.repeat = repeat; this.delay = delay; }
        public int Receive(byte[] buffer)
        {
            if (Closed) return 0;
            if (packet != null && (!sent || repeat))
            {
                if (delay > 0 && closed.Wait(delay)) return 0;
                packet.CopyTo(buffer, 0); sent = true; return packet.Length;
            }
            closed.Wait(); return 0;
        }
        public void Close() => closed.Set();
        public void Dispose() => Close();
    }
    private sealed class FakeTransport : IBluetoothTransport
    {
        private readonly ConcurrentQueue<IBluetoothConnection> connections = new();
        private readonly ManualResetEventSlim closed = new();
        private readonly bool failOpen, failAccept;
        public TaskCompletionSource<bool> AcceptEntered { get; } = Signal();
        private int acceptCount;
        public int AcceptCount => Volatile.Read(ref acceptCount);
        public bool Closed => closed.IsSet;
        public FakeTransport(params IBluetoothConnection[] clients) { foreach (var value in clients) connections.Enqueue(value); }
        public FakeTransport(bool failOpen, bool failAccept) { this.failOpen = failOpen; this.failAccept = failAccept; }
        public string Open() { if (failOpen) throw new IOException("simulated Bluetooth initialization failure"); return "蓝牙已就绪"; }
        public IBluetoothConnection Accept()
        {
            Interlocked.Increment(ref acceptCount); AcceptEntered.TrySetResult(true);
            if (failAccept) throw new IOException("simulated listener failure");
            if (connections.TryDequeue(out var connection)) return connection;
            closed.Wait(); throw new OperationCanceledException();
        }
        public void Close() => closed.Set();
        public void Dispose() { Close(); foreach (var connection in connections) connection.Dispose(); }
    }
}
