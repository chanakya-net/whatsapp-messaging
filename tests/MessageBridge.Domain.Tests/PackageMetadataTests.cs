namespace MessageBridge.Domain.Tests;

public class PackageMetadataTests
{
    [Fact]
    public void All_Projects_Should_Have_PackageLicenseExpression()
    {
        var solutionRoot = FindSolutionRoot();
        var projects = Directory.GetFiles(solutionRoot, "*.csproj", SearchOption.AllDirectories);

        Assert.NotEmpty(projects);
        foreach (var project in projects)
        {
            var content = File.ReadAllText(project);
            Assert.True(content.Contains("PackageLicenseExpression"),
                $"Project {Path.GetFileName(project)} missing PackageLicenseExpression");
        }
    }

    [Fact]
    public void Solution_Should_Have_EditorConfig()
    {
        var solutionRoot = FindSolutionRoot();
        var editorConfig = Path.Combine(solutionRoot, ".editorconfig");
        Assert.True(File.Exists(editorConfig), "Solution missing .editorconfig");
    }

    [Fact]
    public void Solution_Should_Have_DirectoryBuildProps()
    {
        var solutionRoot = FindSolutionRoot();
        var dirProps = Path.Combine(solutionRoot, "Directory.Build.props");
        Assert.True(File.Exists(dirProps), "Solution missing Directory.Build.props");
    }

    [Fact]
    public void Solution_Should_Have_DirectoryPackagesProps()
    {
        var solutionRoot = FindSolutionRoot();
        var dirPkgProps = Path.Combine(solutionRoot, "Directory.Packages.props");
        Assert.True(File.Exists(dirPkgProps), "Solution missing Directory.Packages.props");
    }

    private static string FindSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current) && current != "/")
        {
            if (File.Exists(Path.Combine(current, "MessageBridge.sln")))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new InvalidOperationException("Could not find solution root");
    }
}
