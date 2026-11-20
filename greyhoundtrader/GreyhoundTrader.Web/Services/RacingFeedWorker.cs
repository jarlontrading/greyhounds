namespace RacingTrader.Web.Services;

using System.Globalization;
using MySqlConnector;

internal sealed class RacingFeedWorker(
    RacingFeedClient feed,
    BetAhoyFeed betAhoy,
    RacingFeedRepository feedRepository,
    CanonicalEventService canonicalEvents,
    RacingRepository repository,
    BetfairBiasService betfairBias,
    SpreadexRacingService spreadex,
    RacingPublisher publisher,
    RacingActivityLog activity,
    RaceAuditTrail audit,
    RaceTerminalGate terminalGate,
    RacingFeedDiagnostics diagnostics,
    ILogger<RacingFeedWorker> logger) : BackgroundService
{
    // Greyhound Trader owns only sport 4. It must never consume the live
    // horse process's bounded pricing queue.
    private static readonly int[] Sports = [4];
    private readonly Dictionary<long, string> _lastRunnerSnapshots = new();
    private readonly Dictionary<string, string> _lastRejectedRunnerFingerprints = new();
    private readonly Dictionary<long, string> _spreadexSuspendedRunnerSnapshots = new();
    private readonly HashSet<long> _pendingDirectEventIds = [];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bet365 capture intentionally starts after five seconds and Spreadex needs a few
        // more seconds to restore its model subscriptions. Do not price queued races from
        // stale BetsAPI cards before the two live bridges are ready.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        // Let the lightweight live-price bridges initialise and process the
        // already-known card before the first heavyweight full-feed discovery.
        var nextDiscovery = DateTime.MinValue;
        var nextSubscriptionReconcile = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            diagnostics.CycleStarted();
            try
            {
                if (DateTime.UtcNow >= nextDiscovery)
                {
                    nextDiscovery = DateTime.UtcNow.AddMinutes(5);
                    foreach (var sportId in Sports)
                    {
                        var discoveredEvents = await feed.GetEventsAsync(sportId, stoppingToken);
                        var events = await canonicalEvents.ReconcileBet365Async(discoveredEvents, stoppingToken);
                        foreach (var discovered in events)
                            await audit.WriteAsync("discovery-observed", discovered.EventId, discovered.SportId,
                                discovered.Meeting, discovered.Name, discovered.StartTimeUnix, "BET365/BETSAPI",
                                "pre-event feed canonical reconciliation",
                                "Race observed in the scheduled discovery cycle.",
                                new { discovered.TimeStatus, discovered.UpdatedAtUnix, discovered.OddsUpdatedAtUnix }, stoppingToken);
                        await UpsertEventsWithRetryAsync(events, stoppingToken);
                        // Do not publish a greyhound event shell before its card exists. Sportuno
                        // displays the parent immediately, which creates a blank race until a later
                        // walkthrough supplies the markets and runners. PublishAsync sends the event,
                        // markets and selections together when either a suspended 1.00 card or a
                        // genuinely priced card has been built.
                        var duplicateEvents = await feedRepository.SuppressNonCanonicalDuplicateEventsAsync(stoppingToken);
                        foreach (var duplicate in duplicateEvents)
                            await publisher.PublishEventsAsync([duplicate], stoppingToken);
                        logger.LogInformation("Refreshed {Count} feed events for sport {SportId}.", events.Count, sportId);
                    }
                    if (DateTime.UtcNow >= nextSubscriptionReconcile)
                    {
                        nextSubscriptionReconcile = DateTime.UtcNow.AddMinutes(5);
                        try
                        {
                            var autoSubscriptions = await repository.ReconcileMeetingSubscriptionsAsync(stoppingToken);
                            foreach (var subscription in autoSubscriptions.Where(item => item.SportId == 4))
                            {
                                await activity.WriteAsync("subscription",
                                    $"{subscription.Meeting} auto-subscribed from previous trading history.",
                                    subscription.SportId, meeting: subscription.Meeting, cancellationToken: stoppingToken);
                                logger.LogInformation("Auto-subscribed previously traded {Meeting} competition for sport {SportId}.",
                                    subscription.Meeting, subscription.SportId);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Meeting subscription reconciliation failed; live price publishing will continue.");
                        }
                    }
                }

                // Browser websocket changes are the most time-sensitive price work. Process
                // them before the routine polling batch so they cannot sit behind dozens of
                // unchanged races.
                var candidates = new List<PricingEvent>();
                _pendingDirectEventIds.UnionWith(feed.GetFreshDirectEventIds());
                var directEventIds = _pendingDirectEventIds.ToList();
                var directCandidateIds = new HashSet<long>();
                if (directEventIds.Count > 0)
                {
                    var directCandidates = (await feedRepository.GetPriceCandidatesByIdsAsync(directEventIds, stoppingToken))
                        .Where(item => item.SportId == 4).ToList();
                    var directById = directCandidates.ToDictionary(item => item.EventId);
                    directCandidates = directEventIds
                        .Where(directById.ContainsKey)
                        .Select(eventId => directById[eventId])
                        .ToList();
                    candidates.AddRange(directCandidates);
                    directCandidateIds.UnionWith(directCandidates.Select(x => x.EventId));
                }
                var changedOrLiveCandidates = (await feedRepository.GetPriceCandidatesAsync(stoppingToken))
                    .Where(item => item.SportId == 4).ToList();
                foreach (var changedOrLiveCandidate in changedOrLiveCandidates)
                    if (candidates.All(x => x.EventId != changedOrLiveCandidate.EventId)) candidates.Add(changedOrLiveCandidate);
                var suspensionCandidates = (await feedRepository.GetSuspensionCandidatesAsync(stoppingToken))
                    .Where(item => item.SportId == 4).ToList();
                foreach (var suspensionCandidate in suspensionCandidates)
                    if (candidates.All(x => x.EventId != suspensionCandidate.EventId)) candidates.Add(suspensionCandidate);
                var duePreLiveCandidates = (await feedRepository.GetDuePreLivePriceCandidatesAsync(stoppingToken))
                    .Where(item => item.SportId == 4).ToList();
                foreach (var duePreLiveCandidate in duePreLiveCandidates)
                    if (candidates.All(x => x.EventId != duePreLiveCandidate.EventId)) candidates.Add(duePreLiveCandidate);
                // Always process every websocket-changed race, but bound routine work so a
                // newly arriving change never waits behind a long serial network/DB cycle.
                // Preserve GetPriceCandidatesAsync ordering: explicit -4 reprices are
                // first, followed by -3/-2 feed work. Provider changes fill remaining
                // slots but cannot jump ahead of a user's clean reprice request.
                var workBatch = candidates.Where(x => directCandidateIds.Contains(x.EventId))
                    .Concat(changedOrLiveCandidates)
                    .Concat(suspensionCandidates)
                    .Concat(duePreLiveCandidates)
                    .DistinctBy(x => x.EventId)
                    .Take(10)
                    .ToList();
                diagnostics.CandidatesLoaded(changedOrLiveCandidates.Count,
                    suspensionCandidates.Count, duePreLiveCandidates.Count, workBatch.Count);
                foreach (var racingEvent in workBatch)
                {
                    diagnostics.EventStarted(racingEvent);
                    try
                    {
                        if (terminalGate.IsTerminal(racingEvent.EventId)) continue;
                        if (racingEvent.Status == "2") continue;
                        if (racingEvent.Status == "1")
                        {
                            var originalRaceOff = await feed.IsRaceOffAsync(racingEvent, stoppingToken);
                            if (originalRaceOff != true)
                            {
                                logger.LogInformation("Bet365 event {EventId} is in-play but not race-off; continuing runner price updates.", racingEvent.EventId);
                            }
                            else
                            {
                                var ownsClaim = terminalGate.TryClaim(racingEvent.EventId);
                                if (!ownsClaim && !terminalGate.IsExternallyPublished(racingEvent.EventId))
                                    continue;
                                var shell = racingEvent with { Status = "2" };
                                if (ownsClaim)
                                {
                                    try
                                    {
                                        await publisher.PublishEventsAsync([shell], stoppingToken);
                                        terminalGate.MarkExternallyPublished(racingEvent.EventId);
                                    }
                                    catch
                                    {
                                        terminalGate.Release(racingEvent.EventId);
                                        throw;
                                    }
                                }
                                await feedRepository.TrySuspendEventShellAsync(racingEvent, stoppingToken);
                                await activity.WriteAsync("suspension",
                                    "Markets suspended because Bet365 in-play was confirmed by default_open_right=1.",
                                    racingEvent.SportId, racingEvent.EventId, racingEvent.Meeting, racingEvent.Name, stoppingToken);
                                var suspended = await feedRepository.SuspendRaceAsync(racingEvent, stoppingToken);
                                if (suspended is not null)
                                    await publisher.PublishAsync(suspended, stoppingToken);
                                continue;
                            }
                        }
                        diagnostics.Stage("fetching BetsAPI/providers", racingEvent);
                        var comparison = await feed.GetRunnerSnapshotsAsync(racingEvent, stoppingToken);
                        diagnostics.Stage("checking terminal state", racingEvent);
                        // Suspension runs independently and may close the race
                        // while a slow provider request is in flight. Never let
                        // that stale request reopen or republish the event.
                        if (terminalGate.IsTerminal(racingEvent.EventId) ||
                            await feedRepository.IsEventSuspendedAsync(racingEvent.EventId, stoppingToken))
                            continue;
                        var knownRunnerNames = await feedRepository.GetRaceWinnerRunnerNamesAsync(
                            racingEvent.EventId, stoppingToken);
                        comparison = ValidateRunnerFingerprints(racingEvent, knownRunnerNames, comparison);
                        diagnostics.Stage("storing provider snapshots", racingEvent);
                        try
                        {
                            await feedRepository.StorePriceSourcesAsync(racingEvent, comparison, stoppingToken);
                        }
                        catch (MySqlException ex)
                        {
                            // Provider comparison history is operational telemetry. A
                            // contended audit row must never prevent the fresh snapshot
                            // from driving and publishing the actual racing markets.
                            logger.LogWarning(ex,
                                "Provider snapshot audit write failed for event {EventId}; continuing price calculation.",
                                racingEvent.EventId);
                        }
                        var markerSnapshots = new[] { comparison.BetsApi, comparison.Direct }
                            .Where(x => x?.NonRunners.Count > 0).Cast<FeedPriceSnapshot>().ToList();
                        var validHorseRunnerNames = racingEvent.SportId == 2
                            ? new[] { comparison.Selected, comparison.Direct, comparison.BetsApi }
                                .Where(snapshot => snapshot is not null)
                                .SelectMany(snapshot => snapshot!.Runners)
                                .Where(IsGenuineHorseRunner)
                                .Select(runner => BetfairMarketMatcher.NormalizeRunner(runner.Name))
                                .Where(name => name.Length > 0)
                                .ToHashSet(StringComparer.Ordinal)
                            : null;
                        var spreadexNonRunners = racingEvent.SportId == 2
                            ? spreadex.GetNonRunners(racingEvent)
                            : [];
                        var explicitNonRunners = markerSnapshots.SelectMany(x => x.NonRunners)
                            .Concat(spreadexNonRunners)
                            .Where(x => !x.Name.Contains("Favourite", StringComparison.OrdinalIgnoreCase))
                            .Where(x => validHorseRunnerNames is null ||
                                        validHorseRunnerNames.Contains(BetfairMarketMatcher.NormalizeRunner(x.Name)))
                            // Greyhound bets follow the trap. If an active replacement
                            // occupies the same trap, the withdrawn dog's marker must
                            // not scratch that trap or trigger Rule 4.
                            .Where(marker => racingEvent.SportId != 4 || marker.Identifier is not > 0 ||
                                comparison.Selected?.Runners.Any(runner =>
                                    runner.Identifier == marker.Identifier && runner.HasPrice &&
                                    !runner.ExplicitNonRunner &&
                                    BetfairMarketMatcher.NormalizeRunner(runner.Name) !=
                                    BetfairMarketMatcher.NormalizeRunner(marker.Name)) != true)
                            .GroupBy(x => racingEvent.SportId == 4 && x.Identifier is > 0
                                ? $"trap:{x.Identifier.Value}"
                                : BetfairMarketMatcher.NormalizeRunner(x.Name))
                            .Select(x => x.First()).ToList();
                        var nonRunnerTime = spreadexNonRunners.Count > 0
                            ? DateTime.UtcNow
                            : comparison.BetsApi?.NonRunners.Count > 0
                            ? comparison.BetsApi.UpdatedAtUtc
                            : comparison.Direct?.UpdatedAtUtc ?? DateTime.UtcNow;
                        var withdrawals = await repository.ApplyExplicitFeedNonRunnersAsync(
                            racingEvent.EventId, explicitNonRunners, nonRunnerTime, stoppingToken);
                        foreach (var withdrawal in withdrawals)
                        {
                            var rule4Details = string.Join("; ", withdrawal.MarketPrices?.Select(item =>
                                $"{item.MarketName}: {item.LastPrice:0.00} => {item.Deduction:P0}") ?? []);
                            await activity.WriteAsync("rule4",
                                $"{withdrawal.RunnerName}: winner {withdrawal.PriceAtAction:0.00}, R4 {withdrawal.Rule4Deduction:P0}" +
                                $"||{rule4Details}",
                                racingEvent.SportId, racingEvent.EventId, racingEvent.Meeting, racingEvent.Name, stoppingToken);
                            logger.LogInformation("Applied explicit BetsAPI non-runner {Runner} to event {EventId}.",
                                withdrawal.RunnerName, racingEvent.EventId);
                        }
                        if (comparison.Selected is null) continue;
                        var runners = racingEvent.SportId == 2
                            ? comparison.Selected.Runners.Where(IsGenuineHorseRunner)
                                .GroupBy(runner => BetfairMarketMatcher.NormalizeRunner(runner.Name))
                                .Select(group => group.OrderBy(runner => runner.Identifier).First())
                                .ToList()
                            : comparison.Selected.Runners;
                        // A provider non-runner is authoritative for both the
                        // stored selection state and the completeness check
                        // used to decide whether Spreadex can drive the book.
                        // Previously it was withdrawn in the database but
                        // remained a priced runner in this snapshot, forcing
                        // an unnecessary fallback to BetsAPI for every horse.
                        var explicitNonRunnerNames = explicitNonRunners
                            .Select(nonRunner => BetfairMarketMatcher.NormalizeRunner(nonRunner.Name))
                            .Where(name => name.Length > 0)
                            .ToHashSet(StringComparer.Ordinal);
                        var explicitNonRunnerTraps = racingEvent.SportId == 4
                            ? explicitNonRunners.Where(x => x.Identifier is > 0)
                                .Select(x => x.Identifier!.Value).ToHashSet()
                            : [];
                        runners = runners.Select(runner =>
                            explicitNonRunnerNames.Contains(BetfairMarketMatcher.NormalizeRunner(runner.Name)) ||
                            explicitNonRunnerTraps.Contains(runner.Identifier) ||
                            (racingEvent.SportId == 4 &&
                             BetfairMarketMatcher.NormalizeRunner(runner.Name).Contains("vacant", StringComparison.Ordinal))
                                ? runner with { HasPrice = false, ExplicitNonRunner = true, PricingPrice = null }
                                : runner).ToList();
                        // BetsAPI supplies the complete pre-show book. Once Betfred has
                        // a fresh price for every active trap, switch the whole book to
                        // Betfred atomically; never mix a partial show with BetsAPI.
                        diagnostics.Stage("selecting BetsAPI/Betfred show prices", racingEvent);
                        runners = await feedRepository.ApplyCompleteBetfredShowBookAsync(
                            racingEvent, runners, stoppingToken);
                        var spreadexMatch = (Runners: (IReadOnlyList<FeedRunner>)runners,
                            Prices: (IReadOnlyList<SpreadexRunnerPrice>)[], IndexPoints: (IReadOnlyList<double>?)null);
                        // 210003 is a runner/selection suspension, not proof that
                        // the race is OFF. Retain its last valid quote for model
                        // continuity, but temporarily deactivate every selection
                        // derived from that runner below. Explicit event-level OFF
                        // signals are handled separately by RacingSuspensionWorker.
                        var spreadexSuspendedRunnerNames = spreadexMatch.Prices
                            .Where(price => price.HundredSuspended)
                            .Select(price => BetfairMarketMatcher.NormalizeRunner(price.Name))
                            .Where(name => name.Length > 0)
                            .ToHashSet(StringComparer.Ordinal);
                        var spreadexSuspendedCompetitorIds = runners
                            .Where(runner => spreadexSuspendedRunnerNames.Contains(
                                BetfairMarketMatcher.NormalizeRunner(runner.Name)))
                            .Select(runner => runner.CompetitorId)
                            .ToHashSet();
                        // Spreadex is intentionally not part of the initial greyhound
                        // source policy.
                        runners = betAhoy.ApplyFallback(racingEvent, runners);
                        var activeRunnerCount = runners.Count(runner =>
                            runner.HasPrice && !runner.ExplicitNonRunner);
                        var pricedRunnerCount = runners.Count(runner =>
                            runner.HasPrice && !runner.ExplicitNonRunner && runner.EffectivePrice > 1);
                        if (activeRunnerCount < 2 || pricedRunnerCount != activeRunnerCount)
                        {
                            var cardRunners = runners.Where(runner => !runner.ExplicitNonRunner).ToList();
                            var suspendedSignature = "suspended-card|" + string.Join('|', cardRunners
                                .OrderBy(runner => runner.Identifier)
                                .Select(runner => $"{runner.CompetitorId}:{runner.Identifier}:{runner.Name}"));
                            if (cardRunners.Count >= 2 &&
                                (racingEvent.ForceRefresh || !_lastRunnerSnapshots.TryGetValue(
                                    racingEvent.EventId, out var previousCard) || previousCard != suspendedSignature))
                            {
                                var suspended = await ApplyWithRetryAsync(racingEvent, cardRunners, null,
                                    new HashSet<long>(), new HashSet<string>(), stoppingToken, true);
                                if (suspended is not null && !terminalGate.IsTerminal(racingEvent.EventId))
                                {
                                    await publisher.PublishAsync(suspended, stoppingToken);
                                    _lastRunnerSnapshots[racingEvent.EventId] = suspendedSignature;
                                    await activity.WriteAsync("suspended-card-created",
                                        $"Created {cardRunners.Count} runners and all configured markets at 1.00 suspended while awaiting prices.",
                                        racingEvent.SportId, racingEvent.EventId, racingEvent.Meeting,
                                        racingEvent.Name, stoppingToken);
                                }
                            }
                            logger.LogDebug(
                                "Skipped pricing event {EventId}: {PricedRunnerCount}/{ActiveRunnerCount} active runners have Spreadex/BetsAPI prices; Bet365 browser odds are metadata-only.",
                                racingEvent.EventId, pricedRunnerCount, activeRunnerCount);
                            await feedRepository.MarkPricePollCompletedAsync(racingEvent.EventId, stoppingToken);
                            continue;
                        }
                        var signature = string.Join('|', runners.OrderBy(x => x.Identifier)
                            .Select(x => $"{x.CompetitorId}:{x.Identifier}:{x.Price:0.####}:{x.EffectivePrice:0.####}:{x.PricingSource}:{x.HasPrice}:{x.ExplicitNonRunner}:{x.Name}"));
                        signature += "|index:" + (spreadexMatch.IndexPoints is { Count: > 0 }
                            ? string.Join('-', spreadexMatch.IndexPoints.Select(value => value.ToString("0.####", CultureInfo.InvariantCulture)))
                            : "default");
                        var spreadexSuspensionSignature = string.Join(',',
                            spreadexSuspendedCompetitorIds.OrderBy(id => id));
                        signature += $"|spreadex-suspended:{spreadexSuspensionSignature}";
                        if (!racingEvent.ForceRefresh && withdrawals.Count == 0 &&
                            _lastRunnerSnapshots.TryGetValue(racingEvent.EventId, out var previous) && previous == signature)
                        {
                            await feedRepository.MarkPricePollCompletedAsync(racingEvent.EventId, stoppingToken);
                            continue;
                        }
                        diagnostics.Stage("calculating and storing markets", racingEvent);
                        var snapshot = await ApplyWithRetryAsync(racingEvent, runners, spreadexMatch.IndexPoints,
                            spreadexSuspendedCompetitorIds, spreadexSuspendedRunnerNames, stoppingToken);
                        if (snapshot is null) continue;
                        if (terminalGate.IsTerminal(racingEvent.EventId) ||
                            await feedRepository.IsEventSuspendedAsync(racingEvent.EventId, stoppingToken))
                            continue;
                        _spreadexSuspendedRunnerSnapshots.TryGetValue(racingEvent.EventId, out var previousSpreadexSuspensions);
                        if (!string.Equals(previousSpreadexSuspensions, spreadexSuspensionSignature, StringComparison.Ordinal))
                        {
                            _spreadexSuspendedRunnerSnapshots[racingEvent.EventId] = spreadexSuspensionSignature;
                            // Do not emit a synthetic "resumed" entry the first
                            // time an ordinary active race is observed.
                            if (spreadexSuspensionSignature.Length > 0 || previousSpreadexSuspensions is not null)
                                await audit.WriteAsync(
                                    spreadexSuspensionSignature.Length > 0
                                        ? "spreadex-runner-suspension-sent"
                                        : "spreadex-runner-resumed",
                                    racingEvent.EventId, racingEvent.SportId, racingEvent.Meeting, racingEvent.Name,
                                    racingEvent.StartTimeUnix, "SPREADEX",
                                    spreadexSuspensionSignature.Length > 0 ? "runner status 210003" : "runner status active",
                                    spreadexSuspensionSignature.Length > 0
                                        ? $"Suspended all derivatives for {spreadexSuspendedCompetitorIds.Count} runner(s); event and unaffected runners remain active."
                                        : "Reactivated provider-suspended derivatives; Rule 4 and non-runner state remain unchanged.",
                                    new { competitorIds = spreadexSuspendedCompetitorIds }, stoppingToken);
                        }
                        diagnostics.Stage("publishing to Sportuno", racingEvent);
                        await publisher.PublishAsync(snapshot, stoppingToken);
                        _lastRunnerSnapshots[racingEvent.EventId] = signature;
                        var selectionCount = snapshot.Markets.Sum(x => x.Selections.Count);
                        var pricingSources = string.Join("/", runners
                            .Where(runner => runner.HasPrice && !runner.ExplicitNonRunner && runner.EffectivePrice > 1)
                            .Select(runner => runner.PricingSource)
                            .Where(source => !string.IsNullOrWhiteSpace(source))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(source => source));
                        await activity.WriteAsync("price",
                            $"Prices published from {(pricingSources.Length > 0 ? pricingSources : "BETSAPI")} ({comparison.Selected.UpdatedAtUtc:HH:mm:ss} UTC): " +
                            $"{selectionCount} selections across {snapshot.Markets.Count} markets.",
                            racingEvent.SportId, racingEvent.EventId, racingEvent.Meeting, racingEvent.Name, stoppingToken);
                        logger.LogInformation("Published {MarketCount} PROD/UAT markets for {Sport} event {EventId}.",
                            snapshot.Markets.Count, racingEvent.SportId == 2 ? "horse" : "greyhound", racingEvent.EventId);
                        diagnostics.EventCompleted(racingEvent, "published");
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Error(ex, racingEvent);
                        logger.LogWarning(ex, "Could not process racing event {EventId}.", racingEvent.EventId);
                    }
                    finally
                    {
                        if (directCandidateIds.Contains(racingEvent.EventId))
                            _pendingDirectEventIds.Remove(racingEvent.EventId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                diagnostics.Error(ex);
                logger.LogError(ex, "Hosted horse/greyhound feed cycle failed.");
            }

            diagnostics.CycleCompleted();
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<GeneratedEventSnapshot?> ApplyWithRetryAsync(
        PricingEvent racingEvent,
        IReadOnlyList<FeedRunner> runners,
        IReadOnlyList<double>? indexPoints,
        IReadOnlySet<long> spreadexSuspendedCompetitorIds,
        IReadOnlySet<string> spreadexSuspendedRunnerNames,
        CancellationToken cancellationToken,
        bool createSuspendedCard = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await feedRepository.ApplyPricesAsync(racingEvent, runners, indexPoints,
                    spreadexSuspendedCompetitorIds, spreadexSuspendedRunnerNames, cancellationToken,
                    createSuspendedCard);
            }
            catch (MySqlException ex) when (attempt < 4 &&
                (ex.Number is 1205 or 1213 or 1317 ||
                 ex.Message.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase) ||
                 ex.Message.Contains("I/O operation has been aborted", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogDebug("Retrying event {EventId} after database lock contention (attempt {Attempt}).", racingEvent.EventId, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken);
            }
        }
    }

    private async Task UpsertEventsWithRetryAsync(IReadOnlyList<FeedEvent> events, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { await feedRepository.UpsertEventsAsync(events, cancellationToken); return; }
            catch (MySqlException ex) when (ex.Number is 1205 or 1213 && attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
            catch (MySqlException ex) when (ex.Number is 1205 or 1213)
            {
                logger.LogWarning(ex,
                    "Event discovery remained locked after {AttemptCount} attempts; continuing with live price processing.",
                    attempt);
                return;
            }
        }
    }

    private static bool IsGenuineHorseRunner(FeedRunner runner) =>
        runner.Identifier > 0 &&
        !string.IsNullOrWhiteSpace(runner.SilkId) &&
        !runner.Name.Contains("Favourite", StringComparison.OrdinalIgnoreCase);

    private FeedPriceComparison ValidateRunnerFingerprints(
        PricingEvent racingEvent,
        IReadOnlyList<string> knownRunnerNames,
        FeedPriceComparison comparison)
    {
        // There is no independent fingerprint before the first Race Winner
        // snapshot. Date/meeting/off-time remain mandatory in the provider
        // matchers and the first valid snapshot establishes the runner set.
        if (knownRunnerNames.Count < 2) return comparison;

        FeedPriceSnapshot? Validate(FeedPriceSnapshot? snapshot)
        {
            if (snapshot is null) return null;
            var fingerprint = RaceRunnerFingerprint.Compare(
                knownRunnerNames,
                snapshot.Runners.Where(runner => racingEvent.SportId != 2 || IsGenuineHorseRunner(runner))
                    .Select(runner => runner.Name));
            var cacheKey = $"{racingEvent.EventId}:{snapshot.Source}";
            if (fingerprint.IsMatch)
            {
                _lastRejectedRunnerFingerprints.Remove(cacheKey);
                return snapshot;
            }

            var signature = $"{fingerprint.ExpectedCount}:{fingerprint.ActualCount}:{fingerprint.MatchedCount}";
            if (!_lastRejectedRunnerFingerprints.TryGetValue(cacheKey, out var previous) || previous != signature)
            {
                _lastRejectedRunnerFingerprints[cacheKey] = signature;
                logger.LogError(
                    "Rejected {Source} snapshot for event {EventId}: runner fingerprint matched {Matched}/{Largest} " +
                    "({Coverage:P0}); {Required} matches required. The snapshot probably belongs to another race.",
                    snapshot.Source,
                    racingEvent.EventId,
                    fingerprint.MatchedCount,
                    Math.Max(fingerprint.ExpectedCount, fingerprint.ActualCount),
                    fingerprint.Coverage,
                    fingerprint.RequiredMatches);
            }
            return null;
        }

        var direct = Validate(comparison.Direct);
        var betsApi = Validate(comparison.BetsApi);
        // Re-run source selection after rejecting mismatched snapshots. BetsAPI wins
        // with a complete book; otherwise a complete Bet365 direct book may drive it.
        var selected = RacingFeedClient.SelectBetsApiPricingOrMetadata(direct, betsApi);
        return new FeedPriceComparison(selected, direct, betsApi);
    }
}
