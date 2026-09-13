using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface ISteamReviewClient{
    Task<SteamReviewSource> ResolveSourceAsync(string input, CancellationToken cancellationToken);
    Task<SteamReviewPage> GetReviewPageAsync(SteamReviewSource source, string? cursor, CancellationToken cancellationToken);
}