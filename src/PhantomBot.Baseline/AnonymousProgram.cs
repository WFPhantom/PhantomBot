using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure.Steam;

namespace PhantomBot.Baseline;

file static class AnonymousProgram{
    private const int BatchSize = 5_000;
    private const uint CheckpointSize = 100_000;
    private const int EmptyCheckpointLimit = 10;
    private const uint SafetyCeiling = 100_000_000;
    private const uint MaximumValidNextAppId = SafetyCeiling + 1;
    private const int MaximumBatchAttempts = 5;
    private const int MaximumUnexpectedAppsPerBatch = 10;

    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CancellationSaveTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions StrictRequestReadJsonOptions = new(JsonSerializerDefaults.Strict){
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ProgressJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions IndentedWriteJsonOptions = new(JsonSerializerDefaults.Web){
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args){
        if (!TryParseRequestPath(args, out var requestPath)){
            await Console.Error.WriteLineAsync("Usage: PhantomBot.Baseline --request <steam-baseline-request.json>");
            return 2;
        }

        using var cancellation = new ConsoleCancellation();

        try{
            var request = await ReadRequestAsync(requestPath, cancellation.Token);
            ValidateRequest(request);

            var fullRequestPath = Path.GetFullPath(requestPath);
            var directory = Path.GetDirectoryName(fullRequestPath) ?? throw new InvalidOperationException("Baseline request path must have a parent directory.");
            var outputPath = Path.Combine(directory, "steam-app-baseline.json");
            var progressPath = Path.Combine(directory, "steam-app-baseline.progress.json");

            // ReSharper disable once UseAwaitUsing
            using var instanceLock = AcquireInstanceLock(directory);

            DeleteStaleTemporaryFiles(outputPath);
            DeleteStaleTemporaryFiles(progressPath);

            var state = await LoadStateAsync(progressPath, request, cancellation.Token);

            Console.WriteLine("Anonymous PICS baseline scan: no Steam account or API key is used.");
            Console.WriteLine($"Resuming at AppID {state.NextAppId:N0} with {state.Apps.Count:N0} apps saved.");
            Console.WriteLine("The scan checkpoints every 100,000 IDs and may be resumed after cancellation.");

            var completed = await ScanAsync(state, progressPath, cancellation.Token);
            ValidateApps(completed.Apps, progressPath);

            var baseline = new SteamBaselineDocument{
                SchemaVersion = SteamBaselineFormat.SchemaVersion,
                RequestId = request.RequestId,
                StartChangeNumber = request.StartChangeNumber,
                CreatedUtc = DateTimeOffset.UtcNow,
                Apps = completed.Apps,
            };

            await WriteAtomicallyAsync(outputPath, baseline, IndentedWriteJsonOptions, cancellation.Token);

            Console.WriteLine();
            Console.WriteLine($"Baseline complete: {baseline.Apps.Count:N0} PICS-visible AppIDs.");
            Console.WriteLine($"Wrote: {outputPath}");
            Console.WriteLine("The waiting PhantomBot Worker will import it automatically.");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested){
            await Console.Error.WriteLineAsync("Baseline scan cancelled. Run the same command to resume.");
            return 130;
        }
        catch (Exception exception){
            return ReportError(exception);
        }
    }

    private static async Task<SteamBaselineScanState> ScanAsync(SteamBaselineScanState initialState, string progressPath, CancellationToken cancellationToken){
        var apps = initialState.Apps.OrderBy(static app => app.AppId).ToList();
        var knownAppIds = apps.Select(static app => app.AppId).ToHashSet();
        var nextAppId = initialState.NextAppId;
        var emptyCheckpointCount = initialState.EmptyCheckpointCount;
        var foundInCurrentCheckpoint = false;

        if (emptyCheckpointCount >= EmptyCheckpointLimit) return CreateState(initialState, nextAppId, emptyCheckpointCount, apps);

        using var client = new SteamPicsClient(NullLogger<SteamPicsClient>.Instance);

        try{
            await client.StartAsync(cancellationToken);

            while (nextAppId <= SafetyCeiling){
                var checkpointEnd = Math.Min(checked(nextAppId + CheckpointSize - 1), SafetyCeiling);
                foundInCurrentCheckpoint = false;

                while (nextAppId <= checkpointEnd){
                    var count = checked((int)Math.Min(BatchSize, checkpointEnd - nextAppId + 1));
                    var batchStart = nextAppId;
                    var batchEndExclusive = checked(batchStart + (uint)count);
                    var found = await ScanBatchWithRetryAsync(client, batchStart, count, cancellationToken);

                    List<SteamAppListEntry> validApps = [];
                    var unexpectedAppCount = 0;
                    uint firstUnexpectedAppId = 0;

                    foreach (var app in found.OrderBy(static app => app.AppId)){
                        if (app.AppId < batchStart || app.AppId >= batchEndExclusive){
                            if (unexpectedAppCount == 0) firstUnexpectedAppId = app.AppId;

                            unexpectedAppCount++;
                            continue;
                        }
                        validApps.Add(app);
                    }

                    if (unexpectedAppCount > 0){
                        if (unexpectedAppCount >= MaximumUnexpectedAppsPerBatch) throw new InvalidDataException($"Steam returned {unexpectedAppCount} AppIDs outside requested batch {batchStart}-{batchEndExclusive - 1}; first unexpected AppID: {firstUnexpectedAppId}. Refusing to trust this response.");

                        await Console.Error.WriteLineAsync($"Steam returned {unexpectedAppCount} AppID(s) outside requested batch {batchStart}-{batchEndExclusive - 1}; first unexpected AppID: {firstUnexpectedAppId}. They were ignored.");
                    }

                    foundInCurrentCheckpoint |= validApps.Count > 0;

                    // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
                    foreach (var app in validApps){
                        if (knownAppIds.Add(app.AppId)) apps.Add(app);
                    }

                    nextAppId = batchEndExclusive;

                    if (nextAppId <= checkpointEnd) await Task.Delay(BatchDelay, cancellationToken);
                }

                emptyCheckpointCount = foundInCurrentCheckpoint ? 0 : emptyCheckpointCount + 1;

                var state = CreateState(initialState, nextAppId, emptyCheckpointCount, apps);
                await WriteAtomicallyAsync(progressPath, state, ProgressJsonOptions, cancellationToken);

                Console.WriteLine($"Scanned through AppID {checkpointEnd:N0}; found {apps.Count:N0} apps; consecutive empty checkpoints: {emptyCheckpointCount}.");

                if (emptyCheckpointCount >= EmptyCheckpointLimit) return state;
            }
        }
        catch (Exception exception){
            var resumableEmptyCheckpointCount = foundInCurrentCheckpoint ? 0 : emptyCheckpointCount;
            var state = CreateState(initialState, nextAppId, resumableEmptyCheckpointCount, apps);
            var reason = exception is OperationCanceledException && cancellationToken.IsCancellationRequested ? "cancellation" : "failure";
            await TrySaveProgressAsync(progressPath, state, reason);
            throw;
        }
        finally{
            using var stopTimeout = new CancellationTokenSource(StopTimeout);

            try{
                await client.StopAsync(stopTimeout.Token);
            }
            catch (Exception exception){
                await Console.Error.WriteLineAsync($"Failed to disconnect cleanly from Steam: {exception.Message}");
            }
        }

        throw new InvalidDataException("The anonymous scan reached its safety ceiling without finding the end of the AppID space.");
    }

    private static async Task<IReadOnlyList<SteamAppListEntry>> ScanBatchWithRetryAsync(SteamPicsClient client, uint firstAppId, int count, CancellationToken cancellationToken){
        for (var attempt = 1;; attempt++){
            try{
                return await client.ScanAppIdsAsync(firstAppId, count, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && attempt < MaximumBatchAttempts){
                var delay = TimeSpan.FromSeconds(5 * (1 << (attempt - 1)));
                await Console.Error.WriteLineAsync($"Steam scan batch beginning at AppID {firstAppId:N0} failed on attempt {attempt} of {MaximumBatchAttempts}: {exception.Message}. Retrying in {delay.TotalSeconds:N0} seconds.");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    // ReSharper disable once ParameterTypeCanBeEnumerable.Local
    private static SteamBaselineScanState CreateState(SteamBaselineScanState initialState, uint nextAppId, int emptyCheckpointCount, List<SteamAppListEntry> apps) => new(){
        SchemaVersion = SteamBaselineFormat.SchemaVersion,
        RequestId = initialState.RequestId,
        StartChangeNumber = initialState.StartChangeNumber,
        NextAppId = nextAppId,
        EmptyCheckpointCount = emptyCheckpointCount,
        Apps = [.. apps.Where(app => app.AppId < nextAppId)],
    };

    private static async Task TrySaveProgressAsync(string progressPath, SteamBaselineScanState state, string reason){
        using var timeout = new CancellationTokenSource(CancellationSaveTimeout);

        try{
            await WriteAtomicallyAsync(progressPath, state, ProgressJsonOptions, timeout.Token);
            await Console.Error.WriteLineAsync($"Saved progress after {reason} at AppID {state.NextAppId:N0}.");
        }
        catch (Exception exception){
            await Console.Error.WriteLineAsync($"Could not save progress after {reason}: {exception.Message}");
        }
    }

    private static async Task<SteamBaselineScanState> LoadStateAsync(string progressPath, SteamBaselineRequest request, CancellationToken cancellationToken){
        var progressExists = File.Exists(progressPath);
        SteamBaselineScanState? state;

        try{
            state = await TryReadAsync<SteamBaselineScanState>(progressPath, ProgressJsonOptions, cancellationToken);
        }
        catch (JsonException exception){
            await ArchiveUnusableProgressAsync(progressPath, $"it is not valid progress JSON: {exception.Message}");
            return CreateInitialState(request);
        }

        if (state is null){
            if (progressExists) await ArchiveUnusableProgressAsync(progressPath, "it contains no state");

            return CreateInitialState(request);
        }

        List<string> mismatches = [];

        if (state.SchemaVersion != SteamBaselineFormat.SchemaVersion) mismatches.Add($"schema version {state.SchemaVersion} does not equal {SteamBaselineFormat.SchemaVersion}");

        if (!string.Equals(state.RequestId, request.RequestId, StringComparison.Ordinal)) mismatches.Add("RequestId does not match the current request");

        if (state.StartChangeNumber != request.StartChangeNumber) mismatches.Add($"start change number {state.StartChangeNumber} does not equal {request.StartChangeNumber}");

        if (state.NextAppId is 0 or > MaximumValidNextAppId) mismatches.Add($"next AppID {state.NextAppId} is outside the supported range");

        if (mismatches.Count > 0){
            await ArchiveUnusableProgressAsync(progressPath, string.Join("; ", mismatches));
            return CreateInitialState(request);
        }

        try{
            ValidateProgressState(state);
        }
        catch (InvalidDataException exception){
            await ArchiveUnusableProgressAsync(progressPath, exception.Message);
            return CreateInitialState(request);
        }

        return state;
    }

    private static async Task ArchiveUnusableProgressAsync(string progressPath, string reason){
        var archivePath = progressPath + ".unusable";

        try{
            File.Move(progressPath, archivePath, true);
            await Console.Error.WriteLineAsync($"Ignoring unusable progress file because {reason}. Preserved it as '{archivePath}'.");
            return;
        }
        catch (Exception archiveException) when (archiveException is IOException or UnauthorizedAccessException){
            await Console.Error.WriteLineAsync($"Could not archive unusable progress file '{progressPath}': {archiveException.Message}");
        }

        try{
            File.Delete(progressPath);
            await Console.Error.WriteLineAsync("Deleted the unusable progress file because it could not be archived.");
        }
        catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException){
            throw new InvalidDataException($"The progress file is unusable and cannot be archived or deleted. Close any program reading '{progressPath}', or delete it manually, then run the command again.", deleteException);
        }
    }

    private static SteamBaselineScanState CreateInitialState(SteamBaselineRequest request) => new(){
        SchemaVersion = SteamBaselineFormat.SchemaVersion,
        RequestId = request.RequestId,
        StartChangeNumber = request.StartChangeNumber,
        NextAppId = 1,
        Apps = [],
    };

    private static void ValidateProgressState(SteamBaselineScanState state){
        if (state.EmptyCheckpointCount < 0) throw new InvalidDataException("The baseline progress file contains a negative empty-checkpoint count.");

        var appIds = new HashSet<uint>();

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var app in state.Apps){
            if (app.AppId == 0) throw new InvalidDataException("The baseline progress file contains the invalid AppID 0.");

            if (app.AppId >= state.NextAppId) throw new InvalidDataException($"The baseline progress file contains AppID {app.AppId}, which is not below its next AppID {state.NextAppId}.");

            if (!appIds.Add(app.AppId)) throw new InvalidDataException($"The baseline progress file contains duplicate AppID {app.AppId}.");
        }
    }

    private static void ValidateRequest(SteamBaselineRequest request){
        if (request.SchemaVersion != SteamBaselineFormat.SchemaVersion || string.IsNullOrWhiteSpace(request.RequestId) || request.StartChangeNumber == 0 || request.CreatedUtc == default) throw new InvalidDataException("The baseline request file is invalid or unsupported.");
    }

    private static void ValidateApps(
        IReadOnlyCollection<SteamAppListEntry> apps,
        string progressPath){
        if (apps.Count < SteamBaselineFormat.MinimumAppCount) throw new InvalidDataException($"The scan found only {apps.Count} apps and appears incomplete. Progress is pinned by '{progressPath}'; delete that file to force a full rescan.");

        if (!SteamBaselineFormat.ContainsMusicCoverageSentinel(apps)) throw new InvalidDataException($"The scan did not contain the Music coverage sentinel, so Music coverage could not be verified. Progress is pinned by '{progressPath}'; delete that file to force a full rescan.");
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private static bool TryParseRequestPath(string[] args, out string requestPath){
        requestPath = string.Empty;

        if (args.Length != 2 || !string.Equals(args[0], "--request", StringComparison.Ordinal)) return false;

        requestPath = args[1];
        return !string.IsNullOrWhiteSpace(requestPath);
    }

    private static int ReportError(Exception exception){
        Console.Error.WriteLine("Baseline scan failed:");
        Console.Error.WriteLine(exception);
        return 1;
    }

    private static async Task<SteamBaselineRequest> ReadRequestAsync(string path, CancellationToken cancellationToken){
        try{
            var value = await TryReadAsync<SteamBaselineRequest>(path, StrictRequestReadJsonOptions, cancellationToken);
            return value ?? throw new InvalidDataException($"Required file was not found or was empty: {path}");
        }
        catch (JsonException exception){
            throw new InvalidDataException("The baseline request JSON is invalid or was written by an unsupported PhantomBot version.", exception);
        }
    }

    private static async Task<T?> TryReadAsync<T>(string path, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken){
        if (!File.Exists(path)) return default;

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions, cancellationToken);
    }

    private static async Task WriteAtomicallyAsync<T>(string path, T value, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken){
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Baseline path must have a parent directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try{
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough)){
                await JsonSerializer.SerializeAsync(stream, value, jsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally{
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path){
        try{
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
            Console.Error.WriteLine($"Could not delete temporary file '{path}': {exception.Message}");
        }
    }

    private static void DeleteStaleTemporaryFiles(string targetPath){
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        var fileNamePrefix = Path.GetFileName(fullPath) + ".";
        var staleBefore = DateTime.UtcNow - StaleTemporaryFileAge;

        foreach (var temporaryPath in Directory.EnumerateFiles(directory, fileNamePrefix + "*.tmp", SearchOption.TopDirectoryOnly)){
            try{
                if (!temporaryPath.EndsWith(".tmp", StringComparison.Ordinal)) continue;

                if (File.GetLastWriteTimeUtc(temporaryPath) <= staleBefore) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
                Console.Error.WriteLine($"Could not inspect or delete stale temporary file '{temporaryPath}': {exception.Message}");
            }
        }
    }

    private static FileStream AcquireInstanceLock(string directory){
        var lockPath = Path.Combine(directory, "steam-app-baseline.lock");

        try{
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        }
        catch (UnauthorizedAccessException exception){
            throw new InvalidOperationException($"PhantomBot does not have permission to open baseline lock file '{lockPath}'.", exception);
        }
        catch (IOException exception){
            throw new InvalidOperationException($"Another baseline scan may already be using '{directory}', or lock file '{lockPath}' is unavailable.", exception);
        }
    }

    private sealed class ConsoleCancellation : IDisposable{
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationToken _token;
        private readonly Lock _sync = new();
        private readonly PosixSignalRegistration? _terminationRegistration;
        private bool _disposed;

        public ConsoleCancellation(){
            _token = _source.Token;
            Console.CancelKeyPress += OnCancelKeyPress;

            if (!OperatingSystem.IsWindows()) _terminationRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationSignal);
        }

        public bool IsCancellationRequested => _token.IsCancellationRequested;

        public CancellationToken Token => _token;

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs){
            eventArgs.Cancel = true;
            RequestCancellation();
        }

        private void OnTerminationSignal(PosixSignalContext context){
            context.Cancel = true;
            RequestCancellation();
        }

        private void RequestCancellation(){
            lock (_sync){
                if (!_disposed) _source.Cancel();
            }
        }

        public void Dispose(){
            Console.CancelKeyPress -= OnCancelKeyPress;
            _terminationRegistration?.Dispose();

            lock (_sync){
                if (_disposed) return;

                _source.Dispose();
                _disposed = true;
            }
        }
    }
}