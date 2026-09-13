using PhantomBot.Core.Abstractions;

namespace PhantomBot.Worker.Services;

// ReSharper disable PrimaryConstructorParameterCaptureDisallowed
public sealed class ReviewStoreInitializer(IReviewSubscriptionStore store) : IHostedService{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}