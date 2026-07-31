using CraftQueue.Server;
using CraftQueue.Shared;
using System.Xml.Linq;
using Xunit;

namespace CraftQueue.Tests;

public sealed class VersionConsistencyTests
{
    [Fact]
    public void ProjectAndMetadataVersionsStaySynchronized()
    {
        string repositoryRoot = findRepositoryRoot();
        XDocument serverProject = XDocument.Load(Path.Combine(repositoryRoot, "CraftQueue.Server", "CraftQueue.Server.csproj"));
        XDocument clientProject = XDocument.Load(Path.Combine(repositoryRoot, "CraftQueue.Client", "CraftQueue.Client.csproj"));

        Assert.Equal(CraftQueueProtocol.Version, elementValue(serverProject, "Version"));
        Assert.Equal(CraftQueueProtocol.Version, elementValue(clientProject, "Version"));

        string[] sptPackageVersions = serverProject
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference"
                && ((string?)element.Attribute("Include"))?.StartsWith("SPTarkov.", StringComparison.Ordinal) == true)
            .Select(element => (string?)element.Attribute("Version"))
            .Where(version => version != null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([CraftQueueProtocol.SptVersion], sptPackageVersions);
        var metadata = new ModMetadata();
        Assert.Equal(CraftQueueProtocol.Version, metadata.Version.ToString());
        Assert.Equal($"~{CraftQueueProtocol.SptVersion}", metadata.SptVersion.ToString());
    }

    private static string elementValue(XDocument document, string localName)
    {
        return document.Descendants().First(element => element.Name.LocalName == localName).Value.Trim();
    }

    private static string findRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CraftQueue.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("CraftQueue repository root was not found.");
    }
}
