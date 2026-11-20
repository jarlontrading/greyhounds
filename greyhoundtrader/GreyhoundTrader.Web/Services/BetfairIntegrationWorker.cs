using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetfairIntegrationWorker(
    ExternalSettings settings,
    BetfairClient client,
    BetfairRepository repository,
    BetfairMarketMatcher matcher,
    ILogger<BetfairIntegrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        if (!settings.BetfairEnabled)
        {
            logger.LogInformation("Betfair integration is disabled. Add the optional Betfair settings when the delayed API key is ready.");
            return;
        }
        if (!settings.BetfairConfigured)
        {
            logger.LogWarning("Betfair integration is enabled but a 16-character app key and a usable authentication method are required.");
            return;
        }

        var nextCatalogue = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow >= nextCatalogue)
                {
                    await DiscoverAsync(stoppingToken);
                    nextCatalogue = DateTime.UtcNow.AddSeconds(settings.BetfairCatalogueSeconds);
                }
                await PollPricesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Betfair delayed-feed cycle failed.");
                try { await repository.RecordErrorAsync(ex.Message, stoppingToken); }
                catch (Exception recordError) when (recordError is not OperationCanceledException)
                { logger.LogDebug(recordError, "Could not record Betfair integration error state."); }
            }
            await Task.Delay(TimeSpan.FromSeconds(settings.BetfairPollSeconds), stoppingToken);
        }
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = await repository.GetCandidatesAsync(cancellationToken);
        if (candidates.Count == 0) return;
        var from = candidates.Min(x => x.StartTimeUtc).AddMinutes(-10);
        var to = candidates.Max(x => x.StartTimeUtc).AddMinutes(10);
        var catalogues = await client.GetWinMarketsAsync(from, to, cancellationToken);
        var matches = matcher.Match(candidates, catalogues);
        await repository.SaveMatchesAsync(matches, cancellationToken);
        logger.LogInformation(
            "Betfair catalogue matched {Matched} events; {Partial} partial and {Unmatched} unmatched.",
            matches.Count(x => x.Status == "MATCHED"),
            matches.Count(x => x.Status is "PARTIAL" or "MARKET_MATCHED"),
            matches.Count(x => x.Status == "UNMATCHED"));
    }

    private async Task PollPricesAsync(CancellationToken cancellationToken)
    {
        var links = await repository.GetLinkedMarketsAsync(cancellationToken);
        if (links.Count == 0) return;
        var books = await client.GetMarketBooksAsync(links.Select(x => x.MarketId).ToList(), cancellationToken);
        await repository.SaveBooksAsync(links, books, cancellationToken);
        logger.LogDebug("Stored delayed Betfair prices for {MarketCount} matched markets.", books.Count);
    }
}
