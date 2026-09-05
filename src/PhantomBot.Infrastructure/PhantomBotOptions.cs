namespace PhantomBot.Infrastructure;

// ReSharper disable PropertyCanBeMadeInitOnly.Global
public sealed class PhantomBotOptions{
    public const string SectionName = "PhantomBot";
    public ulong NewAppDiscordChannelId{ get; set; }
    public int PollIntervalSeconds{ get; set; } = 15;
    public int MetadataRetrySeconds{ get; set; } = 60;
    public bool PostUnknownApps{ get; set; }
    public string DatabasePath{ get; set; } = "data/phantombot.db";
    public string BaselineRequestPath{ get; set; } = "data/steam-baseline-request.json";
    public string BaselinePath{ get; set; } = "data/steam-app-baseline.json";
}