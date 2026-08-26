using System.Net;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VpnContextLifetimeSelfTests
{
    public static int Run()
    {
        try
        {
            ReleasesContextAfterLastOutboundSession();
            ReleasedContextsBecomeCollectibleAcrossLongReconnectChurn();
            Console.WriteLine("PASS: VPN contexts release deterministic resources and become collectible after reconnect churn");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: VPN context lifetime regression: {ex}");
            return 1;
        }
    }

    private static void ReleasesContextAfterLastOutboundSession()
    {
        const int sessionCount = 256;
        var context = CreateContext(1);

        for (var i = 0; i < sessionCount; i++)
        {
            if (!context.TryAcquireConnectionReference())
            {
                throw new InvalidOperationException($"Unable to acquire session reference {i}.");
            }
        }

        if (context.ReferenceCount != sessionCount + 1)
        {
            throw new InvalidOperationException(
                $"Expected {sessionCount + 1} references before disconnect, got {context.ReferenceCount}.");
        }

        context.MarkDisconnected();

        if (context.IsAlive)
        {
            throw new InvalidOperationException("Disconnected context still reports IsAlive=true.");
        }

        if (context.IsDisposed)
        {
            throw new InvalidOperationException("Context disposed before active outbound sessions released their references.");
        }

        if (context.ReferenceCount != sessionCount)
        {
            throw new InvalidOperationException(
                $"Manager owner reference was not released on disconnect. Remaining={context.ReferenceCount}.");
        }

        if (context.TryAcquireConnectionReference())
        {
            throw new InvalidOperationException("A disconnected context accepted a new outbound-session reference.");
        }

        for (var i = 0; i < sessionCount; i++)
        {
            context.ReleaseConnectionReference();
        }

        if (!context.IsDisposed || context.ReferenceCount != 0)
        {
            throw new InvalidOperationException(
                $"Context did not release its CTS after the last session. Disposed={context.IsDisposed}, refs={context.ReferenceCount}.");
        }

        // Must remain idempotent after the deterministic final release.
        context.Dispose();
    }

    private static void ReleasedContextsBecomeCollectibleAcrossLongReconnectChurn()
    {
        var references = CreateAndReleaseContexts(2000);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var alive = references.Count(reference => reference.IsAlive);
        if (alive != 0)
        {
            throw new InvalidOperationException(
                $"{alive} of {references.Length} released VpnContext instances remained strongly reachable after forced test collection.");
        }
    }

    private static WeakReference[] CreateAndReleaseContexts(int count)
    {
        var references = new WeakReference[count];
        for (var i = 0; i < count; i++)
        {
            var context = CreateContext(i + 10);
            references[i] = new WeakReference(context);
            context.MarkDisconnected();

            if (!context.IsDisposed)
            {
                throw new InvalidOperationException($"Context {i} did not dispose after owner-only disconnect.");
            }
        }

        return references;
    }

    private static VpnContext CreateContext(int interfaceIndex) =>
        new(
            "test-entry",
            IPAddress.Parse("10.20.30.40"),
            new VpnInterfaceInfo(
                "test-interface",
                "test-interface",
                interfaceIndex,
                [IPAddress.Parse("10.20.30.1")]),
            IPAddress.Parse("10.20.30.254"));
}
