using System.Globalization;
using System.Text;
using NetCord;
using NetCord.Rest;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Discord;

public static class SteamReviewMessageFactory{
    private const int MaximumNameLength = 256;
    private const int MaximumReviewTextLength = 1_700;
    private const int MaximumFieldValueLength = 1_024;

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
        var reviewUrl = RequireWebUrl(review.Url);
        var recommendation = GetRecommendationLabel(review.Recommendation);
        var text = Truncate(review.Text, MaximumReviewTextLength);
        var fields = new List<EmbedFieldProperties>{
            new(){
                Name = "Recommendation",
                Value = recommendation,
                Inline = true,
            },
            new(){
                Name = "Source",
                Value = GetSourceLabel(source.Kind),
                Inline = true,
            },
        };

        AddLinkField(fields, "Steam Review", "View on Steam", reviewUrl);

        if (!string.IsNullOrWhiteSpace(review.ExternalReviewUrl)) AddLinkField(fields, "Full Review", "Read the full review", RequireWebUrl(review.ExternalReviewUrl));

        if (review.PublishedUtc is{ } publishedUtc){
            var timestamp = publishedUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            fields.Add(new EmbedFieldProperties{
                Name = "Posted",
                Value = $"<t:{timestamp}:f>",
                Inline = true,
            });
        }

        var thumbnailUrl = string.IsNullOrWhiteSpace(review.ThumbnailUrl) ? null : RequireWebUrl(review.ThumbnailUrl);

        var pendingId = pendingReview.Id.ToString(CultureInfo.InvariantCulture);

        return new MessageProperties{
            Content = $"New review by {EscapeMarkdown(sourceName)}",
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
                    Description = text is null ? null : EscapeMarkdown(text),
                    Thumbnail = thumbnailUrl is null ? null : new EmbedThumbnailProperties(thumbnailUrl),
                    Fields = fields,
                },
            ],
        };
    }

    private static string GetRecommendationLabel(SteamReviewRecommendation recommendation) => recommendation switch{
        SteamReviewRecommendation.Recommended => "Recommended",
        SteamReviewRecommendation.NotRecommended => "Not Recommended",
        SteamReviewRecommendation.Informational => "Informational",
        _ => throw new ArgumentOutOfRangeException(nameof(recommendation), recommendation, "Unknown review recommendation."),
    };

    private static string GetSourceLabel(SteamReviewSourceKind kind) => kind switch{
        SteamReviewSourceKind.User => "Steam User",
        SteamReviewSourceKind.Curator => "Steam Curator",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown review source kind."),
    };

    private static Color GetColor(SteamReviewRecommendation recommendation) => recommendation switch{
        SteamReviewRecommendation.Recommended => new Color(0x5BA32B),
        SteamReviewRecommendation.NotRecommended => new Color(0xD9534F),
        SteamReviewRecommendation.Informational => new Color(0x3498DB),
        _ => throw new ArgumentOutOfRangeException(nameof(recommendation), recommendation, "Unknown review recommendation."),
    };

    // ReSharper disable once SuggestBaseTypeForParameter
    private static void AddLinkField(List<EmbedFieldProperties> fields, string name, string label, string url){
        var escapedUrl = url.Replace("\\", "%5C", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);

        var value = $"[{label}]({escapedUrl})";

        if (value.Length > MaximumFieldValueLength) return;

        fields.Add(new EmbedFieldProperties{
            Name = name,
            Value = value,
            Inline = false,
        });
    }

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