using System.Globalization;
using Microsoft.Extensions.Options;
using NetCord.Rest;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Discord;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed class NetCordRetiredAppNotifier(RestClient restClient, IOptions<PhantomBotOptions> options) : IRetiredAppNotifier{
    private const int UnknownMessageErrorCode = 10_008;
    private readonly ulong _channelId = options.Value.RemovedAppsDiscordChannelId;

    public async Task<ulong> PostAsync(SteamAppMetadata app, CancellationToken cancellationToken){
        var message = SteamAppMessageFactory.CreateRetired(app);
        var result = await restClient.SendMessageAsync(_channelId, message, cancellationToken: cancellationToken);

        return result.Id;
    }

    public async Task<ulong> UpdateAsync(ulong messageId, SteamAppMetadata app, CancellationToken cancellationToken){
        var updated = SteamAppMessageFactory.CreateRetired(app);

        try{
            await restClient.ModifyMessageAsync(_channelId, messageId, message => {
                message.Content = updated.Content;
                message.AllowedMentions = updated.AllowedMentions;
                message.Embeds = updated.Embeds;
            }, cancellationToken: cancellationToken);
            return messageId;
        }
        catch (RestException exception) when (exception.Error?.Code == UnknownMessageErrorCode){
            var replacement = SteamAppMessageFactory.CreateRetired(app);

            replacement.Nonce = new NonceProperties($"pb-r-{messageId.ToString(CultureInfo.InvariantCulture)}"){
                Unique = true,
            };

            var result = await restClient.SendMessageAsync(_channelId, replacement, cancellationToken: cancellationToken);

            return result.Id;
        }
    }
}