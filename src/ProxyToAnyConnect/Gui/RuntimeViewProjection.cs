using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal static class RuntimeViewProjection
{
    internal const string DesiredRuntimeMissingStatus =
        "Настройки сохранены; runtime ещё не создан или ожидает согласования.";
    internal const string RuntimeOnlyStatus =
        "Runtime больше не присутствует в сохранённой конфигурации; ожидается завершение cleanup.";

    public static IReadOnlyList<ProxyRuntimeView> ProjectProxies(
        IEnumerable<ProxyOptions> desired,
        IEnumerable<ProxyRuntimeSnapshot> actual,
        string? configurationError)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(actual);

        var desiredById = desired.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var actualById = actual.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var ids = desiredById.Keys
            .Concat(actualById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return ids
            .Select(id =>
            {
                desiredById.TryGetValue(id, out var configured);
                actualById.TryGetValue(id, out var runtime);

                if (configured is not null && runtime.Id is not null)
                {
                    return new ProxyRuntimeView(
                        id,
                        configured.Name,
                        configured.ListenAddress,
                        configured.ListenPort,
                        configured.VpnConnectionId,
                        runtime.State.ToString(),
                        runtime.LastError ?? string.Empty,
                        runtime.ReceivedBytes,
                        runtime.SentBytes,
                        IsDesired: true,
                        HasRuntime: true,
                        CanToggle: true,
                        ActionText: runtime.State is ProxyInstanceState.Running or ProxyInstanceState.Starting
                            ? "Пауза"
                            : "Запустить");
                }

                if (configured is not null)
                {
                    return new ProxyRuntimeView(
                        id,
                        configured.Name,
                        configured.ListenAddress,
                        configured.ListenPort,
                        configured.VpnConnectionId,
                        string.IsNullOrWhiteSpace(configurationError) ? "Pending" : "Error",
                        string.IsNullOrWhiteSpace(configurationError)
                            ? DesiredRuntimeMissingStatus
                            : configurationError,
                        ReceivedBytes: 0,
                        SentBytes: 0,
                        IsDesired: true,
                        HasRuntime: false,
                        CanToggle: false,
                        ActionText: string.Empty);
                }

                return new ProxyRuntimeView(
                    id,
                    runtime.Name,
                    runtime.ListenAddress,
                    runtime.ListenPort,
                    runtime.VpnConnectionId,
                    runtime.State.ToString(),
                    CombineStatus(RuntimeOnlyStatus, runtime.LastError),
                    runtime.ReceivedBytes,
                    runtime.SentBytes,
                    IsDesired: false,
                    HasRuntime: true,
                    CanToggle: false,
                    ActionText: string.Empty);
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<L2tpRuntimeView> ProjectVpns(
        IEnumerable<L2tpOptions> desired,
        IEnumerable<L2tpRuntimeSnapshot> actual,
        string? configurationError,
        Func<string, string?>? latestStatus = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(actual);

        var desiredById = desired.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var actualById = actual.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var ids = desiredById.Keys
            .Concat(actualById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return ids
            .Select(id =>
            {
                desiredById.TryGetValue(id, out var configured);
                actualById.TryGetValue(id, out var runtime);
                var status = latestStatus?.Invoke(id);

                if (configured is not null && runtime.Id is not null)
                {
                    return new L2tpRuntimeView(
                        id,
                        configured.Name,
                        configured.Mode,
                        configured.Shared,
                        runtime.State.ToString(),
                        runtime.LocalIPv4,
                        runtime.InterfaceIndex,
                        runtime.ActiveProxyCount,
                        runtime.ReceivedBytes,
                        runtime.SentBytes,
                        runtime.AveragePingMilliseconds,
                        status ?? string.Empty,
                        IsDesired: true,
                        HasRuntime: true);
                }

                if (configured is not null)
                {
                    return new L2tpRuntimeView(
                        id,
                        configured.Name,
                        configured.Mode,
                        configured.Shared,
                        string.IsNullOrWhiteSpace(configurationError) ? "Pending" : "Error",
                        LocalIPv4: null,
                        InterfaceIndex: null,
                        ActiveProxyCount: 0,
                        ReceivedBytes: 0,
                        SentBytes: 0,
                        AveragePingMilliseconds: null,
                        string.IsNullOrWhiteSpace(configurationError)
                            ? DesiredRuntimeMissingStatus
                            : configurationError,
                        IsDesired: true,
                        HasRuntime: false);
                }

                return new L2tpRuntimeView(
                    id,
                    runtime.Name,
                    runtime.Mode,
                    runtime.Shared,
                    runtime.State.ToString(),
                    runtime.LocalIPv4,
                    runtime.InterfaceIndex,
                    runtime.ActiveProxyCount,
                    runtime.ReceivedBytes,
                    runtime.SentBytes,
                    runtime.AveragePingMilliseconds,
                    CombineStatus(RuntimeOnlyStatus, status),
                    IsDesired: false,
                    HasRuntime: true);
            })
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string CombineStatus(string primary, string? secondary) =>
        string.IsNullOrWhiteSpace(secondary) ? primary : $"{primary} {secondary}";
}

internal readonly record struct ProxyRuntimeView(
    string Id,
    string Name,
    string ListenAddress,
    int ListenPort,
    string VpnConnectionId,
    string State,
    string Status,
    long ReceivedBytes,
    long SentBytes,
    bool IsDesired,
    bool HasRuntime,
    bool CanToggle,
    string ActionText);

internal readonly record struct L2tpRuntimeView(
    string Id,
    string Name,
    L2tpConnectionMode Mode,
    bool Shared,
    string State,
    string? LocalIPv4,
    int? InterfaceIndex,
    int ActiveProxyCount,
    long ReceivedBytes,
    long SentBytes,
    double? AveragePingMilliseconds,
    string Status,
    bool IsDesired,
    bool HasRuntime);
