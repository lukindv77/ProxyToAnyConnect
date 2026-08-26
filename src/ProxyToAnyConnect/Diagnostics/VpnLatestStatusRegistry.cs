using System.Globalization;
using System.Reflection;

namespace ProxyToAnyConnect.Diagnostics;

internal sealed record VpnLatestStatus(string Text, DateTimeOffset UpdatedUtc);

internal static class VpnLatestStatusRegistry
{
    private const int MaxEntries = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, VpnLatestStatus> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    public static VpnLatestStatus? Get(string vpnId)
    {
        if (string.IsNullOrWhiteSpace(vpnId))
        {
            return null;
        }

        lock (Gate)
        {
            return Entries.TryGetValue(vpnId, out var status) ? status : null;
        }
    }

    public static void Remove(string vpnId)
    {
        if (string.IsNullOrWhiteSpace(vpnId))
        {
            return;
        }

        lock (Gate)
        {
            Entries.Remove(vpnId);
        }
    }

    internal static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    internal static void UpdateFromLog(
        string eventName,
        string message,
        object? data,
        Exception? exception)
    {
        if (!eventName.StartsWith("vpn.", StringComparison.Ordinal) || data is null)
        {
            return;
        }

        var vpnId = GetString(data, "VpnId");
        if (string.IsNullOrWhiteSpace(vpnId))
        {
            return;
        }

        var text = eventName switch
        {
            "vpn.state" => GetString(data, "Current") is { Length: > 0 } state
                ? $"State: {state}"
                : message,

            "vpn.profile.validated" => "Windows L2TP profile validated",
            "vpn.ephemeral.prepared" => "Custom ephemeral L2TP prepared",
            "vpn.routes.validated" => "Default-route guard OK",

            "vpn.ras.connected" => BuildConnectedText(data),

            "vpn.keepalive.failed" => BuildKeepaliveFailureText(data),
            "vpn.keepalive.recovered" => BuildKeepaliveRecoveredText(data),

            "vpn.reconnect.cooldown_active" => GetLong(data, "RetryAfterMilliseconds") is { } remaining
                ? $"Reconnect cooldown: {remaining} ms"
                : "Reconnect cooldown active",

            "vpn.reconnect.cooldown_armed" => GetString(data, "Reason") is { Length: > 0 } reason
                ? $"Reconnect cooldown: {reason}"
                : "Reconnect cooldown armed",

            "vpn.connection.rejected" => $"Rejected: {exception?.Message ?? message}",
            "vpn.monitor.fail_closed" => $"Fail-closed: {exception?.Message ?? message}",
            "vpn.ras.hangup" => "Disconnected",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (Gate)
        {
            if (!Entries.ContainsKey(vpnId) && Entries.Count >= MaxEntries)
            {
                var oldest = Entries.MinBy(pair => pair.Value.UpdatedUtc);
                if (!string.IsNullOrEmpty(oldest.Key))
                {
                    Entries.Remove(oldest.Key);
                }
            }

            Entries[vpnId] = new VpnLatestStatus(text, DateTimeOffset.UtcNow);
        }
    }

    private static string BuildConnectedText(object data)
    {
        var localIp = GetString(data, "LocalIPv4");
        var interfaceIndex = GetLong(data, "InterfaceIndex");

        if (!string.IsNullOrWhiteSpace(localIp) && interfaceIndex is not null)
        {
            return $"Connected: {localIp}, ifIndex {interfaceIndex.Value}";
        }

        return !string.IsNullOrWhiteSpace(localIp)
            ? $"Connected: {localIp}"
            : "RAS connected";
    }

    private static string BuildKeepaliveFailureText(object data)
    {
        var count = GetLong(data, "FailureCount");
        var threshold = GetLong(data, "FailureThreshold");
        var target = GetString(data, "Target");

        var attempts = count is not null && threshold is not null
            ? $" {count.Value}/{threshold.Value}"
            : string.Empty;
        var destination = string.IsNullOrWhiteSpace(target) ? string.Empty : $" -> {target}";
        return $"Keepalive failed{attempts}{destination}";
    }

    private static string BuildKeepaliveRecoveredText(object data)
    {
        var rtt = GetDouble(data, "RoundTripMilliseconds");
        return rtt is null
            ? "Keepalive recovered"
            : $"Keepalive recovered: {rtt.Value:F0} ms";
    }

    private static string? GetString(object data, string propertyName)
    {
        var value = GetPropertyValue(data, propertyName);
        return value switch
        {
            null => null,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static long? GetLong(object data, string propertyName)
    {
        var value = GetPropertyValue(data, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static double? GetDouble(object data, string propertyName)
    {
        var value = GetPropertyValue(data, propertyName);
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static object? GetPropertyValue(object data, string propertyName) =>
        data.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(data);
}
