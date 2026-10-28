using System.Diagnostics;

namespace LyricBridge;

public sealed class BluetoothServer : IDisposable
{
    public static readonly Guid ServiceId = new("8b5c1f34-4290-4b98-964e-ded6c66d4f90");
    private readonly object gate = new();
    private readonly Func<IBluetoothTransport> transportFactory;
    private readonly TimeSpan validStateTimeout;
    private readonly Func<int, TimeSpan> retryDelay;
    private CancellationTokenSource? cancellation;
    private IBluetoothTransport? transport;
    private IBluetoothConnection? connection;
    private Task? worker;
    public event Action<string>? Status;
    public event Action<MediaSnapshot>? SnapshotReceived;

    public BluetoothServer() : this(() => new NativeBluetoothTransport(), TimeSpan.FromSeconds(12)) { }
    internal BluetoothServer(Func<IBluetoothTransport> factory, TimeSpan timeout, Func<int, TimeSpan>? delay = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        transportFactory = factory; validStateTimeout = timeout;
        retryDelay = delay ?? (attempt => TimeSpan.FromSeconds(Math.Min(15, Math.Pow(2, Math.Min(attempt, 4)))));
    }
    internal Task Completion { get { lock (gate) return worker ?? Task.CompletedTask; } }
    public void Start()
    {
        lock (gate)
        {
            if (worker is { IsCompleted: false }) return;
            cancellation = new();
            var lifetime = cancellation;
            // Native accept/receive are blocking; keep them off the shared thread pool.
            worker = Task.Factory.StartNew(() => Run(lifetime), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }
    private void Run(CancellationTokenSource lifetime)
    {
        var token = lifetime.Token;
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                IBluetoothTransport? current = null;
                TimeSpan delay = TimeSpan.Zero;
                try
                {
                    current = transportFactory();
                    lock (gate) { token.ThrowIfCancellationRequested(); transport = current; }
                    string ready = current.Open();
                    Status?.Invoke(ready);
                    while (!token.IsCancellationRequested)
                    {
                        var incoming = current.Accept();
                        try
                        {
                            lock (gate) { token.ThrowIfCancellationRequested(); connection = incoming; }
                            Status?.Invoke("手机已连接，等待播放状态");
                            if (ReadConnection(incoming, token)) failures = 0;
                        }
                        finally
                        {
                            lock (gate) { if (ReferenceEquals(connection, incoming)) connection = null; }
                            incoming.Dispose();
                        }
                        if (!token.IsCancellationRequested) Status?.Invoke("手机已断开，等待重新连接");
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        delay = retryDelay(failures++);
                        Status?.Invoke($"蓝牙连接失败，正在恢复：{ex.Message}；{delay.TotalSeconds:0.#} 秒后自动重试，请检查电脑蓝牙已打开");
                    }
                }
                finally
                {
                    current?.Dispose();
                    lock (gate) { if (ReferenceEquals(transport, current)) transport = null; }
                }
                // Stop wakes this wait immediately, including when Bluetooth was unavailable at startup.
                if (!token.IsCancellationRequested && delay > TimeSpan.Zero) token.WaitHandle.WaitOne(delay);
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(cancellation, lifetime)) { cancellation = null; lifetime.Dispose(); }
            }
        }
    }
    private bool ReadConnection(IBluetoothConnection incoming, CancellationToken token)
    {
        var framer = new LineFramer(); byte[] buffer = new byte[4096];
        long lastValid = Stopwatch.GetTimestamp(), lastInvalidNotice = 0;
        int expired = 0; bool receivedValid = false;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(validStateTimeout.TotalMilliseconds / 4, 10, 1000));
        using var watchdog = new Timer(_ =>
        {
            // Only a successfully parsed media/idle frame refreshes this deadline, never raw bytes.
            if (!token.IsCancellationRequested && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastValid)) >= validStateTimeout
                && Interlocked.CompareExchange(ref expired, 1, 0) == 0) incoming.Close();
        }, null, interval, interval);
        try
        {
            while (!token.IsCancellationRequested && Volatile.Read(ref expired) == 0)
            {
                int count = incoming.Receive(buffer); if (count <= 0) break;
                foreach (string line in framer.Append(buffer, count))
                {
                    try
                    {
                        var snapshot = MediaSnapshot.Parse(line);
                        Interlocked.Exchange(ref lastValid, Stopwatch.GetTimestamp()); receivedValid = true;
                        SnapshotReceived?.Invoke(snapshot);
                    }
                    catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
                    {
                        // Avoid flooding the UI queue with notices from malformed frames.
                        if (lastInvalidNotice == 0 || Stopwatch.GetElapsedTime(lastInvalidNotice).TotalSeconds >= 1)
                        { lastInvalidNotice = Stopwatch.GetTimestamp(); Status?.Invoke("收到无效状态：" + ex.Message); }
                    }
                }
            }
        }
        catch (Exception ex) { if (!token.IsCancellationRequested && Volatile.Read(ref expired) == 0) Status?.Invoke("连接读取失败：" + ex.Message); }
        if (!token.IsCancellationRequested && Volatile.Read(ref expired) != 0) Status?.Invoke($"手机超过 {validStateTimeout.TotalSeconds:0.#} 秒未发送有效状态，已释放连接并等待重新连接");
        return receivedValid;
    }
    public void Stop()
    {
        lock (gate)
        {
            cancellation?.Cancel(); connection?.Close(); transport?.Close();
        }
    }
    public void Dispose() => Stop();
    internal static int AddressSize => NativeBluetoothTransport.AddressSize;
}
