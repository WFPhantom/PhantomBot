using System.Text.Json;
using Microsoft.Extensions.Options;
using PhantomBot.Core.Domain;
using PhantomBot.Infrastructure;

namespace PhantomBot.Worker.Services;

// ReSharper disable once PrimaryConstructorParameterCaptureDisallowed
public sealed partial class SteamBaselineCoordinator(IOptions<PhantomBotOptions> options, IHostEnvironment environment, ILogger<SteamBaselineCoordinator> logger){
    private static readonly TimeSpan BaselinePollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WaitingLogInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web){
        WriteIndented = true,
        RespectNullableAnnotations = true,
    };

    internal string RequestPath{ get; } = ResolvePath(options.Value.BaselineRequestPath, environment.ContentRootPath);
    private string BaselinePath{ get; } = ResolvePath(options.Value.BaselinePath, environment.ContentRootPath);

    internal async Task<SteamBaselineRequest> GetOrCreateRequestAsync(uint startChangeNumber, CancellationToken cancellationToken){
        var existing = await ReadRequestAsync(cancellationToken);

        if (existing is not null && IsUsableRequest(existing, startChangeNumber)) return existing;

        if (existing is not null) LogReplacingRequest(logger, RequestPath, existing.SchemaVersion, existing.StartChangeNumber, startChangeNumber);

        var request = new SteamBaselineRequest{
            SchemaVersion = SteamBaselineFormat.SchemaVersion,
            RequestId = Guid.NewGuid().ToString("N"),
            StartChangeNumber = startChangeNumber,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        await WriteAtomicallyAsync(RequestPath, request, cancellationToken);

        return request;
    }

    internal async Task<SteamBaselineDocument> WaitForBaselineAsync(SteamBaselineRequest request, CancellationToken cancellationToken){
        string? reportedMismatch = null;

        var nextWaitingLogUtc = DateTimeOffset.UtcNow + WaitingLogInterval;

        while (true){
            var baseline = await ReadBaselineAsync(cancellationToken);

            if (baseline is not null){
                var mismatch = GetBaselineMismatch(baseline, request);

                if (mismatch is null){
                    ValidateBaseline(baseline);
                    return baseline;
                }

                if (!string.Equals(reportedMismatch, mismatch, StringComparison.Ordinal)){
                    LogIgnoringMismatchedBaseline(logger, BaselinePath, mismatch);

                    reportedMismatch = mismatch;
                }
            }

            if (DateTimeOffset.UtcNow >= nextWaitingLogUtc){
                // ReSharper disable once PrimaryConstructorParameterCaptureDisallowed
                LogStillWaitingForBaseline(logger, BaselinePath);

                nextWaitingLogUtc = DateTimeOffset.UtcNow + WaitingLogInterval;
            }
            await Task.Delay(BaselinePollInterval, cancellationToken);
        }
    }

    private async Task<SteamBaselineRequest?> ReadRequestAsync(CancellationToken cancellationToken){
        try{
            return await TryReadAsync<SteamBaselineRequest>(RequestPath, cancellationToken);
        }
        catch (JsonException exception){
            throw new InvalidDataException($"The Steam baseline request file '{RequestPath}' contains invalid or unsupported JSON. Delete it and restart the Worker to create a new request.", exception);
        }
    }

    private async Task<SteamBaselineDocument?> ReadBaselineAsync(CancellationToken cancellationToken){
        try{
            return await TryReadAsync<SteamBaselineDocument>(BaselinePath, cancellationToken);
        }
        catch (JsonException exception){
            throw new InvalidDataException($"The Steam baseline file '{BaselinePath}' contains invalid or unsupported JSON. Delete it and run the baseline scanner again.", exception);
        }
    }

    private static bool IsUsableRequest(SteamBaselineRequest request, uint startChangeNumber) => request.SchemaVersion == SteamBaselineFormat.SchemaVersion && !string.IsNullOrWhiteSpace(request.RequestId) && request.StartChangeNumber == startChangeNumber && request.CreatedUtc != default;

    private static string? GetBaselineMismatch(SteamBaselineDocument baseline, SteamBaselineRequest request){
        if (baseline.SchemaVersion != SteamBaselineFormat.SchemaVersion) return $"schema version {baseline.SchemaVersion} does not match the required version {SteamBaselineFormat.SchemaVersion}";

        if (!string.Equals(baseline.RequestId, request.RequestId, StringComparison.Ordinal)) return "its request ID does not match the active request";

        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (baseline.StartChangeNumber != request.StartChangeNumber) return $"its start change number {baseline.StartChangeNumber} does not match {request.StartChangeNumber}";

        return null;
    }

    private void ValidateBaseline(SteamBaselineDocument baseline){
        var recovery = $" Delete '{BaselinePath}' and the scanner progress file next to '{RequestPath}', then run the baseline scanner again.";

        if (baseline.CreatedUtc == default) throw new InvalidDataException("Steam baseline has no creation timestamp." + recovery);

        if (baseline.Apps.Count < SteamBaselineFormat.MinimumAppCount) throw new InvalidDataException($"Steam baseline contains only {baseline.Apps.Count} apps and appears incomplete." + recovery);

        var appIds = new HashSet<uint>();

        foreach (var app in baseline.Apps){
            if (app.AppId == 0) throw new InvalidDataException("Steam baseline contains AppID 0." + recovery);

            if (!appIds.Add(app.AppId)) throw new InvalidDataException($"Steam baseline contains duplicate AppID {app.AppId}." + recovery);
        }

        if (!SteamBaselineFormat.ContainsMusicCoverageSentinel(baseline.Apps)) throw new InvalidDataException("Steam baseline did not contain verifiable Music coverage." + recovery);
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken){
        if (!File.Exists(path)) return default;

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken){
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Baseline path must have a parent directory.");

        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try{
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough)){
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally{
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath){
        try{
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
            // failure must not mask the original operation result
        }
    }

    private static string ResolvePath(string configuredPath, string contentRootPath) => Path.IsPathRooted(configuredPath) ? Path.GetFullPath(configuredPath) : Path.GetFullPath(configuredPath, contentRootPath);

    [LoggerMessage(2100, LogLevel.Warning, "Replacing unusable Steam baseline request '{RequestPath}': existing schema version {ExistingSchemaVersion}, existing start change {ExistingStartChangeNumber}, required start change {RequiredStartChangeNumber}.")]
    private static partial void LogReplacingRequest(ILogger logger, string requestPath, int existingSchemaVersion, uint existingStartChangeNumber, uint requiredStartChangeNumber);
    [LoggerMessage(2101, LogLevel.Warning, "Ignoring Steam baseline file '{BaselinePath}' because {Reason}; waiting for a matching baseline.")]
    private static partial void LogIgnoringMismatchedBaseline(ILogger logger, string baselinePath, string reason);
    [LoggerMessage(2102, LogLevel.Information, "Still waiting for a matching Steam baseline at '{BaselinePath}'.")]
    private static partial void LogStillWaitingForBaseline(ILogger logger, string baselinePath);
}