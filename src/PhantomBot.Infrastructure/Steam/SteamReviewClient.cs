using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Core.Exceptions;

namespace PhantomBot.Infrastructure.Steam;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class SteamReviewClient(IHttpClientFactory httpClientFactory) : ISteamReviewClient{
    public const string HttpClientName = "SteamReviews";
    private const int CuratorPageSize = 100;
    private const ulong IndividualSteamIdBase = 76_561_197_960_265_728;

    public Task<SteamReviewSource> ResolveSourceAsync(string input, CancellationToken cancellationToken){
        if (string.IsNullOrWhiteSpace(input)) throw new SteamReviewInputException("Enter a SteamID64, curator ID, or Steam profile/curator URL.");

        var value = input.Trim();

        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)){
            if (IsIndividualSteamId(numericId)) return ResolveUserAsync($"profiles/{FormatId(numericId)}/", numericId, cancellationToken);

            return numericId is > 0 and <= uint.MaxValue ? ResolveCuratorAsync(numericId, cancellationToken) : throw new SteamReviewInputException("Enter a SteamID64, curator ID, or Steam profile/curator URL.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsWebUri(uri) || !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new SteamReviewInputException("Enter a valid Steam profile or curator URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2){
            if (segments[0].Equals("profiles", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) && IsIndividualSteamId(steamId)) return ResolveUserAsync($"profiles/{FormatId(steamId)}/", steamId, cancellationToken);

            if (segments[0].Equals("id", StringComparison.OrdinalIgnoreCase)){
                var vanityName = Uri.UnescapeDataString(segments[1]);

                if (string.IsNullOrWhiteSpace(vanityName) || vanityName.Contains('/', StringComparison.Ordinal) || vanityName.Contains('\\', StringComparison.Ordinal)) throw new SteamReviewInputException("The Steam vanity URL is invalid.");

                return ResolveUserAsync($"id/{Uri.EscapeDataString(vanityName)}/", null, cancellationToken);
            }
        }

        if (!uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase)) throw new SteamReviewInputException("Use a Steam profile URL, user review URL, curator URL, or Store URL containing curator_clanid.");

        if (segments.Length >= 2 && segments[0].Equals("curator", StringComparison.OrdinalIgnoreCase) && TryParseCuratorSegment(segments[1], out var curatorId)) return ResolveCuratorAsync(curatorId, cancellationToken);

        if (segments.Length < 2 || !segments[0].Equals("app", StringComparison.OrdinalIgnoreCase)) throw new SteamReviewInputException("Use a Steam profile URL, user review URL, curator URL, or Store URL containing curator_clanid.");

        var curatorValue = GetQueryParameter(uri, "curator_clanid");

        if (uint.TryParse(curatorValue, NumberStyles.None, CultureInfo.InvariantCulture, out var linkedCuratorId) && linkedCuratorId != 0) return ResolveCuratorAsync(linkedCuratorId, cancellationToken);

        throw new SteamReviewInputException("Use a Steam profile URL, user review URL, curator URL, or Store URL containing curator_clanid.");
    }

    public Task<SteamReviewPage> GetReviewPageAsync(SteamReviewSource source, string? cursor, CancellationToken cancellationToken){
        ArgumentNullException.ThrowIfNull(source);

        return source.Kind switch{
            SteamReviewSourceKind.User when IsIndividualSteamId(source.Id) => GetUserReviewPageAsync(source, ParseCursor(cursor, 1), cancellationToken),
            SteamReviewSourceKind.Curator when source.Id is > 0 and <= uint.MaxValue => GetCuratorReviewPageAsync(source, ParseCursor(cursor, 0), cancellationToken),
            _ => throw new ArgumentException("The review source has an invalid kind or ID.", nameof(source)),
        };
    }

    private async Task<SteamReviewSource> ResolveUserAsync(string profilePath, ulong? expectedId, CancellationToken cancellationToken){
        var html = await GetTextAsync($"https://steamcommunity.com/{profilePath}?l=english", cancellationToken);

        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);

        RejectPrivateProfile(document);

        using var profileData = ReadProfileData(document);

        var root = profileData.RootElement;
        var steamIdText = ReadRequiredString(root, "steamid");
        var name = ReadRequiredString(root, "personaname").Trim();

        if (!ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) || !IsIndividualSteamId(steamId) || (expectedId is not null && expectedId.Value != steamId) || name.Length == 0) throw new InvalidDataException("Steam returned invalid or mismatched profile identity data.");

        return new SteamReviewSource{
            Kind = SteamReviewSourceKind.User,
            Id = steamId,
            Name = name,
            Url = GetProfileUrl(steamId),
        };
    }

    private async Task<SteamReviewSource> ResolveCuratorAsync(ulong curatorId, CancellationToken cancellationToken){
        var url = GetCuratorUrl(curatorId);
        var html = await GetTextAsync($"{url}?l=english", cancellationToken);

        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);

        var nameLink = RequireElement(document, ".curator_name a");
        var name = ReadText(nameLink);

        if (name.Length == 0 || !Uri.TryCreate(nameLink.GetAttribute("href"), UriKind.Absolute, out var identityUri) || !identityUri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Steam did not return a recognizable curator profile.");

        var segments = identityUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 2 || !segments[0].Equals("curator", StringComparison.OrdinalIgnoreCase) || !TryParseCuratorSegment(segments[1], out var returnedId) || returnedId != curatorId) throw new InvalidDataException("Steam returned a different curator than the one requested.");

        return new SteamReviewSource{
            Kind = SteamReviewSourceKind.Curator,
            Id = curatorId,
            Name = name,
            Url = url,
        };
    }

    private async Task<SteamReviewPage> GetUserReviewPageAsync(SteamReviewSource source, int page, CancellationToken cancellationToken){
        var url = $"{GetProfileUrl(source.Id)}recommended/?l=english&p={FormatNumber(page)}";
        var html = await GetTextAsync(url, cancellationToken);

        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);

        RejectPrivateProfile(document);

        var list = RequireElement(document, "#tabs_basebg.review_list");
        var contents = RequireElement(list, "#leftContents");
        var total = ReadUserReviewCount(list);
        var reviews = new List<SteamReview>();
        var appIds = new HashSet<uint>();

        foreach (var box in contents.QuerySelectorAll(".review_box")){
            var capsuleLink = RequireElement(box, ".leftcol a.game_capsule_ctn");
            var appId = ReadAppIdFromUrl(capsuleLink.GetAttribute("href"));
            var recommendation = ReadRecommendation(ReadText(RequireElement(box, ".vote_header .title")));
            var text = ReadText(RequireElement(box, ".rightcol .content"));
            var image = box.QuerySelector("img.game_capsule");

            if (!appIds.Add(appId)) throw new InvalidDataException("Steam returned duplicate user reviews within one page.");

            reviews.Add(new SteamReview{
                Source = source,
                AppId = appId,
                AppName = NullIfWhiteSpace(image?.GetAttribute("alt")),
                Recommendation = recommendation,
                Text = text,
                Url = $"{GetProfileUrl(source.Id)}recommended/{FormatId(appId)}/",
                ThumbnailUrl = OptionalWebUrl(image?.GetAttribute("src")),
            });
        }

        if (total == 0){
            if (page != 1 || reviews.Count != 0) throw new InvalidDataException("Steam's empty review count conflicts with the requested page or its contents.");

            return new SteamReviewPage(reviews, null);
        }

        if (reviews.Count == 0 || reviews.Count > total)
            throw new InvalidDataException("Steam's review count does not match the parsed review list.");

        var paging = contents.QuerySelector(".workshopBrowsePagingInfo");

        if (paging is null){
            if (page == 1 && reviews.Count == total) return new SteamReviewPage(reviews, null);

            throw new InvalidDataException("Steam's user review pagination is missing.");
        }

        var match = UserPagingPattern().Match(ReadText(paging));

        if (!match.Success) throw new InvalidDataException("Steam's user review pagination format was not recognized.");

        var first = ParseDisplayedNumber(match.Groups[1].Value);
        var last = ParseDisplayedNumber(match.Groups[2].Value);
        var pagingTotal = ParseDisplayedNumber(match.Groups[3].Value);

        var currentPage = RequireElement(contents, ".workshopBrowsePagingControls .page_current");

        if (ParseDisplayedNumber(ReadText(currentPage)) != page || first < 1 || last < first || last > total || pagingTotal != total || last - first + 1 != reviews.Count || (page == 1 && first != 1) || (page > 1 && first == 1)) throw new InvalidDataException("Steam returned inconsistent user review pagination.");

        var nextCursor = last < total ? FormatNumber(checked(page + 1)) : null;

        return new SteamReviewPage(reviews, nextCursor);
    }

    private async Task<SteamReviewPage> GetCuratorReviewPageAsync(SteamReviewSource source, int start, CancellationToken cancellationToken){
        var url = $"{GetCuratorUrl(source.Id)}ajaxgetfilteredrecommendations/" + $"?start={FormatNumber(start)}&count={FormatNumber(CuratorPageSize)}&sort=recent&l=english";

        var json = await GetTextAsync(url, cancellationToken);

        using var response = JsonDocument.Parse(json);

        var root = response.RootElement;

        if (ReadRequiredInteger(root, "success") != 1) throw new InvalidDataException("Steam did not successfully return the curator's reviews.");

        var total = ReadRequiredInteger(root, "total_count");
        var returnedStart = ReadRequiredInteger(root, "start");
        var pageSize = ReadRequiredInteger(root, "pagesize");
        var html = ReadRequiredString(root, "results_html");

        if (total < 0 || returnedStart != start || start > total || pageSize is <= 0 or > CuratorPageSize) throw new InvalidDataException("Steam returned invalid curator review pagination.");

        using var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken);

        var reviews = new List<SteamReview>();
        var appIds = new HashSet<uint>();

        foreach (var item in document.QuerySelectorAll(".recommendation")){
            var capsule = RequireElement(item, "[data-ds-appid]");
            var appIdText = capsule.GetAttribute("data-ds-appid");

            if (!uint.TryParse(appIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0 || !appIds.Add(appId)) throw new InvalidDataException("Steam returned an invalid or duplicate curator review AppID.");

            var recommendation = ReadRecommendation(ReadText(RequireElement(item, ".recommendation_type_ctn > span")));
            var text = ReadText(RequireElement(item, ".recommendation_desc"));
            var image = capsule.QuerySelector("img");
            var externalLink = item.QuerySelector(".recommendation_readmore a[href]:not(.recommendation_link)");

            reviews.Add(new SteamReview{
                Source = source,
                AppId = appId,
                AppName = NullIfWhiteSpace(image?.GetAttribute("alt")),
                Recommendation = recommendation,
                Text = text,
                Url = GetCuratorUrl(source.Id),
                ExternalReviewUrl = OptionalWebUrl(externalLink?.GetAttribute("href")),
                ThumbnailUrl = OptionalWebUrl(image?.GetAttribute("src")),
            });
        }

        var expectedCount = Math.Min(pageSize, total - start);

        if (reviews.Count != expectedCount) throw new InvalidDataException("Steam's curator review count does not match the parsed HTML.");

        if (reviews.Count == 0){
            if (start != 0 || total != 0) throw new InvalidDataException("The curator review list changed during pagination; restart the scan.");

            return new SteamReviewPage(reviews, null);
        }

        var nextStart = checked(start + reviews.Count);
        var nextCursor = nextStart < total ? FormatNumber(nextStart) : null;

        return new SteamReviewPage(reviews, nextCursor);
    }

    private async Task<string> GetTextAsync(string url, CancellationToken cancellationToken){
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, cancellationToken);

        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("Steam returned an empty response.") : text;
    }

    private static JsonDocument ReadProfileData(IParentNode document){
        foreach (var bytes in from script in document.QuerySelectorAll("script") select script.TextContent into text let match = ProfileDataPattern().Match(text) where match.Success select Encoding.UTF8.GetBytes(text[(match.Index + match.Length)..])){
            var reader = new Utf8JsonReader(bytes);

            return JsonDocument.ParseValue(ref reader);
        }

        throw new InvalidDataException("Steam's profile identity data was not found.");
    }

    private static int ReadUserReviewCount(IParentNode list){
        // ReSharper disable once LoopCanBePartlyConvertedToQuery
        foreach (var statistic in list.QuerySelectorAll(".review_stat")){
            var label = statistic.QuerySelector(".giantNumberSubhead");

            if (label is null || !ReadText(label).Equals("Products reviewed", StringComparison.OrdinalIgnoreCase)) continue;

            return ParseDisplayedNumber(ReadText(RequireElement(statistic, ".giantNumber")));
        }

        throw new InvalidDataException("Steam's explicit user review count was not found.");
    }

    private static SteamReviewRecommendation ReadRecommendation(string value) => value switch{
        "Recommended" => SteamReviewRecommendation.Recommended,
        "Not Recommended" => SteamReviewRecommendation.NotRecommended,
        "Informational" => SteamReviewRecommendation.Informational,
        _ => throw new InvalidDataException($"Steam returned an unrecognized review recommendation: {value}"),
    };

    private static void RejectPrivateProfile(IParentNode document){
        if (document.QuerySelector(".profile_private_info") is not null) throw new InvalidDataException("The Steam profile is private or its reviews are not publicly accessible.");
    }

    private static IElement RequireElement(IParentNode parent, string selector) => parent.QuerySelector(selector) ?? throw new InvalidDataException($"Steam's response is missing the expected element: {selector}");

    private static string ReadText(INode node){
        var builder = new StringBuilder();

        AppendText(node, builder);

        return WhitespacePattern().Replace(builder.ToString(), " ").Trim();
    }

    private static void AppendText(INode node, StringBuilder builder){
        switch (node){
            case IText text:
                builder.Append(text.Data);

                return;
            case IElement{ LocalName: "script" or "style" or "textarea" }:
                return;
            case IElement{ LocalName: "br" }:
                builder.Append(' ');

                return;
            case IElement element:{
                var separatesText = element.LocalName is "p" or "div" or "li" or "blockquote";

                if (separatesText) builder.Append(' ');

                foreach (var child in element.ChildNodes) AppendText(child, builder);

                if (separatesText) builder.Append(' ');

                return;
            }
        }

        foreach (var child in node.ChildNodes) AppendText(child, builder);
    }

    private static uint ReadAppIdFromUrl(string? value){
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsWebUri(uri) || (!uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("store.steampowered.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Steam returned an unrecognized review app link.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 2 && segments[0].Equals("app", StringComparison.OrdinalIgnoreCase) && uint.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId != 0) return appId;

        throw new InvalidDataException("Steam returned an unrecognized review app link.");
    }

    private static int ReadRequiredInteger(JsonElement parent, string propertyName){
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(propertyName, out var value)) throw new InvalidDataException($"Steam's response is missing {propertyName}.");

        if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) || (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number))) return number;

        throw new InvalidDataException($"Steam returned an invalid integer for {propertyName}.");
    }

    private static string ReadRequiredString(JsonElement parent, string propertyName){
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is{ } text) return text;

        throw new InvalidDataException($"Steam's response is missing a string value for {propertyName}.");
    }

    private static int ParseDisplayedNumber(string value){
        var normalized = value.Replace(",", string.Empty, StringComparison.Ordinal).Trim();

        return int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException("Steam returned an unrecognized review count.");
    }

    private static int ParseCursor(string? cursor, int firstValue){
        if (cursor is null) return firstValue;

        if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= firstValue) return value;

        throw new ArgumentException("The review pagination cursor is invalid.", nameof(cursor));
    }

    private static bool TryParseCuratorSegment(string segment, out uint curatorId){
        var separator = segment.IndexOf('-', StringComparison.Ordinal);
        var idText = separator < 0 ? segment : segment[..separator];

        return uint.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out curatorId) && curatorId != 0;
    }

    private static string? GetQueryParameter(Uri uri, string name) => (from part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries) let separator = part.IndexOf('=', StringComparison.Ordinal) where separator >= 0 let key = Uri.UnescapeDataString(part[..separator]) where key.Equals(name, StringComparison.OrdinalIgnoreCase) select Uri.UnescapeDataString(part[(separator + 1)..])).FirstOrDefault();
    private static string? OptionalWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsWebUri(uri) ? uri.AbsoluteUri : null;
    private static bool IsWebUri(Uri uri) => uri.Scheme is "https" or "http";
    private static bool IsIndividualSteamId(ulong steamId) => steamId is > IndividualSteamIdBase and <= IndividualSteamIdBase + uint.MaxValue;
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string GetProfileUrl(ulong steamId) => $"https://steamcommunity.com/profiles/{FormatId(steamId)}/";
    private static string GetCuratorUrl(ulong curatorId) => $"https://store.steampowered.com/curator/{FormatId(curatorId)}/";
    private static string FormatId(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\bg_rgProfileData\s*=\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileDataPattern();
    [GeneratedRegex(@"^Showing\s+([\d,]+)\s*-\s*([\d,]+)\s+of\s+([\d,]+)\s+entries$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPagingPattern();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}