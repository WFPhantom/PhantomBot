namespace PhantomBot.Core.Domain;

public enum SteamReviewSubscriptionStatus{
    Initializing = 0,
    Active = 1,
}

public sealed record SteamReviewSubscription{
    public required long Id{ get; init; }
    public required SteamReviewSource Source{ get; init; }
    public required SteamReviewSubscriptionStatus Status{ get; init; }
    public required DateTimeOffset CreatedUtc{ get; init; }
    public required DateTimeOffset NextCheckUtc{ get; init; }
    public DateTimeOffset? LastSuccessfulCheckUtc{ get; init; }
    public int ConsecutiveFailures{ get; init; }
    public string? LastError{ get; init; }
}