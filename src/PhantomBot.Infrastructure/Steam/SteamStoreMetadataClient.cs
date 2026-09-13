using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace PhantomBot.Infrastructure.Steam;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed partial class SteamStoreMetadataClient(IHttpClientFactory httpClientFactory, ILogger<SteamStoreMetadataClient> logger) : IDisposable{
    public const string HttpClientName = "SteamStoreMetadata";
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumInlineRetryDelay = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _nextRequestUtc = DateTimeOffset.MinValue;
    private int _disposed;

    internal async Task<SteamStoreMetadata?> GetAsync(uint appId, CancellationToken cancellationToken){
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _requestGate.WaitAsync(cancellationToken);

        try{
            if (!await WaitForRequestWindowAsync(true, cancellationToken)) return null;

            return await SendWithRetryAsync(appId, cancellationToken);
        }
        finally{
            _requestGate.Release();
        }
    }

    private async Task<SteamStoreMetadata?> SendWithRetryAsync(uint appId, CancellationToken cancellationToken){
        var appIdText = appId.ToString(CultureInfo.InvariantCulture);

        using var httpClient = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 0; attempt < 2; attempt++){
            try{
                ExtendCooldown(MinimumRequestInterval);

                using var response = await httpClient.GetAsync($"api/appdetails?appids={appIdText}&l=english", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (TryGetRateLimitDelay(response, out var retryDelay)){
                    ExtendCooldown(retryDelay);

                    LogRateLimited(logger, appId, (int)response.StatusCode, retryDelay.TotalSeconds);

                    if (attempt != 0 || retryDelay > MaximumInlineRetryDelay) return null;

                    await WaitForRequestWindowAsync(false, cancellationToken);

                    continue;
                }

                if (!response.IsSuccessStatusCode){
                    LogUnsuccessfulResponse(logger, appId, (int)response.StatusCode);

                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                if (!document.RootElement.TryGetProperty(appIdText, out var appResponse) || !IsSuccessful(appResponse) || !appResponse.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

                return new SteamStoreMetadata(CleanDescription(GetString(data, "short_description")), GetReleaseDateText(data), GetStringArray(data, "developers"), GetStringArray(data, "publishers"), GetString(data, "header_image"));
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested){
                LogRequestFailed(logger, exception, appId);

                return null;
            }
            catch (HttpRequestException exception){
                LogRequestFailed(logger, exception, appId);

                return null;
            }
            catch (JsonException exception){
                LogRequestFailed(logger, exception, appId);

                return null;
            }
            catch (IOException exception){
                LogRequestFailed(logger, exception, appId);

                return null;
            }
        }
        return null;
    }

    private async Task<bool> WaitForRequestWindowAsync(bool abandonLongCooldown, CancellationToken cancellationToken){
        var remaining = _nextRequestUtc - DateTimeOffset.UtcNow;

        if (remaining <= TimeSpan.Zero) return true;

        if (abandonLongCooldown && remaining > MaximumInlineRetryDelay) return false;

        await Task.Delay(remaining, cancellationToken);

        return true;
    }

    private void ExtendCooldown(TimeSpan delay){
        var proposed = DateTimeOffset.UtcNow + delay;

        if (proposed > _nextRequestUtc) _nextRequestUtc = proposed;
    }

    private static bool TryGetRateLimitDelay(HttpResponseMessage response, out TimeSpan delay){
        var retryAfter = response.Headers.RetryAfter;

        if (response.StatusCode == HttpStatusCode.TooManyRequests || (response.StatusCode == HttpStatusCode.ServiceUnavailable && retryAfter is not null)){
            delay = GetRetryAfterDelay(retryAfter) ?? DefaultRateLimitDelay;

            return true;
        }

        delay = TimeSpan.Zero;
        return false;
    }

    private static TimeSpan? GetRetryAfterDelay(RetryConditionHeaderValue? retryAfter){
        if (retryAfter?.Delta is{ } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter?.Date is not{ } date) return null;

        var delay = date - DateTimeOffset.UtcNow;

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static bool IsSuccessful(JsonElement appResponse) => appResponse.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True;

    private static string? GetString(JsonElement parent, string propertyName){
        if (!parent.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String) return null;

        var result = value.GetString();

        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private static List<string> GetStringArray(JsonElement parent, string propertyName){
        var result = new List<string>();

        if (!parent.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array) return result;

        foreach (var value in values.EnumerateArray()){
            if (value.ValueKind == JsonValueKind.String && value.GetString() is{ } text && !string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
        }

        return result;
    }

    private static string? GetReleaseDateText(JsonElement data){
        if (!data.TryGetProperty("release_date", out var releaseDate) || releaseDate.ValueKind != JsonValueKind.Object) return null;

        var date = GetString(releaseDate, "date");

        if (date is not null) return date;

        return releaseDate.TryGetProperty("coming_soon", out var comingSoon) && comingSoon.ValueKind is JsonValueKind.True ? "To be announced" : null;
    }

    private static string? CleanDescription(string? description){
        if (description is null) return null;

        var withoutTags = HtmlTagRegex().Replace(description, " ");

        var decoded = WebUtility.HtmlDecode(withoutTags);

        var cleaned = string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [LoggerMessage(1100, LogLevel.Warning, "Steam Store metadata request for AppID {AppId} returned HTTP {StatusCode}; continuing with PICS metadata.")]
    private static partial void LogUnsuccessfulResponse(ILogger logger, uint appId, int statusCode);
    [LoggerMessage(1101, LogLevel.Warning, "Steam Store metadata request for AppID {AppId} failed; continuing with PICS metadata.")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, uint appId);
    [LoggerMessage(1102, LogLevel.Warning, "Steam Store metadata request for AppID {AppId} returned HTTP {StatusCode}; applying a {RetryDelaySeconds}-second cooldown.")]
    private static partial void LogRateLimited(ILogger logger, uint appId, int statusCode, double retryDelaySeconds);

    public void Dispose(){
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _requestGate.Dispose();
    }
}

internal sealed record SteamStoreMetadata(string? Description, string? ReleaseDateText, IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, string? ThumbnailUrl);