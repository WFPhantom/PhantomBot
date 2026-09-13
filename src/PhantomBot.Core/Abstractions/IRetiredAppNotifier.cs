using PhantomBot.Core.Domain;

namespace PhantomBot.Core.Abstractions;

public interface IRetiredAppNotifier{
    Task<ulong> PostAsync(SteamAppMetadata app, CancellationToken cancellationToken);
    Task<ulong> UpdateAsync(ulong messageId, SteamAppMetadata app, CancellationToken cancellationToken);
}