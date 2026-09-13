using System.Text.Json;
using NetCord;
using NetCord.Rest;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure.Discord;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamReviewMessageFactoryTests{
    [Fact]
    public void CreateBuildsRichMessage(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/full-review",
                ThumbnailUrl = "https://example.com/capsule.jpg",
                PublishedUtc = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            },
        };

        var message = SteamReviewMessageFactory.Create(pending);
        var embed = GetEmbed(message);
        var author = embed.Author;
        Assert.NotNull(author);

        var thumbnail = embed.Thumbnail;
        Assert.NotNull(thumbnail);
        Assert.Equal("New review by Test Reviewer", message.Content);
        Assert.Equal("Test Reviewer", author.Name);
        Assert.Equal(pending.Review.Source.Url, author.Url);
        Assert.Equal("Test Game", embed.Title);
        Assert.Equal("https://store.steampowered.com/app/570/", embed.Url);
        Assert.Equal("A test review.", embed.Description);
        Assert.Equal("https://example.com/capsule.jpg", thumbnail.Url);

        var fields = embed.Fields;
        Assert.NotNull(fields);

        // ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
        Assert.Collection(fields, static field => {
            Assert.Equal("Recommendation", field.Name);
            Assert.Equal("Recommended", field.Value);
        }, static field => {
            Assert.Equal("Source", field.Name);
            Assert.Equal("Steam User", field.Value);
        }, static field => {
            Assert.Equal("Steam Review", field.Name);
            Assert.Equal("[View on Steam](https://steamcommunity.com/profiles/76561198297114542/recommended/570/)", field.Value);
        }, static field => {
            Assert.Equal("Full Review", field.Name);
            Assert.Equal("[Read the full review](https://example.com/full-review)", field.Value);
        }, static field => {
            Assert.Equal("Posted", field.Name);
            Assert.Equal("<t:1700000000:f>", field.Value);
        });
    }

    [Theory]
    [InlineData(SteamReviewRecommendation.Recommended, "Recommended", 0x5BA32B)]
    [InlineData(SteamReviewRecommendation.NotRecommended, "Not Recommended", 0xD9534F)]
    [InlineData(SteamReviewRecommendation.Informational, "Informational", 0x3498DB)]
    public void CreateUsesExpectedRecommendationLabelAndColor(SteamReviewRecommendation recommendation, string expectedLabel, int expectedColor){
        var pending = CreatePending(recommendation: recommendation);

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        Assert.Equal(expectedLabel, GetField(embed, "Recommendation").Value);
        Assert.Equal(new Color(expectedColor), embed.Color);
    }

    [Theory]
    [InlineData(SteamReviewSourceKind.User, "Test Reviewer", "Steam User")]
    [InlineData(SteamReviewSourceKind.Curator, "Test Curator", "Steam Curator")]
    public void CreateIdentifiesReviewSource(SteamReviewSourceKind sourceKind, string expectedName, string expectedSourceLabel){
        var pending = CreatePending(sourceKind);
        var message = SteamReviewMessageFactory.Create(pending);
        var embed = GetEmbed(message);
        var author = embed.Author;
        Assert.NotNull(author);
        Assert.Equal($"New review by {expectedName}", message.Content);
        Assert.Equal(expectedName, author.Name);
        Assert.Equal(pending.Review.Source.Url, author.Url);
        Assert.Equal(expectedSourceLabel, GetField(embed, "Source").Value);
        Assert.Equal($"[View on Steam]({pending.Review.Url})", GetField(embed, "Steam Review").Value);
    }

    [Theory]
    [InlineData(1L, "pb-rv-1")]
    [InlineData(2L, "pb-rv-2")]
    [InlineData(long.MaxValue, "pb-rv-9223372036854775807")]
    public void CreateSerializesExpectedNonceAndDisablesMentions(long pendingId, string expectedNonce){
        var pending = CreatePending() with{
            Id = pendingId,
        };

        var message = SteamReviewMessageFactory.Create(pending);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message));

        var root = document.RootElement;

        Assert.Equal(expectedNonce, root.GetProperty("nonce").GetString());
        Assert.True(root.GetProperty("enforce_nonce").GetBoolean());

        var allowedMentions = root.GetProperty("allowed_mentions");

        Assert.Equal(0, allowedMentions.GetProperty("parse").GetArrayLength());
    }

    [Fact]
    public void CreateOmitsMissingOptionalMetadata(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Text = "",
                AppName = null,
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        Assert.Equal("App 570", embed.Title);
        Assert.Null(embed.Description);
        Assert.Null(embed.Thumbnail);

        var fields = embed.Fields;
        Assert.NotNull(fields);

        Assert.Collection(fields, static field => Assert.Equal("Recommendation", field.Name), static field => Assert.Equal("Source", field.Name), static field => Assert.Equal("Steam Review", field.Name));
    }

    [Fact]
    public void CreateEscapesMarkdownInSourceNameAndReviewText(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Source = pending.Review.Source with{
                    Name = "Reviewer [One]",
                },
                Text = "Good *game* [really].",
            },
        };

        var message = SteamReviewMessageFactory.Create(pending);
        var embed = GetEmbed(message);

        Assert.Equal(@"New review by Reviewer \[One\]", message.Content);
        Assert.Equal(@"Good \*game\* \[really\].", embed.Description);
    }

    [Fact]
    public void CreateDoesNotSplitSurrogatePairWhenTruncatingAppName(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                AppName = new string('a', 254) + "😀b",
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        Assert.Equal(new string('a', 254) + "…", embed.Title);
    }

    [Fact]
    public void CreateDoesNotSplitSurrogatePairWhenTruncatingReviewText(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Text = new string('a', 1_698) + "😀b",
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        Assert.Equal(new string('a', 1_698) + "…", embed.Description);
    }

    [Fact]
    public void CreateKeepsEscapedReviewTextWithinEmbedLimit(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Text = new string('*', 2_000),
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        var description = embed.Description;
        Assert.NotNull(description);
        Assert.Equal(string.Concat(Enumerable.Repeat(@"\*", 1_699)) + "…", description);
        Assert.InRange(description.Length, 1, 4_096);
    }

    [Fact]
    public void CreateEscapesParenthesesInExternalReviewLink(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/review_(full)",
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        Assert.Equal("[Read the full review](https://example.com/review_%28full%29)", GetField(embed, "Full Review").Value);
    }

    [Fact]
    public void CreateOmitsOversizedExternalLinkInsteadOfTruncatingIt(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/" + new string('a', 1_100),
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));
        var fields = embed.Fields;
        Assert.NotNull(fields);
        Assert.DoesNotContain(fields, static field => field.Name == "Full Review");
        Assert.Equal($"[View on Steam]({pending.Review.Url})", GetField(embed, "Steam Review").Value);
    }

    private static PendingSteamReview CreatePending(SteamReviewSourceKind sourceKind = SteamReviewSourceKind.User, SteamReviewRecommendation recommendation = SteamReviewRecommendation.Recommended){
        var isCurator = sourceKind == SteamReviewSourceKind.Curator;

        var source = new SteamReviewSource{
            Kind = sourceKind,
            Id = isCurator ? 1_850UL : 76_561_198_297_114_542UL,
            Name = isCurator ? "Test Curator" : "Test Reviewer",
            Url = isCurator ? "https://store.steampowered.com/curator/1850/" : "https://steamcommunity.com/profiles/76561198297114542/",
        };

        var review = new SteamReview{
            Source = source,
            AppId = 570,
            AppName = "Test Game",
            Recommendation = recommendation,
            Text = "A test review.",
            Url = isCurator ? source.Url : "https://steamcommunity.com/profiles/76561198297114542/recommended/570/",
        };
        return new PendingSteamReview(42, 7, review);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static EmbedProperties GetEmbed(MessageProperties message){
        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        return Assert.Single(embeds);
    }

    private static EmbedFieldProperties GetField(EmbedProperties embed, string name){
        var fields = embed.Fields;
        Assert.NotNull(fields);

        return Assert.Single(fields, field => field.Name == name);
    }
}