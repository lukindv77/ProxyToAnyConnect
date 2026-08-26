using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect;

internal static class Program
{
    private const string VerifyOnlyArgument = "--verify-only";
    private const string HelpArgument = "--help";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ProxyToAnyConnect supports Windows only.");
            return 2;
        }

        if (args.Any(argument => argument.Equals(HelpArgument, StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return 0;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            var verifyOnly = args.Any(argument =>
                argument.Equals(VerifyOnlyArgument, StringComparison.OrdinalIgnoreCase));

            var configArgument = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
            var configPath = configArgument is not null
                ? Path.GetFullPath(configArgument)
                : Path.Combine(AppContext.BaseDirectory, "appsettings.json");

            var unknownArguments = args.Where(argument =>
                    argument.StartsWith("--", StringComparison.Ordinal) &&
                    !argument.Equals(VerifyOnlyArgument, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (unknownArguments.Length > 0)
            {
                Console.Error.WriteLine($"Unknown argument(s): {string.Join(", ", unknownArguments)}");
                PrintUsage();
                return 2;
            }

            var options = await AppOptions.LoadAsync(configPath, shutdown.Token);
            await using var rasConnectionManager = new RasConnectionManager(options.L2tp);

            try
            {
                var vpn = await rasConnectionManager.ConnectAsync(shutdown.Token);
                PrintVpnContext(vpn, rasConnectionManager.LastVerification);

                if (verifyOnly)
                {
                    Console.WriteLine("Verification-only mode completed successfully. Proxy listener was not started.");
                    return 0;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
            {
                Console.Error.WriteLine($"Initial L2TP connection/verification failed: {ex.Message}");

                if (verifyOnly)
                {
                    Console.Error.WriteLine("Verification-only mode failed. Proxy listener was not started.");
                    return 3;
                }

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

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  ProxyToAnyConnect.exe [appsettings.json]");
        Console.WriteLine("  ProxyToAnyConnect.exe [appsettings.json] --verify-only");
        Console.WriteLine();
        Console.WriteLine("--verify-only establishes L2TP, runs all fail-closed verification guards,");
        Console.WriteLine("prints the verified VPN context, and exits without starting the proxy listener.");
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
