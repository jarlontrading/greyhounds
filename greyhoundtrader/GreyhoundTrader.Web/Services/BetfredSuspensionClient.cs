using System.Text.Json;

namespace RacingTrader.Web.Services;

internal sealed record BetfredSuspensionClientStatus(
    bool Connected, string ServiceUrl, DateTime? LastSuccessUtc,
    DateTime? LastSignalUtc, int SignalCount, string? LastError);

internal sealed class BetfredSuspensionClient(
    IHttpClientFactory clients, ILogger<BetfredSuspensionClient> logger, BetfredRacingService betfred) : BackgroundService
{
    private const string ServiceUrl = "http://127.0.0.1:5388";
    private volatile Signal[] _signals = [];
    private long _lastSuccessTicks;
    private long _lastSignalTicks;
    private string? _lastError;

    public BetfredSuspensionClientStatus Status => new(
        DateTime.UtcNow - TicksToUtc(Interlocked.Read(ref _lastSuccessTicks)) < TimeSpan.FromSeconds(10),
        ServiceUrl, TicksToUtc(Interlocked.Read(ref _lastSuccessTicks)),
        TicksToUtc(Interlocked.Read(ref _lastSignalTicks)), _signals.Length, _lastError);

    public bool TryGetRaceOffSignal(PricingEvent racingEvent, out string reason)
    {
        foreach (var signal in _signals.OrderByDescending(item => item.ObservedAtUtc))
        {
            // Course headings disappeared from the DOM group text. Join the
            // exact provider event ID to the existing dated metadata cache.
            if (!BetfredOffIdentity.Matches(racingEvent, betfred.GetOffRaceIdentity(signal.BetfredEventId),
                    signal.BetfredEventId, signal.CardText, signal.ObservedAtUtc, DateTime.UtcNow)) continue;
            reason = $"Betfred SERVICE reported Race is Running (event {signal.BetfredEventId}, observed {signal.ObservedAtUtc:O}).";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var response = await clients.CreateClient("BetfredSuspensionService")
                    .GetAsync(ServiceUrl + "/api/signals", stoppingToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                _signals = await JsonSerializer.DeserializeAsync<Signal[]>(stream,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), stoppingToken) ?? [];
                Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
                if (_signals.Length > 0)
                    Interlocked.Exchange(ref _lastSignalTicks, _signals.Max(item => item.ObservedAtUtc).Ticks);
                _lastError = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogDebug(ex, "Betfred SERVICE signal read failed.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    private static DateTime? TicksToUtc(long ticks) => ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
    private sealed record Signal(long BetfredEventId, string MeetingText, string CardText, DateTime ObservedAtUtc);
}
