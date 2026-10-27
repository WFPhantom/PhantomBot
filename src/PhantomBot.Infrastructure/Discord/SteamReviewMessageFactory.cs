using System.Globalization;
using System.Text;
using NetCord;
using NetCord.Rest;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Discord;

public static class SteamReviewMessageFactory{
    private const int MaximumNameLength = 256;
    private const int MaximumReviewTextLength = 1_700;
    private const int MaximumLinkLength = 1_024;
    private const int MaximumDescriptionLength = 4_096;
    private const int MaximumEmbedTotalLength = 6_000;
    private const string LinkSeparator = "\n\n";

    public static MessageProperties Create(PendingSteamReview pendingReview){
        ArgumentNullException.ThrowIfNull(pendingReview);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pendingReview.Id);

        var review = pendingReview.Review;
        ArgumentNullException.ThrowIfNull(review);

        var source = review.Source;
        ArgumentNullException.ThrowIfNull(source);

        if (review.AppId == 0) throw new ArgumentException("A review must have a non-zero AppID.", nameof(pendingReview));

        var sourceName = Truncate(source.Name, MaximumNameLength) ?? throw new ArgumentException("The review source must have a name.", nameof(pendingReview));
        var appId = review.AppId.ToString(CultureInfo.InvariantCulture);
        var appName = Truncate(review.AppName, MaximumNameLength) ?? $"App {appId}";
        var appUrl = $"https://store.steampowered.com/app/{appId}/";
        var sourceUrl = RequireWebUrl(source.Url);
        var recommendation = GetRecommendationLabel(review.Recommendation);
        var link = CreateFullReviewLink(review);

        var linkLength = link is null ? 0 : link.Length + LinkSeparator.Length;
        var budget = Math.Min(MaximumDescriptionLength, MaximumEmbedTotalLength - sourceName.Length - appName.Length - recommendation.Length) - linkLength;
        var text = Truncate(review.Text, Math.Max(1, Math.Min(MaximumReviewTextLength, budget / 2)));
        var escapedText = text is null ? null : EscapeMarkdown(text);

        var description = (escapedText, link) switch{
            (null, null) => null,
            (null, _) => link,
            (_, null) => escapedText,
            _ => escapedText + LinkSeparator + link,
        };

        var thumbnailUrl = string.IsNullOrWhiteSpace(review.ThumbnailUrl) ? null : RequireWebUrl(review.ThumbnailUrl);
        var pendingId = pendingReview.Id.ToString(CultureInfo.InvariantCulture);

        return new MessageProperties{
            AllowedMentions = AllowedMentionsProperties.None,
            Nonce = new NonceProperties($"pb-rv-{pendingId}"){
                Unique = true,
            },
            Embeds = [
                new EmbedProperties{
                    Color = GetColor(review.Recommendation),
                    Author = new EmbedAuthorProperties{
                        Name = sourceName,
                        Url = sourceUrl,
                    },
                    Title = appName,
                    Url = appUrl,
                    Description = description,
                    Thumbnail = thumbnailUrl is null ? null : new EmbedThumbnailProperties(thumbnailUrl),
                    Footer = new EmbedFooterProperties{
                        Text = recommendation,
                    },
                    Timestamp = review.PublishedUtc,
                },
            ],
        };
    }

    private static string? CreateFullReviewLink(SteamReview review){
        var url = review.Source.Kind switch{
            SteamReviewSourceKind.User => RequireWebUrl(review.Url),
            SteamReviewSourceKind.Curator => string.IsNullOrWhiteSpace(review.ExternalReviewUrl) ? null : RequireWebUrl(review.ExternalReviewUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(review), review.Source.Kind, "Unknown review source kind."),
        };

        if (url is null) return null;

        var link = $"[Read the full review]({EscapeUrl(url)})";

        return link.Length > MaximumLinkLength ? null : link;
    }

    private static string GetRecommendationLabel(SteamReviewRecommendation recommendation) => recommendation switch{
        SteamReviewRecommendation.Recommended => "Recommended",
        SteamReviewRecommendation.NotRecommended => "Not Recommended",
        SteamReviewRecommendation.Informational => "Informational",
        _ => throw new ArgumentOutOfRangeException(nameof(recommendation), recommendation, "Unknown review recommendation."),
    };

    private static Color GetColor(SteamReviewRecommendation recommendation) => recommendation switch{
        SteamReviewRecommendation.Recommended => new Color(0x5BA32B),
        SteamReviewRecommendation.NotRecommended => new Color(0xD9534F),
        SteamReviewRecommendation.Informational => new Color(0x3498DB),
        _ => throw new ArgumentOutOfRangeException(nameof(recommendation), recommendation, "Unknown review recommendation."),
    };

    private static string EscapeUrl(string url) => url.Replace("\\", "%5C", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);

    private static string RequireWebUrl(string? value){
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") return uri.AbsoluteUri;

        throw new ArgumentException("Review links and images must use HTTP or HTTPS URLs.", nameof(value));
    }

    private static string EscapeMarkdown(string value){
        var result = new StringBuilder(value.Length);

        foreach (var character in value){
            if (character is '\\' or '[' or ']' or '(' or ')' or '*' or '_' or '~' or '`' or '|' or '>' or '<' or '#') result.Append('\\');

            result.Append(character);
        }

        return result.ToString();
    }

    private static string? Truncate(string? value, int maximumLength){
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        if (trimmed.Length <= maximumLength) return trimmed;

        var length = maximumLength - 1;

        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1]) && char.IsLowSurrogate(trimmed[length])) length--;

        return trimmed[..length] + "…";
    }
}