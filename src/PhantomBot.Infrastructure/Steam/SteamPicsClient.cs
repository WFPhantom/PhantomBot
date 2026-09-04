using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhantomBot.Core.Abstractions;
using PhantomBot.Core.Domain;
using SteamKit2;

namespace PhantomBot.Infrastructure.Steam;

public sealed partial class SteamPicsClient : ISteamCatalogClient, IHostedService, IDisposable{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan OperationalReadinessTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan PicsOperationTimeout = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly SteamClient _client = new();
    private readonly CallbackManager _callbacks;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;
    private readonly SteamStoreMetadataClient? _storeMetadataClient;
    private readonly ILogger<SteamPicsClient> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _reconnectSync = new();

    private TaskCompletionSource<bool> _ready = NewReadySource();
    private Task? _reconnectTask;
    private Task? _callbackTask;
    private volatile bool _stopping;
    private int _disposed;

    public SteamPicsClient(ILogger<SteamPicsClient> logger, SteamStoreMetadataClient? storeMetadataClient = null){
        _logger = logger;
        _storeMetadataClient = storeMetadataClient;

        _steamUser = _client.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler is unavailable.");

        _steamApps = _client.GetHandler<SteamApps>() ?? throw new InvalidOperationException("SteamApps handler is unavailable.");

        _callbacks = new CallbackManager(_client);

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);

        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        _callbacks.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    }

    public async Task StartAsync(CancellationToken cancellationToken){
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _callbackTask = Task.Run(RunCallbacksAsync, CancellationToken.None);

        LogConnecting(_logger);
        _client.Connect();

        await Volatile.Read(ref _ready).Task.WaitAsync(StartupTimeout, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken){
        if (!_stopping){
            _stopping = true;

            if (_client.IsConnected) _steamUser.LogOff();

            _client.Disconnect();
            await _shutdown.CancelAsync();
        }

        Task? reconnectTask;

        lock (_reconnectSync){
            reconnectTask = _reconnectTask;
        }

        if (reconnectTask is not null) await reconnectTask.WaitAsync(cancellationToken);

        if (_callbackTask is not null) await _callbackTask.WaitAsync(cancellationToken);
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => Volatile.Read(ref _ready).Task.WaitAsync(OperationalReadinessTimeout, cancellationToken);

    public async Task<uint> GetCurrentChangeNumberAsync(CancellationToken cancellationToken){
        await WaitUntilReadyAsync(cancellationToken);

        var changesJob = _steamApps.PICSGetChangesSince(0, false);

        changesJob.Timeout = PicsOperationTimeout;

        var changes = await changesJob.ToTask().WaitAsync(cancellationToken);

        return changes.CurrentChangeNumber;
    }

    public async Task<SteamChangeSet> GetChangesSinceAsync(uint lastProcessedChangeNumber, CancellationToken cancellationToken){
        await WaitUntilReadyAsync(cancellationToken);

        var changesJob = _steamApps.PICSGetChangesSince(lastProcessedChangeNumber);

        changesJob.Timeout = PicsOperationTimeout;

        var changes = await changesJob.ToTask().WaitAsync(cancellationToken);

        var appChanges = changes.AppChanges.ToDictionary(static item => item.Key, static item => item.Value.ChangeNumber);

        return new SteamChangeSet(changes.CurrentChangeNumber, changes.RequiresFullUpdate || changes.RequiresFullAppUpdate, appChanges);
    }

    public async Task<IReadOnlyDictionary<uint, SteamAppMetadata>> GetAppMetadataAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken){
        if (appIds.Count == 0) return new Dictionary<uint, SteamAppMetadata>();

        await WaitUntilReadyAsync(cancellationToken);

        var tokensJob = _steamApps.PICSGetAccessTokens(appIds, []);

        tokensJob.Timeout = PicsOperationTimeout;

        var tokens = await tokensJob.ToTask().WaitAsync(cancellationToken);

        var requests = appIds.Select(appId => new SteamApps.PICSRequest(appId, tokens.AppTokens.GetValueOrDefault(appId))).ToArray();

        var productInfoJob = _steamApps.PICSGetProductInfo(requests, []);

        productInfoJob.Timeout = PicsOperationTimeout;

        var responses = await productInfoJob.ToTask().WaitAsync(cancellationToken);

        if (responses.Results is not{ } productInfoResponses) throw new InvalidDataException("Steam returned no product-information response.");

        var result = new Dictionary<uint, SteamAppMetadata>();

        // ReSharper disable once LoopCanBePartlyConvertedToQuery
        foreach (var response in productInfoResponses){
            foreach (var product in response.Apps.Values) result[product.ID] = ReadMetadata(product);
        }

        if (_storeMetadataClient is not null) await AddStoreMetadataAsync(result, _storeMetadataClient, cancellationToken);

        return result;
    }

    private static async Task AddStoreMetadataAsync(Dictionary<uint, SteamAppMetadata> apps, SteamStoreMetadataClient storeMetadataClient, CancellationToken cancellationToken){
        foreach (var appId in apps.Keys.ToArray()){
            var pics = apps[appId];

            if (!pics.Kind.IsWanted()) continue;

            var store = await storeMetadataClient.GetAsync(appId, cancellationToken);

            if (store is null) continue;

            apps[appId] = pics with{
                Description = store.Description ?? pics.Description,

                ReleaseDateText = store.ReleaseDateText ?? pics.ReleaseDateText,

                Developers = store.Developers.Count > 0 ? store.Developers : pics.Developers,

                Publishers = store.Publishers.Count > 0 ? store.Publishers : pics.Publishers,

                ThumbnailUrl = pics.ThumbnailUrl ?? store.ThumbnailUrl,
            };
        }
    }

    public async Task<IReadOnlyList<SteamAppListEntry>> ScanAppIdsAsync(uint firstAppId, int count, CancellationToken cancellationToken){
        ArgumentOutOfRangeException.ThrowIfZero(firstAppId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await WaitUntilReadyAsync(cancellationToken);

        var requests = new SteamApps.PICSRequest[count];

        for (var index = 0; index < requests.Length; index++) requests[index] = new SteamApps.PICSRequest(checked(firstAppId + (uint)index));

        var productInfoJob = _steamApps.PICSGetProductInfo(requests, []);

        productInfoJob.Timeout = PicsOperationTimeout;

        var responses = await productInfoJob.ToTask().WaitAsync(cancellationToken);

        if (responses.Results is not{ } productInfoResponses) throw new InvalidDataException($"Steam returned no product-information response for the AppID range beginning at {firstAppId}.");

        var apps = new Dictionary<uint, SteamAppListEntry>();

        foreach (var response in productInfoResponses){
            foreach (var product in response.Apps.Values){
                var common = product.KeyValues["common"];

                var name = Normalize(common["name"].AsString());

                var rawType = Normalize(common["type"].AsString());

                apps.TryAdd(product.ID, new SteamAppListEntry(product.ID, name, rawType));
            }
        }
        return [.. apps.Values.OrderBy(static app => app.AppId)];
    }

    private async Task RunCallbacksAsync(){
        var shutdownToken = _shutdown.Token;

        while (!shutdownToken.IsCancellationRequested){
            try{
                await _callbacks.RunWaitCallbackAsync(shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested){
                return;
            }
            catch (Exception exception){
                LogCallbackProcessingFailed(_logger, exception);

                try{
                    await Task.Delay(ReconnectDelay, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested){
                    return;
                }
            }
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback _){
        if (_stopping){
            _client.Disconnect();
            return;
        }

        LogConnected(_logger);
        _steamUser.LogOnAnonymous();
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback){
        if (callback.Result == EResult.OK){
            LogSessionReady(_logger);

            Volatile.Read(ref _ready).TrySetResult(true);

            return;
        }

        LogLogonFailed(_logger, callback.Result);
        _client.Disconnect();
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback){
        if (!_stopping) LogLoggedOff(_logger, callback.Result);
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback _){
        if (_stopping) return;

        LogDisconnected(_logger);

        lock (_reconnectSync){
            var ready = Volatile.Read(ref _ready);

            if (ready.Task.IsCompleted) Volatile.Write(ref _ready, NewReadySource());

            if (_reconnectTask is null || _reconnectTask.IsCompleted) _reconnectTask = ReconnectAsync();
        }
    }

    private async Task ReconnectAsync(){
        while (!_stopping){
            try{
                await Task.Delay(ReconnectDelay, _shutdown.Token);

                if (_stopping) return;

                if (!_client.IsConnected) _client.Connect();

                await WaitUntilReadyAsync(_shutdown.Token);
                return;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested){
                return;
            }
            catch (Exception exception){
                LogReconnectFailed(_logger, exception);

                if (_client.IsConnected) _client.Disconnect();
            }
        }
    }

    private static TaskCompletionSource<bool> NewReadySource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static SteamAppMetadata ReadMetadata(SteamApps.PICSProductInfoCallback.PICSProductInfo product){
        var common = product.KeyValues["common"];

        var extended = product.KeyValues["extended"];

        var name = Normalize(common["name"].AsString());

        var rawType = Normalize(common["type"].AsString());

        return new SteamAppMetadata(product.ID, name, SteamAppClassifier.Classify(rawType), product.ChangeNumber){
            ReleaseDateText = GetReleaseDateText(common),

            Developers = GetAssociations(common, extended, "developer"),

            Publishers = GetAssociations(common, extended, "publisher"),

            ThumbnailUrl = GetThumbnailUrl(product.ID, common),
        };
    }

    private static string? GetLocalizedValue(KeyValue value){
        var result = Normalize(value["english"].AsString()) ?? Normalize(value["en"].AsString()) ?? Normalize(value.AsString());

        if (result is not null) return result;

        foreach (var child in value.Children){
            result = Normalize(child.AsString());

            if (result is not null) return result;
        }

        return null;
    }

    private static List<string> GetAssociations(KeyValue common, KeyValue extended, string associationType){
        var names = new List<string>();

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var association in common["associations"].Children){
            var type = Normalize(association["type"].AsString());

            var name = Normalize(association["name"].AsString());

            if (name is not null && string.Equals(type, associationType, StringComparison.OrdinalIgnoreCase) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        if (names.Count != 0) return names;

        var fallback = FirstNonBlank(Normalize(extended[associationType].AsString()), Normalize(common[associationType].AsString()));

        if (fallback is not null) names.Add(fallback);

        return names;
    }

    private static string? GetReleaseDateText(KeyValue common){
        var rawDate = FirstNonBlank(Normalize(common["steam_release_date"].AsString()), Normalize(common["store_release_date"].AsString()));

        if (long.TryParse(rawDate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)){
            if (seconds > 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

            rawDate = null;
        }

        if (rawDate is not null) return rawDate;

        var releaseState = FirstNonBlank(Normalize(common["releasestate"].AsString()), Normalize(common["ReleaseState"].AsString()));

        return string.Equals(releaseState, "prerelease", StringComparison.OrdinalIgnoreCase) ? "To be announced" : null;
    }

    private static string? GetThumbnailUrl(uint appId, KeyValue common){
        var assetPath = FirstNonBlank(GetLocalizedValue(common["library_assets_full"]["library_capsule"]["image"]), GetLocalizedValue(common["header_image"]), GetLocalizedValue(common["small_capsule"]));

        if (assetPath is null) return null;

        if (Uri.TryCreate(assetPath, UriKind.Absolute, out var absoluteUri)) return absoluteUri.ToString();

        var escapedPath = string.Join("/", assetPath.Replace('\\', '/').TrimStart('/').Split('/').Select(Uri.EscapeDataString));

        return $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/{escapedPath}";
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    [LoggerMessage(1000, LogLevel.Information, "Connecting anonymously to Steam...")]
    private static partial void LogConnecting(ILogger logger);

    [LoggerMessage(1002, LogLevel.Information, "Connected to Steam; logging on anonymously.")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(1003, LogLevel.Information, "Anonymous Steam session is ready.")]
    private static partial void LogSessionReady(ILogger logger);

    [LoggerMessage(1004, LogLevel.Warning, "Anonymous Steam logon failed with {Result}.")]
    private static partial void LogLogonFailed(ILogger logger, EResult result);

    [LoggerMessage(1005, LogLevel.Warning, "Steam logged off with {Result}.")]
    private static partial void LogLoggedOff(ILogger logger, EResult result);

    [LoggerMessage(1006, LogLevel.Warning, "Steam disconnected; reconnecting shortly.")]
    private static partial void LogDisconnected(ILogger logger);

    [LoggerMessage(1007, LogLevel.Warning, "Steam reconnect attempt failed; another attempt will follow.")]
    private static partial void LogReconnectFailed(ILogger logger, Exception exception);

    [LoggerMessage(1008, LogLevel.Error, "Steam callback processing failed; the callback loop will continue.")]
    private static partial void LogCallbackProcessingFailed(ILogger logger, Exception exception);

    public void Dispose(){
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _stopping = true;
        _client.Disconnect();

        if (!_shutdown.IsCancellationRequested) _shutdown.Cancel();

        if ((_callbackTask is null || _callbackTask.IsCompleted) && (_reconnectTask is null || _reconnectTask.IsCompleted)) _shutdown.Dispose();
    }
}