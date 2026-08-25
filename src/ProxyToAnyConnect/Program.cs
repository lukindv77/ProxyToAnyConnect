using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ProxyToAnyConnect supports Windows only.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var configPath = args.Length > 0
                ? Path.GetFullPath(args[0])
                : Path.Combine(AppContext.BaseDirectory, "appsettings.json");

            var options = await AppOptions.LoadAsync(configPath, shutdown.Token);
            await using var rasConnectionManager = new RasConnectionManager(options.L2tp);

            try
            {
                var vpn = await rasConnectionManager.ConnectAsync(shutdown.Token);
                PrintVpnContext(vpn, rasConnectionManager.LastVerification);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                Console.Error.WriteLine($"Initial L2TP connection/verification failed: {ex.Message}");
                Console.Error.WriteLine("Proxy remains fail-closed and will retry L2TP verification on demand.");
            }

            var dnsResolver = new L2tpDnsResolver();
            var socketFactory = new L2tpSocketFactory(rasConnectionManager, dnsResolver);
            var proxyServer = new ProxyServer(options.Proxy, socketFactory);

            await proxyServer.RunAsync(shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void PrintVpnContext(VpnContext vpn, VpnVerificationResult? verification)
    {
        Console.WriteLine($"L2TP READY: {vpn.EntryName}");
        Console.WriteLine($"  IPv4: {vpn.LocalIPv4}");
        Console.WriteLine($"  Interface: {vpn.InterfaceName} (index {vpn.InterfaceIndex})");
        Console.WriteLine(
            vpn.DnsServers.Count == 0
                ? "  DNS: none"
                : $"  DNS: {string.Join(", ", vpn.DnsServers)}");

        if (verification is null)
        {
            return;
        }

        Console.WriteLine($"  Verification target IPv4: {verification.ProbeTargetIPv4}");
        if (verification.PublicIPv4ComparisonPerformed)
        {
            Console.WriteLine($"  Expected public IPv4: {verification.ExpectedPublicIPv4}");
            Console.WriteLine($"  Observed public IPv4: {verification.ObservedPublicIPv4}");
            Console.WriteLine("  Public IPv4 verification: PASSED");
        }
        else
        {
            Console.WriteLine("  Public IPv4 equality check: SKIPPED (publicAddress is a DNS name)");
            if (verification.ObservedPublicIPv4 is not null)
            {
                Console.WriteLine($"  Probe observed public IPv4: {verification.ObservedPublicIPv4}");
            }
        }
    }
}
