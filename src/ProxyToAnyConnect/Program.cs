using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Gui;
using ProxyToAnyConnect.Runtime;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ApplicationConfiguration.Initialize();

        var configPath = ResolveConfigPath(args);
        AppOptions options;
        try
        {
            options = AppOptions.LoadForEditingAsync(configPath, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось прочитать настройки:\n{ex.Message}",
                "ProxyToAnyConnect",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            options = new AppOptions();
        }

        AppLog.Configure(options.Logging);
        AppLog.Info(
            "application.start",
            "ProxyToAnyConnect GUI started.",
            new { ConfigPath = configPath });

        try
        {
            // Crash recovery must not depend on another CustomEphemeral dial ever
            // occurring. A previous process may have died after creating a private
            // PBK and the next configuration may use only Windows profiles. Recover
            // only directories that opt into the managed marker protocol and whose
            // exclusive owner lock is no longer held by a live process.
            EphemeralRasPhonebook.CleanupOrphanedSessionDirectories();
        }
        catch (Exception ex)
        {
            // Temporary-resource recovery is best-effort diagnostics. It must never
            // prevent the repair/settings GUI from opening.
            AppLog.Warning(
                "vpn.ephemeral.startup_recovery_failed",
                "Startup recovery of abandoned private RAS sessions did not complete.",
                new { Error = ex.Message });
        }

        try
        {
            var runtimeHost = new ProxyRuntimeHost(options);
            var form = new MainForm(options, configPath, runtimeHost);
            var context = new ProxyApplicationContext(form, runtimeHost);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            AppLog.Error("application.fatal", "ProxyToAnyConnect GUI terminated with a fatal error.", ex);
            MessageBox.Show(
                ex.ToString(),
                "ProxyToAnyConnect — fatal error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            AppLog.Info("application.stop", "ProxyToAnyConnect GUI stopped.");
            AppLog.Shutdown();
        }
    }

    private static string ResolveConfigPath(string[] args)
    {
        var explicitPath = args.FirstOrDefault(argument =>
            !string.IsNullOrWhiteSpace(argument) &&
            !argument.StartsWith("--", StringComparison.Ordinal));

        return explicitPath is null
            ? Path.Combine(AppContext.BaseDirectory, "appsettings.json")
            : Path.GetFullPath(explicitPath);
    }
}
