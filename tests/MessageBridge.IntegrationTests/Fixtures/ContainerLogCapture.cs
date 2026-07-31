using DotNet.Testcontainers.Containers;

namespace MessageBridge.IntegrationTests.Fixtures;

internal static class ContainerLogCapture
{
    internal static async Task<string?> CaptureAsync(
        IContainer container,
        string containerName,
        Func<string> getConnectionString)
    {
        var contents = await GetSanitizedContentsAsync(
            container,
            containerName,
            getConnectionString);

        try
        {
            var directory = Path.Combine(FindSolutionRoot(), "artifacts", "container-logs");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"{containerName}.log"),
                contents);
            return null;
        }
        catch (Exception exception)
        {
            return $"{containerName} diagnostic write failed ({exception.GetType().Name}).";
        }
    }

    private static async Task<string> GetSanitizedContentsAsync(
        IContainer container,
        string containerName,
        Func<string> getConnectionString)
    {
        try
        {
            var connectionString = getConnectionString();
            var (stdout, stderr) = await container.GetLogsAsync();
            var contents = $"[stdout]{Environment.NewLine}{stdout}"
                + $"{Environment.NewLine}[stderr]{Environment.NewLine}{stderr}";
            return ContainerLogSanitizer.Sanitize(contents, connectionString);
        }
        catch (Exception exception)
        {
            return ContainerLogSanitizer.Sanitize(
                $"{containerName} log capture unavailable ({exception.GetType().Name}).");
        }
    }

    private static string FindSolutionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MessageBridge.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate MessageBridge.sln.");
    }
}
