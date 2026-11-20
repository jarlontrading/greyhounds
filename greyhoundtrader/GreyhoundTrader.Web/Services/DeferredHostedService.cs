namespace RacingTrader.Web.Services;

internal static class DeferredWorkerStartupGate
{
    internal static readonly SemaphoreSlim Instance = new(1, 1);
}

/// <summary>
/// Starts a worker outside the host's critical startup path. The wrapper itself starts
/// immediately, so Kestrel remains available while browser and feed dependencies initialise.
/// </summary>
internal sealed class DeferredHostedService<T>(
    IServiceProvider services,
    ILogger<DeferredHostedService<T>> logger) : IHostedService
    where T : class, IHostedService
{
    private T? _service;
    private Task? _startup;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startup = Task.Run(async () =>
        {
            try
            {
                // Microsoft DI serializes singleton construction internally. Starting
                // every wrapper on Task.Run at once can create a cross-service lock
                // cycle when workers depend on one another. Resolve/start one graph
                // at a time, after Kestrel has been free to bind.
                await DeferredWorkerStartupGate.Instance.WaitAsync(cancellationToken);
                try
                {
                    logger.LogInformation("Resolving deferred worker {Worker}.", typeof(T).Name);
                    var service = services.GetRequiredService<T>();
                    logger.LogInformation("Resolved deferred worker {Worker}; starting.", typeof(T).Name);
                    Volatile.Write(ref _service, service);
                    await service.StartAsync(cancellationToken);
                    logger.LogInformation("Deferred worker {Worker} started.", typeof(T).Name);
                }
                finally
                {
                    DeferredWorkerStartupGate.Instance.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deferred worker {Worker} failed to start.", typeof(T).Name);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_startup is not null)
        {
            try { await _startup.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        }

        if (Volatile.Read(ref _service) is { } service)
            await service.StopAsync(cancellationToken);
    }
}
