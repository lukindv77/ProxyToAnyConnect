using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class IcmpBoundPingSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await LoopbackProbeCompletesAndReleasesNativeOwnershipAsync();
            await PreCancelledProbeDoesNotAcquireNativeOwnershipAsync();
            await CancellationDrainsObservedOutstandingProbeAsync();

            Console.WriteLine(
                "PASS: bound ICMP keepalive uses async native completion with zero residual operations");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: bound ICMP keepalive lifecycle regression: {ex}");
            return 1;
        }
    }

    private static async Task LoopbackProbeCompletesAndReleasesNativeOwnershipAsync()
    {
        AssertNoActiveOperations("before loopback probe");

        var result = await IcmpBoundPing.SendAsync(
            IPAddress.Loopback,
            IPAddress.Loopback,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        if (!result.Success || result.RoundTripTime is null)
        {
            throw new InvalidOperationException(
                $"Windows loopback ICMP probe failed: error={result.ErrorCode}.");
        }

        AssertNoActiveOperations("after loopback probe");
    }

    private static async Task PreCancelledProbeDoesNotAcquireNativeOwnershipAsync()
    {
        AssertNoActiveOperations("before pre-cancelled probe");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            _ = await IcmpBoundPing.SendAsync(
                IPAddress.Loopback,
                IPAddress.Loopback,
                TimeSpan.FromSeconds(2),
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AssertNoActiveOperations("after pre-cancelled probe");
            return;
        }

        throw new InvalidOperationException("Pre-cancelled ICMP probe did not propagate cancellation.");
    }

    private static async Task CancellationDrainsObservedOutstandingProbeAsync()
    {
        var source = FindNonLoopbackLocalIPv4();
        if (source is null)
        {
            Console.WriteLine(
                "INFO: no non-loopback IPv4 is available for mid-flight ICMP cancellation coverage.");
            return;
        }

        var destinations = new[]
        {
            IPAddress.Parse("192.0.2.1"),
            IPAddress.Parse("198.51.100.1"),
            IPAddress.Parse("203.0.113.1")
        };

        foreach (var destination in destinations)
        {
            AssertNoActiveOperations($"before cancellation probe to {destination}");
            using var cancellation = new CancellationTokenSource();
            var probe = IcmpBoundPing.SendAsync(
                source,
                destination,
                TimeSpan.FromMilliseconds(750),
                cancellation.Token);

            var observedOutstanding = await WaitForActiveOperationAsync(probe);
            if (!observedOutstanding)
            {
                _ = await probe;
                AssertNoActiveOperations($"after immediate probe to {destination}");
                continue;
            }

            cancellation.Cancel();
            try
            {
                _ = await probe;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AssertNoActiveOperations($"after cancelled probe to {destination}");
                return;
            }

            throw new InvalidOperationException(
                $"Outstanding ICMP probe to {destination} did not propagate caller cancellation.");
        }

        Console.WriteLine(
            "INFO: TEST-NET ICMP probes completed immediately on this runner; " +
            "loopback and pre-cancel ownership checks still ran.");
    }

    private static async Task<bool> WaitForActiveOperationAsync(Task probe)
    {
        var deadline = Environment.TickCount64 + 250;
        while (Environment.TickCount64 < deadline)
        {
            if (IcmpBoundPing.ActiveNativeOperations > 0)
            {
                return true;
            }

            if (probe.IsCompleted)
            {
                return false;
            }

            await Task.Delay(1);
        }

        return IcmpBoundPing.ActiveNativeOperations > 0;
    }

    private static IPAddress? FindNonLoopbackLocalIPv4()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address));
    }

    private static void AssertNoActiveOperations(string phase)
    {
        var active = IcmpBoundPing.ActiveNativeOperations;
        if (active != 0)
        {
            throw new InvalidOperationException(
                $"Expected zero active native ICMP operations {phase}; observed {active}.");
        }
    }
}
