using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Commands;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
[SlashCommand("review", "Manage Steam review subscriptions", Contexts = [InteractionContextType.Guild], IntegrationTypes = [ApplicationIntegrationType.GuildInstall])]
public sealed partial class ReviewModule(ISteamReviewClient steam, IReviewSubscriptionStore store, IOptions<PhantomBotOptions> options, IHostApplicationLifetime applicationLifetime, ILogger<ReviewModule> logger) : ApplicationCommandModule<ApplicationCommandContext>{
    private const int SubscriptionsPerPage = 5;
    private readonly ulong _reviewChannelId = options.Value.ReviewDiscordChannelId;
    private readonly ulong _reviewManagerRoleId = options.Value.ReviewManagerRoleId;

    [SubSlashCommand("add", "Follow a Steam user or curator without posting their existing reviews")]
    public Task AddAsync([SlashCommandParameter(Description = "SteamID64, curator ID, or Steam profile/curator URL", MinLength = 1, MaxLength = 2_048)] string source){
        return RunCommandAsync("add", true, async cancellationToken => {
            var resolved = await steam.ResolveSourceAsync(source, cancellationToken);

            var added = await store.TryAddAsync(resolved, DateTimeOffset.UtcNow, cancellationToken);

            var name = EscapeMarkdown(Truncate(resolved.Name, 150));

            var response = added ? $"Added **{name}**. Its existing reviews are being recorded without notifications. Use `/review list` to check when it becomes active." : $"**{name}** is already subscribed. Use `/review list` to check its status.";

            await SetResponseAsync(response, cancellationToken);
        });
    }

    [SubSlashCommand("remove", "Stop following a subscribed Steam user or curator")]
    public Task RemoveAsync([SlashCommandParameter(Description = "Choose a subscription to remove", AutocompleteProviderType = typeof(ReviewSubscriptionAutocompleteProvider), MinLength = 1, MaxLength = 20)] string subscription){
        return RunCommandAsync("remove", true, async cancellationToken => {
            if (!long.TryParse(subscription, NumberStyles.None, CultureInfo.InvariantCulture, out var subscriptionId) || subscriptionId <= 0){
                await SetResponseAsync("Choose a subscription from the autocomplete suggestions.", cancellationToken);

                return;
            }

            var subscriptions = await store.GetSubscriptionsAsync(cancellationToken);

            var existing = subscriptions.FirstOrDefault(item => item.Id == subscriptionId);

            if (existing is null || !await store.RemoveAsync(subscriptionId, cancellationToken)){
                await SetResponseAsync("That subscription has already been removed. Select it again if it was re-added.", cancellationToken);

                return;
            }

            var name = EscapeMarkdown(Truncate(existing.Source.Name, 150));

            await SetResponseAsync($"Removed **{name}**. Re-adding it will seed its current review history again, " + "so reviews published during the gap will not be announced.", cancellationToken);
        });
    }

    [SubSlashCommand("list", "Show review subscriptions and their initialization or retry status")]
    public Task ListAsync([SlashCommandParameter(Description = "Page number", MinValue = 1)] int page = 1){
        return RunCommandAsync("list", false, async cancellationToken => {
            var subscriptions = await store.GetSubscriptionsAsync(cancellationToken);

            if (subscriptions.Count == 0){
                await SetResponseAsync("There are no review subscriptions. A member with the review manager role can add one using `/review add`.", cancellationToken);

                return;
            }

            var ordered = subscriptions.OrderBy(static subscription => subscription.Source.Kind).ThenBy(static subscription => subscription.Source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static subscription => subscription.Id).ToArray();

            var pageCount = (ordered.Length - 1) / SubscriptionsPerPage + 1;

            if (page < 1 || page > pageCount){
                await SetResponseAsync($"Choose a page between 1 and {FormatNumber(pageCount)}.", cancellationToken);

                return;
            }

            var now = DateTimeOffset.UtcNow;

            var fields = ordered.Skip((page - 1) * SubscriptionsPerPage).Take(SubscriptionsPerPage).Select(subscription => new EmbedFieldProperties{
                Name = Truncate(subscription.Source.Name, 150),
                Value = FormatSubscription(subscription, now),
                Inline = false,
            }).ToArray();

            var embed = new EmbedProperties{
                Title = $"Review subscriptions — page {FormatNumber(page)}/{FormatNumber(pageCount)}",
                Description = $"{FormatNumber(ordered.Length)} subscription(s).",
                Color = new Color(0x3498DB),
                Fields = fields,
            };

            await ModifyResponseAsync(message => {
                message.Content = string.Empty;
                message.AllowedMentions = AllowedMentionsProperties.None;
                message.Embeds = [embed];
            }, cancellationToken: cancellationToken);
        });
    }

    private async Task RunCommandAsync(string commandName, bool requiresManagerRole, Func<CancellationToken, Task> action){
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral), cancellationToken: applicationLifetime.ApplicationStopping);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);

        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        try{
            if (_reviewChannelId == 0){
                await SetResponseAsync("The review channel has not been configured.", timeout.Token);

                return;
            }

            if (Context.Guild is not{ } guild){
                await SetResponseAsync("Server information is unavailable; try again shortly.", timeout.Token);

                return;
            }

            if (!guild.Channels.ContainsKey(_reviewChannelId)){
                await SetResponseAsync("Use this command in the server containing the configured review channel.", timeout.Token);

                return;
            }

            if (requiresManagerRole){
                if (_reviewManagerRoleId == 0){
                    await SetResponseAsync("The review manager role has not been configured.", timeout.Token);

                    return;
                }

                if (!HasManagerRole(Context.User, _reviewManagerRoleId)){
                    await SetResponseAsync("You need the configured review manager role to add or remove subscriptions.", timeout.Token);

                    return;
                }
            }
            await action(timeout.Token);
        }
        catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested){
            // The bot is shutting down.
        }
        catch (OperationCanceledException){
            await SetResponseAsync("The request timed out. Check `/review list` before retrying.", applicationLifetime.ApplicationStopping);
        }
        catch (ArgumentException exception){
            await SetResponseAsync(EscapeMarkdown(Truncate(exception.Message, 600)), applicationLifetime.ApplicationStopping);
        }
        catch (Exception exception){
            LogCommandFailed(logger, commandName, exception);

            await SetResponseAsync("The request could not be completed. Check `/review list` before retrying; details were logged.", applicationLifetime.ApplicationStopping);
        }
    }

    internal static bool HasManagerRole(User user, ulong roleId) => roleId != 0 && user is GuildUser member && member.RoleIds.Contains(roleId);

    private Task<RestMessage> SetResponseAsync(string content, CancellationToken cancellationToken){
        return ModifyResponseAsync(message => {
            message.Content = content;
            message.AllowedMentions = AllowedMentionsProperties.None;
            message.Embeds = [];
        }, cancellationToken: cancellationToken);
    }

    private static string FormatSubscription(SteamReviewSubscription subscription, DateTimeOffset now){
        var sourceId = subscription.Source.Id.ToString(CultureInfo.InvariantCulture);
        var builder = new StringBuilder();

        builder.Append(GetSourceLabel(subscription.Source.Kind));
        builder.Append(" · Steam ID: `");
        builder.Append(sourceId);
        builder.AppendLine("`");
        builder.Append("Status: **");
        builder.Append(subscription.Status switch{
            SteamReviewSubscriptionStatus.Initializing => "Initializing",
            SteamReviewSubscriptionStatus.Active => "Active",
            _ => "Unknown",
        });
        builder.AppendLine("**");

        if (subscription.ConsecutiveFailures > 0) builder.AppendLine(subscription.NextCheckUtc > now ? "Waiting to retry." : "Retry is due.");

        builder.Append("Added: ");
        builder.AppendLine(FormatTimestamp(subscription.CreatedUtc));

        builder.Append("Next check: ");
        builder.AppendLine(FormatTimestamp(subscription.NextCheckUtc));

        if (subscription.LastSuccessfulCheckUtc is{ } lastSuccessfulCheck){
            builder.Append("Last successful check: ");
            builder.AppendLine(FormatTimestamp(lastSuccessfulCheck));
        }

        if (subscription.ConsecutiveFailures <= 0) return builder.ToString().TrimEnd();

        builder.Append("Consecutive failures: ");
        builder.AppendLine(FormatNumber(subscription.ConsecutiveFailures));

        if (string.IsNullOrWhiteSpace(subscription.LastError)) return builder.ToString().TrimEnd();

        builder.Append("Last failure: ");
        builder.Append(EscapeMarkdown(Truncate(subscription.LastError, 200)));

        return builder.ToString().TrimEnd();
    }

    internal static string GetSourceLabel(SteamReviewSourceKind kind) => kind switch{
        SteamReviewSourceKind.User => "User",
        SteamReviewSourceKind.Curator => "Curator",
        _ => "Unknown",
    };

    internal static string Truncate(string value, int maximumLength){
        var trimmed = value.Trim();

        if (trimmed.Length <= maximumLength) return trimmed;

        var length = maximumLength - 1;

        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1]) && char.IsLowSurrogate(trimmed[length])) length--;

        return trimmed[..length] + "…";
    }

    private static string EscapeMarkdown(string value){
        var builder = new StringBuilder(value.Length);

        foreach (var character in value){
            if (character is '\\' or '[' or ']' or '(' or ')' or '*' or '_' or '~' or '`' or '|' or '>' or '<' or '#') builder.Append('\\');

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(DateTimeOffset value){
        var timestamp = value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        return $"<t:{timestamp}:R>";
    }

    private static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(EventId = 6100, Level = LogLevel.Error, Message = "The /review {CommandName} command failed.")]
    private static partial void LogCommandFailed(ILogger logger, string commandName, Exception exception);
}

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class ReviewSubscriptionAutocompleteProvider(IReviewSubscriptionStore store, IOptions<PhantomBotOptions> options, ILogger<ReviewSubscriptionAutocompleteProvider> logger) : IAutocompleteProvider<AutocompleteInteractionContext>{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context){
        var reviewChannelId = options.Value.ReviewDiscordChannelId;
        var reviewManagerRoleId = options.Value.ReviewManagerRoleId;

        if (reviewChannelId == 0 || context.Guild is not{ } guild || !guild.Channels.ContainsKey(reviewChannelId) || !ReviewModule.HasManagerRole(context.User, reviewManagerRoleId)) return [];

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try{
            var subscriptions = await store.GetSubscriptionsAsync(timeout.Token);
            var input = option.Value?.Trim() ?? string.Empty;

            return [.. subscriptions.Where(subscription => Matches(subscription, input)).OrderBy(static subscription => subscription.Source.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static subscription => subscription.Id).Take(25).Select(static subscription => new ApplicationCommandOptionChoiceProperties(FormatChoiceName(subscription), subscription.Id.ToString(CultureInfo.InvariantCulture)))];
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested){
            return [];
        }
        catch (Exception exception){
            LogAutocompleteFailed(logger, exception);

            return [];
        }
    }

    private static bool Matches(SteamReviewSubscription subscription, string input) => input.Length == 0 || subscription.Source.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || subscription.Source.Id.ToString(CultureInfo.InvariantCulture).Contains(input, StringComparison.Ordinal) || ReviewModule.GetSourceLabel(subscription.Source.Kind).Contains(input, StringComparison.OrdinalIgnoreCase);

    private static string FormatChoiceName(SteamReviewSubscription subscription){
        var kind = ReviewModule.GetSourceLabel(subscription.Source.Kind);
        var sourceId = subscription.Source.Id.ToString(CultureInfo.InvariantCulture);
        var suffix = $" ({kind} {sourceId})";
        var name = subscription.Source.Name.Replace('\r', ' ').Replace('\n', ' ');

        return ReviewModule.Truncate(name, 100 - suffix.Length) + suffix;
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Review subscription autocomplete failed.")]
    private static partial void LogAutocompleteFailed(ILogger logger, Exception exception);
}