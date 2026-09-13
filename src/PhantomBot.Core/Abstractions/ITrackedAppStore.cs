using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface ITrackedAppStore{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<long> CountAppsAsync(CancellationToken cancellationToken);
    Task SeedAppsAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint currentChangeNumber, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<uint, TrackedSteamApp>> GetAppsAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrackedSteamApp>> GetPendingMetadataAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken);
    Task UpsertAppAsync(TrackedSteamApp app, CancellationToken cancellationToken);
    Task<uint?> GetLastChangeNumberAsync(CancellationToken cancellationToken);
    Task SetLastChangeNumberAsync(uint changeNumber, CancellationToken cancellationToken);
}