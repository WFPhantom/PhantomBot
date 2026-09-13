using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetCord;
using NetCord.Gateway;
using NetCord.JsonModels;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Core.Exceptions;
using PhantomBot.Infrastructure;
using PhantomBot.Worker.Commands;

namespace PhantomBot.Worker.Tests;

// ReSharper disable once MemberCanBeFileLocal
public sealed class ReviewModuleTests{
    [Theory]
    [InlineData("add", false)]
    [InlineData("add", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    public async Task CommandsRejectMembersWithoutRoleEvenWhenAdministrator(string command, bool administrator){
        using var test = new TestState(false, administrator);

        await test.ExecuteAsync(command);

        Assert.Equal("You need the configured review manager role to add or remove subscriptions.", test.ResponseContent);
        Assert.Equal(0, test.Steam.ResolveCount);
        Assert.Equal(0, test.Store.ReadCount);
        Assert.Equal(0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);
        Assert.Single(test.Store.Subscriptions);

        test.AssertEphemeralDeferral();
    }

    [Fact]
    public async Task MemberWithRoleCanAddSubscription(){
        using var test = new TestState(true);

        test.Store.Subscriptions.Clear();

        await test.ExecuteAsync("add");

        Assert.Equal(1, test.Steam.ResolveCount);
        Assert.Equal(1, test.Store.AddCount);
        Assert.Equal(test.Steam.Source, Assert.Single(test.Store.Subscriptions).Source);
        Assert.Contains("Added **PC Gamer**.", test.ResponseContent, StringComparison.Ordinal);

        test.AssertEphemeralDeferral();
    }

    [Fact]
    public async Task MemberWithRoleCanRemoveSubscription(){
        using var test = new TestState(true);

        await test.ExecuteAsync("remove");

        Assert.Equal(1, test.Store.RemoveCount);
        Assert.Empty(test.Store.Subscriptions);
        Assert.Contains("Removed **PC Gamer**.", test.ResponseContent, StringComparison.Ordinal);

        test.AssertEphemeralDeferral();
    }

    [Fact]
    public async Task DeliberateInputErrorIsEscapedAndShownWithoutWritingSubscription(){
        using var test = new TestState(true);

        test.Steam.ResolveFailure = new SteamReviewInputException("Use a Steam profile [URL].");

        var original = Assert.Single(test.Store.Subscriptions);

        await test.ExecuteAsync("add");

        Assert.Equal(@"Use a Steam profile \[URL\].", test.ResponseContent);
        Assert.Equal(1, test.Steam.ResolveCount);
        Assert.Equal(0, test.Store.ReadCount);
        Assert.Equal(0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);
        Assert.Equal(original, Assert.Single(test.Store.Subscriptions));
        Assert.Empty(test.Logger.Entries);

        test.AssertEphemeralDeferral();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedArgumentErrorIsLoggedAndUsesGenericResponse(bool fromStore){
        using var test = new TestState(true);

        var failure = fromStore ? new ArgumentOutOfRangeException(null, "Internal store details.") : new ArgumentException("Internal resolver details.");

        if (fromStore) test.Store.AddFailure = failure;
        else test.Steam.ResolveFailure = failure;

        var original = Assert.Single(test.Store.Subscriptions);

        await test.ExecuteAsync("add");

        Assert.Equal("The request could not be completed. Check `/review list` before retrying; details were logged.", test.ResponseContent);
        Assert.Equal(1, test.Steam.ResolveCount);
        Assert.Equal(0, test.Store.ReadCount);
        Assert.Equal(fromStore ? 1 : 0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);
        Assert.Equal(original, Assert.Single(test.Store.Subscriptions));

        var entry = Assert.Single(test.Logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);

        test.AssertEphemeralDeferral();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListRemainsAccessibleWithOrWithoutRole(bool hasRole){
        using var test = new TestState(hasRole);

        await test.ExecuteAsync("list");

        Assert.Equal(1, test.Store.ReadCount);
        Assert.Equal(0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);

        var response = Assert.Single(test.Discord.Responses);
        var embed = Assert.Single(response.GetProperty("embeds").EnumerateArray());

        Assert.Equal("Review subscriptions — page 1/1", embed.GetProperty("title").GetString());

        var field = Assert.Single(embed.GetProperty("fields").EnumerateArray());

        Assert.Equal("PC Gamer", field.GetProperty("name").GetString());

        test.AssertEphemeralDeferral();
    }

    [Theory]
    [InlineData(SteamReviewSubscriptionStatus.Initializing, false)]
    [InlineData(SteamReviewSubscriptionStatus.Initializing, true)]
    [InlineData(SteamReviewSubscriptionStatus.Active, false)]
    [InlineData(SteamReviewSubscriptionStatus.Active, true)]
    public async Task ListDescribesUnclearedFailureWithoutInferringRetryProgress(SteamReviewSubscriptionStatus status, bool checkInFuture){
        using var test = new TestState(false);

        var now = DateTimeOffset.UtcNow;

        var subscription = test.Store.Subscriptions[0] with{
            Status = status,
            NextCheckUtc = checkInFuture ? now.AddHours(1) : now.AddHours(-1),
            ConsecutiveFailures = 2,
            LastError = "Steam response was incomplete.",
            LastSuccessfulCheckUtc = status == SteamReviewSubscriptionStatus.Active ? now.AddHours(-2) : null,
        };

        test.Store.Subscriptions[0] = subscription;

        await test.ExecuteAsync("list");

        var response = Assert.Single(test.Discord.Responses);
        var embed = Assert.Single(response.GetProperty("embeds").EnumerateArray());
        var field = Assert.Single(embed.GetProperty("fields").EnumerateArray());
        var value = field.GetProperty("value").GetString();

        Assert.NotNull(value);

        var expectedStatus = status == SteamReviewSubscriptionStatus.Active ? "Active" : "Initializing";

        Assert.Contains($"Status: **{expectedStatus}**", value, StringComparison.Ordinal);
        Assert.Contains("The last attempt failed; no subsequent full scan has completed successfully.", value, StringComparison.Ordinal);
        Assert.Contains("Next scheduled check: ", value, StringComparison.Ordinal);
        Assert.Contains("Consecutive failed attempts: 2", value, StringComparison.Ordinal);
        Assert.Contains("Last failure: Steam response was incomplete.", value, StringComparison.Ordinal);
        Assert.DoesNotContain("Waiting to retry.", value, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry is due.", value, StringComparison.Ordinal);
        Assert.Equal(subscription, Assert.Single(test.Store.Subscriptions));
        Assert.Equal(0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);

        test.AssertEphemeralDeferral();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedAutocompleteReturnsNoChoicesWithoutReadingStore(
        bool administrator){
        using var test = new TestState(false, administrator);

        var choices = await test.AutocompleteAsync("PC");

        Assert.Empty(choices);
        Assert.Equal(0, test.Store.ReadCount);
        Assert.Empty(test.Discord.Responses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pc")]
    [InlineData("1850")]
    [InlineData("curator")]
    public async Task AuthorizedAutocompleteFindsSubscription(string input){
        using var test = new TestState(true);

        var choices = await test.AutocompleteAsync(input);
        var choice = Assert.Single(choices);

        Assert.Equal("PC Gamer (Curator 1850)", choice.Name);
        Assert.Equal("17", choice.StringValue);
        Assert.Equal(1, test.Store.ReadCount);
    }

    [Fact]
    public async Task AuthorizedAutocompleteReturnsNoChoicesForNonMatchingInput(){
        using var test = new TestState(true);

        var choices = await test.AutocompleteAsync("no-matching-subscription");

        Assert.Empty(choices);
        Assert.Equal(1, test.Store.ReadCount);
        Assert.Empty(test.Discord.Responses);
    }

    [Theory]
    [InlineData("unconfigured", "The review channel has not been configured.")]
    [InlineData("unavailable", "Server information is unavailable; try again shortly.")]
    [InlineData("wrong-server", "Use this command in the server containing the configured review channel.")]
    public async Task InvalidChannelOrGuildPreventsCommandAndAutocomplete(
        string scenario,
        string expectedResponse){
        using var test = new TestState(true, guildAvailable: scenario != "unavailable", channelInGuild: scenario != "wrong-server");

        if (scenario == "unconfigured") test.Settings.ReviewDiscordChannelId = 0;

        await test.ExecuteAsync("add");

        Assert.Equal(expectedResponse, test.ResponseContent);
        Assert.Equal(0, test.Steam.ResolveCount);
        Assert.Equal(0, test.Store.AddCount);

        var choices = await test.AutocompleteAsync(string.Empty);

        Assert.Empty(choices);
        Assert.Equal(0, test.Store.ReadCount);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task UnconfiguredRoleDeniesChangesAndAutocomplete(string command){
        using var test = new TestState(true);

        test.Settings.ReviewManagerRoleId = 0;

        await test.ExecuteAsync(command);

        Assert.Equal("The review manager role has not been configured.", test.ResponseContent);
        Assert.Equal(0, test.Steam.ResolveCount);
        Assert.Equal(0, test.Store.ReadCount);
        Assert.Equal(0, test.Store.AddCount);
        Assert.Equal(0, test.Store.RemoveCount);
        Assert.Empty(await test.AutocompleteAsync(string.Empty));
        Assert.Equal(0, test.Store.ReadCount);
    }

    private sealed class TestState : IDisposable{
        private const ulong GuildId = 100;
        private const ulong ChannelId = 200;
        private const ulong ManagerRoleId = 300;
        private readonly GatewayClient _gateway;
        private readonly ServiceProvider _services;
        private readonly Guild? _guild;
        private readonly ulong[] _roles;
        private readonly Permissions _permissions;
        private readonly List<InteractionCallbackProperties> _callbacks = [];
        private readonly ApplicationCommandService<ApplicationCommandContext, AutocompleteInteractionContext> _commands;

        public TestState(bool hasRole, bool administrator = false, bool guildAvailable = true, bool channelInGuild = true){
            _roles = hasRole ? [ManagerRoleId] : [999UL];
            _permissions = administrator ? Permissions.Administrator : default;

            Settings = new PhantomBotOptions{
                ReviewDiscordChannelId = ChannelId,
                ReviewManagerRoleId = ManagerRoleId,
            };

            _gateway = new GatewayClient(
                new BotToken("MTIz.test.signature"),
                new GatewayClientConfiguration{
                    Intents = GatewayIntents.Guilds,
                    RestClientConfiguration = new RestClientConfiguration{
                        RequestHandler = Discord,
                    },
                });

            if (guildAvailable){
                _guild = new Guild(
                    new JsonGuild{
                        Id = GuildId,
                        Name = "Test server",
                        Channels = channelInGuild
                            ? [
                                new JsonChannel{
                                    Id = ChannelId,
                                    Type = ChannelType.TextGuildChannel,
                                    Name = "reviews",
                                }
                            ]
                            : [],
                    },
                    123,
                    _gateway.Rest,
                    IDictionaryProvider.OfDictionary);
            }

            Store.Subscriptions.Add(CreateSubscription(Steam.Source));

            var services = new ServiceCollection();

            services.AddSingleton<ISteamReviewClient>(Steam);
            services.AddSingleton<IReviewSubscriptionStore>(Store);
            services.AddSingleton(Options.Create(Settings));
            services.AddSingleton<IHostApplicationLifetime>(new TestApplicationLifetime());
            services.AddSingleton<ILogger<ReviewModule>>(Logger);
            services.AddSingleton<ILogger<ReviewSubscriptionAutocompleteProvider>>(NullLogger<ReviewSubscriptionAutocompleteProvider>.Instance);

            _services = services.BuildServiceProvider();

            // ReSharper disable once ArrangeObjectCreationWhenTypeNotEvident
            _commands = new(ApplicationCommandServiceConfiguration<ApplicationCommandContext>.Default with{
                Storage = new NameAndTypeApplicationCommandServiceStorage<ApplicationCommandContext>(),
            });
            _commands.AddModule<ReviewModule>();
        }

        public PhantomBotOptions Settings{ get; }
        public FakeSteam Steam{ get; } = new();
        public FakeStore Store{ get; } = new();
        public FakeDiscord Discord{ get; } = new();
        public TestLogger<ReviewModule> Logger{ get; } = new();

        public string ResponseContent{
            get{
                var response = Assert.Single(Discord.Responses);
                var content = response.GetProperty("content").GetString();

                Assert.NotNull(content);

                return content;
            }
        }

        // ReSharper disable ArrangeObjectCreationWhenTypeNotEvident
        public async Task ExecuteAsync(string command){
            JsonApplicationCommandInteractionDataOption[] arguments = command switch{
                "add" =>[
                    new(){
                        Name = "source",
                        Type = ApplicationCommandOptionType.String,
                        Value = "1850",
                    }
                ],
                "remove" =>[
                    new(){
                        Name = "subscription",
                        Type = ApplicationCommandOptionType.String,
                        Value = "17",
                    }
                ],
                "list" =>[],
                _ => throw new ArgumentException("Unknown test command.", nameof(command)),
            };

            var interaction = new SlashCommandInteraction(CreateInteraction(command, arguments, InteractionType.ApplicationCommand), _guild, CaptureResponseAsync, _gateway.Rest);

            var result = await _commands.ExecuteAsync(new ApplicationCommandContext(interaction, _gateway), _services);

            AssertSucceeded(result);
        }

        public async Task<ApplicationCommandOptionChoiceProperties[]> AutocompleteAsync(string input){
            _callbacks.Clear();

            var interaction = new AutocompleteInteraction(
                CreateInteraction(
                    "remove",
                    [
                        new JsonApplicationCommandInteractionDataOption{
                            Name = "subscription",
                            Type = ApplicationCommandOptionType.String,
                            Value = input,
                            Focused = true,
                        }
                    ],
                    InteractionType.Autocomplete),
                _guild,
                CaptureResponseAsync,
                _gateway.Rest);

            var result = await _commands.ExecuteAutocompleteAsync(new AutocompleteInteractionContext(interaction, _gateway), _services);

            AssertSucceeded(result);

            var callback = Assert.IsType<InteractionCallbackProperties<InteractionCallbackChoicesDataProperties>>(Assert.Single(_callbacks));

            var choices = callback.Data.Choices;
            Assert.NotNull(choices);

            return [.. choices];
        }

        public void AssertEphemeralDeferral(){
            var callback = Assert.IsType<InteractionCallbackProperties<InteractionMessageProperties>>(Assert.Single(_callbacks));

            Assert.Equal(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral).Type, callback.Type);

            Assert.Equal(MessageFlags.Ephemeral, callback.Data.Flags);
        }

        private JsonInteraction CreateInteraction(string command, JsonApplicationCommandInteractionDataOption[] arguments, InteractionType type) =>
            new(){
                Id = 500,
                ApplicationId = 123,
                Type = type,
                Token = "test-interaction",
                GuildId = GuildId,
                Context = InteractionContextType.Guild,
                Channel = new JsonChannel{
                    Id = ChannelId,
                    Type = ChannelType.TextGuildChannel,
                    Name = "reviews",
                },
                GuildUser = new JsonGuildUser{
                    User = new JsonUser{
                        Id = 400,
                        Username = "Test member",
                    },
                    RoleIds = _roles,
                    Permissions = _permissions,
                },
                Entitlements = [],
                Data = new JsonInteractionData{
                    Name = "review",
                    Type = ApplicationCommandType.ChatInput,
                    Options = [
                        new JsonApplicationCommandInteractionDataOption{
                            Name = command,
                            Type = ApplicationCommandOptionType.SubCommand,
                            Options = arguments,
                        }
                    ],
                },
            };

        private Task<InteractionCallbackResponse?> CaptureResponseAsync(IInteraction interaction, InteractionCallbackProperties callback, bool withResponse, RestRequestProperties? properties, CancellationToken cancellationToken){
            _callbacks.Add(callback);

            return Task.FromResult<InteractionCallbackResponse?>(null);
        }

        private static void AssertSucceeded(IExecutionResult result){
            Assert.False(result is IFailResult, result is IFailResult failure ? failure.Message : null);
        }

        public void Dispose(){
            _services.Dispose();
            _gateway.Dispose();
        }
    }

    private static SteamReviewSubscription CreateSubscription(SteamReviewSource source){
        var now = DateTimeOffset.UtcNow;

        return new SteamReviewSubscription{
            Id = 17,
            Source = source,
            Status = SteamReviewSubscriptionStatus.Initializing,
            CreatedUtc = now,
            NextCheckUtc = now,
        };
    }

    private sealed class FakeSteam : ISteamReviewClient{
        public SteamReviewSource Source{ get; } = new(){
            Kind = SteamReviewSourceKind.Curator,
            Id = 1850,
            Name = "PC Gamer",
            Url = "https://store.steampowered.com/curator/1850/",
        };

        public int ResolveCount{ get; private set; }
        public Exception? ResolveFailure{ get; set; }

        public Task<SteamReviewSource> ResolveSourceAsync(string input, CancellationToken cancellationToken){
            ResolveCount++;

            if (ResolveFailure is{ } failure) throw failure;

            return Task.FromResult(Source);
        }

        public Task<SteamReviewPage> GetReviewPageAsync(SteamReviewSource source, string? cursor, CancellationToken cancellationToken) => throw new InvalidOperationException("Commands must not scan review pages.");
    }

    private sealed class FakeStore : IReviewSubscriptionStore{
        public List<SteamReviewSubscription> Subscriptions{ get; } = [];
        public int ReadCount{ get; private set; }
        public int AddCount{ get; private set; }
        public int RemoveCount{ get; private set; }
        public Exception? AddFailure{ get; set; }

        public Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsAsync(CancellationToken cancellationToken){
            ReadCount++;

            return Task.FromResult<IReadOnlyList<SteamReviewSubscription>>([.. Subscriptions]);
        }

        public Task<bool> TryAddAsync(SteamReviewSource source, DateTimeOffset now, CancellationToken cancellationToken){
            AddCount++;

            if (AddFailure is{ } failure) throw failure;

            if (Subscriptions.Any(subscription => subscription.Source.Kind == source.Kind && subscription.Source.Id == source.Id)) return Task.FromResult(false);

            Subscriptions.Add(CreateSubscription(source));

            return Task.FromResult(true);
        }

        public Task<bool> RemoveAsync(long subscriptionId, CancellationToken cancellationToken){
            RemoveCount++;

            return Task.FromResult(Subscriptions.RemoveAll(subscription => subscription.Id == subscriptionId) != 0);
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SteamReviewSubscription>> GetSubscriptionsByIdsAsync(IReadOnlyCollection<long> subscriptionIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SteamReviewSubscription>> GetDueSubscriptionsAsync(DateTimeOffset dueAt, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ScheduleNextPageAsync(long subscriptionId, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int?> SeedReviewPageAsync(long subscriptionId, IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompleteInitializationAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> EnqueueNewReviewsAsync(long subscriptionId, IReadOnlyCollection<SteamReview> reviews, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CompletePollAsync(long subscriptionId, SteamReviewSource source, DateTimeOffset completedUtc, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RecordFailureAsync(long subscriptionId, string failureReason, DateTimeOffset nextCheckUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PendingSteamReview>> GetPendingReviewsAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> MarkReviewPostedAsync(long pendingReviewId, ulong discordMessageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestLogger<T> : ILogger<T>{
        public List<(LogLevel Level, Exception? Exception)> Entries{ get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter){
            Entries.Add((logLevel, exception));
        }
    }

    private sealed class FakeDiscord : IRestRequestHandler{
        public List<JsonElement> Responses{ get; } = [];

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default){
            using (request){
                Assert.Equal(HttpMethod.Patch, request.Method);

                var content = request.Content;
                Assert.NotNull(content);

                using var document = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));

                Responses.Add(document.RootElement.Clone());
            }

            return new HttpResponseMessage(HttpStatusCode.OK){
                Content = new StringContent(
                    """
                    {
                      "id": "600",
                      "channel_id": "200",
                      "author": {"id": "123", "username": "PhantomBot", "bot": true},
                      "content": "",
                      "mentions": [],
                      "mention_roles": [],
                      "attachments": [],
                      "embeds": [],
                      "components": []
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        public void AddDefaultHeader(string name, IEnumerable<string> values){
        }

        public void Dispose(){
        }
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime{
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication(){
        }
    }
}