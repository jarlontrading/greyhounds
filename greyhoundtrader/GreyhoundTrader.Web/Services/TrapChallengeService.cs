using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

public sealed class TrapChallengeService(
    RacingRepository repository,
    TimeformGreyhoundForecastService timeform)
{
    internal const int MarketTypeId = 2002;
    internal const int MarketSuffix = 9700;
    internal const long EventNamespace = 1_000_000_000L;
    private static readonly TimeZoneInfo UkTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");

    public async Task<MeetingTrapChallengeBoard> GetAsync(
        string meeting, CancellationToken cancellationToken, long? anchorStartTimeUnix = null)
    {
        var races = SelectMeetingDay(
            await repository.GetMeetingTrapChallengeRacesAsync(meeting, cancellationToken),
            anchorStartTimeUnix);
        races = timeform.ApplyTrapChallengeFallback(meeting, races);
        return Calculate(meeting, races);
    }

    internal async Task<GeneratedEventSnapshot?> BuildPublishingSnapshotAsync(
        string meeting, long anchorStartTimeUnix, CancellationToken cancellationToken)
    {
        var races = SelectMeetingDay(
            await repository.GetMeetingTrapChallengeRacesAsync(meeting, cancellationToken),
            anchorStartTimeUnix);
        races = timeform.ApplyTrapChallengeFallback(meeting, races);
        if (races.Count < 2) return null;
        var firstRace = races.OrderBy(race => race.StartTimeUnix).First();
        var candidateEventId = checked(EventNamespace + firstRace.EventId);
        var meetingDate = TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(firstRace.StartTimeUnix), UkTimeZone).Date;
        var isEveningSession = (await repository.GetMeetingsAsync(4, cancellationToken))
            .Any(other =>
            {
                if (other.FirstStartUnix >= firstRace.StartTimeUnix ||
                    RacingEventNames.MeetingKey(other.Name) != RacingEventNames.MeetingKey(meeting))
                    return false;
                var otherDate = TimeZoneInfo.ConvertTime(
                    DateTimeOffset.FromUnixTimeSeconds(other.FirstStartUnix), UkTimeZone).Date;
                return otherDate == meetingDate;
            });
        var eventId = await repository.ResolveTrapChallengeEventIdAsync(
            meeting, meetingDate, candidateEventId, firstRace.StartTimeUnix, cancellationToken);
        return BuildPublishingSnapshot(meeting, races, eventId, isEveningSession);
    }

    internal static GeneratedEventSnapshot? BuildPublishingSnapshot(
        string meeting, IReadOnlyList<TrapChallengeRaceInput> races, long? stableEventId = null,
        bool isEveningSession = false)
    {
        if (races.Count < 2) return null;
        var ordered = races.OrderBy(race => race.StartTimeUnix).ToList();
        var board = Calculate(meeting, ordered);
        var firstRace = ordered[0];
        var eventId = stableEventId ?? checked(EventNamespace + firstRace.EventId);
        var marketId = checked(eventId * 10_000 + MarketSuffix);
        var complete = board.Status == "resulted";
        var marketActive = board.Status == "active";
        // Calculate() only reports "resulted" once the recorded winners reconcile with the
        // race count, so the leader count here is always at least one. The explicit floor
        // stays as a second line of defence: a zero maximum would make every trap a leader.
        var mostWins = complete ? board.Selections.Max(x => x.Winners) : 0;
        var leaders = complete && mostWins > 0
            ? board.Selections.Where(selection => selection.Winners == mostWins)
                .Select(selection => selection.Trap).ToHashSet()
            : [];
        var prices = SnapPrices(board.Selections);
        var displayMeetingName = DisplayMeetingName(meeting, isEveningSession);
        var eventName = $"{displayMeetingName} Trap Challenge";
        var selections = board.Selections.Select((selection, index) => new GeneratedSelection
        {
            EventId = eventId,
            MarketName = "Trap Challenge",
            SelectionId = checked(marketId * 100 + selection.Trap),
            CompetitorId = null,
            Name = $"Trap {selection.Trap} ({selection.Winners} {(selection.Winners == 1 ? "winner" : "winners")})",
            Identifier = selection.Trap.ToString(),
            PriceBack = prices[index],
            PriceBackLast = prices[index],
            PriceLay = prices[index],
            Index = 0,
            Line = 0,
            Active = marketActive ? 1 : 0,
            Display = 1,
            Scratch = 0,
            Rule4Time = null,
            // No deduction: Calculate() prices P(trap is paid), which already carries the
            // chance of a tie, so a joint leader is settled as an ordinary full winner.
            Rule4Deduction = 0,
            AssetUrl = RacingFeedRepository.GreyhoundTrapAssetUrl(selection.Trap),
            Result = complete ? leaders.Contains(selection.Trap) ? "1" : "0" : null,
            MarketId = marketId
        }).ToList();
        var market = new GeneratedMarket(
            marketId, "Trap Challenge", MarketTypeId, 10,
            marketActive, !complete, selections);
        var challengeEvent = new PricingEvent(
            eventId, 4, displayMeetingName, eventName, firstRace.StartTimeUnix,
            complete ? "3" : board.Status == "suspended" ? "2" : "0",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return new GeneratedEventSnapshot(challengeEvent, [market]);
    }

    internal static MeetingTrapChallengeBoard Calculate(
        string meeting,
        IReadOnlyList<TrapChallengeRaceInput> races)
    {
        var winners = new int[6];
        var futureRaces = new List<double[]>();
        var pricedRaces = 0;
        var resultedRaces = 0;
        var waitingFor = new List<string>();
        TrapChallengeRaceInput? suspendedRace = null;

        foreach (var race in races)
        {
            var entries = race.Runners
                .GroupBy(x => x.Trap)
                .Select(group => group
                    .OrderBy(x => x.Scratch || IsVacant(x.Name))
                    .ThenByDescending(x => x.CurrentPrice > 1)
                    .First())
                .ToList();
            if (IsResulted(race.Status))
            {
                var raceWinners = entries.Where(IsWinner).Select(x => x.Trap).Distinct().ToList();
                if (raceWinners.Count > 0)
                {
                    foreach (var trap in raceWinners) winners[trap - 1]++;
                    resultedRaces++;
                    pricedRaces++;
                    continue;
                }
                if (IsVoid(entries))
                {
                    resultedRaces++;
                    pricedRaces++;
                    continue;
                }
                waitingFor.Add(RaceLabel(race));
                continue;
            }

            if (IsSuspended(race) && suspendedRace is null) suspendedRace = race;
            var live = entries.Where(x => !x.Scratch && !IsVacant(x.Name)).ToList();
            var missingPrice = live.Any(x => EffectivePrice(x) <= 1);
            if (live.Count < 2 || missingPrice)
            {
                waitingFor.Add(RaceLabel(race));
                continue;
            }
            var weights = new double[6];
            foreach (var runner in live) weights[runner.Trap - 1] = 1d / EffectivePrice(runner);
            var book = weights.Sum();
            if (book <= 0)
            {
                waitingFor.Add(RaceLabel(race));
                continue;
            }
            for (var trap = 0; trap < 6; trap++) weights[trap] /= book;
            futureRaces.Add(weights);
            pricedRaces++;
        }

        if (races.Count == 0)
            return Empty(meeting, "waiting", "Waiting for meeting races", 0, winners);
        if (waitingFor.Count > 0)
            return Empty(meeting, suspendedRace is null ? "waiting" : "suspended",
                suspendedRace is null
                    ? $"Waiting for complete prices/results: {string.Join(", ", waitingFor)}"
                    : $"Suspended - awaiting {RaceLabel(suspendedRace)} result; incomplete prices/results: {string.Join(", ", waitingFor)}",
                races.Count, winners, pricedRaces, resultedRaces);

        var states = new Dictionary<TrapScore, double> { [new()] = 1d };
        foreach (var race in futureRaces)
        {
            var next = new Dictionary<TrapScore, double>();
            foreach (var (score, stateProbability) in states)
                for (var trap = 0; trap < 6; trap++)
                {
                    if (race[trap] <= 0) continue;
                    var updated = score.Increment(trap);
                    next[updated] = next.GetValueOrDefault(updated) + stateProbability * race[trap];
                }
            states = next;
        }

        // settlement[trap] is P(this trap is paid), and settlement is "pays every joint
        // leader in full". It used to accumulate stateProbability / leaders.Count - the
        // dead-heat stake split - so the market was priced as though a tie split the stake
        // while settlement paid each tied trap a full winner. That paid E[number of leaders]
        // against 100% of turnover: measured over 2- to 8-race meetings the market returned
        // 1.50 to 1.96 per unit staked.
        //
        // A deduction cannot repair this. Sportuno applies rule4_deduction to WINNINGS, so
        // matching a dead-heat return of stake / n * P needs
        // d = 1 - (P - n) / (n * (P - 1)), which depends on the price each bet was struck at
        // - and bets here are struck at many prices across a meeting. It also exceeds 1.0
        // whenever P <= n (a 3-way tie at 3.00 needs d = 1.000), which is not expressible at
        // all. So the price carries the tie instead, and settlement needs no deduction.
        var settlement = new double[6];
        var tieProbability = 0d;
        foreach (var (score, stateProbability) in states)
        {
            var totals = score.Values.Select((value, trap) => value + winners[trap]).ToArray();
            var maximum = totals.Max();
            var leaders = Enumerable.Range(0, 6).Where(trap => totals[trap] == maximum).ToList();
            if (leaders.Count > 1) tieProbability += stateProbability;
            foreach (var trap in leaders) settlement[trap] += stateProbability;
        }

        var selections = Enumerable.Range(0, 6).Select(trap => new TrapChallengeSelection(
            trap + 1,
            winners[trap],
            settlement[trap],
            settlement[trap] > 0 ? 1d / settlement[trap] : null)).ToList();
        // Every race has reached a terminal state, but that alone does not make the board
        // settleable. A void race contributes no winner and an in-race dead heat contributes
        // two, so reconcile the recorded winners against the race count before resulting.
        // Without this an abandoned meeting reaches Max(Winners) == 0, every trap ties on
        // zero, and all six settle as winners.
        var allRacesTerminal = resultedRaces == races.Count;
        var recordedWinners = winners.Sum();
        var complete = allRacesTerminal && recordedWinners == races.Count;
        var status = complete ? "resulted"
            : allRacesTerminal || suspendedRace is not null ? "suspended"
            : "active";
        var message = complete
            ? "Meeting complete"
            : allRacesTerminal
                ? $"Not settleable - {recordedWinners} winner(s) recorded across {races.Count} races; needs review"
                : suspendedRace is not null
                    ? $"Suspended - awaiting {RaceLabel(suspendedRace)} result"
                    : $"{resultedRaces}/{races.Count} races resulted";
        return new(meeting, status, message, races.Count, pricedRaces, resultedRaces, tieProbability, selections);
    }

    private static MeetingTrapChallengeBoard Empty(
        string meeting, string status, string message, int raceCount, int[] winners,
        int pricedRaceCount = 0, int resultedRaceCount = 0) =>
        new(meeting, status, message, raceCount, pricedRaceCount, resultedRaceCount, 0,
            Enumerable.Range(0, 6).Select(trap =>
                new TrapChallengeSelection(trap + 1, winners[trap], null, null)).ToList());

    private static double EffectivePrice(TrapChallengeRunnerInput runner) =>
        runner.CurrentPrice > 1 ? runner.CurrentPrice : runner.PreviousPrice > 1 ? runner.PreviousPrice : 0;
    private static bool IsVacant(string name) =>
        name.Trim().StartsWith("Vacant", StringComparison.OrdinalIgnoreCase) ||
        name.Trim().Equals("Reserve", StringComparison.OrdinalIgnoreCase);
    private static bool IsWinner(TrapChallengeRunnerInput runner) =>
        runner.FinishingPosition == 1 || Normalise(runner.Result) is "1" or "won" or "winner";
    private static bool IsVoid(IReadOnlyList<TrapChallengeRunnerInput> runners) =>
        runners.Count > 0 && runners.All(runner => IsVacant(runner.Name) ||
            Normalise(runner.Result) is "-1" or "99" or "void" ||
            Normalise(runner.ResultStatus) is "void" or "no race" or "abandoned");
    private static bool IsResulted(string status) => Normalise(status) is "3" or "resulted" or "post game";
    private static bool IsSuspended(TrapChallengeRaceInput race) =>
        race.InProgress || !race.Active || Normalise(race.Status) is "1" or "2" or "live" or "in play" or "suspended" or "post play";
    private static string Normalise(string? value) => value?.Trim().ToLowerInvariant() ?? "";
    private static string RaceLabel(TrapChallengeRaceInput race) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), UkTimeZone).ToString("HH:mm");

    private static IReadOnlyList<TrapChallengeRaceInput> SelectMeetingDay(
        IReadOnlyList<TrapChallengeRaceInput> races, long? anchorStartTimeUnix)
    {
        if (races.Count == 0) return races;
        var anchor = anchorStartTimeUnix is { } value
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(value), UkTimeZone).Date
            : TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, UkTimeZone).Date;
        var selected = races.Where(race =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), UkTimeZone).Date == anchor)
            .OrderBy(race => race.StartTimeUnix).ToList();
        if (selected.Count > 0) return selected;
        return races.GroupBy(race =>
                TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), UkTimeZone).Date)
            .OrderBy(group => Math.Abs((group.Key - anchor).TotalDays))
            .ThenBy(group => group.Key)
            .First().OrderBy(race => race.StartTimeUnix).ToList();
    }

    // Once every joint leader is paid in full the six traps stop being a partition: more
    // than one can win, backing all six is not a hedge, and a book walked to 100% therefore
    // says nothing about the liability. Margin here is per selection - each trap must be
    // priced at or below its own fair price - so each is snapped DOWN to the ladder and the
    // book walk is gone. A trap that can no longer be paid is quoted at the ladder ceiling
    // rather than the old 1.00, which was a malformed price on a live selection.
    private static double[] SnapPrices(IReadOnlyList<TrapChallengeSelection> selections) =>
        selections.Select(selection =>
        {
            if (selection.FairPrice is not (>= 1 and <= 1001)) return RacingMarketCalculator.MaximumLadderOdds;
            var fair = selection.FairPrice.Value;
            var snapped = RacingMarketCalculator.SnapToOddsLadder(fair);
            // Snap() rounds to the nearest rung, which can land above fair and hand back a
            // negative margin on that selection.
            return snapped > fair ? RacingMarketCalculator.MoveOneLadderShorter(snapped) : snapped;
        }).ToArray();

    private static string DisplayMeetingName(string meeting, bool isEveningSession)
    {
        var trimmed = meeting.Trim();
        foreach (var suffix in new[] { " EVE", " AM", " PM" })
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].TrimEnd();
                break;
            }
        return isEveningSession ? $"{trimmed} Evening" : trimmed;
    }

    private readonly record struct TrapScore(int T1 = 0, int T2 = 0, int T3 = 0, int T4 = 0, int T5 = 0, int T6 = 0)
    {
        public int[] Values => [T1, T2, T3, T4, T5, T6];
        public TrapScore Increment(int trap) => trap switch
        {
            0 => this with { T1 = T1 + 1 },
            1 => this with { T2 = T2 + 1 },
            2 => this with { T3 = T3 + 1 },
            3 => this with { T4 = T4 + 1 },
            4 => this with { T5 = T5 + 1 },
            5 => this with { T6 = T6 + 1 },
            _ => this
        };
    }
}

internal sealed record TrapChallengeRaceInput(
    long EventId,
    long StartTimeUnix,
    string Status,
    bool Active,
    bool InProgress,
    IReadOnlyList<TrapChallengeRunnerInput> Runners);

internal sealed record TrapChallengeRunnerInput(
    int Trap,
    string Name,
    double CurrentPrice,
    double PreviousPrice,
    bool Scratch,
    string? Result,
    int? FinishingPosition,
    string? ResultStatus);
