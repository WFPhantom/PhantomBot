namespace PhantomBot.Core.Domain;

public static class SteamAppClassifier{
    public static SteamAppKind Classify(string? rawType){
        if (string.IsNullOrWhiteSpace(rawType)) return SteamAppKind.Unknown;

        var type = rawType.AsSpan().Trim();

        if (type.Equals("game", StringComparison.OrdinalIgnoreCase)) return SteamAppKind.Game;

        if (type.Equals("dlc", StringComparison.OrdinalIgnoreCase)) return SteamAppKind.Dlc;

        if (type.Equals("beta", StringComparison.OrdinalIgnoreCase)) return SteamAppKind.Beta;

        if (type.Equals("music", StringComparison.OrdinalIgnoreCase)) return SteamAppKind.Music;

        if (type.Equals("demo", StringComparison.OrdinalIgnoreCase)) return SteamAppKind.Demo;

        return type.Equals("hardware", StringComparison.OrdinalIgnoreCase) ? SteamAppKind.Hardware : SteamAppKind.Other;
    }

    public static bool IsWanted(this SteamAppKind kind) => kind is SteamAppKind.Game or SteamAppKind.Dlc or SteamAppKind.Beta or SteamAppKind.Music or SteamAppKind.Demo or SteamAppKind.Hardware;
}