using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using PhantomBot.Core.Abstractions;
using PhantomBot.Infrastructure;
using PhantomBot.Infrastructure.Discord;
using PhantomBot.Infrastructure.Persistence;
using PhantomBot.Infrastructure.Steam;
using PhantomBot.Worker.Commands;
using PhantomBot.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["Discord:Token"])) throw new InvalidOperationException("Discord:Token must be set.");

builder.Services.AddOptions<PhantomBotOptions>().Bind(builder.Configuration.GetSection(PhantomBotOptions.SectionName))
    .Validate(static options => options.NewAppDiscordChannelId != 0, "PhantomBot:NewAppDiscordChannelId must be set.")
    .Validate(static options => options.RemovedAppsDiscordChannelId != 0, "PhantomBot:RemovedAppsDiscordChannelId must be set.")
    .Validate(static options => options.ReviewDiscordChannelId != 0, "PhantomBot:ReviewDiscordChannelId must be set.")
    .Validate(static options => options.RemovedAppsDiscordChannelId != options.NewAppDiscordChannelId, "PhantomBot:RemovedAppsDiscordChannelId must be different from PhantomBot:NewAppDiscordChannelId.")
    .Validate(static options => options.ReviewManagerRoleId != 0, "PhantomBot:ReviewManagerRoleId must be set.")
    .Validate(static options => options.PollIntervalSeconds >= 10, "PollIntervalSeconds must be at least 10.")
    .Validate(static options => options.MetadataRetrySeconds >= 10, "MetadataRetrySeconds must be at least 10.")
    .Validate(static options => options.ReviewPollIntervalSeconds >= 10, "ReviewPollIntervalSeconds must be at least 10.")
    .Validate(static options => !string.IsNullOrWhiteSpace(options.DatabasePath), "DatabasePath must be set.")
    .Validate(static options => !string.IsNullOrWhiteSpace(options.BaselineRequestPath), "BaselineRequestPath must be set.")
    .Validate(static options => !string.IsNullOrWhiteSpace(options.BaselinePath), "BaselinePath must be set.")
    .ValidateOnStart();

builder.Services.Configure<HostOptions>(static options => {
    options.ServicesStartConcurrently = false;
});

builder.Services.AddHostedService<ReviewStoreInitializer>();

builder.Services.AddDiscordGateway(static options => options.Intents = GatewayIntents.Guilds);

builder.Services.AddApplicationCommands(static options => options.AutoRegisterCommands = true);

builder.Services.AddHttpClient(SteamStoreMetadataClient.HttpClientName, static client => {
    client.BaseAddress = new Uri("https://store.steampowered.com/", UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomBot/1.0 (+https://github.com/WFPhantom/PhantomBot)");
});

builder.Services.AddHttpClient(SteamReviewClient.HttpClientName, static client => {
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomBot/1.0 (+https://github.com/WFPhantom/PhantomBot)");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
});

builder.Services.AddSingleton<SteamStoreMetadataClient>();
builder.Services.AddSingleton<SteamPicsClient>();
builder.Services.AddSingleton<ISteamCatalogClient>(static services => services.GetRequiredService<SteamPicsClient>());

builder.Services.AddHostedService(static services => services.GetRequiredService<SteamPicsClient>());

builder.Services.AddSingleton<ISteamReviewClient, SteamReviewClient>();
builder.Services.AddSingleton<INewAppNotifier, NetCordAppNotifier>();
builder.Services.AddSingleton<IRetiredAppNotifier, NetCordRetiredAppNotifier>();
builder.Services.AddSingleton<IReviewNotifier, NetCordReviewNotifier>();
builder.Services.AddSingleton<ITrackedAppStore, SqliteTrackedAppStore>();
builder.Services.AddSingleton<IReviewSubscriptionStore, SqliteReviewSubscriptionStore>();
builder.Services.AddSingleton<SteamBaselineCoordinator>();

builder.Services.AddHostedService<NewAppMonitorService>();
builder.Services.AddHostedService<ReviewMonitorService>();

using var host = builder.Build();

host.AddApplicationCommandModule<ReviewModule>();

await host.RunAsync();