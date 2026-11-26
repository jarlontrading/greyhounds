namespace RacingTrader.Web.Services;

internal sealed class RacingMarketCalculator(GreyhoundDistanceMarkets? distances = null, PlacePricingOptions? placePricing = null)
{
    private readonly PlacePricingOptions placeOptions = (placePricing ?? new PlacePricingOptions()).Validate();

    private const double TargetBook = 1.0;
    private const double MinimumWinnerBook = .99;
    private const double BookTolerance = .0000005;
    private const int MaximumBookRounds = 200;
    private static readonly double[] FractionalOddsLadder = BuildFractionalOddsLadder();
    private static readonly Lazy<double[][]> MinimumTwoWayFractionalBooks = new(() => BuildMinimumFractionalBooks(2));
    private static readonly Lazy<double[][]> MinimumThreeWayFractionalBooks = new(() => BuildMinimumFractionalBooks(3));
    private static double MaximumOdds => FractionalOddsLadder[^1];

    internal const int RaceWinnerSuffix = 9001;
    internal const int Top2Suffix = 9002;
    internal const int GreyhoundGroupsSuffix = 9003;
    internal const int Top3Suffix = 9004;
    internal const int BettingWithoutSuffix = 9005;
    internal const int Insure2Suffix = 9006;
    internal const int Insure3Suffix = 9007;
    internal const int Insure4Suffix = 9008;
    internal const int Index502510Suffix = 9009;
    internal const int Index50302010Suffix = 9010;
    internal const int Top4Suffix = 9011;
    internal const int BettingWithoutDisplayOrderStart = 50;
    internal const int MatchBetDisplayOrderStart = 70;
    internal const int ReservedNewMarketDisplayOrderStart = 90;
    internal const int DynamicMarketSlots = 20;
    internal const int ForecastMarketType = 1012;
    internal const int ForecastFirstSuffix = 9300;
    internal const int ForecastLastSuffix = ForecastFirstSuffix + DynamicMarketSlots - 1;
    internal const int MultiTrapsMarketType = 1013;
    internal const int MultiTrapsFirstSuffix = 9400;
    internal const int TripleTrapsMarketType = 1014;
    internal const int TripleTrapsSuffix = 9500;

    private static readonly double[] Place2Intercepts =
    [0,0,0,0,0,0,1.162990295,1.09701834,1.052165122,1.028563389,1.01430293,1.004883226,0.990481473,0.982827716,0.980323422,0.98073604,0.980284612,0.966939257,0.979301957,0.964702201,0.969579539,0.966502697,0.987995704,0.969448202,0.98663485];
    private static readonly double[] Place3Intercepts =
    [0,0,0,0,0,0,2.068759741,1.89433011,1.7773512,1.70670382,1.658218324,1.621171929,1.577675256,1.550555646,1.530372476,1.521606609,1.51005756,1.477117573,1.491165295,1.460389371,1.442553301,1.45207663,1.458122891,1.433752768,1.438782316];
    private static readonly double[] Place4Intercepts =
    [0,0,0,0,0,0,3.036410343,2.68368774,2.463179103,2.329933018,2.239006644,2.170164576,2.095740161,2.046984668,2.0082352,1.988439392,1.965184946,1.915160074,1.926981784,1.885082173,1.844115131,1.866959433,1.855325846,1.830123951,1.818897794];

    public IReadOnlyList<GeneratedMarket> Build(PricingEvent racingEvent, IReadOnlyList<PricingRunner> runners,
        PricingConfiguration config, IReadOnlyList<double>? spreadexIndexPoints = null)
    {
        if (runners.Count == 0) return [];
        var raceWinner = BuildRaceWinner(racingEvent, runners);
        var normalizedRunners = WithRaceWinnerPrices(runners, raceWinner.Selections);
        var placeBooks = new Lazy<double[][]>(() => PlaceProbabilityBooks(racingEvent.SportId, normalizedRunners));
        var eligibleRunners = EligibleRunnerCount(runners);
        var markets = new List<GeneratedMarket>
        {
            RequireRunners(raceWinner, eligibleRunners, 2),
            RequireRunners(BuildPlace(racingEvent, normalizedRunners, 2, Top2Suffix, "Top 2", 1002, 20, placeBooks.Value[2]), eligibleRunners, 4)
        };
        markets.Add(config.Top3Enabled
            ? RequireRunners(BuildPlace(racingEvent, normalizedRunners, 3, Top3Suffix, "Top 3", 1003, 30, placeBooks.Value[3]), eligibleRunners, 5)
            : Disabled(racingEvent.EventId, Top3Suffix, "Top 3", 1003, 30));
        var fourthPlaceMarketsEnabled = config.Insure4Enabled && racingEvent.SportId != 4;
        markets.Add(fourthPlaceMarketsEnabled
            ? RequireRunners(BuildPlace(racingEvent, normalizedRunners, 4, Top4Suffix, "Top 4", 1008, 35, placeBooks.Value[4]), eligibleRunners, 6)
            : Disabled(racingEvent.EventId, Top4Suffix, "Top 4", 1008, 35));
        // Index markets are a horse-racing product derived from the normalized
        // 100% Race Winner book. They are not enabled for greyhounds yet.
        if (racingEvent.SportId == 2)
        {
            markets.Add(RequireRunners(BuildIndex(racingEvent, normalizedRunners, Index502510Suffix, "50-25-10 Index", 1010, 40,
                Rule4IndexPoints(spreadexIndexPoints)), eligibleRunners, 4));
            markets.Add(RequireRunners(BuildIndex(racingEvent, normalizedRunners, Index50302010Suffix, "50-30-20-10 Index", 1011, 45, [50, 30, 20, 10]), eligibleRunners, 6));
        }

        foreach (var without in config.BettingWithoutMarkets)
            markets.Add(RequireRunners(BuildBettingWithout(racingEvent, normalizedRunners, without), eligibleRunners, 4));
        markets.Add(config.Insure2Enabled ? RequireRunners(BuildInsure(racingEvent, normalizedRunners, 2, Insure2Suffix, "Insure Bet 2 Places", 1005, 160, placeBooks.Value[2]), eligibleRunners, 4) : Disabled(racingEvent.EventId, Insure2Suffix, "Insure Bet 2 Places", 1005, 160));
        markets.Add(config.Insure3Enabled ? RequireRunners(BuildInsure(racingEvent, normalizedRunners, 3, Insure3Suffix, "Insure Bet 3 Places", 1006, 170, placeBooks.Value[3]), eligibleRunners, 5) : Disabled(racingEvent.EventId, Insure3Suffix, "Insure Bet 3 Places", 1006, 170));
        markets.Add(fourthPlaceMarketsEnabled ? RequireRunners(BuildInsure(racingEvent, normalizedRunners, 4, Insure4Suffix, "Insure Bet 4 Places", 1009, 180, placeBooks.Value[4]), eligibleRunners, 6) : Disabled(racingEvent.EventId, Insure4Suffix, "Insure Bet 4 Places", 1009, 180));

        foreach (var pair in config.MatchBets)
            markets.Add(RequireRunners(BuildMatchBet(racingEvent, normalizedRunners, pair), eligibleRunners, 4));
        foreach (var forecast in config.Forecasts)
            markets.Add(RequireRunners(BuildForecast(racingEvent, normalizedRunners, forecast), eligibleRunners, 2));
        if (racingEvent.SportId == 4)
        {
            if (distances?.Build(racingEvent) is { } distanceMarket) markets.Add(distanceMarket);
            markets.Add(BuildGreyhoundGroups(racingEvent, normalizedRunners));
            if (config.MultiTrapsEnabled)
                markets.Add(BuildTrapProductMarket(racingEvent, normalizedRunners, 2,
                    "Multi-Traps", MultiTrapsMarketType, MultiTrapsFirstSuffix, 2.5, 29.5, 200,
                    config.MultiTrapsLine));
            if (config.TripleTrapsEnabled)
                markets.Add(BuildTrapProductMarket(racingEvent, normalizedRunners, 3,
                    "Triple-Traps", TripleTrapsMarketType, TripleTrapsSuffix, 6.5, 119.5, 210,
                    config.TripleTrapsLine));
        }
        // Do not create even empty fourth-place market shells for greyhounds.
        return markets.Where(x => racingEvent.SportId != 4 ||
            x.MarketId % 10000 is not (Top4Suffix or Insure4Suffix))
            .OrderBy(x => x.DisplayOrder).ToList();
    }

    private static GeneratedMarket BuildTrapProductMarket(
        PricingEvent ev, IReadOnlyList<PricingRunner> runners, int places, string family,
        int marketType, int firstSuffix, double firstLine, double lastLine, int displayOrderStart,
        double? fixedLine)
    {
        var live = runners.Select((runner, index) => (runner, index))
            .Where(x => x.runner.Active && !x.runner.Scratch &&
                        int.TryParse(x.runner.Identifier, out var trap) && trap is >= 1 and <= 6)
            .Select(x => (x.runner, x.index, trap: int.Parse(x.runner.Identifier))).ToList();
        if (live.Count < places)
            return new GeneratedMarket(MakeMarketId(ev.EventId, firstSuffix), family, marketType,
                displayOrderStart, false, false, []);
        var wins = WinProbabilities(runners);
        var outcomes = new List<(int Product, double Probability)>();
        foreach (var first in live)
        foreach (var second in live.Where(x => x.index != first.index))
        {
            var p2 = wins[first.index] > 0 && wins[first.index] < 1
                ? wins[first.index] * wins[second.index] / (1 - wins[first.index]) : 0;
            if (places == 2)
            {
                outcomes.Add((first.trap * second.trap, p2));
                continue;
            }
            foreach (var third in live.Where(x => x.index != first.index && x.index != second.index))
            {
                var remaining = 1 - wins[first.index] - wins[second.index];
                var probability = remaining > 0 ? p2 * wins[third.index] / remaining : 0;
                outcomes.Add((first.trap * second.trap * third.trap, probability));
            }
        }

        var candidates = Enumerable.Range(0, (int)Math.Round(lastLine - firstLine) + 1)
            .Select(index => firstLine + index)
            .Select(line => new
            {
                Line = line,
                Over = Math.Clamp(outcomes.Where(x => x.Product > line).Sum(x => x.Probability), 0, 1)
            })
            .ToList();
        var pickEm = fixedLine is double lockedLine
            ? candidates.OrderBy(x => Math.Abs(x.Line - lockedLine)).First()
            : candidates.OrderBy(x => Math.Abs(x.Over - .5)).ThenBy(x => x.Line).First();
        var marketId = MakeMarketId(ev.EventId, firstSuffix);
        var underProbability = Math.Clamp(1 - pickEm.Over, 0, 1);
        var selections = new List<GeneratedSelection>
        {
            LineSelection(ev, marketId, family, 1, $"Under {pickEm.Line:0.0}", pickEm.Line, underProbability),
            LineSelection(ev, marketId, family, 2, $"Over {pickEm.Line:0.0}", pickEm.Line, pickEm.Over)
        };
        WalkMarketToTargetBook(selections, TargetBook, MinimumWinnerBook);
        return new GeneratedMarket(marketId, family, marketType, displayOrderStart, true, true, selections);
    }

    private static GeneratedSelection LineSelection(PricingEvent ev, long marketId, string marketName,
        int ordinal, string name, double line, double probability) => new()
    {
        EventId = ev.EventId, MarketId = marketId, MarketName = marketName,
        SelectionId = marketId * 100L + ordinal, Name = name, Identifier = ordinal.ToString("00"),
        PriceBack = probability > 0 ? Snap(1 / probability) : MaximumOdds,
        PriceBackLast = probability > 0 ? Snap(1 / probability) : MaximumOdds,
        PriceLay = 0, Index = ordinal, Line = line, Active = 1, Display = 1, Scratch = 0
    };

    internal IReadOnlyList<ProviderIndexPrice> BuildProviderIndexes(
        PricingEvent racingEvent,
        IReadOnlyList<PricingRunner> runners)
    {
        var eligible = runners.Where(runner => runner.Active && !runner.Scratch).ToList();
        // Never normalize a partial provider book: doing so redistributes the
        // missing runners' probability and manufactures false index values.
        if (eligible.Count < 2 || eligible.Any(runner => runner.Price <= 1)) return [];

        var raceWinner = BuildRaceWinner(racingEvent, runners);
        var normalized = WithRaceWinnerPrices(runners, raceWinner.Selections);
        var index502510 = eligible.Count >= 4
            ? BuildIndex(racingEvent, normalized, Index502510Suffix, "50-25-10 Index", 1010, 40, [50, 25, 10])
            : null;
        var index50302010 = eligible.Count >= 6
            ? BuildIndex(racingEvent, normalized, Index50302010Suffix, "50-30-20-10 Index", 1011, 45, [50, 30, 20, 10])
            : null;

        double? Find(GeneratedMarket? market, PricingRunner runner) => market?.Selections
            .FirstOrDefault(selection =>
                (runner.CompetitorId is > 0 && selection.CompetitorId == runner.CompetitorId) ||
                selection.Name.Equals(runner.Name, StringComparison.OrdinalIgnoreCase))
            ?.PriceBack;

        return runners.Select(runner => new ProviderIndexPrice(
            runner.CompetitorId,
            runner.Name,
            Find(index502510, runner),
            Find(index50302010, runner))).ToList();
    }

    internal IReadOnlyList<TrapLineMarketGuide> BuildEvenGreyhoundLineGuide() =>
    [
        BuildEvenGuide("Multi-Traps", "1st trap × 2nd trap", 2, 2.5, 29.5),
        BuildEvenGuide("Triple-Traps", "1st trap × 2nd trap × 3rd trap", 3, 6.5, 119.5)
    ];

    private static TrapLineMarketGuide BuildEvenGuide(
        string name, string formula, int places, double firstLine, double lastLine)
    {
        var outcomes = new List<int>();
        for (var first = 1; first <= 6; first++)
        for (var second = 1; second <= 6; second++)
        {
            if (second == first) continue;
            if (places == 2) { outcomes.Add(first * second); continue; }
            for (var third = 1; third <= 6; third++)
                if (third != first && third != second) outcomes.Add(first * second * third);
        }
        var candidates = Enumerable.Range(0, (int)Math.Round(lastLine - firstLine) + 1)
            .Select(index => firstLine + index)
            .Select(line =>
            {
                var over = outcomes.Count(value => value > line) / (double)outcomes.Count;
                var under = 1 - over;
                return new { Line = line, Under = under, Over = over };
            }).ToList();
        var pickEm = candidates.OrderBy(x => Math.Abs(x.Over - .5)).ThenBy(x => x.Line).First().Line;
        return new TrapLineMarketGuide(name, formula, pickEm, candidates.Select(x => new TrapLineGuidePoint(
            x.Line, x.Under, x.Under > 0 ? Snap(1 / x.Under) : MaximumOdds,
            x.Over, x.Over > 0 ? Snap(1 / x.Over) : MaximumOdds,
            x.Line == pickEm)).ToList());
    }

    internal IReadOnlyList<RacingTrader.Web.Models.RaceCardSpreadPrice> BuildRaceCardSpreads(
        IReadOnlyList<RacingTrader.Web.Models.RunnerRow> runners)
    {
        var live = runners.Where(r => r.Active && r.Display && !r.Withdrawn).ToList();
        if (live.Count < 2 || live.Any(r =>
                                           !r.Prices.TryGetValue("Race Winner", out var price) || price.Price <= 1 ||
                                           !int.TryParse(r.Identifier, out var card) || card <= 0))
            return RaceCardDefinitions.Select(d => Empty(d)).ToList();

        var pricing = live.Select(r => new PricingRunner
        {
            EventId = 0, MarketId = 0, SelectionId = r.SelectionId, CompetitorId = r.CompetitorId,
            MarketName = "Race Winner", Name = r.Name, Identifier = r.Identifier,
            Price = r.Prices["Race Winner"].Price, PreviousPrice = r.Prices["Race Winner"].Price,
            Active = true, Display = true, Scratch = false
        }).ToList();
        var wins = WinProbabilities(pricing);
        // These are the actual probabilities represented by our 200% and 300%
        // place books, not payout odds. They deliberately sum to 2 and 3.
        var top2 = live.Select(r => r.Prices.TryGetValue("Top 2", out var price) && price.Price > 1
            ? 1d / price.Price : double.NaN).ToArray();
        var top3 = live.Select(r => r.Prices.TryGetValue("Top 3", out var price) && price.Price > 1
            ? 1d / price.Price : double.NaN).ToArray();
        var cards = live.Select(r => int.Parse(r.Identifier)).ToArray();
        var spreadexFractionals = live.Select(r => r.SpreadexPrice is > 1
            ? r.SpreadexPrice.Value - 1d : double.NaN).ToArray();
        var favourite = Enumerable.Range(0, live.Count)
            .OrderBy(i => live[i].SpreadexPrice ?? live[i].ModelSourcePrice ?? double.MaxValue)
            .ThenBy(i => cards[i]).First();

        double multi = 0;
        for (var i = 0; i < wins.Length; i++)
            for (var j = 0; j < wins.Length; j++)
                if (i != j && wins[i] < 1)
                    multi += wins[i] * wins[j] / (1 - wins[i]) * cards[i] * cards[j];

        var values = new Dictionary<string, double>
        {
            ["double-number"] = Enumerable.Range(0, wins.Length).Sum(i => wins[i] * cards[i] * 2),
            ["race-squared-number"] = Enumerable.Range(0, wins.Length).Sum(i => wins[i] * cards[i] * cards[i]),
            ["race-multi-mules"] = multi
        };
        if (top2.All(double.IsFinite) && top3.All(double.IsFinite))
            values["favourite-performance"] = 25 * wins[favourite] +
                10 * Math.Max(0, top2[favourite] - wins[favourite]) +
                5 * Math.Max(0, top3[favourite] - top2[favourite]);
        // SP is the Spreadex fractional payoff weighted by the appropriate
        // 100%-derived win/place probability. Never substitute our derived odds.
        if (spreadexFractionals.All(double.IsFinite))
        {
            values["sp-winner"] = Enumerable.Range(0, wins.Length).Sum(i => wins[i] * spreadexFractionals[i]);
            if (top2.All(double.IsFinite)) values["sp-2-places"] = Enumerable.Range(0, wins.Length).Sum(i => top2[i] * spreadexFractionals[i]);
            if (top3.All(double.IsFinite)) values["sp-3-places"] = Enumerable.Range(0, wins.Length).Sum(i => top3[i] * spreadexFractionals[i]);
        }
        return RaceCardDefinitions.Select(definition => values.TryGetValue(definition.Key, out var value)
            ? Quote(definition, value)
            : Empty(definition)).ToList();
    }

    private sealed record RaceCardDefinition(string Key, string Name, double Width, double Tick, string Help);
    private static readonly RaceCardDefinition[] RaceCardDefinitions =
    [
        new("double-number", "Double Number of Winner", 1, .1, "Twice the racecard number of the winning horse."),
        new("favourite-performance", "Favourite Performance", 1.5, .25, "Favourite scores 25 for first, 10 for second, 5 for third and 0 otherwise. Lower racecard number breaks joint-favourite ties."),
        new("sp-winner", "SP of Winner", 1.5, .25, "Fractional starting price of the winning horse."),
        new("sp-2-places", "SP 2 Places", 2, .5, "Sum of the fractional starting prices of the first two finishers."),
        new("sp-3-places", "SP 3 Places", 3, .5, "Sum of the fractional starting prices of the first three finishers."),
        new("race-squared-number", "Race Squared Number", 3, .5, "Racecard number of the winner, squared."),
        new("race-multi-mules", "Race Multi-Mules", 3, .5, "Winner racecard number multiplied by the second-placed racecard number."),
        new("double-draw", "Double Draw of Winner", 1, .1, "Twice the winning horse's stall number. Unavailable until genuine stall data is present.")
    ];
    private static RacingTrader.Web.Models.RaceCardSpreadPrice Quote(RaceCardDefinition d, double midpoint)
    {
        double Round(double value) => Math.Round(value / d.Tick, MidpointRounding.AwayFromZero) * d.Tick;
        var bid = Math.Max(0, Round(midpoint - d.Width / 2));
        var offer = Math.Max(bid + d.Tick, Round(midpoint + d.Width / 2));
        return new(d.Key, d.Name, bid, offer, Math.Round(midpoint, 2), d.Help);
    }
    private static RacingTrader.Web.Models.RaceCardSpreadPrice Empty(RaceCardDefinition d) =>
        new(d.Key, d.Name, null, null, null, d.Help);

    private static GeneratedMarket BuildRaceWinner(PricingEvent ev, IReadOnlyList<PricingRunner> runners)
    {
        var marketId = MakeMarketId(ev.EventId, RaceWinnerSuffix);
        var selections = runners
            .Select((runner, index) => Clone(runner, marketId, RunnerOrdinal(runner, index), "Race Winner", runner.Price, runner.Scratch))
            .ToList();
        NormalizeRaceWinnerProportionally(selections, TargetBook, MinimumWinnerBook);
        return Market(marketId, "Race Winner", 1001, 10, selections);
    }

    internal IReadOnlyList<FeedRunner> NormalizeFeedWinnerBook(IReadOnlyList<FeedRunner> runners)
    {
        var prices = runners.Select(runner => runner.EffectivePrice > 1
                ? Snap(runner.EffectivePrice)
                : runner.EffectivePrice)
            .ToArray();
        var liveIndexes = runners.Select((runner, index) => (runner, index))
            .Where(item => item.runner.HasPrice && !item.runner.ExplicitNonRunner && prices[item.index] > 1)
            .Select(item => item.index)
            .ToList();
        if (liveIndexes.Count < 2)
            return runners.Select((runner, index) => prices[index] > 1
                ? runner with { PricingPrice = prices[index] }
                : runner).ToList();

        var book = liveIndexes.Sum(index => 1 / prices[index]);
        if (book + BookTolerance >= MinimumWinnerBook)
        {
            for (var round = 0; book - TargetBook > BookTolerance && round < MaximumBookRounds; round++)
            {
                var moves = liveIndexes.Select(index =>
                    {
                        var next = NextOddsPriceUp(prices[index]);
                        return new { Index = index, Next = next, Reduction = 1 / prices[index] - 1 / next };
                    })
                    .Where(move => move.Next > prices[move.Index])
                    .ToList();
                if (moves.Count == 0) break;

                var fullRoundBook = book - moves.Sum(move => move.Reduction);
                if (fullRoundBook + BookTolerance >= TargetBook)
                {
                    foreach (var move in moves) prices[move.Index] = move.Next;
                    book = fullRoundBook;
                    continue;
                }

                var remaining = moves.ToList();
                while (remaining.Count > 0)
                {
                    var currentDistance = Math.Abs(book - TargetBook);
                    var best = remaining
                        .Select(move => new { Move = move, NextBook = book - move.Reduction })
                        .Where(candidate => candidate.NextBook + BookTolerance >= MinimumWinnerBook &&
                            Math.Abs(candidate.NextBook - TargetBook) + BookTolerance < currentDistance)
                        .OrderBy(candidate => Math.Abs(candidate.NextBook - TargetBook))
                        .ThenBy(candidate => runners[candidate.Move.Index].CompetitorId)
                        .FirstOrDefault();
                    if (best is null) break;
                    prices[best.Move.Index] = best.Move.Next;
                    book = best.NextBook;
                    remaining.Remove(best.Move);
                }
                break;
            }
        }

        return runners.Select((runner, index) => prices[index] > 1
            ? runner with { PricingPrice = prices[index] }
            : runner).ToList();
    }

    private static void NormalizeRaceWinnerProportionally(
        List<GeneratedSelection> selections, double targetBook, double minimumBook)
    {
        // Race Winner uses the same balanced ladder walk as the other derived
        // markets. Complete whole rounds first and at most one partial round,
        // so every live runner is moved N or N+1 rungs from its provider quote.
        WalkMarketToTargetBook(selections, targetBook, minimumBook);
    }

    private static GeneratedMarket BuildPlace(PricingEvent ev, IReadOnlyList<PricingRunner> runners, int places, int suffix, string name, int type, int order, double[] placeProbabilities)
    {
        var probabilities = placeProbabilities;
        var marketId = MakeMarketId(ev.EventId, suffix);
        var selections = runners.Select((runner, index) =>
        {
            var probability = runner.Scratch ? 0 : Math.Min(.999999, probabilities[index]);
            return Clone(runner, marketId, RunnerOrdinal(runner, index), name, probability > 0 ? Snap(1 / probability) : MaximumOdds, runner.Scratch);
        }).ToList();
        WalkMarketToTargetBook(selections, places, places - .01);
        return Market(marketId, name, type, order, selections);
    }

    private static GeneratedMarket BuildBettingWithout(PricingEvent ev, IReadOnlyList<PricingRunner> runners, PricingBettingWithout config)
    {
        var excluded = runners.Select((runner, index) => (runner, index)).FirstOrDefault(x => x.runner.SelectionId == config.ExcludedSelectionId);
        if (!config.Enabled || config.Voided || excluded.runner is null || excluded.runner.Scratch)
            return Disabled(ev.EventId, config.MarketSuffix, "Betting Without", 1004, config.DisplayOrder);
        var probabilities = WinProbabilities(runners);
        var remaining = 1 - probabilities[excluded.index];
        var name = $"Betting Without {excluded.runner.Name}";
        if (remaining <= 0) return Disabled(ev.EventId, config.MarketSuffix, name, 1004, config.DisplayOrder);
        var marketId = MakeMarketId(ev.EventId, config.MarketSuffix);
        var selections = runners.Select((runner, index) => (runner, index)).Where(x => x.index != excluded.index).Select(x =>
        {
            var probability = x.runner.Scratch ? 0 : Math.Min(.999999, probabilities[x.index] / remaining);
            return Clone(x.runner, marketId, RunnerOrdinal(x.runner, x.index), name, probability > 0 ? Snap(1 / probability) : MaximumOdds, x.runner.Scratch);
        }).ToList();
        WalkMarketToTargetBook(selections, TargetBook, MinimumWinnerBook);
        return Market(marketId, name, 1004, config.DisplayOrder, selections);
    }

    private static GeneratedMarket BuildIndex(
        PricingEvent ev,
        IReadOnlyList<PricingRunner> runners,
        int suffix,
        string name,
        int type,
        int order,
        IReadOnlyList<double> points)
    {
        var wins = WinProbabilities(runners);
        // Index points must use the exact finishing-order distribution. The
        // regression shortcut used by fixed-place products materially
        // understated strong favourites (for example 23.82 versus an exact
        // 30.88), creating false executable arbs against Spreadex.
        var cumulative = Enumerable.Range(1, points.Count)
            .Select(place => place == 1 ? wins : ExactPlaceProbabilities(wins, place))
            .ToArray();
        var marketId = MakeMarketId(ev.EventId, suffix);
        var selections = runners.Select((runner, runnerIndex) =>
        {
            var expected = 0d;
            for (var placeIndex = 0; placeIndex < points.Count; placeIndex++)
            {
                var exact = cumulative[placeIndex][runnerIndex] -
                    (placeIndex == 0 ? 0 : cumulative[placeIndex - 1][runnerIndex]);
                expected += Math.Max(0, exact) * points[placeIndex];
            }
            expected = Math.Round(expected, 2, MidpointRounding.AwayFromZero);
            return new GeneratedSelection
            {
                EventId = ev.EventId,
                MarketId = marketId,
                MarketName = name,
                SelectionId = marketId * 100L + RunnerOrdinal(runner, runnerIndex),
                CompetitorId = runner.CompetitorId,
                Name = runner.Name,
                Identifier = runner.Identifier,
                PriceBack = expected,
                PriceBackLast = expected,
                PriceLay = 0,
                Index = int.TryParse(runner.Identifier, out var index) ? index : runnerIndex + 1,
                Line = expected,
                Active = runner.Scratch ? 0 : runner.Active ? 1 : 0,
                Display = runner.Scratch ? 0 : 1,
                Scratch = runner.Scratch ? 1 : 0,
                Rule4Time = runner.Scratch ? runner.Rule4Time : null,
                Rule4Deduction = runner.Scratch ? runner.Rule4Deduction : 0d,
                NonRunnerTime = runner.Scratch ? runner.Rule4Time : null,
                NonRunnerPrice = runner.Scratch && runner.Rule4Time is not null ? runner.Rule4Deduction : null,
                AssetUrl = runner.AssetUrl,
                Result = runner.Result
            };
        }).ToList();
        return Market(marketId, name, type, order, selections);
    }

    private static IReadOnlyList<double> Rule4IndexPoints(IReadOnlyList<double>? spreadexPoints)
    {
        double[] standard = [50, 25, 10];
        if (spreadexPoints is not { Count: 3 } || spreadexPoints.Any(value => value <= 0)) return standard;

        // Spreadex publishes the reduced settlement points in the panel name after a Rule 4.
        // Retain their calculation for future local Rule 4 support: for a withdrawn runner with
        // midpoint M in a market whose original total is T, scale every original settlement point P
        // by (T - M) / T. Example: 8-11 has midpoint 9.5, so (85-9.5)/85*50 = 44.4;
        // the resulting schedule is 44.4/22.2/8.9. The 100 Win Index reduction is separate/TBD.
        // For now, only use a genuinely reduced, descending schedule received from Spreadex.
        var reduced = spreadexPoints[0] <= standard[0] && spreadexPoints[1] <= standard[1] &&
            spreadexPoints[2] <= standard[2] && spreadexPoints[0] >= spreadexPoints[1] &&
            spreadexPoints[1] >= spreadexPoints[2] && spreadexPoints.Sum() < standard.Sum() - .01;
        return reduced ? spreadexPoints : standard;
    }

    private static GeneratedMarket BuildInsure(PricingEvent ev, IReadOnlyList<PricingRunner> runners, int places, int suffix, string name, int type, int order, double[] placeProbabilities)
    {
        var wins = WinProbabilities(runners);
        var placed = placeProbabilities;
        var marketId = MakeMarketId(ev.EventId, suffix);
        var selections = runners.Select((runner, index) =>
        {
            var refund = Math.Max(0, placed[index] - wins[index]);
            var price = !runner.Scratch && wins[index] > 0 ? Math.Max(1.01, (1 - refund) / wins[index]) : 1000;
            return Clone(runner, marketId, RunnerOrdinal(runner, index), name, Snap(price), runner.Scratch);
        }).ToList();
        return Market(marketId, name, type, order, selections);
    }

    private static GeneratedMarket BuildMatchBet(PricingEvent ev, IReadOnlyList<PricingRunner> runners, PricingMatchBet pair)
    {
        var a = runners.FirstOrDefault(x => x.SelectionId == pair.RunnerASelectionId);
        var b = runners.FirstOrDefault(x => x.SelectionId == pair.RunnerBSelectionId);
        var marketId = MakeMarketId(ev.EventId, pair.MarketSuffix);
        var name = a is not null && b is not null ? $"Match Betting: {a.Name} v {b.Name}" : "Match Betting";
        if (!pair.Enabled || a is null || b is null)
            return new GeneratedMarket(marketId, name, 1001,
                MatchBetDisplayOrderStart + pair.MarketSuffix - 9100, false, false, []);
        var mustVoid = pair.Voided || a.Scratch || b.Scratch;
        var weights = WinProbabilities(runners);
        var indexA = runners.IndexOf(a);
        var indexB = runners.IndexOf(b);
        var total = weights[indexA] + weights[indexB];
        var pa = !mustVoid && total > 0 ? Math.Min(.999999, weights[indexA] / total) : 0;
        var pb = !mustVoid && total > 0 ? Math.Min(.999999, weights[indexB] / total) : 0;
        var selections = new List<GeneratedSelection>
        {
            Clone(a, marketId, 1, name, pa > 0 ? Snap(1 / pa) : 1, a.Scratch),
            Clone(b, marketId, 2, name, pb > 0 ? Snap(1 / pb) : 1, b.Scratch)
        };
        if (mustVoid) selections.ForEach(x => { x.Active = 0; x.Display = 1; x.Result = RacingResultCodes.Void; });
        else WalkMarketToTargetBook(selections, TargetBook, MinimumWinnerBook);
        return new GeneratedMarket(marketId, name, 1001,
            MatchBetDisplayOrderStart + pair.MarketSuffix - 9100, !mustVoid, !mustVoid, selections);
    }

    private static GeneratedMarket BuildForecast(
        PricingEvent ev,
        IReadOnlyList<PricingRunner> runners,
        PricingForecast forecast)
    {
        var a = runners.FirstOrDefault(x => x.SelectionId == forecast.RunnerASelectionId);
        var b = runners.FirstOrDefault(x => x.SelectionId == forecast.RunnerBSelectionId);
        var marketId = MakeMarketId(ev.EventId, forecast.MarketSuffix);
        var name = a is not null && b is not null ? $"Forecast: {a.Name} / {b.Name}" : "Forecast";
        var order = ReservedNewMarketDisplayOrderStart + forecast.MarketSuffix - ForecastFirstSuffix;
        if (!forecast.Enabled || a is null || b is null)
            return new GeneratedMarket(marketId, name, ForecastMarketType, order, false, false, []);

        var mustVoid = forecast.Voided || a.Scratch || b.Scratch;
        var weights = WinProbabilities(runners);
        var indexA = runners.IndexOf(a);
        var indexB = runners.IndexOf(b);
        var pa = weights[indexA];
        var pb = weights[indexB];
        var aThenB = !mustVoid && pa > 0 && pb > 0 && pa < 1
            ? Math.Min(.999999, pa * pb / (1 - pa))
            : 0;
        var bThenA = !mustVoid && pa > 0 && pb > 0 && pb < 1
            ? Math.Min(.999999, pb * pa / (1 - pb))
            : 0;
        var either = Math.Min(.999999, aThenB + bThenA);
        var selections = new List<GeneratedSelection>
        {
            ForecastSelection(ev, marketId, name, 1, $"{a.Name} to beat {b.Name}", a, aThenB, mustVoid),
            ForecastSelection(ev, marketId, name, 2, $"{b.Name} to beat {a.Name}", b, bThenA, mustVoid),
            ForecastSelection(
                ev,
                marketId,
                name,
                3,
                a is not null && b is not null
                    ? $"Reverse Forecast: {a.Name} / {b.Name}"
                    : "Reverse Forecast",
                null,
                either,
                mustVoid)
        };
        return new GeneratedMarket(marketId, name, ForecastMarketType, order, !mustVoid, !mustVoid, selections);
    }

    private static GeneratedSelection ForecastSelection(
        PricingEvent ev,
        long marketId,
        string marketName,
        int ordinal,
        string name,
        PricingRunner? runner,
        double probability,
        bool voided) => new()
    {
        EventId = ev.EventId,
        MarketId = marketId,
        MarketName = marketName,
        SelectionId = marketId * 100L + ordinal,
        CompetitorId = runner?.CompetitorId,
        Name = name,
        Identifier = runner?.Identifier ?? "E",
        PriceBack = probability > 0 ? Snap(1 / probability) : MaximumOdds,
        PriceBackLast = probability > 0 ? Snap(1 / probability) : MaximumOdds,
        PriceLay = 0,
        Index = ordinal,
        Active = voided ? 0 : 1,
        Display = 1,
        Scratch = voided ? 1 : 0,
        AssetUrl = runner?.AssetUrl,
        Result = voided ? RacingResultCodes.Void : null
    };

    private static GeneratedMarket BuildGreyhoundGroups(PricingEvent ev, IReadOnlyList<PricingRunner> runners)
    {
        var groups = new (int[] traps, string name, string id)[]
        {
            ([1,3,5], "Odd", "01"), ([2,4,6], "Even", "02"),
            ([1,2,3], "Traps 1 or 2 or 3", "03"), ([4,5,6], "Traps 4 or 5 or 6", "04"),
            ([1,2], "Traps 1 or 2", "05"), ([3,4], "Traps 3 or 4", "06"), ([5,6], "Traps 5 or 6", "07")
        };
        var marketId = MakeMarketId(ev.EventId, GreyhoundGroupsSuffix);
        var selections = groups.Select((group, index) =>
        {
            var members = runners.Where(x => int.TryParse(x.Identifier, out var trap) && group.traps.Contains(trap)).ToList();
            var live = members.Where(x => !x.Scratch && x.Active && x.Price > 1).ToList();
            var price = live.Count == 0 ? 1 : Snap(1 / live.Sum(x => 1 / x.Price));
            return new GeneratedSelection
            {
                EventId = ev.EventId, MarketId = marketId, MarketName = "Inside-Middle-Outside-Odd-Even",
                SelectionId = marketId * 100 + index + 1, Name = group.name, Identifier = group.id,
                PriceBack = price, PriceBackLast = price, PriceLay = 0, Active = live.Count > 0 ? 1 : 0,
                Display = 1, Scratch = live.Count > 0 ? 0 : 1
            };
        }).ToList();
        WalkMarketToTargetBook(selections, 3, 2.99);
        return Market(marketId, "Inside-Middle-Outside-Odd-Even", 1001, 35, selections);
    }

    private static GeneratedMarket Market(long marketId, string name, int type, int order, IReadOnlyList<GeneratedSelection> selections) =>
        new(marketId, name, type, order, selections.Any(x => x.Active != 0 && x.Scratch == 0), IsPublicMarket(marketId), selections);
    private static GeneratedMarket Disabled(long eventId, int suffix, string name, int type, int order) =>
        new(MakeMarketId(eventId, suffix), name, type, order, false, false, []);
    private static GeneratedMarket RequireRunners(GeneratedMarket market, int actual, int minimum)
    {
        if (actual >= minimum) return market;
        foreach (var selection in market.Selections)
        {
            selection.Active = 0;
            selection.Display = 0;
        }
        return market with { Active = false, Display = false };
    }
    private static int EligibleRunnerCount(IEnumerable<PricingRunner> runners) =>
        runners.Count(runner => !runner.Scratch && runner.Active && runner.Display && runner.Price > 1);
    private static bool IsPublicMarket(long marketId) =>
        marketId % 10000 is not (Index502510Suffix or Index50302010Suffix);
    private static long MakeMarketId(long eventId, int suffix) => eventId * 10000L + suffix;

    private static GeneratedSelection Clone(PricingRunner runner, long marketId, int ordinal, string marketName, double price, bool nonRunner) => new()
    {
        EventId = runner.EventId, MarketId = marketId, MarketName = marketName,
        SelectionId = marketId * 100L + ordinal, CompetitorId = runner.CompetitorId,
        Name = runner.Name, Identifier = runner.Identifier, PriceBack = price, PriceBackLast = price,
        PriceLay = 0, Index = int.TryParse(runner.Identifier, out var index) ? index : ordinal,
        Active = nonRunner ? 0 : runner.Active ? 1 : 0, Display = runner.Display ? 1 : 0, Scratch = nonRunner ? 1 : 0,
        Rule4Time = nonRunner ? runner.Rule4Time : null,
        Rule4Deduction = nonRunner ? runner.Rule4Deduction : 0d,
        NonRunnerTime = nonRunner ? runner.Rule4Time : null,
        NonRunnerPrice = nonRunner && runner.Rule4Time is not null ? runner.Rule4Deduction : null,
        AssetUrl = runner.AssetUrl,
        // Vacant traps are known voids before the race. Sending 99 from the
        // first selection payload also keeps the back office free of pending
        // placeholders if a provider omits the vacant row from its result.
        Result = IsVacantTrap(runner.Name) ? RacingResultCodes.Void : runner.Result
    };

    private static bool IsVacantTrap(string name) =>
        name.Trim().StartsWith("Vacant Trap", StringComparison.OrdinalIgnoreCase) ||
        name.Trim().Equals("Vacant", StringComparison.OrdinalIgnoreCase);

    private static int RunnerOrdinal(PricingRunner runner, int zeroBasedIndex) =>
        int.TryParse(runner.Identifier, out var clothNumber) && clothNumber is > 0 and < 100
            ? clothNumber
            : zeroBasedIndex + 1;

    private static double[] WinProbabilities(IReadOnlyList<PricingRunner> runners)
    {
        var odds = runners.Select(x => x.Scratch || x.Price <= 1 ? 1e18 : x.Price).ToArray();
        var tax = -1d + odds.Sum(x => 1 / x);
        var result = odds.Select(x => x - tax > 0 ? Math.Max(0, (1 - tax) / (x - tax)) : 0).ToArray();
        var total = result.Sum();
        if (total > 0) for (var i = 0; i < result.Length; i++) result[i] /= total;
        return result;
    }

    internal double[][] PlaceProbabilityBooks(int sportId, IReadOnlyList<PricingRunner> runners)
    {
        var parameters = placeOptions.ForSport(sportId);
        if (!parameters.UsesDiscountedHarville)
            return [[], [], PlaceProbabilities(runners, 2), PlaceProbabilities(runners, 3), PlaceProbabilities(runners, 4)];
        var wins = WinProbabilities(runners);
        // Remove non-runners exactly: the legacy win transform uses a tiny
        // placeholder weight which must not be amplified by fractional powers.
        for (var i = 0; i < wins.Length; i++)
            if (runners[i].Scratch || runners[i].Price <= 1) wins[i] = 0;
        return DiscountedHarville.Calculate(wins, parameters);
    }

    private static double[] PlaceProbabilities(IReadOnlyList<PricingRunner> runners, int places)
    {
        var wins = WinProbabilities(runners);
        if (runners.Count is >= 6 and <= 24)
        {
            var (intercept, linear, quadratic) = places switch
            {
                2 => (Place2Intercepts[runners.Count], .9469516879, -.0404628491),
                3 => (Place3Intercepts[runners.Count], .9904870786, -.0190863692),
                4 => (Place4Intercepts[runners.Count], 1.0656611357, .0056873752),
                _ => throw new ArgumentOutOfRangeException(nameof(places))
            };
            var result = new double[runners.Count];
            var shares = new double[runners.Count];
            for (var i = 0; i < runners.Count; i++)
            {
                var p = Math.Clamp(wins[i], 1e-15, .999999999999999);
                var logit = Math.Log(p / (1 - p));
                result[i] = Logistic(intercept + linear * logit + quadratic * logit * logit);
                shares[i] = Math.Sqrt(result[i]) * (1 - result[i]);
            }
            var correction = shares.Sum();
            var delta = places - result.Sum();
            if (correction > 0) for (var i = 0; i < result.Length; i++) result[i] = Math.Clamp(result[i] + shares[i] * delta / correction, 0, .999999);
            return result;
        }
        return ExactPlaceProbabilities(wins, places);
    }

    private static double[] ExactPlaceProbabilities(double[] weights, int places)
    {
        var result = new double[weights.Length];
        Enumerate(weights, places, 0, 1, new bool[weights.Length], result);
        return result;
    }
    private static void Enumerate(double[] weights, int places, int depth, double path, bool[] used, double[] result)
    {
        if (depth >= places) return;
        var remaining = weights.Where((_, i) => !used[i]).Sum();
        if (remaining <= 0) return;
        for (var i = 0; i < weights.Length; i++)
        {
            if (used[i] || weights[i] <= 0) continue;
            var probability = path * weights[i] / remaining;
            result[i] += probability;
            used[i] = true;
            Enumerate(weights, places, depth + 1, probability, used, result);
            used[i] = false;
        }
    }
    private static double Logistic(double value) => value >= 35 ? .999999999999999 : value <= -35 ? 1e-15 : 1 / (1 + Math.Exp(-value));

    private static void WalkMarketToTargetBook(List<GeneratedSelection> selections, double targetBook, double minimumBook)
    {
        foreach (var selection in selections.Where(IsInBook))
        {
            selection.PriceBack = Snap(selection.PriceBack);
            selection.PriceBackLast = selection.PriceBack;
        }

        var liveSelections = selections.Where(IsInBook).ToList();
        if (liveSelections.Count == 0) return;
        var book = CalculateBook(selections);
        var seed = unchecked((int)(liveSelections[0].EventId ^ liveSelections.Sum(x => x.SelectionId)));
        var random = new Random(seed);

        // This adjustment is an uplift only. Never shorten an individual
        // runner merely because snapping the provider quote produced a small
        // underround; doing that created mixed positive/negative move badges.
        if (book + BookTolerance < minimumBook) return;

        for (var round = 0; book - targetBook > BookTolerance && round < MaximumBookRounds; round++)
        {
            var roundMoves = liveSelections
                .Select(selection =>
                {
                    var nextPrice = NextOddsPriceUp(selection.PriceBack);
                    return new
                    {
                        Selection = selection,
                        NextPrice = nextPrice,
                        BookReduction = 1 / selection.PriceBack - 1 / nextPrice
                    };
                })
                .Where(move => move.NextPrice > move.Selection.PriceBack)
                .ToList();
            if (roundMoves.Count == 0) break;

            // Keep moves balanced: complete whole rounds while that remains sensible.
            // The final round may move only a subset, so every runner finishes on N or N+1 moves.
            var fullRoundBook = book - roundMoves.Sum(move => move.BookReduction);
            if (fullRoundBook + BookTolerance >= targetBook)
            {
                foreach (var move in roundMoves)
                {
                    move.Selection.PriceBack = move.NextPrice;
                    move.Selection.PriceBackLast = move.NextPrice;
                }
                book = fullRoundBook;
                continue;
            }

            var remaining = roundMoves.ToList();
            var partialMoveMade = false;
            while (remaining.Count > 0)
            {
                var currentDistance = Math.Abs(book - targetBook);
                var best = remaining
                    .Select(move => new { Move = move, NextBook = book - move.BookReduction })
                    .Where(candidate => candidate.NextBook + BookTolerance >= minimumBook &&
                        Math.Abs(candidate.NextBook - targetBook) + BookTolerance < currentDistance)
                    .OrderBy(candidate => Math.Abs(candidate.NextBook - targetBook))
                    .ThenBy(candidate => candidate.Move.Selection.SelectionId)
                    .FirstOrDefault();
                if (best is null) break;

                best.Move.Selection.PriceBack = best.Move.NextPrice;
                best.Move.Selection.PriceBackLast = best.Move.NextPrice;
                book = best.NextBook;
                remaining.Remove(best.Move);
                partialMoveMade = true;
            }
            if (partialMoveMade) return;
            break;
        }
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (var i = values.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static double CalculateBook(IEnumerable<GeneratedSelection> selections) =>
        selections.Where(IsInBook).Sum(selection => 1 / selection.PriceBack);

    private static bool IsInBook(GeneratedSelection selection) =>
        selection.Scratch == 0 && selection.Active != 0 && selection.Display != 0 && selection.PriceBack > 1;

    private static double Snap(double price)
    {
        if (double.IsNaN(price) || double.IsInfinity(price)) return MaximumOdds;
        price = Math.Clamp(price, FractionalOddsLadder[0], FractionalOddsLadder[^1]);
        var index = Array.BinarySearch(FractionalOddsLadder, price);
        if (index >= 0) return FractionalOddsLadder[index];
        index = ~index;
        if (index == 0) return FractionalOddsLadder[0];
        if (index >= FractionalOddsLadder.Length) return FractionalOddsLadder[^1];
        var lower = FractionalOddsLadder[index - 1];
        var upper = FractionalOddsLadder[index];
        return price - lower <= upper - price ? lower : upper;
    }

    private static double NextOddsPriceUp(double price)
    {
        var snapped = Snap(price);
        var index = Array.BinarySearch(FractionalOddsLadder, snapped);
        return index < 0 || index >= FractionalOddsLadder.Length - 1 ? FractionalOddsLadder[^1] : FractionalOddsLadder[index + 1];
    }

    private static double PreviousOddsPriceDown(double price)
    {
        var snapped = Snap(price);
        var index = Array.BinarySearch(FractionalOddsLadder, snapped);
        return index <= 0 ? FractionalOddsLadder[0] : FractionalOddsLadder[index - 1];
    }

    internal static int CountLadderMoves(double sourcePrice, double finalPrice)
    {
        var sourceIndex = Array.BinarySearch(FractionalOddsLadder, Snap(sourcePrice));
        var finalIndex = Array.BinarySearch(FractionalOddsLadder, Snap(finalPrice));
        return sourceIndex < 0 || finalIndex < 0 ? 0 : finalIndex - sourceIndex;
    }

    internal static double SnapToOddsLadder(double price) => Snap(price);
    internal static double MoveOneLadderShorter(double price) => PreviousOddsPriceDown(price);
    internal static double MaximumLadderOdds => MaximumOdds;

    internal static double[] SnapMarketToMinimumFractionalBook(IReadOnlyList<double> fairPrices)
    {
        if (fairPrices.Count is not (2 or 3))
            throw new ArgumentOutOfRangeException(nameof(fairPrices), "Only two-way and three-way books are supported.");
        var fairProbabilities = fairPrices.Select(price => 1d / price).ToArray();
        var fairBook = fairProbabilities.Sum();
        for (var index = 0; index < fairProbabilities.Length; index++)
            fairProbabilities[index] /= fairBook;

        var bestDistortion = double.MaxValue;
        double[]? best = null;
        var candidates = fairPrices.Count == 2
            ? MinimumTwoWayFractionalBooks.Value
            : MinimumThreeWayFractionalBooks.Value;
        foreach (var candidate in candidates)
        {
            var distortion = candidate.Select((price, index) =>
                Math.Pow(1d / price - fairProbabilities[index], 2)).Sum();
            if (distortion < bestDistortion)
            {
                bestDistortion = distortion;
                best = candidate;
            }
        }
        return best?.ToArray() ?? throw new InvalidOperationException("No fractional-price book could be formed.");
    }

    private static double[][] BuildMinimumFractionalBooks(int selectionCount)
    {
        const double exactTolerance = 1e-12;
        var bestBook = double.MaxValue;
        var books = new List<double[]>();
        var candidate = new double[selectionCount];
        void Search(int depth, double book)
        {
            if (depth == selectionCount)
            {
                if (book + exactTolerance < TargetBook) return;
                if (book < bestBook - exactTolerance)
                {
                    bestBook = book;
                    books.Clear();
                }
                if (Math.Abs(book - bestBook) <= exactTolerance)
                    books.Add(candidate.ToArray());
                return;
            }
            foreach (var price in FractionalOddsLadder)
            {
                candidate[depth] = price;
                Search(depth + 1, book + 1d / price);
            }
        }
        Search(0, 0);
        return books.ToArray();
    }

    private static IReadOnlyList<PricingRunner> WithRaceWinnerPrices(
        IReadOnlyList<PricingRunner> runners, IReadOnlyList<GeneratedSelection> raceWinner)
    {
        return runners.Select((runner, index) => new PricingRunner
        {
            EventId = runner.EventId, MarketId = runner.MarketId, SelectionId = runner.SelectionId,
            CompetitorId = runner.CompetitorId, MarketName = runner.MarketName,
            Name = runner.Name, Identifier = runner.Identifier,
            Price = raceWinner[index].PriceBack, PreviousPrice = runner.PreviousPrice,
            Active = runner.Active, Display = runner.Display, Scratch = runner.Scratch,
            AssetUrl = runner.AssetUrl, Result = runner.Result
        }).ToList();
    }

    private static double[] BuildFractionalOddsLadder()
    {
        (int numerator, int denominator)[] fractions =
        [
            (1,5000),(1,2500),(1,1500),(1,1000),(1,500),(1,250),(1,200),(1,100),(1,66),(1,50),
            (1,33),(1,25),(1,20),(1,16),(1,14),(1,12),(1,10),(1,9),(1,8),(1,7),(1,6),(1,5),
            (2,9),(1,4),(2,7),(1,3),(4,11),(2,5),(4,9),(1,2),(8,15),(4,7),(8,13),(4,6),(8,11),
            (4,5),(10,11),(1,1),(11,10),(6,5),(5,4),(11,8),(6,4),
            (13,8),(7,4),(15,8),(2,1),(11,5),(9,4),(5,2),
            (11,4),(3,1),(16,5),(10,3),(7,2),(4,1),(9,2),
            (5,1),(11,2),(6,1),(13,2),(7,1),(15,2),(8,1),(9,1),(10,1),(11,1),(12,1),(14,1),(16,1),
            (18,1),(20,1),(22,1),(25,1),(28,1),(33,1),(40,1),(50,1),(66,1),(80,1),(100,1),
            (125,1),(150,1),(200,1),(250,1),(300,1),(500,1),(750,1),(1000,1)
        ];
        return fractions.Select(value => 1d + (double)value.numerator / value.denominator).ToArray();
    }
}

internal static class PricingRunnerListExtensions
{
    public static int IndexOf(this IReadOnlyList<PricingRunner> source, PricingRunner value)
    {
        for (var i = 0; i < source.Count; i++) if (ReferenceEquals(source[i], value)) return i;
        return -1;
    }
}
