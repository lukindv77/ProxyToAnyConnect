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

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory);
        var temporaryPath = fullPath + ".tmp";

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, fullPath, overwrite: true);
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

        var duplicateEndpoint = Proxies
            .Where(proxy => proxy.Enabled)
            .GroupBy(proxy => $"{proxy.ListenAddress}:{proxy.ListenPort}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEndpoint is not null)
        {
            throw new InvalidOperationException(
                $"Multiple enabled proxies use the same listener endpoint {duplicateEndpoint.Key}.");
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

        if (l2tp.MonitorIntervalMilliseconds is < 250 or > 60000)
        {
            throw new InvalidOperationException($"L2TP '{l2tp.Name}' monitorIntervalMilliseconds is outside the allowed range.");
        }

        if (l2tp.RouteMonitorIntervalMilliseconds is < 1000 or > 300000)
        {
            throw new InvalidOperationException($"L2TP '{l2tp.Name}' routeMonitorIntervalMilliseconds is outside the allowed range.");
        }

        if (l2tp.ReconnectCooldownMilliseconds is < 0 or > 300000)
        {
            throw new InvalidOperationException($"L2TP '{l2tp.Name}' reconnectCooldownMilliseconds is outside the allowed range.");
        }

        switch (l2tp.Mode)
        {
            case L2tpConnectionMode.ExistingWindowsProfile:
                if (string.IsNullOrWhiteSpace(l2tp.EntryName))
                {
                    throw new InvalidOperationException($"L2TP '{l2tp.Name}' entryName is required.");
                }
                break;
            case L2tpConnectionMode.CustomEphemeral:
                ValidateCustomL2tp(l2tp.Name, l2tp.Custom);
                break;
            default:
                throw new InvalidOperationException($"Unsupported l2tp.mode '{l2tp.Mode}'.");
        }

        ValidateVerification(l2tp.Name, l2tp.Verification);
        ValidateKeepalive(l2tp.Name, l2tp.Keepalive);
    }

    private static void ValidateCustomL2tp(string name, CustomL2tpOptions custom)
    {
        if (string.IsNullOrWhiteSpace(custom.ServerAddress) ||
            (!IPAddress.TryParse(custom.ServerAddress, out _) &&
             Uri.CheckHostName(custom.ServerAddress) != UriHostNameType.Dns))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom serverAddress must be an IP address or DNS host name.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.UserName))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom userName is required.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.ProtectedPassword))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom password is required.");
        }

        if (custom.IpsecAuthentication == L2tpIpsecAuthentication.PreSharedKey &&
            string.IsNullOrWhiteSpace(custom.ProtectedPreSharedKey))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom pre-shared key is required.");
        }

        if (!custom.AllowPap && !custom.AllowChap && !custom.AllowMsChapV2)
        {
            throw new InvalidOperationException($"L2TP '{name}' must enable at least one PPP authentication protocol.");
        }
    }

    private static void ValidateVerification(string name, VerificationOptions verification)
    {
        if (string.IsNullOrWhiteSpace(verification.PublicAddress))
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.publicAddress is required.");
        }

        if (IPAddress.TryParse(verification.PublicAddress, out var publicIp))
        {
            if (publicIp.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException(
                    $"L2TP '{name}' verification.publicAddress supports IPv4 or a domain name only.");
            }
        }
        else if (Uri.CheckHostName(verification.PublicAddress) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' verification.publicAddress must be an IPv4 address or DNS host name.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProbeHost) ||
            Uri.CheckHostName(verification.ProbeHost) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.probeHost must be a DNS host name.");
        }

        if (verification.ProbePort is < 1 or > 65535)
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.probePort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProbePath) ||
            !verification.ProbePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.probePath must start with '/'.");
        }

        if (verification.TimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.timeoutSeconds must be between 1 and 60.");
        }
    }

    private static void ValidateKeepalive(string name, KeepaliveOptions keepalive)
    {
        if (keepalive.IntervalSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException($"L2TP '{name}' keepalive.intervalSeconds must be between 1 and 3600.");
        }

        if (keepalive.TimeoutMilliseconds is < 100 or > 60000)
        {
            throw new InvalidOperationException($"L2TP '{name}' keepalive.timeoutMilliseconds must be between 100 and 60000.");
        }

        if (keepalive.FailureThreshold is < 1 or > 100)
        {
            throw new InvalidOperationException($"L2TP '{name}' keepalive.failureThreshold must be between 1 and 100.");
        }

        if (keepalive.Mode == L2tpKeepaliveMode.CustomIPv4 &&
            (!IPAddress.TryParse(keepalive.CustomIPv4, out var address) ||
             address.AddressFamily != AddressFamily.InterNetwork))
        {
            throw new InvalidOperationException($"L2TP '{name}' keepalive.customIPv4 must be an IPv4 address.");
        }
    }

    private static void EnsureUniqueIds(IEnumerable<string> ids, string kind)
    {
        var values = ids.ToArray();
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"Every {kind} must have a non-empty id.");
        }

        var duplicate = values
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Duplicate {kind} id '{duplicate.Key}'.");
        }
    }

    private static bool IsLocalIPv4(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        return NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Any(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                            unicast.Address.Equals(address));
    }
}

internal sealed class ProxyOptions
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = "Proxy";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; init; } = "127.0.0.1";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; init; } = 18080;

    [JsonPropertyName("vpnConnectionId")]
    public string VpnConnectionId { get; init; } = string.Empty;

    [JsonPropertyName("maxHeaderBytes")]
    public int MaxHeaderBytes { get; init; } = 65536;

    [JsonPropertyName("clientHeaderTimeoutSeconds")]
    public int ClientHeaderTimeoutSeconds { get; init; } = 15;

    [JsonPropertyName("outboundConnectTimeoutSeconds")]
    public int OutboundConnectTimeoutSeconds { get; init; } = 15;

    [JsonPropertyName("dnsTimeoutMilliseconds")]
    public int DnsTimeoutMilliseconds { get; init; } = 3000;
}

[JsonConverter(typeof(JsonStringEnumConverter<L2tpConnectionMode>))]
internal enum L2tpConnectionMode
{
    ExistingWindowsProfile,
    CustomEphemeral
}

internal sealed class L2tpOptions
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = "L2TP";

    [JsonPropertyName("shared")]
    public bool Shared { get; init; }

    [JsonPropertyName("mode")]
    public L2tpConnectionMode Mode { get; init; } = L2tpConnectionMode.ExistingWindowsProfile;

    [JsonPropertyName("entryName")]
    public string EntryName { get; init; } = "ProxyToAnyConnect-L2TP";

    [JsonPropertyName("monitorIntervalMilliseconds")]
    public int MonitorIntervalMilliseconds { get; init; } = 1000;

    [JsonPropertyName("routeMonitorIntervalMilliseconds")]
    public int RouteMonitorIntervalMilliseconds { get; init; } = 5000;

    [JsonPropertyName("reconnectCooldownMilliseconds")]
    public int ReconnectCooldownMilliseconds { get; init; } = 5000;

    [JsonPropertyName("verification")]
    public VerificationOptions Verification { get; init; } = new();

    [JsonPropertyName("keepalive")]
    public KeepaliveOptions Keepalive { get; init; } = new();

    [JsonPropertyName("custom")]
    public CustomL2tpOptions Custom { get; init; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<L2tpIpsecAuthentication>))]
internal enum L2tpIpsecAuthentication
{
    PreSharedKey,
    MachineCertificate
}

[JsonConverter(typeof(JsonStringEnumConverter<L2tpEncryptionMode>))]
internal enum L2tpEncryptionMode
{
    None,
    Optional,
    Required,
    Maximum
}

[JsonConverter(typeof(JsonStringEnumConverter<L2tpKeepaliveMode>))]
internal enum L2tpKeepaliveMode
{
    Off,
    VpnServerInternalIPv4,
    CustomIPv4
}

internal sealed class KeepaliveOptions
{
    [JsonPropertyName("mode")]
    public L2tpKeepaliveMode Mode { get; init; } = L2tpKeepaliveMode.Off;

    [JsonPropertyName("customIPv4")]
    public string CustomIPv4 { get; init; } = string.Empty;

    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; init; } = 10;

    [JsonPropertyName("timeoutMilliseconds")]
    public int TimeoutMilliseconds { get; init; } = 2000;

    [JsonPropertyName("failureThreshold")]
    public int FailureThreshold { get; init; } = 3;
}

internal sealed class CustomL2tpOptions
{
    [JsonPropertyName("serverAddress")]
    public string ServerAddress { get; init; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = string.Empty;

    [JsonPropertyName("useCurrentWindowsCredentials")]
    public bool UseCurrentWindowsCredentials { get; init; }

    [JsonPropertyName("protectedPassword")]
    public string ProtectedPassword { get; init; } = string.Empty;

    [JsonPropertyName("ipsecAuthentication")]
    public L2tpIpsecAuthentication IpsecAuthentication { get; init; } = L2tpIpsecAuthentication.PreSharedKey;

    [JsonPropertyName("protectedPreSharedKey")]
    public string ProtectedPreSharedKey { get; init; } = string.Empty;

    [JsonPropertyName("encryption")]
    public L2tpEncryptionMode Encryption { get; init; } = L2tpEncryptionMode.Required;

    [JsonPropertyName("allowPap")]
    public bool AllowPap { get; init; }

    [JsonPropertyName("allowChap")]
    public bool AllowChap { get; init; }

    [JsonPropertyName("allowMsChapV2")]
    public bool AllowMsChapV2 { get; init; } = true;
}

internal sealed class VerificationOptions
{
    [JsonPropertyName("publicAddress")]
    public string PublicAddress { get; init; } = string.Empty;

    [JsonPropertyName("probeHost")]
    public string ProbeHost { get; init; } = "api.ipify.org";

    [JsonPropertyName("probePort")]
    public int ProbePort { get; init; } = 443;

    [JsonPropertyName("probePath")]
    public string ProbePath { get; init; } = "/";

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; init; } = 10;

    [JsonPropertyName("maxResponseBytes")]
    public int MaxResponseBytes { get; init; } = 65536;
}

internal sealed class LoggingOptions
{
    // Empty means AppContext.BaseDirectory (the directory containing/running the utility).
    [JsonPropertyName("directory")]
    public string Directory { get; init; } = string.Empty;

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; init; } = 30;

    [JsonPropertyName("consoleJson")]
    public bool ConsoleJson { get; init; }
}
