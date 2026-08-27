using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.SelfTests;

internal static class ConfigurationPersistenceSelfTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ProxyToAnyConnect",
            "config-persistence-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await SuccessfulSavePublishesCompleteJsonAsync(root);
            await PreCancelledSavePreservesPublishedFileAsync(root);
            await PublicationFailureCleansUniqueTemporaryFileAsync(root);

            Console.WriteLine(
                "PASS: configuration persistence publishes complete files and cleans cancelled/failed save ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: configuration persistence regression: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task SuccessfulSavePublishesCompleteJsonAsync(string root)
    {
        var path = Path.Combine(root, "successful.json");
        var options = CreateOptions("Successful persistence");

        await options.SaveAsync(path, CancellationToken.None);
        var loaded = await AppOptions.LoadAsync(path, CancellationToken.None);
        if (loaded.Proxies.Count != 1 ||
            !string.Equals(loaded.Proxies[0].Name, "Successful persistence", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Published configuration did not round-trip as one complete JSON document.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(path), "successful save");
    }

    private static async Task PreCancelledSavePreservesPublishedFileAsync(string root)
    {
        var path = Path.Combine(root, "cancelled.json");
        const string original = "previous-complete-generation";
        await File.WriteAllTextAsync(path, original);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await CreateOptions("Cancelled persistence").SaveAsync(path, cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled configuration save unexpectedly completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var actual = await File.ReadAllTextAsync(path);
        if (!string.Equals(actual, original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cancelled configuration save modified the previously published generation.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(path), "cancelled save");
    }

    private static async Task PublicationFailureCleansUniqueTemporaryFileAsync(string root)
    {
        var targetDirectory = Path.Combine(root, "blocked-target.json");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            await CreateOptions("Failed publication").SaveAsync(targetDirectory, CancellationToken.None);
            throw new InvalidOperationException(
                "Configuration save unexpectedly replaced a directory with the JSON file.");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // Windows may report the directory publication collision through either
            // IOException or UnauthorizedAccessException depending on filesystem.
        }

        if (!Directory.Exists(targetDirectory))
        {
            throw new InvalidOperationException("Failed publication damaged the existing destination directory.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(targetDirectory), "failed publication");
    }

    private static AppOptions CreateOptions(string proxyName)
    {
        var options = new AppOptions();
        options.Proxies[0].Name = proxyName;
        options.Proxies[0].Enabled = false;
        return options;
    }

    private static void AssertNoTemporarySiblings(string root, string fileName, string phase)
    {
        var pattern = $".{fileName}.*.tmp";
        var leftovers = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly);
        if (leftovers.Length != 0)
        {
            throw new InvalidOperationException(
                $"{phase}: configuration save left {leftovers.Length} owned temporary file(s)."
            );
        }
    }
}
