namespace MessageBridge.Coverage.Tests;

public class CoverageConfigTests
{
    [Fact]
    public void RunsettingsFileExists()
    {
        var path = GetRunsettingsPath();
        File.Exists(path).Should().BeTrue($"runsettings must exist at {path}");
    }

    [Fact]
    public void RunsettingsContainsCoberturaCoverageFormat()
    {
        var content = ReadRunsettings();
        content.Should().Contain("<Format>cobertura</Format>", "must specify Cobertura output format");
    }

    [Fact]
    public void RunsettingsExcludesOnlyApprovedPaths()
    {
        var content = ReadRunsettings();

        var approvedExclusions = new[]
        {
            "Generated", "Migrations", "Worker", "Bootstrap"
        };

        foreach (var approved in approvedExclusions)
        {
            content.Should().Contain(approved, $"must exclude {approved} paths");
        }
    }

    [Fact]
    public void RunsettingsIsValidXml()
    {
        var content = ReadRunsettings();
        var action = () =>
        {
            using (var reader = new System.Xml.XmlTextReader(new System.IO.StringReader(content)))
            {
                while (reader.Read()) { }
            }
        };
        action.Should().NotThrow("runsettings must be valid XML");
    }

    private static string ReadRunsettings()
    {
        return File.ReadAllText(GetRunsettingsPath());
    }

    private static string GetRunsettingsPath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CoverageConfigTests).Assembly.Location)!;
        var testsDir = Path.GetFullPath(Path.Combine(assemblyDir, "../../../.."));
        return Path.Combine(testsDir, "coverlet.runsettings");
    }
}
