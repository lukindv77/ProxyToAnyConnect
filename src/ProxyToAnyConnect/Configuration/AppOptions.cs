using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyToAnyConnect.Configuration;

internal sealed class AppOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [JsonPropertyName("proxies")]
    public List<ProxyOptions> Proxies { get; init; } =
    [
        new ProxyOptions
        {
            Id = "proxy-1",
            Name = "Proxy 1",
            ListenAddress = "127.0.0.1",
            ListenPort = 18080,
            VpnConnectionId = "vpn-1"
        }
    ];

    [JsonPropertyName("vpnConnections")]
    public List<L2tpOptions> VpnConnections { get; init; } =
    [
        new L2tpOptions
        {
            Id = "vpn-1",
            Name = "L2TP 1",
            Shared = false,
            Mode = L2tpConnectionMode.ExistingWindowsProfile,
            EntryName = "ProxyToAnyConnect-L2TP"
        }
    ];

    [JsonPropertyName("logging")]
    public LoggingOptions Logging { get; init; } = new();

    public static async Task<AppOptions> LoadAsync(string path, CancellationToken cancellationToken)
    {
        var options = await LoadForEditingAsync(path, cancellationToken);
        options.Validate();
        return options;
    }

    public static async Task<AppOptions> LoadForEditingAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new AppOptions();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppOptions>(stream, JsonOptions, cancellationToken)
            ?? new AppOptions();
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);

        // Never share one fixed .tmp name between save generations. A cancelled or
        // failed write owns only its unique sibling and can clean it without touching
        // another foreground save. Keeping the temporary file in the destination
        // directory also keeps the final replace on the same filesystem/volume.
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Cancellation before this publication boundary must leave the previous
            // complete configuration untouched. After this point the synchronous
            // same-volume rename owns publication and is intentionally not aborted.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            // Best effort only: never replace the primary serialization/move error
            // with a secondary stale-temp cleanup failure.
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal void Validate()
    {
        if (Proxies.Count == 0)
        {
            throw new InvalidOperationException("At least one proxy configuration is required.");
        }

        if (VpnConnections.Count == 0)
        {
            throw new InvalidOperationException("At least one L2TP connection configuration is required.");
        }

        EnsureUniqueIds(Proxies.Select(proxy => proxy.Id), "proxy");
        EnsureUniqueIds(VpnConnections.Select(vpn => vpn.Id), "L2TP connection");

        foreach (var proxy in Proxies)
        {
            ValidateProxy(proxy);
        }

        foreach (var vpn in VpnConnections)
        {
            ValidateL2tp(vpn);
        }

        var vpnById = VpnConnections.ToDictionary(vpn => vpn.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in Proxies)
        {
            if (!vpnById.ContainsKey(proxy.VpnConnectionId))
            {
                throw new InvalidOperationException(
                    $"Proxy '{proxy.Name}' references missing L2TP connection '{proxy.VpnConnectionId}'.");
            }
        }

        foreach (var dedicated in VpnConnections.Where(vpn => !vpn.Shared))
        {
            var referenceCount = Proxies.Count(proxy =>
                proxy.VpnConnectionId.Equals(dedicated.Id, StringComparison.OrdinalIgnoreCase));
            if (referenceCount > 1)
            {
                throw new InvalidOperationException(
                    $"L2TP connection '{dedicated.Name}' is dedicated but referenced by {referenceCount} proxies. " +
                    "Mark it as shared or assign a separate L2TP connection to each proxy.");
            }
        }

        // Compare the actual IPv4 endpoint rather than the user-provided text.
        // IPAddress accepts legacy equivalent IPv4 forms (for example 127.1 ==
        // 127.0.0.1), and those must never bypass the listener collision guard.
        var duplicateEndpoint = Proxies
            .Where(proxy => proxy.Enabled)
            .GroupBy(proxy =>
                (Address: IPAddress.Parse(proxy.ListenAddress), Port: proxy.ListenPort))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEndpoint is not null)
        {
            throw new InvalidOperationException(
                $"Multiple enabled proxies use the same listener endpoint " +
                $"{duplicateEndpoint.Key.Address}:{duplicateEndpoint.Key.Port}.");
        }

        if (!string.IsNullOrWhiteSpace(Logging.Directory) &&
            Logging.Directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidOperationException("logging.directory contains invalid path characters.");
        }

        if (Logging.RetentionDays is < 1 or > 3650)
        {
            throw new InvalidOperationException("logging.retentionDays must be between 1 and 3650.");
        }
    }

    private static void ValidateProxy(ProxyOptions proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy.Name))
        {
            throw new InvalidOperationException($"Proxy '{proxy.Id}' must have a name.");
        }

        if (string.IsNullOrWhiteSpace(proxy.VpnConnectionId))
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' must reference an L2TP connection.");
        }

        if (!IPAddress.TryParse(proxy.ListenAddress, out var listenAddress) ||
            listenAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' listenAddress must be an IPv4 address.");
        }

        if (!IsLocalIPv4(listenAddress))
        {
            throw new InvalidOperationException(
                $"Proxy '{proxy.Name}' listenAddress '{listenAddress}' is not assigned to this computer.");
        }

        if (proxy.ListenPort is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' listenPort must be between 1 and 65535.");
        }

        if (proxy.MaxConcurrentConnections is < 1 or > 100000)
        {
            throw new InvalidOperationException(
                $"Proxy '{proxy.Name}' maxConcurrentConnections must be between 1 and 100000.");
        }

        if (proxy.MaxHeaderBytes is < 4096 or > 1024 * 1024)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' maxHeaderBytes is outside the allowed range.");
        }

        if (proxy.ClientHeaderTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' clientHeaderTimeoutSeconds must be between 1 and 300.");
        }

        if (proxy.OutboundConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' outboundConnectTimeoutSeconds must be between 1 and 300.");
        }

        if (proxy.DnsTimeoutMilliseconds is < 250 or > 60000)
        {
            throw new InvalidOperationException($"Proxy '{proxy.Name}' dnsTimeoutMilliseconds must be between 250 and 60000.");
        }
    }

    private static void ValidateL2tp(L2tpOptions l2tp)
    {
        if (string.IsNullOrWhiteSpace(l2tp.Name))
        {
            throw new InvalidOperationException($"L2TP connection '{l2tp.Id}' must have a name.");
        }

        if (l2tp.MonitorIntervalMilliseconds is < 250 or > 300000)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' monitorIntervalMilliseconds must be between 250 and 300000.");
        }

        if (l2tp.RouteMonitorIntervalMilliseconds is < 250 or > 300000)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' routeMonitorIntervalMilliseconds must be between 250 and 300000.");
        }

        if (l2tp.ReconnectCooldownMilliseconds is < 0 or > 3600000)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' reconnectCooldownMilliseconds must be between 0 and 3600000.");
        }

        ValidateVerification(l2tp);
        ValidateKeepalive(l2tp);

        if (l2tp.Mode == L2tpConnectionMode.ExistingWindowsProfile)
        {
            if (string.IsNullOrWhiteSpace(l2tp.EntryName))
            {
                throw new InvalidOperationException(
                    $"L2TP connection '{l2tp.Name}' requires entryName in ExistingWindowsProfile mode.");
            }
            return;
        }

        if (l2tp.Mode != L2tpConnectionMode.CustomEphemeral)
        {
            throw new InvalidOperationException($"Unsupported L2TP mode '{l2tp.Mode}'.");
        }

        if (string.IsNullOrWhiteSpace(l2tp.ServerAddress))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' requires serverAddress in CustomEphemeral mode.");
        }

        if (!l2tp.UseWindowsCredentials && string.IsNullOrWhiteSpace(l2tp.UserName))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' requires userName when Windows credentials are disabled.");
        }

        if (!l2tp.UseWindowsCredentials && string.IsNullOrWhiteSpace(l2tp.ProtectedPassword))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' requires protectedPassword when Windows credentials are disabled.");
        }

        if (l2tp.Authentication == L2tpAuthenticationMode.PreSharedKey &&
            string.IsNullOrWhiteSpace(l2tp.ProtectedPreSharedKey))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' requires protectedPreSharedKey for PSK authentication.");
        }
    }

    private static void ValidateVerification(L2tpOptions l2tp)
    {
        var verification = l2tp.Verification;
        if (string.IsNullOrWhiteSpace(verification.PublicAddress))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' verification.publicAddress is required.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProbeHost))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' verification.probeHost is required.");
        }

        if (verification.ProbePort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' verification.probePort must be between 1 and 65535.");
        }

        if (verification.TimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' verification.timeoutSeconds must be between 1 and 300.");
        }

        if (verification.MaxResponseBytes is < VerificationOptions.MinResponseBytes or > VerificationOptions.MaxAllowedResponseBytes)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' verification.maxResponseBytes must be between " +
                $"{VerificationOptions.MinResponseBytes} and {VerificationOptions.MaxAllowedResponseBytes}.");
        }
    }

    private static void ValidateKeepalive(L2tpOptions l2tp)
    {
        var keepalive = l2tp.Keepalive;
        if (keepalive.IntervalSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' keepalive.intervalSeconds must be between 1 and 3600.");
        }

        if (keepalive.TimeoutMilliseconds is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' keepalive.timeoutMilliseconds must be between 100 and 60000.");
        }

        if (keepalive.FailureThreshold is < 1 or > 100)
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' keepalive.failureThreshold must be between 1 and 100.");
        }

        if (keepalive.Mode == L2tpKeepaliveMode.CustomIPv4 &&
            (!IPAddress.TryParse(keepalive.CustomIPv4, out var target) ||
             target.AddressFamily != AddressFamily.InterNetwork))
        {
            throw new InvalidOperationException(
                $"L2TP connection '{l2tp.Name}' keepalive.customIPv4 must be an IPv4 address in CustomIPv4 mode.");
        }
    }

    private static void EnsureUniqueIds(IEnumerable<string> ids, string entityName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"Every {entityName} must have a non-empty id.");
            }

            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Duplicate {entityName} id '{id}'.");
            }
        }
    }

    private static bool IsLocalIPv4(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    unicast.Address.Equals(address))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

internal sealed class LoggingOptions
{
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = "logs";

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = 30;

    [JsonPropertyName("consoleJson")]
    public bool ConsoleJson { get; set; }
}

internal sealed class ProxyOptions
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Proxy";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; set; } = "127.0.0.1";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; set; } = 18080;

    [JsonPropertyName("vpnConnectionId")]
    public string VpnConnectionId { get; set; } = "vpn-1";

    [JsonPropertyName("maxConcurrentConnections")]
    public int MaxConcurrentConnections { get; set; } = 512;

    [JsonPropertyName("maxHeaderBytes")]
    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    [JsonPropertyName("clientHeaderTimeoutSeconds")]
    public int ClientHeaderTimeoutSeconds { get; set; } = 15;

    [JsonPropertyName("outboundConnectTimeoutSeconds")]
    public int OutboundConnectTimeoutSeconds { get; set; } = 20;

    [JsonPropertyName("dnsTimeoutMilliseconds")]
    public int DnsTimeoutMilliseconds { get; set; } = 5000;
}

internal enum L2tpConnectionMode
{
    ExistingWindowsProfile,
    CustomEphemeral
}

internal enum L2tpAuthenticationMode
{
    PreSharedKey,
    MachineCertificate
}

internal enum L2tpEncryptionMode
{
    Optional,
    Required,
    Maximum
}

[Flags]
internal enum L2tpAuthProtocols
{
    None = 0,
    Pap = 1,
    Chap = 2,
    MsChapV2 = 4
}

internal enum L2tpKeepaliveMode
{
    Off,
    VpnServerInternalIPv4,
    CustomIPv4
}

internal sealed class L2tpOptions
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "L2TP";

    [JsonPropertyName("shared")]
    public bool Shared { get; set; }

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public L2tpConnectionMode Mode { get; set; } = L2tpConnectionMode.ExistingWindowsProfile;

    [JsonPropertyName("entryName")]
    public string EntryName { get; set; } = "ProxyToAnyConnect-L2TP";

    [JsonPropertyName("serverAddress")]
    public string ServerAddress { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("useWindowsCredentials")]
    public bool UseWindowsCredentials { get; set; }

    [JsonPropertyName("protectedPassword")]
    public string ProtectedPassword { get; set; } = string.Empty;

    [JsonPropertyName("authentication")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public L2tpAuthenticationMode Authentication { get; set; } = L2tpAuthenticationMode.PreSharedKey;

    [JsonPropertyName("protectedPreSharedKey")]
    public string ProtectedPreSharedKey { get; set; } = string.Empty;

    [JsonPropertyName("encryption")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public L2tpEncryptionMode Encryption { get; set; } = L2tpEncryptionMode.Required;

    [JsonPropertyName("authProtocols")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public L2tpAuthProtocols AuthProtocols { get; set; } = L2tpAuthProtocols.MsChapV2;

    [JsonPropertyName("monitorIntervalMilliseconds")]
    public int MonitorIntervalMilliseconds { get; set; } = 1000;

    [JsonPropertyName("routeMonitorIntervalMilliseconds")]
    public int RouteMonitorIntervalMilliseconds { get; set; } = 5000;

    [JsonPropertyName("reconnectCooldownMilliseconds")]
    public int ReconnectCooldownMilliseconds { get; set; } = 5000;

    [JsonPropertyName("verification")]
    public VerificationOptions Verification { get; set; } = new();

    [JsonPropertyName("keepalive")]
    public KeepaliveOptions Keepalive { get; set; } = new();
}

internal sealed class VerificationOptions
{
    internal const int MinResponseBytes = 1024;
    internal const int MaxAllowedResponseBytes = 1024 * 1024;

    [JsonPropertyName("publicAddress")]
    public string PublicAddress { get; set; } = "vpn.example.com";

    [JsonPropertyName("expectedPublicIPv4")]
    public string ExpectedPublicIPv4 { get; set; } = string.Empty;

    [JsonPropertyName("probeHost")]
    public string ProbeHost { get; set; } = "api.ipify.org";

    [JsonPropertyName("probePort")]
    public int ProbePort { get; set; } = 443;

    [JsonPropertyName("probePath")]
    public string ProbePath { get; set; } = "/";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 10;

    [JsonPropertyName("maxResponseBytes")]
    public int MaxResponseBytes { get; set; } = 64 * 1024;
}

internal sealed class KeepaliveOptions
{
    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public L2tpKeepaliveMode Mode { get; set; } = L2tpKeepaliveMode.Off;

    [JsonPropertyName("customIPv4")]
    public string CustomIPv4 { get; set; } = string.Empty;

    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; } = 30;

    [JsonPropertyName("timeoutMilliseconds")]
    public int TimeoutMilliseconds { get; set; } = 1500;

    [JsonPropertyName("failureThreshold")]
    public int FailureThreshold { get; set; } = 3;
}
