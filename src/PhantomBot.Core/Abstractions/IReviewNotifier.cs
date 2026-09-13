using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface IReviewNotifier{
    Task<ulong> PostAsync(PendingSteamReview review, CancellationToken cancellationToken);
}