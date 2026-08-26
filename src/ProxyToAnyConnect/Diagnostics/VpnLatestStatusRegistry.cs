using System.Globalization;
using System.Reflection;

namespace ProxyToAnyConnect.Diagnostics;

internal sealed record VpnLatestStatus(
    string? Activity,
    string? VerificationSummary,
    string? KeepaliveSummary,
    string? ReconnectSummary,
    string? LastFailureReason,
    DateTimeOffset? ReconnectCooldownUntilUtc,
    string CachedText,
    DateTimeOffset UpdatedUtc)
{
    public string Text
    {
        get
        {
            if (ReconnectCooldownUntilUtc is not { } cooldownUntil)
            {
                return CachedText;
            }

            var remaining = cooldownUntil - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return CachedText;
            }

            var remainingText = $"cooldown remaining {Math.Ceiling(remaining.TotalMilliseconds):F0} ms";
            return string.IsNullOrWhiteSpace(CachedText)
                ? remainingText
                : $"{CachedText} | {remainingText}";
        }
    }
}

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

    internal static void UpdateKeepaliveSuccess(string vpnId, string target, TimeSpan roundTripTime)
    {
        if (string.IsNullOrWhiteSpace(vpnId) || roundTripTime < TimeSpan.Zero)
        {
            return;
        }

        Update(
            vpnId,
            status => status with
            {
                KeepaliveSummary = BuildKeepaliveSuccessText(target, roundTripTime.TotalMilliseconds),
                UpdatedUtc = DateTimeOffset.UtcNow
            });
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

        var now = DateTimeOffset.UtcNow;
        Update(
            vpnId,
            status => eventName switch
            {
                "vpn.state" => status with
                {
                    Activity = BuildStateActivity(data, status.Activity),
                    UpdatedUtc = now
                },

                "vpn.profile.validated" => status with
                {
                    Activity = "Windows L2TP profile validated",
                    UpdatedUtc = now
                },

                "vpn.ephemeral.prepared" => status with
                {
                    Activity = "Custom ephemeral L2TP prepared",
                    UpdatedUtc = now
                },

                "vpn.routes.validated" => status with
                {
                    Activity = "Default-route guard OK",
                    UpdatedUtc = now
                },

                "vpn.ras.connected" => status with
                {
                    Activity = BuildConnectedText(data),
                    UpdatedUtc = now
                },

                "vpn.verification.succeeded" => status with
                {
                    Activity = null,
                    VerificationSummary = BuildVerificationText(data),
                    ReconnectSummary = null,
                    ReconnectCooldownUntilUtc = null,
                    UpdatedUtc = now
                },

                "vpn.keepalive.failed" => status with
                {
                    KeepaliveSummary = BuildKeepaliveFailureText(data),
                    UpdatedUtc = now
                },

                "vpn.keepalive.recovered" => status with
                {
                    KeepaliveSummary = BuildKeepaliveRecoveredText(data),
                    UpdatedUtc = now
                },

                "vpn.reconnect.cooldown_active" => status with
                {
                    ReconnectSummary = "Reconnect cooldown active",
                    ReconnectCooldownUntilUtc = GetLong(data, "RetryAfterMilliseconds") is { } remaining
                        ? now.AddMilliseconds(Math.Max(0, remaining))
                        : status.ReconnectCooldownUntilUtc,
                    UpdatedUtc = now
                },

                "vpn.reconnect.cooldown_armed" => status with
                {
                    ReconnectSummary = GetString(data, "Reason") is { Length: > 0 } reason
                        ? $"Reconnect cooldown: {reason}"
                        : "Reconnect cooldown armed",
                    ReconnectCooldownUntilUtc = GetLong(data, "ReconnectCooldownMilliseconds") is { } cooldown
                        ? now.AddMilliseconds(Math.Max(0, cooldown))
                        : status.ReconnectCooldownUntilUtc,
                    UpdatedUtc = now
                },

                "vpn.maintenance.reconnect_attempt" => status with
                {
                    ReconnectSummary = "Reconnect: dialing and verifying",
                    ReconnectCooldownUntilUtc = null,
                    UpdatedUtc = now
                },

                "vpn.maintenance.reconnect_pending" => status with
                {
                    ReconnectSummary = GetString(data, "Error") is { Length: > 0 } error
                        ? $"Reconnect pending: {error}"
                        : "Reconnect pending",
                    UpdatedUtc = now
                },

                "vpn.maintenance.reconnected" => status with
                {
                    ReconnectSummary = "Reconnect completed",
                    ReconnectCooldownUntilUtc = null,
                    UpdatedUtc = now
                },

                "vpn.maintenance.reconnect_discarded" => status with
                {
                    ReconnectSummary = "Reconnect discarded: no active proxy leases",
                    UpdatedUtc = now
                },

                "vpn.connection.rejected" => status with
                {
                    Activity = "Disconnected",
                    LastFailureReason = $"Rejected: {exception?.Message ?? message}",
                    UpdatedUtc = now
                },

                "vpn.monitor.fail_closed" => status with
                {
                    Activity = "Disconnected",
                    LastFailureReason = $"Fail-closed: {exception?.Message ?? message}",
                    UpdatedUtc = now
                },

                "vpn.ras.hangup" => status with
                {
                    Activity = "Disconnected",
                    UpdatedUtc = now
                },

                _ => status
            });
    }

    private static void Update(string vpnId, Func<VpnLatestStatus, VpnLatestStatus> update)
    {
        lock (Gate)
        {
            var exists = Entries.TryGetValue(vpnId, out var current);
            current ??= new VpnLatestStatus(
                Activity: null,
                VerificationSummary: null,
                KeepaliveSummary: null,
                ReconnectSummary: null,
                LastFailureReason: null,
                ReconnectCooldownUntilUtc: null,
                CachedText: string.Empty,
                UpdatedUtc: DateTimeOffset.UtcNow);

            var updated = update(current);
            if (ReferenceEquals(updated, current))
            {
                return;
            }

            updated = updated with { CachedText = BuildText(updated) };

            if (!exists && Entries.Count >= MaxEntries)
            {
                var oldest = Entries.MinBy(pair => pair.Value.UpdatedUtc);
                if (!string.IsNullOrEmpty(oldest.Key))
                {
                    Entries.Remove(oldest.Key);
                }
            }

            Entries[vpnId] = updated;
        }
    }

    private static string BuildText(VpnLatestStatus status)
    {
        var parts = new List<string>(5);
        AddIfPresent(parts, status.Activity);
        AddIfPresent(parts, status.VerificationSummary);
        AddIfPresent(parts, status.KeepaliveSummary);
        AddIfPresent(parts, status.ReconnectSummary);
        AddIfPresent(parts, status.LastFailureReason);
        return string.Join(" | ", parts);
    }

    private static void AddIfPresent(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value);
        }
    }

    private static string? BuildStateActivity(object data, string? currentActivity)
    {
        return GetString(data, "Current") switch
        {
            "Dialing" => "Dialing",
            "Verifying" => "Verifying",
            "Ready" => null,
            "Disconnected" => "Disconnected",
            { Length: > 0 } state => $"State: {state}",
            _ => currentActivity
        };
    }

    private static string BuildConnectedText(object data)
    {
        var localIp = GetString(data, "LocalIPv4");
        var interfaceIndex = GetLong(data, "InterfaceIndex");

        if (!string.IsNullOrWhiteSpace(localIp) && interfaceIndex is not null)
        {
            return $"RAS connected: {localIp}, ifIndex {interfaceIndex.Value}";
        }

        return !string.IsNullOrWhiteSpace(localIp)
            ? $"RAS connected: {localIp}"
            : "RAS connected";
    }

    private static string BuildVerificationText(object data)
    {
        var probeTarget = GetString(data, "ProbeTargetIPv4");
        var observedPublicIp = GetString(data, "ObservedPublicIPv4");
        var expectedPublicIp = GetString(data, "ExpectedPublicIPv4");
        var comparisonPerformed = GetBool(data, "PublicIPv4ComparisonPerformed");
        var localIp = GetString(data, "LocalIPv4");
        var interfaceIndex = GetLong(data, "InterfaceIndex");

        var route = !string.IsNullOrWhiteSpace(localIp) && interfaceIndex is not null
            ? $"{localIp}/if{interfaceIndex.Value}"
            : localIp;
        var publicIdentity = comparisonPerformed == true && !string.IsNullOrWhiteSpace(expectedPublicIp)
            ? $"egress {observedPublicIp ?? "?"} = {expectedPublicIp}"
            : !string.IsNullOrWhiteSpace(observedPublicIp)
                ? $"egress {observedPublicIp}"
                : "egress probe passed";
        var probe = string.IsNullOrWhiteSpace(probeTarget) ? string.Empty : $", probe {probeTarget}";
        var boundRoute = string.IsNullOrWhiteSpace(route) ? string.Empty : $", {route}";
        return $"Verified: {publicIdentity}{probe}{boundRoute}";
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
        var target = GetString(data, "Target") ?? string.Empty;
        var rtt = GetDouble(data, "RoundTripMilliseconds");
        return rtt is null
            ? "Keepalive recovered"
            : BuildKeepaliveSuccessText(target, rtt.Value, prefix: "Keepalive recovered");
    }

    private static string BuildKeepaliveSuccessText(
        string target,
        double roundTripMilliseconds,
        string prefix = "Keepalive")
    {
        var destination = string.IsNullOrWhiteSpace(target) ? string.Empty : $" -> {target}";
        return $"{prefix}: {roundTripMilliseconds:F0} ms{destination}";
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

    private static bool? GetBool(object data, string propertyName)
    {
        var value = GetPropertyValue(data, propertyName);
        if (value is bool boolValue)
        {
            return boolValue;
        }

        return value is null
            ? null
            : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
                ? parsed
                : null;
    }

    private static object? GetPropertyValue(object data, string propertyName) =>
        data.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(data);
}
