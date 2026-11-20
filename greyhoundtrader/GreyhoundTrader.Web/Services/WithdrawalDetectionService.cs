namespace RacingTrader.Web.Services;

public sealed class WithdrawalDetectionService(
    RacingRepository repository,
    ILogger<WithdrawalDetectionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var samples = await repository.CapturePriceHistoryAsync(stoppingToken);
                if (samples > 0) logger.LogDebug("Captured {Count} changed Race Winner price(s).", samples);
                var applied = await repository.ApplyPendingHorseWithdrawalsAsync(stoppingToken);
                if (applied > 0) logger.LogInformation("Applied {Count} automatic Rule 4 withdrawal(s).", applied);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic withdrawal detection failed.");
            }
        }
    }
}
