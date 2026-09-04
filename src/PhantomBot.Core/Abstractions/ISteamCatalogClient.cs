using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface ISteamCatalogClient{
    Task WaitUntilReadyAsync(CancellationToken cancellationToken);
    Task<uint> GetCurrentChangeNumberAsync(CancellationToken cancellationToken);
    Task<SteamChangeSet> GetChangesSinceAsync(uint lastProcessedChangeNumber, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken);
}