namespace PhantomBot.Core.Domain;

// Numeric values are persisted in SQLite and MUST NEVER BE REORDERED, RENUMBERED, OR REUSED, hence why the ordering is fucked
public enum SteamAppKind{
    Unknown = 0,
    Game = 1,
    Dlc = 2,
    Beta = 3,
    Other = 4,
    Music = 5,
    Demo = 6,
    Hardware = 7,
    Application = 8,
}

public enum TrackingStatus{
    Seeded = 0,
    PendingMetadata = 1,
    Announced = 2,
    Ignored = 3,
    SeededIncomplete = 4,
}

public sealed record SteamAppMetadata(uint AppId, string? Name, SteamAppKind Kind, uint ChangeNumber){
    public string? Description{ get; init; }
    public string? ReleaseDateText{ get; init; }
    public IReadOnlyList<string> Developers{ get; init; } = [];
    public IReadOnlyList<string> Publishers{ get; init; } = [];
    public string? ThumbnailUrl{ get; init; }
    public bool IsRetired{ get; init; }
}

public sealed record TrackedSteamApp{
    public required uint AppId{ get; init; }
    public string? Name{ get; init; }
    public required SteamAppKind Kind{ get; init; }
    public required TrackingStatus Status{ get; init; }
    public required uint FirstSeenChange{ get; init; }
    public required uint LastSeenChange{ get; init; }
    public ulong? DiscordMessageId{ get; init; }
    public required DateTimeOffset FirstSeenUtc{ get; init; }
    public required DateTimeOffset UpdatedUtc{ get; init; }
    public DateTimeOffset? NextMetadataCheckUtc{ get; init; }
    public required bool IsRetired{ get; init; }
    public ulong? RetirementDiscordMessageId{ get; init; }
}

public sealed record SteamChangeSet(uint CurrentChangeNumber, bool RequiresFullAppUpdate, IReadOnlyDictionary<uint, uint> AppChangeNumbers);

public sealed record SteamAppListEntry(uint AppId, string? Name, string? RawType = null, bool IsRetired = false);