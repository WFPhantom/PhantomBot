using System.Text;
using NetCord;
using NetCord.Rest;
using PhantomBot.Core.Domain;

namespace PhantomBot.Infrastructure.Discord;

public static class SteamAppMessageFactory{
    private const int MaximumEmbedFieldValueLength = 1_024;

    public static MessageProperties Create(SteamAppMetadata app){
        var displayName = Truncate(app.Name, 256) ?? $"App {app.AppId}";

        return CreateMessage(app, displayName, FormatNotification("New", app, displayName), $"pb-app-{app.AppId}");
    }

    public static MessageProperties CreateRetired(SteamAppMetadata app){
        if (!app.IsRetired) throw new ArgumentException("A retirement message requires retired Steam metadata.", nameof(app));

        var displayName = Truncate(app.Name, 256) ?? $"App {app.AppId}";

        return CreateMessage(app, displayName, FormatNotification("Retired", app, displayName), $"pb-retired-{app.AppId}");
    }

    private static MessageProperties CreateMessage(SteamAppMetadata app, string displayName, string content, string nonce){
        var appUrl = GetAppUrl(app.AppId);
        var fields = new List<EmbedFieldProperties>();

        AddField(fields, "Release Date", app.ReleaseDateText);
        AddField(fields, "Developer", FormatAssociations(app.Developers, "developer"));
        AddField(fields, "Publisher", FormatAssociations(app.Publishers, "publisher"));

        return new MessageProperties{
            Content = content,
            AllowedMentions = AllowedMentionsProperties.None,
            Nonce = new NonceProperties(nonce){
                Unique = true,
            },
            Embeds = [
                new EmbedProperties{
                    Color = GetColor(app.Kind),
                    Author = new EmbedAuthorProperties{
                        Name = $"{GetTypeName(app.Kind)} {app.AppId} • View on SteamDB",
                        Url = appUrl,
                        IconUrl = "https://steamdb.info/static/logos/32px.png",
                    },
                    Title = displayName,
                    Url = appUrl,
                    Description = Truncate(app.Description, 300),
                    Thumbnail = string.IsNullOrWhiteSpace(app.ThumbnailUrl) ? null : new EmbedThumbnailProperties(app.ThumbnailUrl),
                    Fields = fields.Count == 0 ? null : fields,
                },
            ],
        };
    }

    private static string GetAppUrl(uint appId) => $"https://steamdb.info/app/{appId}/";

    private static string FormatNotification(string action, SteamAppMetadata app, string displayName){
        var escapedName = EscapeLinkText(displayName);

        return $"{action} {GetTypeName(app.Kind)}: [{escapedName}]({GetAppUrl(app.AppId)})";
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static void AddField(List<EmbedFieldProperties> fields, string name, string? value){
        var truncated = Truncate(value, MaximumEmbedFieldValueLength);

        if (truncated is null) return;

        fields.Add(new EmbedFieldProperties{
            Name = name,
            Value = truncated,
            Inline = true,
        });
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static string? FormatAssociations(IReadOnlyList<string> associations, string route){
        return associations.Count == 0 ? null : string.Join(", ", associations.Take(4).Select(name => $"[{EscapeLinkText(name)}](https://steamdb.info/{route}/{Uri.EscapeDataString(name)}/)"));
    }

    private static string EscapeLinkText(string value){
        var result = new StringBuilder(value.Length);

        foreach (var character in value){
            if (character is '\\' or '[' or ']' or '(' or ')' or '*' or '_' or '~' or '`') result.Append('\\');

            result.Append(character);
        }

        return result.ToString();
    }

    private static string? Truncate(string? value, int maximumLength){
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();

        if (trimmed.Length <= maximumLength) return trimmed;

        var length = maximumLength - 1;

        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1]) && char.IsLowSurrogate(trimmed[length])) length--;

        return trimmed[..length] + "…";
    }

    private static string GetTypeName(SteamAppKind kind) => kind switch{
        SteamAppKind.Game => "Game",
        SteamAppKind.Dlc => "DLC",
        SteamAppKind.Beta => "Beta",
        SteamAppKind.Music => "Music",
        SteamAppKind.Demo => "Demo",
        SteamAppKind.Hardware => "Hardware",
        SteamAppKind.Application => "Application",
        _ => "App",
    };

    private static Color GetColor(SteamAppKind kind) => kind switch{
        SteamAppKind.Game => new Color(0x5BA32B),
        SteamAppKind.Dlc => new Color(0x9B59B6),
        SteamAppKind.Beta => new Color(0xC9C62D),
        SteamAppKind.Music => new Color(0xD65C84),
        SteamAppKind.Demo => new Color(0x3498DB),
        SteamAppKind.Hardware => new Color(0x95A5A6),
        _ => new Color(0x5865F2),
    };
}