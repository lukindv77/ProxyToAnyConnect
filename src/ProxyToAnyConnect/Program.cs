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

            // Dial immediately at startup. Failure is fail-closed for proxy traffic but does
            // not terminate the local proxy: a later request will attempt RasDial again.
            try
            {
                var vpn = await rasConnectionManager.ConnectAsync(shutdown.Token);
                PrintVpnContext(vpn);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Console.Error.WriteLine($"Initial L2TP connection failed: {ex.Message}");
                Console.Error.WriteLine("Proxy will remain fail-closed and retry L2TP on demand.");
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

    private static void PrintVpnContext(VpnContext vpn)
    {
        Console.WriteLine($"L2TP connected: {vpn.EntryName}");
        Console.WriteLine($"  IPv4: {vpn.LocalIPv4}");
        Console.WriteLine($"  Interface: {vpn.InterfaceName} (index {vpn.InterfaceIndex})");
        Console.WriteLine(
            vpn.DnsServers.Count == 0
                ? "  DNS: none"
                : $"  DNS: {string.Join(", ", vpn.DnsServers)}");
    }
}
