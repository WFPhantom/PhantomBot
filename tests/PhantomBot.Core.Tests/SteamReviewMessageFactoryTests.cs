using System.Text.Json;
using NetCord;
using NetCord.Rest;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure.Discord;

namespace PhantomBot.Core.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class SteamReviewMessageFactoryTests{
    private const string UserReviewUrl = "https://steamcommunity.com/profiles/76561198297114542/recommended/570/";
    private const string Separator = "\n\n";

    [Fact]
    public void CreateBuildsUserReviewMessage(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                ThumbnailUrl = "https://example.com/capsule.jpg",
            },
        };

        var message = SteamReviewMessageFactory.Create(pending);
        var embed = GetEmbed(message);
        Assert.Null(message.Content);

        var author = embed.Author;
        Assert.NotNull(author);
        Assert.Equal("Test Reviewer", author.Name);
        Assert.Equal(pending.Review.Source.Url, author.Url);
        Assert.Equal("Test Game", embed.Title);
        Assert.Equal("https://store.steampowered.com/app/570/", embed.Url);
        Assert.Equal("A test review." + Separator + FullReviewLink(UserReviewUrl), embed.Description);
        var thumbnail = embed.Thumbnail;
        Assert.NotNull(thumbnail);
        Assert.Equal("https://example.com/capsule.jpg", thumbnail.Url);

        var footer = embed.Footer;
        Assert.NotNull(footer);
        Assert.Equal("Recommended", footer.Text);

        Assert.Null(embed.Fields);
        Assert.Null(embed.Timestamp);
    }

    [Theory]
    [InlineData(SteamReviewRecommendation.Recommended, "Recommended", 0x5BA32B)]
    [InlineData(SteamReviewRecommendation.NotRecommended, "Not Recommended", 0xD9534F)]
    [InlineData(SteamReviewRecommendation.Informational, "Informational", 0x3498DB)]
    public void CreateUsesExpectedRecommendationLabelAndColor(SteamReviewRecommendation recommendation, string expectedLabel, int expectedColor){
        var embed = GetEmbed(SteamReviewMessageFactory.Create(CreatePending(recommendation: recommendation)));

        var footer = embed.Footer;
        Assert.NotNull(footer);
        Assert.Equal(expectedLabel, footer.Text);
        Assert.Equal(new Color(expectedColor), embed.Color);
    }

    [Fact]
    public void CuratorReviewLinksToExternalReview(){
        var pending = CreatePending(SteamReviewSourceKind.Curator);

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/full-review",
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        var author = embed.Author;
        Assert.NotNull(author);
        Assert.Equal("Test Curator", author.Name);
        Assert.Equal(pending.Review.Source.Url, author.Url);
        Assert.Equal("A test review." + Separator + FullReviewLink("https://example.com/full-review"), embed.Description);
    }

    [Fact]
    public void CuratorReviewWithoutExternalReviewHasNoLink(){
        var embed = GetEmbed(SteamReviewMessageFactory.Create(CreatePending(SteamReviewSourceKind.Curator)));

        Assert.Equal("A test review.", embed.Description);
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
        Assert.Equal(0, root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
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
        Assert.Equal(FullReviewLink(UserReviewUrl), embed.Description);
        Assert.Null(embed.Thumbnail);
    }

    [Fact]
    public void CuratorReviewWithoutTextOrExternalReviewHasNoDescription(){
        var pending = CreatePending(SteamReviewSourceKind.Curator);

        pending = pending with{
            Review = pending.Review with{
                Text = "",
            },
        };

        Assert.Null(GetEmbed(SteamReviewMessageFactory.Create(pending)).Description);
    }

    [Fact]
    public void CreateEscapesMarkdownInReviewTextOnly(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Source = pending.Review.Source with{
                    Name = "Reviewer [One]",
                },
                Text = "Good *game* [really].",
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        var author = embed.Author;
        Assert.NotNull(author);
        Assert.Equal("Reviewer [One]", author.Name);
        Assert.Equal(@"Good \*game\* \[really\]." + Separator + FullReviewLink(UserReviewUrl), embed.Description);
    }

    [Fact]
    public void CreateDoesNotSplitSurrogatePairWhenTruncatingAppName(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                AppName = new string('a', 254) + "😀b",
            },
        };

        Assert.Equal(new string('a', 254) + "…", GetEmbed(SteamReviewMessageFactory.Create(pending)).Title);
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

        Assert.Equal(new string('a', 1_698) + "…" + Separator + FullReviewLink(UserReviewUrl), embed.Description);
    }

    [Fact]
    public void CreateKeepsEscapedReviewTextAndLinkWithinEmbedLimits(){
        var pending = CreatePending();

        pending = pending with{
            Review = pending.Review with{
                Source = pending.Review.Source with{
                    Name = new string('s', 300),
                },
                AppName = new string('n', 300),
                Text = new string('*', 5_000),
            },
        };

        var embed = GetEmbed(SteamReviewMessageFactory.Create(pending));

        var description = embed.Description;
        Assert.NotNull(description);
        Assert.EndsWith(Separator + FullReviewLink(UserReviewUrl), description, StringComparison.Ordinal);
        Assert.InRange(description.Length, 1, 4_096);

        var author = embed.Author;
        var footer = embed.Footer;
        Assert.NotNull(author);
        Assert.NotNull(footer);

        var total = (author.Name?.Length ?? 0) + (embed.Title?.Length ?? 0) + description.Length + (footer.Text?.Length ?? 0);

        Assert.InRange(total, 1, 6_000);
    }

    [Fact]
    public void CreateEscapesParenthesesInExternalReviewLink(){
        var pending = CreatePending(SteamReviewSourceKind.Curator);

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/review_(full)",
            },
        };

        var description = GetEmbed(SteamReviewMessageFactory.Create(pending)).Description;

        Assert.NotNull(description);
        Assert.EndsWith("[Read the full review](https://example.com/review_%28full%29)", description, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateOmitsOversizedExternalLinkInsteadOfTruncatingIt(){
        var pending = CreatePending(SteamReviewSourceKind.Curator);

        pending = pending with{
            Review = pending.Review with{
                ExternalReviewUrl = "https://example.com/" + new string('a', 1_100),
            },
        };

        Assert.Equal("A test review.", GetEmbed(SteamReviewMessageFactory.Create(pending)).Description);
    }

    private static string FullReviewLink(string url) => $"[Read the full review]({url})";

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
            Url = isCurator ? source.Url : UserReviewUrl,
        };
        return new PendingSteamReview(42, 7, review);
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static EmbedProperties GetEmbed(MessageProperties message){
        var embeds = message.Embeds;
        Assert.NotNull(embeds);

        return Assert.Single(embeds);
    }
}