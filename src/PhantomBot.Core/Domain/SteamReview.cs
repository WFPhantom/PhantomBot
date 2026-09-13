namespace PhantomBot.Core.Domain;

public enum SteamReviewSourceKind{
    User = 0,
    Curator = 1,
}

public enum SteamReviewRecommendation{
    Recommended = 0,
    NotRecommended = 1,
    Informational = 2,
}

public sealed record SteamReviewSource{
    public required SteamReviewSourceKind Kind{ get; init; }
    public required ulong Id{ get; init; }
    public required string Name{ get; init; }
    public required string Url{ get; init; }
}

public sealed record SteamReview{
    public required SteamReviewSource Source{ get; init; }
    public required uint AppId{ get; init; }
    public string? AppName{ get; init; }
    public required SteamReviewRecommendation Recommendation{ get; init; }
    public required string Text{ get; init; }
    public required string Url{ get; init; }
    public string? ExternalReviewUrl{ get; init; }
    public string? ThumbnailUrl{ get; init; }
    public DateTimeOffset? PublishedUtc{ get; init; }
}

public sealed record SteamReviewPage(IReadOnlyList<SteamReview> Reviews, string? NextCursor);
public sealed record PendingSteamReview(long Id, long SubscriptionId, SteamReview Review);