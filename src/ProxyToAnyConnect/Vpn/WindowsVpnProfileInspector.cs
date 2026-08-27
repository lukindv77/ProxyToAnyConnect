using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProxyToAnyConnect.Vpn;

internal sealed class WindowsVpnProfileInspector
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessTerminationTimeout = TimeSpan.FromSeconds(2);

    public async Task<VpnProfileInfo> InspectAsync(string entryName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var escapedName = entryName.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $name = '{{escapedName}}'
            $profile = Get-VpnConnection -Name $name -ErrorAction SilentlyContinue
            if ($null -eq $profile) {
                $profile = Get-VpnConnection -Name $name -AllUserConnection -ErrorAction SilentlyContinue
            }
            if ($null -eq $profile) {
                throw "VPN profile '$name' was not found."
            }
            [pscustomobject]@{
                Name = [string]$profile.Name
                TunnelType = [string]$profile.TunnelType
                SplitTunneling = [bool]$profile.SplitTunneling
                AllUserConnection = [bool]$profile.AllUserConnection
            } | ConvertTo-Json -Compress
            """;

        var stdout = await ExecutePowerShellAsync(script, $"inspect VPN profile '{entryName}'", cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<VpnProfileInfo>(stdout)
                ?? throw new InvalidOperationException("VPN profile inspection returned an empty result.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse VPN profile information for '{entryName}'. Output: {stdout}",
                ex);
        }
    }

    public async Task<IReadOnlyList<VpnProfileInfo>> ListL2tpProfilesAsync(CancellationToken cancellationToken)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            $profiles = @()
            $profiles += @(Get-VpnConnection -ErrorAction SilentlyContinue)
            $profiles += @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue)
            $result = @(
                $profiles |
                    Where-Object { [string]$_.TunnelType -eq 'L2tp' } |
                    ForEach-Object {
                        [pscustomobject]@{
                            Name = [string]$_.Name
                            TunnelType = [string]$_.TunnelType
                            SplitTunneling = [bool]$_.SplitTunneling
                            AllUserConnection = [bool]$_.AllUserConnection
                        }
                    } |
                    Sort-Object Name, AllUserConnection -Unique
            )
            ConvertTo-Json -InputObject $result -Compress
            """;

        var stdout = await ExecutePowerShellAsync(script, "enumerate Windows L2TP profiles", cancellationToken);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<VpnProfileInfo>>(stdout) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse Windows L2TP profile list. Output: {stdout}",
                ex);
        }
    }

    public static void ValidateForProxy(VpnProfileInfo profile)
    {
        if (!profile.TunnelType.Equals("L2tp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"VPN profile '{profile.Name}' uses tunnel type '{profile.TunnelType}'. L2TP is required.");
        }

        if (!profile.SplitTunneling)
        {
            throw new InvalidOperationException(
                $"VPN profile '{profile.Name}' is configured as full-tunnel. Enable split tunneling before using ProxyToAnyConnect.");
        }
    }

    public static string? ResolveRasPhoneBook(VpnProfileInfo profile)
    {
        if (!profile.AllUserConnection)
        {
            // NULL lets RAS use the current user's default phone book.
            return null;
        }

        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonApplicationData))
        {
            throw new InvalidOperationException("Windows CommonApplicationData path is unavailable.");
        }

        var phoneBook = Path.Combine(
            commonApplicationData,
            "Microsoft",
            "Network",
            "Connections",
            "Pbk",
            "rasphone.pbk");

        if (!File.Exists(phoneBook))
        {
            throw new InvalidOperationException(
                $"Global RAS phone book for AllUserConnection was not found at '{phoneBook}'.");
        }

        return phoneBook;
    }

    internal static async Task<string> ExecutePowerShellAsync(
        string script,
        string operation,
        CancellationToken cancellationToken,
        TimeSpan? operationTimeout = null)
    {
        var effectiveOperationTimeout = operationTimeout ?? InspectionTimeout;
        if (effectiveOperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powershellPath))
        {
            throw new InvalidOperationException($"Windows PowerShell was not found at '{powershellPath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start Windows PowerShell to {operation}.");
        }

        // Keep draining both redirected pipes independently of caller cancellation.
        // Cancellation owns the process lifetime below and kills/drains the helper
        // before returning, so cancelling the readers separately would only make it
        // easier for a verbose child to block on a full redirected pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(effectiveOperationTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var callerCancelled = cancellationToken.IsCancellationRequested;
            await TerminateAndDrainAsync(process, stdoutTask, stderrTask);

            if (callerCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            throw new TimeoutException($"Timed out while attempting to {operation}.");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}. PowerShell exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static async Task TerminateAndDrainAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        TryKill(process);

        using var cleanupTimeout = new CancellationTokenSource(ProcessTerminationTimeout);
        try
        {
            await process.WaitForExitAsync(cleanupTimeout.Token);
        }
        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Timed out while draining cancelled PowerShell process {process.Id}.");
        }
        catch (InvalidOperationException)
        {
            // Process already exited or was never associated by the time cleanup ran.
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cleanupTimeout.Token);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to fully drain cancelled PowerShell redirected streams: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            // Process exited between HasExited and Kill, or the platform refused
            // termination. The bounded drain still prevents cancellation teardown
            // from waiting forever.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to terminate PowerShell helper cleanly: {ex.Message}");
        }
    }
}

internal sealed record VpnProfileInfo(
    string Name,
    string TunnelType,
    bool SplitTunneling,
    bool AllUserConnection)
{
    public string DisplayName =>
        $"{Name} [{(AllUserConnection ? "All users" : "Current user")}]" +
        (SplitTunneling ? string.Empty : " — full tunnel");
}
