using FoodMarket;
using Xunit;

namespace FoodMarket.Tests;

public sealed class ReleaseUpdaterTests
{
    [Theory]
    [InlineData("0.1.0+buildhash", "v0.1.1", true)]
    [InlineData("0.2.0-alpha.1", "v0.2.0", true)]
    [InlineData("0.2.0", "v0.1.9", false)]
    [InlineData("0.2.0", "v0.2.0", false)]
    [InlineData("0.2.0", "v0.3.0-rc.1", false)]
    public void OnlyNewerStableReleasesAreInstalled(string current, string tag, bool expected) =>
        Assert.Equal(expected, ReleaseUpdater.IsNewer(current, tag));

    [Fact]
    public void OnlyGithubReleaseRepositoriesAreAccepted()
    {
        Assert.Equal("https://api.github.com/repos/A1i98/FoodMarket/releases/latest",
            ReleaseUpdater.ApiUrl("https://github.com/A1i98/FoodMarket/releases/").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => ReleaseUpdater.ApiUrl("https://elsewhere.example/A1i98/FoodMarket/releases"));
        Assert.Throws<ArgumentException>(() => ReleaseUpdater.ApiUrl("https://github.com/A1i98/FoodMarket/releases/download"));
    }

    [Fact]
    public void ChecksumMustMatchExactAssetName()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, ReleaseUpdater.Checksum($"{hash}  FoodMarket-win-x64.zip\n", "FoodMarket-win-x64.zip"));
        Assert.Null(ReleaseUpdater.Checksum($"{hash}  FoodMarket-win-x64.zip.old\n", "FoodMarket-win-x64.zip"));
    }
}
