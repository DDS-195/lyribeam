using System.Diagnostics;
using System.Reflection;

namespace LyricBridge;

internal static class ActivationRegressionTests
{
    public static async Task<Dictionary<string, bool>> RunAsync(string? root = null)
    {
        var checks = new Dictionary<string, bool>();
        static string Name() => SingleInstanceActivation.TestPrefix + Guid.NewGuid().ToString("N");
        static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        static async Task<bool> Arrives(Task<bool> value) { try { return await value.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { return false; } }

        string name = Name();
        using (var primary = new SingleInstanceActivation(name))
        using (var secondary = new SingleInstanceActivation(name))
            checks["Second launch is a client and never a second primary"] = primary.IsPrimary && !secondary.IsPrimary;

        name = Name();
        using (var primary = new SingleInstanceActivation(name))
        {
            using (var early = new SingleInstanceActivation(name)) early.RequestActivation();
            var received = Signal(); primary.Listen(() => received.TrySetResult(true));
            checks["A request before listener registration is preserved"] = await Arrives(received.Task);
        }

        name = Name();
        using (var primary = new SingleInstanceActivation(name))
        using (var secondary = new SingleInstanceActivation(name))
        {
            var first = Signal(); var second = Signal(); int count = 0;
            primary.Listen(() => { if (Interlocked.Increment(ref count) == 1) first.TrySetResult(true); else second.TrySetResult(true); });
            secondary.RequestActivation(); bool one = await Arrives(first.Task);
            secondary.RequestActivation(); bool two = await Arrives(second.Task);
            checks["Separate desktop launches can repeatedly restore one instance"] = one && two && count == 2;
        }

        name = Name(); int afterDispose = 0;
        using (var primary = new SingleInstanceActivation(name))
        using (var secondary = new SingleInstanceActivation(name))
        {
            primary.Listen(() => Interlocked.Increment(ref afterDispose));
            primary.Dispose(); secondary.RequestActivation(); await Task.Delay(100);
            checks["Disposed activation listener cannot queue another restore"] = afterDispose == 0;
        }

        string isolatedA = Name(), isolatedB = Name(); int crossSignals = 0;
        using (var a = new SingleInstanceActivation(isolatedA))
        using (var b = new SingleInstanceActivation(isolatedB))
        using (var client = new SingleInstanceActivation(isolatedA))
        {
            var received = Signal(); a.Listen(() => received.TrySetResult(true)); b.Listen(() => Interlocked.Increment(ref crossSignals));
            client.RequestActivation(); bool one = await Arrives(received.Task); await Task.Delay(80);
            checks["Independent test or preview identities do not cross-activate"] = one && crossSignals == 0;
        }

        name = Name();
        using (var primary = new SingleInstanceActivation(name))
        {
            var received = Signal(); primary.Listen(() => received.TrySetResult(true));
            string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
            var launch = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                launch.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            launch.ArgumentList.Add("--activation-test-signal"); launch.ArgumentList.Add(name);
            using var child = Process.Start(launch)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
            checks["A separate process signals the existing instance without UI or Bluetooth"] = child.ExitCode == 0 && await Arrives(received.Task);
        }

        name = Name();
        using (var first = new SingleInstanceActivation(name)) { }
        using (var next = new SingleInstanceActivation(name))
            checks["Closing all handles permits the next normal launch"] = next.IsPrimary;
        checks["Test-only signal cannot target the production receiver"] = SingleInstanceActivation.SignalIsolatedTest(SingleInstanceActivation.InstanceName) == 2;
        return checks;
    }

}
