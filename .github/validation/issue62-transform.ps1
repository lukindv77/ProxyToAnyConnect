Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $raw = [IO.File]::ReadAllText($Path)
    $useCrLf = $raw.Contains("`r`n")
    $text = $raw.Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        $anchor = (($oldNormalized -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1).Trim()
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path' for '$anchor', found $actualCount."
    }

    $updated = $text.Replace($oldNormalized, $newNormalized)
    if ($useCrLf) {
        $updated = $updated.Replace("`n", "`r`n")
    }
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$dialer = 'src/ProxyToAnyConnect/Vpn/RasDialer.cs'
$core = 'src/ProxyToAnyConnect/Vpn/RasConnectionManager.Core.cs'
$tests = 'tests/ProxyToAnyConnect.SelfTests/RasDialerSelfTests.cs'

# Put the already-cancelled guard under the existing password-clearing finally.
Replace-Exact $dialer @'
        ArgumentNullException.ThrowIfNull(dialParams);
        cancellationToken.ThrowIfCancellationRequested();

        var terminal = new TaskCompletionSource<RasDialTerminalState>(
'@ @'
        ArgumentNullException.ThrowIfNull(dialParams);

        var terminal = new TaskCompletionSource<RasDialTerminalState>(
'@

Replace-Exact $dialer @'
        nint handle = 0;
        try
        {
            uint initialResult;
'@ @'
        nint handle = 0;
        try
        {
            // The password carrier is already owned by this method. Keep the
            // cancellation guard inside the clearing finally so a pre-cancelled
            // operation cannot leave plaintext referenced until GC.
            cancellationToken.ThrowIfCancellationRequested();

            uint initialResult;
'@

# Make ConnectCore own one outer password scope that includes phonebook retrieval,
# diagnostics and the RasDialer handoff. This covers errors before RasDialer begins.
Replace-Exact $core @'
        RasNative.RasDialParams dialParams;
        if (explicitDialParams is null)
        {
            dialParams = new RasNative.RasDialParams
            {
                DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialParams>()),
                SzEntryName = entryName
            };

            var getParamsResult = RasNative.RasGetEntryDialParamsW(phoneBook, dialParams, out var hasSavedPassword);
            if (getParamsResult != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to load RAS entry '{entryName}': {RasNative.DescribeError(getParamsResult)}");
            }

            AppLog.Info(
                "vpn.ras.parameters_loaded",
                "RAS dial parameters were loaded from the Windows phone book.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    EntryName = entryName,
                    HasSavedPassword = hasSavedPassword,
                    PhoneBookScope = phoneBook is null ? "CurrentUserDefault" : "ExplicitPhoneBook"
                });
        }
        else
        {
            dialParams = explicitDialParams;
            AppLog.Info(
                "vpn.ras.parameters_loaded",
                "RAS dial parameters were prepared from the custom ephemeral L2TP configuration.",
                new
                {
                    VpnId = _options.Id,
                    VpnName = _options.Name,
                    EntryName = entryName,
                    Mode = "CustomEphemeral",
                    HasExplicitUserName = !_options.Custom.UseCurrentWindowsCredentials
                });
        }

        var handle = await _dialer.DialAsync(
            phoneBook,
            dialParams,
            cancellationToken);
'@ @'
        var dialParams = explicitDialParams ?? new RasNative.RasDialParams
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialParams>()),
            SzEntryName = entryName
        };

        var handle = await ExecuteDialPasswordScopeAsync(
            dialParams,
            async () =>
            {
                if (explicitDialParams is null)
                {
                    var getParamsResult = RasNative.RasGetEntryDialParamsW(
                        phoneBook,
                        dialParams,
                        out var hasSavedPassword);
                    if (getParamsResult != RasNative.ErrorSuccess)
                    {
                        throw new InvalidOperationException(
                            $"Unable to load RAS entry '{entryName}': {RasNative.DescribeError(getParamsResult)}");
                    }

                    AppLog.Info(
                        "vpn.ras.parameters_loaded",
                        "RAS dial parameters were loaded from the Windows phone book.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            EntryName = entryName,
                            HasSavedPassword = hasSavedPassword,
                            PhoneBookScope = phoneBook is null ? "CurrentUserDefault" : "ExplicitPhoneBook"
                        });
                }
                else
                {
                    AppLog.Info(
                        "vpn.ras.parameters_loaded",
                        "RAS dial parameters were prepared from the custom ephemeral L2TP configuration.",
                        new
                        {
                            VpnId = _options.Id,
                            VpnName = _options.Name,
                            EntryName = entryName,
                            Mode = "CustomEphemeral",
                            HasExplicitUserName = !_options.Custom.UseCurrentWindowsCredentials
                        });
                }

                return await _dialer.DialAsync(
                    phoneBook,
                    dialParams,
                    cancellationToken);
            });
'@

Replace-Exact $core @'
    private static PppProjection GetProjection(nint handle)
'@ @'
    internal static async Task<nint> ExecuteDialPasswordScopeAsync(
        RasNative.RasDialParams dialParams,
        Func<Task<nint>> operation)
    {
        ArgumentNullException.ThrowIfNull(dialParams);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return await operation();
        }
        finally
        {
            // RasGetEntryDialParamsW and custom DPAPI materialization both use this
            // managed carrier. Drop its plaintext reference on every pre-/post-dial
            // exit even if the operation failed before RasDialer took ownership.
            dialParams.SzPassword = string.Empty;
        }
    }

    private static PppProjection GetProjection(nint handle)
'@

Replace-Exact $tests @'
            await NativeHandoffDoesNotRetainManagedPasswordAsync();
            await NativeThrowStillClearsManagedPasswordAsync();
            await ConnectedNotificationReturnsExactHandleAsync();
'@ @'
            await NativeHandoffDoesNotRetainManagedPasswordAsync();
            await NativeThrowStillClearsManagedPasswordAsync();
            await PreCanceledDialClearsManagedPasswordAsync();
            await PreDialScopeFailureClearsManagedPasswordAsync();
            await ConnectedNotificationReturnsExactHandleAsync();
'@

Replace-Exact $tests @'
    private static async Task ConnectedNotificationReturnsExactHandleAsync()
'@ @'
    private static async Task PreCanceledDialClearsManagedPasswordAsync()
    {
        var native = new FakeRasDialNative();
        var dialer = new RasDialer(native);
        var dialParams = CreateDialParams(SyntheticPassword);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            _ = await dialer.DialAsync(null, dialParams, cancellation.Token);
            throw new InvalidOperationException(
                "Already-cancelled RasDial unexpectedly reached native handoff.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "Already-cancelled RasDial retained the managed plaintext password carrier.");
        }

        if (native.PasswordObservedDuringDial is not null)
        {
            throw new InvalidOperationException(
                "Already-cancelled RasDial invoked the native adapter before cancellation propagation.");
        }
    }

    private static async Task PreDialScopeFailureClearsManagedPasswordAsync()
    {
        var dialParams = CreateDialParams(SyntheticPassword);
        try
        {
            _ = await RasConnectionManager.ExecuteDialPasswordScopeAsync(
                dialParams,
                () => throw new SyntheticNativeDialException());
            throw new InvalidOperationException(
                "Synthetic pre-dial failure unexpectedly completed the password scope.");
        }
        catch (SyntheticNativeDialException)
        {
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "RasConnectionManager pre-dial failure retained the managed plaintext password carrier.");
        }
    }

    private static async Task ConnectedNotificationReturnsExactHandleAsync()
'@
