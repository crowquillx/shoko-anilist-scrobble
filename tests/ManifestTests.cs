using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class ManifestTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plugin-manifest.json");
            if (File.Exists(candidate))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException($"Could not locate plugin-manifest.json above {AppContext.BaseDirectory}.");
    }

    private static JsonElement ReadManifest()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "plugin-manifest.json")));
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string?> ReadAssemblyMetadata()
    {
        var assembly = typeof(AniListScrobblePlugin).Assembly;
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void PackageIdIsAValidGuid()
    {
        var manifest = ReadManifest();
        Assert.True(Guid.TryParse(manifest.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public void ManifestIdentityMatchesAssemblyMetadata()
    {
        var manifest = ReadManifest();
        var metadata = ReadAssemblyMetadata();

        Assert.Equal(metadata["PackageID"], manifest.GetProperty("id").GetString());
        Assert.Equal(metadata["PackageName"], manifest.GetProperty("name").GetString());
        Assert.Equal(metadata["PackageOverview"], manifest.GetProperty("overview").GetString());
        Assert.Equal("any", metadata["RuntimeIdentifier"]);
    }

    [Fact]
    public void RequiredFieldsArePresent()
    {
        var manifest = ReadManifest();
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("overview").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("authors").GetString()));
        Assert.NotEmpty(manifest.GetProperty("tags").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, manifest.GetProperty("releases").ValueKind);
    }

    [Fact]
    public void ManifestDoesNotHardcodeRepositoryOwner()
    {
        var manifest = ReadManifest();
        Assert.False(manifest.TryGetProperty("repository_url", out _));
    }
}
