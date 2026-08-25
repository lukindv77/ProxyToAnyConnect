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

    [JsonPropertyName("monitorIntervalMilliseconds")]
    public int MonitorIntervalMilliseconds { get; init; } = 1000;
}
