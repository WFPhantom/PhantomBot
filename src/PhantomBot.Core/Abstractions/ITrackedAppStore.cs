using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface ITrackedAppStore{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<long> CountAppsAsync(CancellationToken cancellationToken);
    Task SeedAppsAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint currentChangeNumber, CancellationToken cancellationToken);
    Task<int> MergeRecoveryBaselineAsync(IReadOnlyCollection<SteamAppListEntry> apps, uint baselineChangeNumber, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<uint, TrackedSteamApp>> GetAppsAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<TrackedSteamApp>> GetPendingMetadataAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<uint>> GetPendingRetirementAppIdsAsync(uint afterAppId, int limit, CancellationToken cancellationToken);
    Task<int> InitializeRetirementStatesAsync(IReadOnlyCollection<SteamAppMetadata> apps, CancellationToken cancellationToken);
    Task UpsertAppAsync(TrackedSteamApp app, CancellationToken cancellationToken);
    Task<uint?> GetLastChangeNumberAsync(CancellationToken cancellationToken);
    Task SetLastChangeNumberAsync(uint changeNumber, CancellationToken cancellationToken);
    Task RecordSuccessfulPollAsync(uint changeNumber, DateTimeOffset polledUtc, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastSuccessfulPollUtcAsync(CancellationToken cancellationToken);
}