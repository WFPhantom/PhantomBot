using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamBaselineTests{
    [Theory]
    [InlineData(1_421_480)]
    [InlineData(2_690_100)]
    public void KnownMusicAppIdVerifiesCoverage(uint appId){
        SteamAppListEntry[] apps = [
            new(appId, "Known Music App"),
        ];

        Assert.True(SteamBaselineFormat.ContainsMusicCoverageSentinel(apps));
    }

    [Fact]
    public void MusicTypeVerifiesCoverage(){
        SteamAppListEntry[] apps = [
            new(5_000_001, "Music App", "music"),
        ];

        Assert.True(SteamBaselineFormat.ContainsMusicCoverageSentinel(apps));
    }

    [Fact]
    public void GameOnlyListDoesNotVerifyMusicCoverage(){
        SteamAppListEntry[] apps = [
            new(570, "Dota 2", "game"),
        ];

        Assert.False(SteamBaselineFormat.ContainsMusicCoverageSentinel(apps));
    }
}