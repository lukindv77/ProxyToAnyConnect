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

    [JsonPropertyName("proxy")]
    public ProxyOptions Proxy { get; init; } = new();

    [JsonPropertyName("l2tp")]
    public L2tpOptions L2tp { get; init; } = new();

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
                         bufferSize: 16 * 1024,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, this, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    internal void Validate()
    {
        ValidateProxy();
        ValidateL2tp();

        if (!string.IsNullOrWhiteSpace(Logging.FilePath) &&
            Logging.FilePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidOperationException("logging.filePath contains invalid path characters.");
        }
    }

    private void ValidateProxy()
    {
        if (!IPAddress.TryParse(Proxy.ListenAddress, out var listenAddress) ||
            listenAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException("proxy.listenAddress must be an IPv4 address.");
        }

        if (!IsLocalIPv4(listenAddress))
        {
            throw new InvalidOperationException(
                $"proxy.listenAddress '{listenAddress}' is not assigned to this computer.");
        }

        if (Proxy.ListenPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("proxy.listenPort must be between 1 and 65535.");
        }

        if (Proxy.MaxHeaderBytes is < 4096 or > 1024 * 1024)
        {
            throw new InvalidOperationException("proxy.maxHeaderBytes is outside the allowed range.");
        }

        if (Proxy.ClientHeaderTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("proxy.clientHeaderTimeoutSeconds must be between 1 and 300.");
        }

        if (Proxy.OutboundConnectTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("proxy.outboundConnectTimeoutSeconds must be between 1 and 300.");
        }

        if (Proxy.DnsTimeoutMilliseconds is < 250 or > 60000)
        {
            throw new InvalidOperationException("proxy.dnsTimeoutMilliseconds must be between 250 and 60000.");
        }
    }

    private void ValidateL2tp()
    {
        if (L2tp.MonitorIntervalMilliseconds is < 250 or > 60000)
        {
            throw new InvalidOperationException("l2tp.monitorIntervalMilliseconds is outside the allowed range.");
        }

        if (L2tp.RouteMonitorIntervalMilliseconds is < 1000 or > 300000)
        {
            throw new InvalidOperationException("l2tp.routeMonitorIntervalMilliseconds is outside the allowed range.");
        }

        if (L2tp.ReconnectCooldownMilliseconds is < 0 or > 300000)
        {
            throw new InvalidOperationException("l2tp.reconnectCooldownMilliseconds is outside the allowed range.");
        }

        switch (L2tp.Mode)
        {
            case L2tpConnectionMode.ExistingWindowsProfile:
                if (string.IsNullOrWhiteSpace(L2tp.EntryName))
                {
                    throw new InvalidOperationException("l2tp.entryName is required for ExistingWindowsProfile mode.");
                }
                break;

            case L2tpConnectionMode.CustomEphemeral:
                ValidateCustomL2tp(L2tp.Custom);
                break;

            default:
                throw new InvalidOperationException($"Unsupported l2tp.mode '{L2tp.Mode}'.");
        }

        ValidateVerification(L2tp.Verification);
    }

    private static void ValidateCustomL2tp(CustomL2tpOptions custom)
    {
        if (string.IsNullOrWhiteSpace(custom.ServerAddress) ||
            (IPAddress.TryParse(custom.ServerAddress, out _) is false &&
             Uri.CheckHostName(custom.ServerAddress) != UriHostNameType.Dns))
        {
            throw new InvalidOperationException("l2tp.custom.serverAddress must be an IP address or DNS host name.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.UserName))
        {
            throw new InvalidOperationException(
                "l2tp.custom.userName is required unless current Windows credentials are used.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.ProtectedPassword))
        {
            throw new InvalidOperationException("l2tp.custom password is required.");
        }

        if (custom.IpsecAuthentication == L2tpIpsecAuthentication.PreSharedKey &&
            string.IsNullOrWhiteSpace(custom.ProtectedPreSharedKey))
        {
            throw new InvalidOperationException("l2tp.custom pre-shared key is required for PSK authentication.");
        }

        if (!custom.AllowPap && !custom.AllowChap && !custom.AllowMsChapV2)
        {
            throw new InvalidOperationException(
                "At least one PPP authentication protocol must be enabled for custom L2TP.");
        }
    }

    private static void ValidateVerification(VerificationOptions verification)
    {
        if (string.IsNullOrWhiteSpace(verification.PublicAddress))
        {
            throw new InvalidOperationException(
                "l2tp.verification.publicAddress is required and must contain the expected public IPv4 or a domain name.");
        }

        if (IPAddress.TryParse(verification.PublicAddress, out var publicIp))
        {
            if (publicIp.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException(
                    "l2tp.verification.publicAddress supports IPv4 or a domain name; IPv6 is not supported yet.");
            }
        }
        else if (Uri.CheckHostName(verification.PublicAddress) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException(
                "l2tp.verification.publicAddress must be an IPv4 address or a valid DNS host name.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProbeHost) ||
            Uri.CheckHostName(verification.ProbeHost) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException("l2tp.verification.probeHost must be a DNS host name.");
        }

        if (verification.ProbePort is < 1 or > 65535)
        {
            throw new InvalidOperationException("l2tp.verification.probePort must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(verification.ProbePath) ||
            !verification.ProbePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("l2tp.verification.probePath must start with '/'.");
        }

        if (verification.TimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException("l2tp.verification.timeoutSeconds must be between 1 and 60.");
        }

        if (verification.MaxResponseBytes is < 1024 or > 1024 * 1024)
        {
            throw new InvalidOperationException("l2tp.verification.maxResponseBytes is outside the allowed range.");
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
    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; init; } = "127.0.0.1";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; init; } = 18080;

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
    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; } = "logs/ProxyToAnyConnect.jsonl";

    [JsonPropertyName("consoleJson")]
    public bool ConsoleJson { get; init; }
}
