using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProxyToAnyConnect.Configuration;

internal sealed class AppOptions
{
    [JsonPropertyName("proxy")]
    public ProxyOptions Proxy { get; init; } = new();

    [JsonPropertyName("l2tp")]
    public L2tpOptions L2tp { get; init; } = new();

    [JsonPropertyName("logging")]
    public LoggingOptions Logging { get; init; } = new();

    public static async Task<AppOptions> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var options = await JsonSerializer.DeserializeAsync<AppOptions>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Configuration file is empty or invalid.");

        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (!IPAddress.TryParse(Proxy.ListenAddress, out var listenAddress) ||
            !IPAddress.IsLoopback(listenAddress))
        {
            throw new InvalidOperationException("proxy.listenAddress must be a loopback address.");
        }

        if (Proxy.ListenPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("proxy.listenPort must be between 1 and 65535.");
        }

        if (Proxy.MaxHeaderBytes is < 4096 or > 1024 * 1024)
        {
            throw new InvalidOperationException("proxy.maxHeaderBytes is outside the allowed range.");
        }

        if (string.IsNullOrWhiteSpace(L2tp.EntryName))
        {
            throw new InvalidOperationException("l2tp.entryName is required.");
        }

        if (L2tp.MonitorIntervalMilliseconds is < 250 or > 60000)
        {
            throw new InvalidOperationException("l2tp.monitorIntervalMilliseconds is outside the allowed range.");
        }

        if (L2tp.RouteMonitorIntervalMilliseconds is < 1000 or > 300000)
        {
            throw new InvalidOperationException("l2tp.routeMonitorIntervalMilliseconds is outside the allowed range.");
        }

        if (!string.IsNullOrWhiteSpace(Logging.FilePath) && Logging.FilePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidOperationException("logging.filePath contains invalid path characters.");
        }

        ValidateVerification(L2tp.Verification);
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
            if (publicIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
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
}

internal sealed class ProxyOptions
{
    [JsonPropertyName("listenAddress")]
    public string ListenAddress { get; init; } = "127.0.0.1";

    [JsonPropertyName("listenPort")]
    public int ListenPort { get; init; } = 18080;

    [JsonPropertyName("maxHeaderBytes")]
    public int MaxHeaderBytes { get; init; } = 65536;
}

internal sealed class L2tpOptions
{
    [JsonPropertyName("entryName")]
    public string EntryName { get; init; } = "ProxyToAnyConnect-L2TP";

    // Fast RAS/PPP health check. This does not perform Internet traffic.
    [JsonPropertyName("monitorIntervalMilliseconds")]
    public int MonitorIntervalMilliseconds { get; init; } = 1000;

    // Independent guard for the host's IPv4 default-route set while the VPN is Ready.
    // A mismatch fails closed and tears down the L2TP connection.
    [JsonPropertyName("routeMonitorIntervalMilliseconds")]
    public int RouteMonitorIntervalMilliseconds { get; init; } = 5000;

    [JsonPropertyName("verification")]
    public VerificationOptions Verification { get; init; } = new();
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
    // Relative paths are resolved against the directory containing appsettings.json.
    // Empty/null disables file logging.
    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; } = "logs/ProxyToAnyConnect.jsonl";

    // Human-readable console status remains enabled independently. This option emits
    // the same structured JSON entries to stdout as well, which is useful for services.
    [JsonPropertyName("consoleJson")]
    public bool ConsoleJson { get; init; }
}
