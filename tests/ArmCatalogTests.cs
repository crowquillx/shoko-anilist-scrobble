using Xunit;

namespace Shoko.AniListScrobble.Tests;

public sealed class ArmCatalogTests
{
    [Fact]
    public void ParseIndexesMalToAnilistAndSkipsIncompleteRows()
    {
        var json = File.ReadAllText(Path.Combine(FindFixtures(), "arm.json"));
        var map = ArmCatalog.Parse(json);

        Assert.Equal(5114, map[5114]);
        Assert.Equal(20958, map[25777]);
        Assert.Equal(1, map[1]);
        Assert.False(map.ContainsKey(0));
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void InMemoryCatalogResolvesFirstMatchingMalId()
    {
        var arm = new InMemoryArmCatalog { Map = { [5114] = 5114, [25777] = 20958 } };
        Assert.True(arm.TryResolve([999, 25777, 5114], out var id));
        Assert.Equal(20958, id);
        Assert.False(arm.TryResolve([123], out _));
    }

    private static string FindFixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "arm.json");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("fixtures/arm.json was not found.");
    }
}
