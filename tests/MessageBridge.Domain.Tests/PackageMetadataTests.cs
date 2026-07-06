using System.Xml.Linq;

namespace MessageBridge.Domain.Tests;

public class PackageMetadataTests
{
    [Fact]
    public void Directory_Build_Props_Should_Define_Shared_Metadata()
    {
        var solutionRoot = FindSolutionRoot();
        var buildProps = LoadXml(Path.Combine(solutionRoot, "Directory.Build.props"));

        Assert.Equal("latest", GetPropertyValue(buildProps, "LangVersion"));
        Assert.Equal("enable", GetPropertyValue(buildProps, "Nullable"));
        Assert.Equal("enable", GetPropertyValue(buildProps, "ImplicitUsings"));
        Assert.Equal("true", GetPropertyValue(buildProps, "GenerateDocumentationFile"));
        Assert.Equal("true", GetPropertyValue(buildProps, "Deterministic"));
        Assert.Equal("true", GetPropertyValue(buildProps, "PublishRepositoryUrl"));
        Assert.Equal("true", GetPropertyValue(buildProps, "EmbedUntrackedSources"));
        Assert.Equal("AGPL-3.0-only", GetPropertyValue(buildProps, "PackageLicenseExpression"));
        Assert.Equal("README.md", GetPropertyValue(buildProps, "PackageReadmeFile"));
        Assert.Equal("https://github.com/chanakya-net/whatsapp-messaging", GetPropertyValue(buildProps, "PackageProjectUrl"));
        Assert.Equal("git", GetPropertyValue(buildProps, "RepositoryType"));
        Assert.Equal("https://github.com/chanakya-net/whatsapp-messaging", GetPropertyValue(buildProps, "RepositoryUrl"));
        Assert.Equal("snupkg", GetPropertyValue(buildProps, "SymbolPackageFormat"));
    }

    [Fact]
    public void Directory_Packages_Props_Should_Enable_Central_Package_Management()
    {
        var solutionRoot = FindSolutionRoot();
        var packagesProps = LoadXml(Path.Combine(solutionRoot, "Directory.Packages.props"));

        Assert.Equal("true", GetPropertyValue(packagesProps, "ManagePackageVersionsCentrally"));
        Assert.Equal("1.1.1", GetPackageVersion(packagesProps, "Microsoft.SourceLink.GitHub"));
    }

    [Fact]
    public void Solution_Should_Have_EditorConfig()
    {
        var solutionRoot = FindSolutionRoot();
        var editorConfig = Path.Combine(solutionRoot, ".editorconfig");
        Assert.True(File.Exists(editorConfig), "Solution missing .editorconfig");
    }

    private static XDocument LoadXml(string path)
    {
        Assert.True(File.Exists(path), $"Missing expected file: {Path.GetFileName(path)}");
        return XDocument.Load(path);
    }

    private static string GetPropertyValue(XDocument document, string propertyName)
    {
        return document
            .Descendants(propertyName)
            .LastOrDefault()?.Value
            ?? throw new InvalidOperationException($"Missing property '{propertyName}'");
    }

    private static string GetPackageVersion(XDocument document, string packageId)
    {
        return document
            .Descendants("PackageVersion")
            .SingleOrDefault(element => string.Equals(element.Attribute("Include")?.Value, packageId, StringComparison.Ordinal))
            ?.Attribute("Version")?.Value
            ?? throw new InvalidOperationException($"Missing package version '{packageId}'");
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
