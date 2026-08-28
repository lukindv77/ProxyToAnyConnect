Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Normalized([string]$Path) {
    return [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}

function Write-PreservingNewlines([string]$Path, [string]$Original, [string]$Normalized) {
    $newline = if ($Original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $output = if ($newline -eq "`r`n") { $Normalized.Replace("`n", "`r`n") } else { $Normalized }
    [IO.File]::WriteAllText($Path, $output, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`n")
    $first = $Text.IndexOf($oldNormalized, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "issue85 transform could not find: $Label" }
    if ($Text.IndexOf($oldNormalized, $first + $oldNormalized.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "issue85 transform found non-unique anchor: $Label"
    }
    return $Text.Substring(0, $first) + $newNormalized + $Text.Substring($first + $oldNormalized.Length)
}

function Replace-Range([string]$Text, [string]$Start, [string]$End, [string]$Replacement, [string]$Label) {
    $startIndex = $Text.IndexOf($Start, [StringComparison]::Ordinal)
    if ($startIndex -lt 0) { throw "issue85 transform could not find range start: $Label" }
    if ($Text.IndexOf($Start, $startIndex + $Start.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "issue85 transform found non-unique range start: $Label"
    }
    $endIndex = $Text.IndexOf($End, $startIndex, [StringComparison]::Ordinal)
    if ($endIndex -lt 0) { throw "issue85 transform could not find range end: $Label" }
    return $Text.Substring(0, $startIndex) + $Replacement.TrimEnd("`n") + $Text.Substring($endIndex)
}

$coordinatorPath = 'src/ProxyToAnyConnect/Runtime/ProxyRuntimeCoordinator.cs'
$coordinatorOriginal = [IO.File]::ReadAllText($coordinatorPath)
$coordinator = $coordinatorOriginal.Replace("`r`n", "`n")
$coordinator = Replace-Exact $coordinator @'
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);
    private readonly object _collectionGate = new();
'@ @'
    private readonly SemaphoreSlim _reconfigureGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly object _collectionGate = new();
'@ 'coordinator dispose gate'
$coordinator = Replace-Exact $coordinator @'
    private AppOptions _options;
    private int _disposed;
'@ @'
    private AppOptions _options;
    private VpnLeaseManager[] _terminalCleanupVpns = [];
    private int _disposed;
    private int _terminalCleanupCompleted;
'@ 'coordinator terminal cleanup fields'

$coordinatorReplacement = @'
    private async Task<Exception?> DisposeTerminalVpnOwnersAsync(
        IReadOnlyList<VpnLeaseManager> vpns,
        string phase,
        Exception? primaryFailure)
    {
        var attempts = new Task<Exception?>[vpns.Count];
        for (var index = 0; index < vpns.Count; index++)
        {
            attempts[index] = DisposeOneOwnedResourceAsync(vpns[index]);
        }

        var residual = new List<VpnLeaseManager>();
        for (var index = 0; index < attempts.Length; index++)
        {
            var failure = await attempts[index];
            if (failure is null)
            {
                continue;
            }

            residual.Add(vpns[index]);
            if (primaryFailure is null)
            {
                primaryFailure = failure;
            }
            else
            {
                primaryFailure.Data[$"CoordinatorCleanup:{phase}:{index}"] =
                    $"{failure.GetType().FullName}: {failure.Message}";
            }
        }

        _terminalCleanupVpns = residual.ToArray();
        return primaryFailure;
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _terminalCleanupCompleted) != 0)
            {
                return;
            }

            var firstDispose = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (!firstDispose)
            {
                var retryFailure = await DisposeTerminalVpnOwnersAsync(
                    _terminalCleanupVpns,
                    "dispose-vpn-retry",
                    primaryFailure: null);
                if (_terminalCleanupVpns.Length == 0)
                {
                    Volatile.Write(ref _terminalCleanupCompleted, 1);
                }

                RethrowCoordinatorCleanupFailure(retryFailure);
                return;
            }

            Exception? cleanupFailure = null;
            var gateEntered = false;
            try
            {
                // Cancel pending foreground lifecycle operations before waiting for their
                // shared operation gate. A throwing linked-token callback is a cleanup
                // defect, but it must not skip disposal of every nested proxy/VPN owner.
                try
                {
                    _lifetime.Cancel();
                }
                catch (Exception ex)
                {
                    CaptureCoordinatorCleanupFailure(ref cleanupFailure, ex, "lifetime-cancel");
                }

                await _reconfigureGate.WaitAsync();
                gateEntered = true;

                ProxyInstanceRuntime[] proxies;
                VpnLeaseManager[] vpns;
                lock (_collectionGate)
                {
                    proxies = _proxyById.Values.ToArray();
                    vpns = _vpnById.Values.ToArray();
                    _proxyById.Clear();
                    _vpnById.Clear();
                    _pendingStartProxyIds.Clear();
                }

                cleanupFailure = await DisposeOwnedResourcesAsync(
                    proxies.Cast<IAsyncDisposable>(),
                    "dispose-proxy",
                    cleanupFailure);
                cleanupFailure = await DisposeTerminalVpnOwnersAsync(
                    vpns,
                    "dispose-vpn",
                    cleanupFailure);
            }
            catch (Exception ex)
            {
                CaptureCoordinatorCleanupFailure(ref cleanupFailure, ex, "dispose-body");
            }
            finally
            {
                if (gateEntered)
                {
                    _reconfigureGate.Release();
                }

                try
                {
                    _reconfigureGate.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureCoordinatorCleanupFailure(ref cleanupFailure, ex, "gate-token");
                }

                try
                {
                    _lifetime.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureCoordinatorCleanupFailure(ref cleanupFailure, ex, "lifetime-token");
                }
            }

            if (_terminalCleanupVpns.Length == 0)
            {
                Volatile.Write(ref _terminalCleanupCompleted, 1);
            }

            RethrowCoordinatorCleanupFailure(cleanupFailure);
        }
        finally
        {
            // Keep this managed gate alive for a possible later cleanup-only retry.
            // AvailableWaitHandle is never requested, so collection of the coordinator
            // after terminal cleanup releases it without an OS-handle leak.
            _disposeGate.Release();
        }
    }
'@
$coordinator = Replace-Range $coordinator "    public async ValueTask DisposeAsync()`n    {" "`n}`n`ninternal readonly record struct L2tpRuntimeSnapshot" $coordinatorReplacement 'coordinator DisposeAsync'
Write-PreservingNewlines $coordinatorPath $coordinatorOriginal $coordinator

$hostPath = 'src/ProxyToAnyConnect/Runtime/ProxyRuntimeHost.cs'
$hostOriginal = [IO.File]::ReadAllText($hostPath)
$hostText = $hostOriginal.Replace("`r`n", "`n")
$hostText = Replace-Exact $hostText @'
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
'@ @'
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
'@ 'host dispose gate'
$hostText = Replace-Exact $hostText @'
    private string? _configurationError;
    private int _disposed;
'@ @'
    private string? _configurationError;
    private int _disposed;
    private int _terminalCleanupCompleted;
'@ 'host terminal cleanup field'
$hostText = Replace-Exact $hostText @'
    public ProxyRuntimeCoordinator? Current => Volatile.Read(ref _current);
'@ @'
    public ProxyRuntimeCoordinator? Current =>
        Volatile.Read(ref _disposed) != 0 ? null : Volatile.Read(ref _current);
'@ 'host hides cleanup-only current'

$hostReplacement = @'
    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _terminalCleanupCompleted) != 0)
            {
                return;
            }

            var firstDispose = Interlocked.Exchange(ref _disposed, 1) == 0;
            if (!firstDispose)
            {
                var retained = Volatile.Read(ref _current);
                if (retained is null)
                {
                    Volatile.Write(ref _terminalCleanupCompleted, 1);
                    return;
                }

                await retained.DisposeAsync();
                _ = Interlocked.CompareExchange(ref _current, null, retained);
                if (Volatile.Read(ref _current) is null)
                {
                    Volatile.Write(ref _terminalCleanupCompleted, 1);
                }
                return;
            }

            Exception? cleanupFailure = null;
            var gateEntered = false;

            // Wake any foreground Start/Pause/Apply operation before waiting for the
            // host gate it may currently own. A throwing linked-token callback is a
            // cleanup defect, but it must not prevent disposal of the exact coordinator.
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception ex)
            {
                CaptureHostCleanupFailure(ref cleanupFailure, ex, "lifetime-cancel");
            }

            try
            {
                await _gate.WaitAsync();
                gateEntered = true;
                var runtime = Volatile.Read(ref _current);
                if (runtime is not null)
                {
                    try
                    {
                        await runtime.DisposeAsync();
                        _ = Interlocked.CompareExchange(ref _current, null, runtime);
                    }
                    catch (Exception ex)
                    {
                        // Keep the exact coordinator private as cleanup-only ownership.
                        // Public Current is already hidden once _disposed is set, and a
                        // later DisposeAsync call can retry the coordinator's residual VPNs.
                        CaptureHostCleanupFailure(ref cleanupFailure, ex, "coordinator-dispose");
                    }
                }
            }
            catch (Exception ex)
            {
                CaptureHostCleanupFailure(ref cleanupFailure, ex, "dispose-body");
            }
            finally
            {
                if (gateEntered)
                {
                    _gate.Release();
                }

                try
                {
                    _gate.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureHostCleanupFailure(ref cleanupFailure, ex, "gate-token");
                }

                try
                {
                    _lifetime.Dispose();
                }
                catch (Exception ex)
                {
                    CaptureHostCleanupFailure(ref cleanupFailure, ex, "lifetime-token");
                }
            }

            if (Volatile.Read(ref _current) is null)
            {
                Volatile.Write(ref _terminalCleanupCompleted, 1);
            }

            RethrowHostCleanupFailure(cleanupFailure);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
'@
$hostText = Replace-Range $hostText "    public async ValueTask DisposeAsync()`n    {" "`n    private static void CaptureHostCleanupFailure(" $hostReplacement 'host DisposeAsync'
Write-PreservingNewlines $hostPath $hostOriginal $hostText

$runnerPath = 'tests/ProxyToAnyConnect.SelfTests/CombinedTestRunner.cs'
$runnerOriginal = [IO.File]::ReadAllText($runnerPath)
$runner = $runnerOriginal.Replace("`r`n", "`n")
$runner = Replace-Exact $runner @'
        await RunAsync(nameof(CoordinatorCleanupFailureSelfTests), CoordinatorCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(RuntimeHostOperationLifetimeSelfTests), RuntimeHostOperationLifetimeSelfTests.RunAsync);
'@ @'
        await RunAsync(nameof(CoordinatorCleanupFailureSelfTests), CoordinatorCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(TerminalRuntimeCleanupRetrySelfTests), TerminalRuntimeCleanupRetrySelfTests.RunAsync);
        await RunAsync(nameof(RuntimeHostOperationLifetimeSelfTests), RuntimeHostOperationLifetimeSelfTests.RunAsync);
'@ 'aggregate registration'
Write-PreservingNewlines $runnerPath $runnerOriginal $runner
