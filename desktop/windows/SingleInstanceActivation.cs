namespace LyricBridge;

// The mutex prevents another receiver; the auto-reset event only requests display of the existing window.
internal sealed class SingleInstanceActivation : IDisposable
{
    internal const string InstanceName = @"Local\LyricBridge.Desktop";
    internal const string TestPrefix = @"Local\LyricBridge.ActivationTest-";
    private readonly object gate = new();
    private readonly Mutex instance;
    private readonly EventWaitHandle request;
    private RegisteredWaitHandle? listener;
    private bool disposed;
    internal bool IsPrimary { get; }

    internal SingleInstanceActivation(string name = InstanceName)
    {
        // Create the signal before claiming the mutex: an early second launch cannot lose its request.
        request = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        try { instance = new Mutex(false, name, out bool created); IsPrimary = created; }
        catch { request.Dispose(); throw; }
    }

    internal void RequestActivation()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            request.Set();
        }
    }

    internal void Listen(Action activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can receive activation.");
            if (listener != null) throw new InvalidOperationException("Activation listener already registered.");
            listener = ThreadPool.RegisterWaitForSingleObject(request, (_, _) =>
            {
                lock (gate) { if (!disposed) activate(); }
            }, null, Timeout.Infinite, false);
        }
    }

    internal static int SignalIsolatedTest(string name)
    {
        if (!name.StartsWith(TestPrefix, StringComparison.Ordinal) || name.Length > 180) return 2;
        using var instance = new SingleInstanceActivation(name);
        if (instance.IsPrimary) return 3;
        instance.RequestActivation(); return 0;
    }

    public void Dispose()
    {
        RegisteredWaitHandle? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; previous = listener; listener = null;
        }
        previous?.Unregister(null);
        request.Dispose(); instance.Dispose();
    }
}
