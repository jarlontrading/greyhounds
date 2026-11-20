using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetfairBiasService(
    ExternalSettings settings,
    BetfairRepository repository,
    ILogger<BetfairBiasService> logger)
{
    public async Task<IReadOnlyList<FeedRunner>> ApplyAsync(
        PricingEvent racingEvent,
        IReadOnlyList<FeedRunner> runners,
        CancellationToken cancellationToken)
    {
        if (!settings.BetfairConfigured || !settings.BetfairBiasEnabled || runners.Count < 2) return runners;
        var references = await repository.GetBiasReferencesAsync(racingEvent.EventId, cancellationToken);
        if (references.Count == 0) return runners;

        var now = DateTime.UtcNow;
        var byIdentifier = references.GroupBy(x => NormalizeIdentifier(x.Identifier)).ToDictionary(x => x.Key, x => x.First());
        var byName = references.GroupBy(x => BetfairMarketMatcher.NormalizeRunner(x.Name)).ToDictionary(x => x.Key, x => x.First());
        var biased = new List<FeedRunner>(runners.Count);
        var changed = 0;
        foreach (var runner in runners)
        {
            var reference = byIdentifier.GetValueOrDefault(NormalizeIdentifier(runner.Identifier.ToString()))
                ?? byName.GetValueOrDefault(BetfairMarketMatcher.NormalizeRunner(runner.Name));
            var price = BiasPrice(runner, reference, now);
            if (price is not null) changed++;
            biased.Add(runner with { PricingPrice = price });
        }
        if (changed > 0)
            logger.LogDebug("Applied delayed Betfair bias to {Count} runners for event {EventId}.", changed, racingEvent.EventId);
        return biased;
    }

    private double? BiasPrice(FeedRunner runner, BetfairBiasReference? reference, DateTime now)
    {
        if (!runner.HasPrice || runner.Price <= 1 || reference is null) return null;
        if (!reference.MarketStatus.Equals("OPEN", StringComparison.OrdinalIgnoreCase)) return null;
        if (reference.InPlay && !settings.BetfairAllowInPlayBias) return null;
        if ((now - reference.CapturedAtUtc).TotalSeconds > settings.BetfairMaximumPriceAgeSeconds) return null;

        double? exchangePrice = null;
        if (reference.BestBackPrice is > 1 && reference.BestLayPrice is > 1)
        {
            var midpoint = (reference.BestBackPrice.Value + reference.BestLayPrice.Value) / 2;
            if ((reference.BestLayPrice.Value - reference.BestBackPrice.Value) / midpoint > settings.BetfairMaximumSpreadPercent) return null;
            var midpointProbability = (1 / reference.BestBackPrice.Value + 1 / reference.BestLayPrice.Value) / 2;
            exchangePrice = 1 / midpointProbability;
        }
        else if (reference.LastTradedPrice is > 1)
            exchangePrice = reference.LastTradedPrice;

        if (exchangePrice is null) return null;
        var sourceProbability = 1 / runner.Price;
        var exchangeProbability = 1 / exchangePrice.Value;
        if (Math.Abs(sourceProbability - exchangeProbability) < settings.BetfairMinimumProbabilityDifference) return null;
        var biasedProbability = sourceProbability * (1 - settings.BetfairBiasWeight) + exchangeProbability * settings.BetfairBiasWeight;
        return biasedProbability > 0 ? 1 / biasedProbability : null;
    }

    private static string NormalizeIdentifier(string value) => value.Trim().TrimStart('0');
}
