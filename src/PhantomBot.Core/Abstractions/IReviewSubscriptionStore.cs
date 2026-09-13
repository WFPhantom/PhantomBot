using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface IReviewSubscriptionStore{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamReviewSubscription>> GetDueSubscriptionsAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken);
    Task<bool> TryAddAsync(SteamReviewSource source, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(long subscriptionId, CancellationToken cancellationToken);
    Task<int?> SeedReviewPageAsync(long subscriptionId, IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken);
    Task<bool> CompleteInitializationAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken);
    Task<bool> EnqueueNewReviewsAsync(long subscriptionId, IReadOnlyCollection<SteamReview> reviews, CancellationToken cancellationToken);
    Task<bool> CompletePollAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken);
    Task<bool> RecordFailureAsync(long subscriptionId, string failureReason, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingSteamReview>> GetPendingReviewsAsync(int limit, CancellationToken cancellationToken);
    Task<bool> MarkReviewPostedAsync(long pendingReviewId, ulong discordMessageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsByIdsAsync(IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken);
    Task<bool> ScheduleNextPageAsync(long subscriptionId, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken);
}