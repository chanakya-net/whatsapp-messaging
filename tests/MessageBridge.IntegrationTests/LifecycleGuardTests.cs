using MessageBridge.IntegrationTests.Fixtures;
using System.Text.RegularExpressions;

namespace MessageBridge.IntegrationTests;

[Trait("Category", "Unit")]
public sealed class LifecycleGuardTests
{
    [Fact]
    public void Container_builders_are_owned_only_by_the_shared_fixture()
    {
        var sourceFiles = GetIntegrationSourceFiles();
        AssertSingleOwner(sourceFiles, @"\bnew\s+Postgre" + @"SqlBuilder\s*\(");
        AssertSingleOwner(sourceFiles, @"\bnew\s+Rabbit" + @"MqBuilder\s*\(");

        var obsoleteFixtureNames = new[]
        {
            "Postgres" + "Fixture",
            "RabbitMq" + "Fixture"
        };
        var obsoleteReferences = sourceFiles
            .Where(file => obsoleteFixtureNames.Any(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(obsoleteReferences);
    }

    [Fact]
    public void Integration_tests_use_only_shared_adaptive_synchronization()
    {
        var allowedOwner = nameof(IntegrationEnvironmentFixture) + ".cs";
        var fixedWaitPatterns = new[]
        {
            @"\bTask\s*\.\s*" + @"Delay\s*\(",
            @"\bThread\s*\.\s*" + @"Sleep\s*\(",
            @"\bThread\s*\.\s*" + @"SpinWait\s*\("
        };
        var violations = GetIntegrationSourceFiles()
            .Where(file => !string.Equals(Path.GetFileName(file), allowedOwner, StringComparison.Ordinal))
            .Where(file => fixedWaitPatterns.Any(pattern => Regex.IsMatch(File.ReadAllText(file), pattern)))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(violations);
    }

    private static void AssertSingleOwner(IReadOnlyList<string> sourceFiles, string constructionPattern)
    {
        var owners = sourceFiles
            .SelectMany(file => FindOccurrences(file, constructionPattern))
            .ToArray();

        var owner = Assert.Single(owners);
        Assert.Equal(nameof(IntegrationEnvironmentFixture) + ".cs", Path.GetFileName(owner));
    }

    private static IEnumerable<string> FindOccurrences(string file, string pattern)
    {
        var source = File.ReadAllText(file);
        foreach (Match _ in Regex.Matches(source, pattern))
        {
            yield return file;
        }
    }

    private static IReadOnlyList<string> GetIntegrationSourceFiles()
    {
        var root = FindSolutionRoot();
        var integrationRoot = Path.Combine(root, "tests", "MessageBridge.IntegrationTests");

        return Directory.GetFiles(integrationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !HasDirectory(file, "bin") && !HasDirectory(file, "obj"))
            .ToArray();
    }

    private static bool HasDirectory(string file, string directoryName) =>
        file.Split(Path.DirectorySeparatorChar).Contains(directoryName, StringComparer.Ordinal);

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
