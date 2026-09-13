using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamAppClassifierTests{
    [Theory]
    [InlineData("game", SteamAppKind.Game)]
    [InlineData("Game", SteamAppKind.Game)]
    [InlineData("  GAME  ", SteamAppKind.Game)]
    [InlineData("dlc", SteamAppKind.Dlc)]
    [InlineData("beta", SteamAppKind.Beta)]
    [InlineData("music", SteamAppKind.Music)]
    [InlineData("demo", SteamAppKind.Demo)]
    [InlineData("hardware", SteamAppKind.Hardware)]
    [InlineData("tool", SteamAppKind.Tool)]
    [InlineData("Tool", SteamAppKind.Tool)]
    [InlineData("  TOOL  ", SteamAppKind.Tool)]
    [InlineData("application", SteamAppKind.Application)]
    [InlineData("Application", SteamAppKind.Application)]
    [InlineData("  APPLICATION  ", SteamAppKind.Application)]
    [InlineData("unrecognized-type", SteamAppKind.Other)]
    [InlineData(null, SteamAppKind.Unknown)]
    [InlineData("", SteamAppKind.Unknown)]
    [InlineData("   ", SteamAppKind.Unknown)]
    public void ClassifyMapsSteamTypes(string? rawType, SteamAppKind expected){
        Assert.Equal(expected, SteamAppClassifier.Classify(rawType));
    }

    [Theory]
    [InlineData(SteamAppKind.Game, true)]
    [InlineData(SteamAppKind.Dlc, true)]
    [InlineData(SteamAppKind.Beta, true)]
    [InlineData(SteamAppKind.Music, true)]
    [InlineData(SteamAppKind.Demo, true)]
    [InlineData(SteamAppKind.Hardware, true)]
    [InlineData(SteamAppKind.Tool, true)]
    [InlineData(SteamAppKind.Application, false)]
    [InlineData(SteamAppKind.Unknown, false)]
    [InlineData(SteamAppKind.Other, false)]
    public void IsWantedIdentifiesNotifiableTypes(SteamAppKind kind, bool expected){
        Assert.Equal(expected, kind.IsWanted());
    }
}