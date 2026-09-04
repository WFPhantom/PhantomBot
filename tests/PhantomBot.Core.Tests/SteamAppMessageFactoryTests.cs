using NetCord.Rest;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure.Discord;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamAppMessageFactoryTests{
    [Fact]
    public void CreateBuildsRichMessage(){
        var app = new SteamAppMetadata(4_237_670, "Submind", SteamAppKind.Game, 42){
            Description = "A test description.",
            ReleaseDateText = "To be announced",
            Developers = ["Inoutbox"],
            Publishers = ["Inoutbox"],
            ThumbnailUrl = "https://example.com/capsule.jpg",
        };

        var message = SteamAppMessageFactory.Create(app);

        var nonce = message.Nonce;
        Assert.NotNull(nonce);
        Assert.True(nonce.Unique);

        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        var embed = Assert.Single(embeds);

        var embedFields = embed.Fields;
        Assert.NotNull(embedFields);

        EmbedFieldProperties[] fields = [.. embedFields];

        var author = embed.Author;
        Assert.NotNull(author);

        var thumbnail = embed.Thumbnail;
        Assert.NotNull(thumbnail);

        Assert.Equal("New Game: [Submind](https://steamdb.info/app/4237670/)", message.Content);
        Assert.Equal("Game 4237670 • View on SteamDB", author.Name);
        Assert.Equal("Submind", embed.Title);
        Assert.Equal("A test description.", embed.Description);
        Assert.Equal("https://example.com/capsule.jpg", thumbnail.Url);

        Assert.Equal(3, fields.Length);

        Assert.Equal("Release Date", fields[0].Name);
        Assert.Equal("To be announced", fields[0].Value);

        Assert.Equal("Developer", fields[1].Name);
        Assert.Equal("[Inoutbox](https://steamdb.info/developer/Inoutbox/)", fields[1].Value);

        Assert.Equal("Publisher", fields[2].Name);
        Assert.Equal("[Inoutbox](https://steamdb.info/publisher/Inoutbox/)", fields[2].Value);
    }

    [Theory]
    [InlineData(SteamAppKind.Game, "New Game")]
    [InlineData(SteamAppKind.Dlc, "New DLC")]
    [InlineData(SteamAppKind.Beta, "New Beta")]
    [InlineData(SteamAppKind.Music, "New Music")]
    [InlineData(SteamAppKind.Demo, "New Demo")]
    [InlineData(SteamAppKind.Hardware, "New Hardware")]
    [InlineData(SteamAppKind.Unknown, "New App")]
    [InlineData(SteamAppKind.Other, "New App")]
    public void CreateUsesExpectedNotificationLabel(SteamAppKind kind, string expectedLabel){
        var app = new SteamAppMetadata(570, "Test App", kind, 42);

        var message = SteamAppMessageFactory.Create(app);

        Assert.Equal($"{expectedLabel}: [Test App](https://steamdb.info/app/570/)", message.Content);
    }

    [Fact]
    public void CreateEscapesDiscordMarkdownInLinkText(){
        var app = new SteamAppMetadata(123, "Pack [Alpha]", SteamAppKind.Dlc, 42);

        var message = SteamAppMessageFactory.Create(app);

        Assert.Equal(@"New DLC: [Pack \[Alpha\]](https://steamdb.info/app/123/)", message.Content);
    }

    [Fact]
    public void CreateUsesAppIdPlaceholderWhenNameIsMissing(){
        var app = new SteamAppMetadata(456, null, SteamAppKind.Unknown, 42);

        var message = SteamAppMessageFactory.Create(app);

        Assert.Equal("New App: [App 456](https://steamdb.info/app/456/)", message.Content);

        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        var embed = Assert.Single(embeds);

        Assert.Equal("App 456", embed.Title);
    }

    [Fact]
    public void CreateDoesNotSplitSurrogatePairWhenTruncatingName(){
        var name = new string('a', 254) + "😀b";

        var app = new SteamAppMetadata(570, name, SteamAppKind.Game, 42);

        var message = SteamAppMessageFactory.Create(app);

        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        var embed = Assert.Single(embeds);

        Assert.Equal(new string('a', 254) + "…", embed.Title);
    }

    [Fact]
    public void CreateTruncatesEmbedFieldValues(){
        var app = new SteamAppMetadata(570, "Test App", SteamAppKind.Game, 42){
            ReleaseDateText = new string('x', 1_100),
        };

        var message = SteamAppMessageFactory.Create(app);

        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        var embed = Assert.Single(embeds);

        var fields = embed.Fields;
        Assert.NotNull(fields);

        var field = Assert.Single(fields);

        var fieldValue = field.Value;
        Assert.NotNull(fieldValue);

        Assert.Equal(1_024, fieldValue.Length);
        Assert.EndsWith("…", fieldValue, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUsesDistinctHardwareColor(){
        var hardware = SteamAppMessageFactory.Create(new SteamAppMetadata(570, "Hardware", SteamAppKind.Hardware, 42));

        var unknown = SteamAppMessageFactory.Create(new SteamAppMetadata(571, "Unknown", SteamAppKind.Unknown, 42));

        var hardwareEmbeds = hardware.Embeds;
        Assert.NotNull(hardwareEmbeds);

        var unknownEmbeds = unknown.Embeds;
        Assert.NotNull(unknownEmbeds);

        var hardwareEmbed = Assert.Single(hardwareEmbeds);
        var unknownEmbed = Assert.Single(unknownEmbeds);

        Assert.NotEqual(unknownEmbed.Color, hardwareEmbed.Color);
    }
}