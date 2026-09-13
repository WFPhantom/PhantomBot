using Microsoft.Extensions.Options;
using NetCord.Rest;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Discord;

// ReSharper disable once PrimaryConstructorParameterCaptureDisallowed
public sealed class NetCordReviewNotifier(RestClient restClient, IOptions<PhantomBotOptions> options) : IReviewNotifier{
    private readonly ulong _channelId = options.Value.ReviewDiscordChannelId;

    public async Task<ulong> PostAsync(PendingSteamReview review, CancellationToken cancellationToken){
        var message = SteamReviewMessageFactory.Create(review);
        var result = await restClient.SendMessageAsync(_channelId, message, cancellationToken: cancellationToken);

        return result.Id;
    }
}