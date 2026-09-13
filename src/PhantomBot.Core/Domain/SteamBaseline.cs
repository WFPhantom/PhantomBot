namespace PhantomBot.Core.Domain;

public static class SteamBaselineFormat{
    public const int SchemaVersion = 2;
    public const int MinimumAppCount = 10_000;

    public static bool ContainsMusicCoverageSentinel(IEnumerable<SteamAppListEntry> apps){
        ArgumentNullException.ThrowIfNull(apps);

        return apps.Any(static app => app.AppId is 1_421_480 or 2_690_100 || SteamAppClassifier.Classify(app.RawType) == SteamAppKind.Music);
    }
}

public sealed class SteamBaselineRequest{
    public required int SchemaVersion{ get; init; }
    public required string RequestId{ get; init; }
    public required uint StartChangeNumber{ get; init; }
    public required DateTimeOffset CreatedUtc{ get; init; }
}

public sealed class SteamBaselineDocument{
    public required int SchemaVersion{ get; init; }
    public required string RequestId{ get; init; }
    public required uint StartChangeNumber{ get; init; }
    public required DateTimeOffset CreatedUtc{ get; init; }
    public required IReadOnlyList<SteamAppListEntry> Apps{ get; init; }
}

public sealed class SteamBaselineScanState{
    public required int SchemaVersion{ get; init; }
    public required string RequestId{ get; init; }
    public required uint StartChangeNumber{ get; init; }
    public uint NextAppId{ get; init; } = 1;
    public int EmptyCheckpointCount{ get; init; }
    public required IReadOnlyList<SteamAppListEntry> Apps{ get; init; }
}