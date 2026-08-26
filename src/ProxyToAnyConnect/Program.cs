using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Gui;
using ProxyToAnyConnect.Runtime;

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
