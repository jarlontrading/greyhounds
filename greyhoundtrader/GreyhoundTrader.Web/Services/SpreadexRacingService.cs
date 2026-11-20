using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Playwright;

namespace RacingTrader.Web.Services;

internal sealed record SpreadexRunnerPrice(
    string Name, double Buy, double Sell, double DecimalPrice, DateTime UpdatedAtUtc,
    double? Index502510Buy = null, double? Index502510Sell = null, DateTime? Index502510UpdatedAtUtc = null,
    string? IndexMarketName = null, bool IndexSuspended = false, bool HundredSuspended = false);

internal sealed record SpreadexRaceCardPrice(
    string Key, string Name, double Bid, double Offer, DateTime UpdatedAtUtc, bool Suspended);

internal sealed record SpreadexSubscriptionStatus(
    int Meetings, int Races, int Models, int SubscribedModels, int PricedRaces,
    long HttpSnapshots, long WebSocketDeltas, DateTime? LastHttpUtc, DateTime? LastDeltaUtc,
    DateTime? LastInboundUtc, DateTime? LastPriceUtc, bool FeedStale, int Recoveries,
    int InitializedModels);

internal sealed record SpreadexMeetingHealth(
    string Meeting, long MeetingDateUnix, string ModelRef, int ExpectedRaces,
    int ParsedRaces, int FixedPricedRaces, int IndexPricedRaces, int ExpectedIndexRaces,
    int SelectionModels, int InitializedSelectionModels, int UsableSelectionModels,
    int StaleSelectionModels, IReadOnlyList<string> AlertRaces,
    DateTime? LastSnapshotUtc, DateTime? LastDeltaUtc, string State);

internal sealed record SpreadexMatchDiagnostics(
    long EventId, bool Complete, int EligibleRunners, int MatchedRunners,
    IReadOnlyList<string> UnmatchedRunners, DateTime CheckedAtUtc);

internal sealed class SpreadexRacingService(
    RacingFeedRepository feedRepository,
    RacingDiagnosticCapture diagnostics,
    RaceAuditTrail audit,
    ILogger<SpreadexRacingService> logger) : BackgroundService
{
    private const string RacingUrl = "https://www.spreadex.com/sports/en-GB/spread-betting/horse-racing";
    private const string ModelUrl = "https://www.spreadex.com/sports/model/api/SubscribeModel?modelRef=";
    private const string MenuModelRef = "m1.s.d.menu.event-type-menu:28";
    private const string InPlayEventsModelRef = "m1.s.d.in-play.now-events:0";
    private const string HorseInPlayGroupModelRef = "m1.s.d.in-play.page.now-group:28";
    private const string AllPanelsPrefix = "m1.s.d.pricing-page.spr.all-panels-lazy:";
    private const string PanelSelectionsPrefix = "m1.s.d.pricing-page.spr.panel-item.selections:";
    private const int ActivePriceStatus = 210002;

    private readonly ConcurrentDictionary<string, SpreadexRace> _races = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, RaceState> _raceStates = new();
    private readonly ConcurrentDictionary<string, ComponentTarget> _componentTargets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HashSet<string>> _componentRefsByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long PageId, MarketKind Kind)> _panelRaceByModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _allPanelsStartUnix = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _modelRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _snapshotBodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedInitialSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _initialSnapshotQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<string> _snapshotQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<long> _persistenceQueue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<long, byte> _queuedPersistence = new();
    private readonly ConcurrentDictionary<string, DateTime> _initializedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _usableSelectionModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _modelSnapshotUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _modelDeltaUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _allPanelMeetings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _allPanelExpectedRaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _allPanelParsedRaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, byte> _auditedInitialisedRacePages = new();
    private readonly ConcurrentDictionary<long, byte> _auditedAppliedEvents = new();
    private readonly HashSet<string> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _subscriptionGate = new();
    private readonly ConcurrentDictionary<string, DateTime> _raceOffSignals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _raceOffSignalReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _bothMarketsSuspendedSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, string> _rejectedRunnerFingerprints = new();
    private readonly ConcurrentDictionary<long, SpreadexMatchDiagnostics> _matchDiagnostics = new();
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private int _invocationId = 1;
    private int _meetingCount;
    private long _httpSnapshots;
    private long _webSocketDeltas;
    private long _lastHttpTicks;
    private long _lastDeltaTicks;
    private long _lastInboundTicks;
    private long _lastPriceTicks;
    private double _usualInboundIntervalSeconds;
    private long _lastRecoveryTicks;
    private int _recoveryBusy;
    private int _recoveryCount;

    public SpreadexSubscriptionStatus Status
    {
        get
        {
            int subscribed;
            lock (_subscriptionGate) subscribed = _subscriptions.Count;
            var lastInbound = TicksToUtc(Interlocked.Read(ref _lastInboundTicks));
            return new(
                Volatile.Read(ref _meetingCount), _raceStates.Count, _modelRefs.Count, subscribed,
                _races.Values.Count(x => x.Prices.Any(p => p.DecimalPrice > 1)),
                Interlocked.Read(ref _httpSnapshots), Interlocked.Read(ref _webSocketDeltas),
                TicksToUtc(Interlocked.Read(ref _lastHttpTicks)), TicksToUtc(Interlocked.Read(ref _lastDeltaTicks)),
                lastInbound, TicksToUtc(Interlocked.Read(ref _lastPriceTicks)),
                IsFeedStale(lastInbound), Volatile.Read(ref _recoveryCount), _initializedModels.Count);
        }
    }

    public IReadOnlyList<SpreadexMeetingHealth> GetMeetingHealth()
    {
        return _allPanelMeetings.Select(pair =>
        {
            var modelRef = pair.Key;
            var meeting = pair.Value;
            var races = _raceStates.Values
                .Where(race => VenueKey(race.Meeting) == VenueKey(meeting))
                .Where(race => _allPanelsStartUnix.TryGetValue(modelRef, out var meetingUnix) &&
                               UkDate(race.StartTimeUnix) == UkDate(meetingUnix))
                .ToList();
            var fixedPriced = 0;
            var indexPriced = 0;
            foreach (var race in races)
            {
                lock (race.Gate)
                {
                    if (race.Runners.Values.Any(runner => runner.HundredBuy > 0)) fixedPriced++;
                    if (race.Runners.Values.Any(runner => runner.IndexBuy > 0)) indexPriced++;
                }
            }
            var selectionModels = _panelRaceByModel
                .Where(owner => races.Any(race => race.PageId == owner.Value.PageId))
                .Select(owner => owner.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var expected = _allPanelExpectedRaces.GetValueOrDefault(modelRef, races.Count);
            var parsed = _allPanelParsedRaces.GetValueOrDefault(modelRef);
            var initializedSelections = selectionModels.Count(model => _initializedModels.ContainsKey(model));
            var usableSelections = selectionModels.Count(model => _usableSelectionModels.ContainsKey(model));
            var staleModels = selectionModels.Where(IsSelectionModelStale).ToList();
            var alertRaces = staleModels
                .Select(model => _panelRaceByModel.TryGetValue(model, out var owner) &&
                                 _raceStates.TryGetValue(owner.PageId, out var race)
                    ? $"{race.Meeting} {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), "GMT Standard Time"):HH:mm}"
                    : model)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value)
                .ToList();
            var expectedIndexRaces = _panelRaceByModel
                .Where(owner => owner.Value.Kind == MarketKind.Index502510 &&
                                races.Any(race => race.PageId == owner.Value.PageId))
                .Select(owner => owner.Value.PageId)
                .Distinct()
                .Count();
            var lastSnapshot = _modelSnapshotUtc.TryGetValue(modelRef, out var snapshotUtc)
                ? snapshotUtc : (DateTime?)null;
            var lastDelta = _modelDeltaUtc.TryGetValue(modelRef, out var deltaUtc)
                ? deltaUtc : (DateTime?)null;
            var state = staleModels.Count > 0 ? $"stale price models ({staleModels.Count})"
                : fixedPriced == expected && indexPriced == expectedIndexRaces && expected > 0 ? "ready"
                : parsed < expected ? "all-races snapshot incomplete"
                : initializedSelections < selectionModels.Count ? "initialising price models"
                : fixedPriced == expected && indexPriced < expectedIndexRaces
                    ? $"missing index prices ({indexPriced}/{expectedIndexRaces})"
                : fixedPriced > 0 ? "partial prices"
                : "no prices";
            return new SpreadexMeetingHealth(
                meeting, _allPanelsStartUnix.GetValueOrDefault(modelRef), modelRef, expected,
                parsed, fixedPriced, indexPriced, expectedIndexRaces, selectionModels.Count,
                initializedSelections, usableSelections,
                staleModels.Count, alertRaces,
                lastSnapshot, lastDelta, state);
        }).OrderBy(health => health.MeetingDateUnix).ThenBy(health => health.Meeting).ToList();
    }

    public (IReadOnlyList<FeedRunner> Runners, IReadOnlyList<SpreadexRunnerPrice> Prices, IReadOnlyList<double>? IndexPoints) Apply(
        PricingEvent ev, IReadOnlyList<FeedRunner> runners)
    {
        if (runners.Where(x => x.CompetitorId > 0).GroupBy(x => x.CompetitorId).Any(x => x.Count() > 1) ||
            runners.GroupBy(x => BetfairMarketMatcher.NormalizeRunner(x.Name)).Any(x => x.Key.Length > 0 && x.Count() > 1))
        {
            _matchDiagnostics[ev.EventId] = new(ev.EventId, false, runners.Count, 0,
                ["Duplicate runner ID or name in source snapshot"], DateTime.UtcNow);
            return (runners, [], null);
        }
        var venue = VenueKey(ev.Meeting);
        var genuineRunners = runners.Where(runner => !IsSyntheticRunner(runner, runners)).ToList();
        var candidates = _races.Values
            .Where(x => Math.Abs(x.StartTimeUnix - ev.StartTimeUnix) <= 180 && VenueKey(x.Meeting) == venue)
            .Select(race => new
            {
                Race = race,
                Fingerprint = RaceRunnerFingerprint.Compare(
                    genuineRunners.Select(runner => runner.Name),
                    race.Prices.Select(price => price.Name))
            })
            .OrderByDescending(match => match.Fingerprint.Coverage)
            .ThenBy(match => Math.Abs(match.Race.StartTimeUnix - ev.StartTimeUnix))
            .ToList();
        var matchedRace = candidates.FirstOrDefault(match => match.Fingerprint.IsMatch);
        if (matchedRace is null)
        {
            var closest = candidates.FirstOrDefault();
            if (closest is not null)
            {
                var fingerprint = closest.Fingerprint;
                var signature =
                    $"{closest.Race.PageId}:{fingerprint.ExpectedCount}:{fingerprint.ActualCount}:{fingerprint.MatchedCount}";
                if (!_rejectedRunnerFingerprints.TryGetValue(ev.EventId, out var previous) || previous != signature)
                {
                    _rejectedRunnerFingerprints[ev.EventId] = signature;
                    logger.LogWarning(
                        "Rejected Spreadex race {PageId} for event {EventId}: runner fingerprint matched {Matched}/{Largest} ({Coverage:P0}).",
                        closest.Race.PageId, ev.EventId, fingerprint.MatchedCount,
                        Math.Max(fingerprint.ExpectedCount, fingerprint.ActualCount), fingerprint.Coverage);
                }
            }
            _matchDiagnostics[ev.EventId] = new(ev.EventId, false,
                genuineRunners.Count(x => x.HasPrice && !x.ExplicitNonRunner), 0,
                ["No race passed the date/time/runner fingerprint"], DateTime.UtcNow);
            return (runners, [], null);
        }
        _rejectedRunnerFingerprints.TryRemove(ev.EventId, out _);
        var race = matchedRace.Race;

        var matched = new List<SpreadexRunnerPrice>();
        var displayPrices = new List<SpreadexRunnerPrice>();
        var eligibleMatchedCount = 0;
        var output = runners.Select(runner =>
        {
            if (IsSyntheticRunner(runner, runners))
                return runner with { HasPrice = false, PricingPrice = null };
            var key = BetfairMarketMatcher.NormalizeRunner(runner.Name);
            var price = race.Prices.FirstOrDefault(x => BetfairMarketMatcher.NormalizeRunner(x.Name) == key);
            if (price is null) return runner;
            displayPrices.Add(price);
            if (price.Buy <= 0 || price.DecimalPrice <= 1) return runner;
            matched.Add(price);
            if (runner.HasPrice && !runner.ExplicitNonRunner) eligibleMatchedCount++;
            return runner with
            {
                PricingPrice = price.DecimalPrice,
                PricingSource = "SPREADEX",
                SourceUpdatedAtUtc = price.UpdatedAtUtc
            };
        }).ToList();
        var eligibleCount = genuineRunners.Count(runner => runner.HasPrice && !runner.ExplicitNonRunner);
        // Spreadex is authoritative per runner. Any runner without a current
        // 100-index quote retains its latest browser/BetsAPI fallback.
        var completeSpreadexBook = eligibleCount >= 2 && eligibleMatchedCount == eligibleCount;
        var unmatched = genuineRunners
            .Where(runner => runner.HasPrice && !runner.ExplicitNonRunner)
            .Where(runner => !race.Prices.Any(price =>
                BetfairMarketMatcher.NormalizeRunner(price.Name) ==
                BetfairMarketMatcher.NormalizeRunner(runner.Name) &&
                price.Buy > 0 && price.DecimalPrice > 1))
            .Select(runner => runner.Name)
            .ToList();
        _matchDiagnostics[ev.EventId] = new(ev.EventId, completeSpreadexBook,
            eligibleCount, eligibleMatchedCount, unmatched, DateTime.UtcNow);
        if (_auditedAppliedEvents.TryAdd(ev.EventId, 0))
            audit.Write("subscription-matched", ev.EventId, ev.SportId, ev.Meeting, ev.Name,
                ev.StartTimeUnix, "SPREADEX", "dated race and runner-fingerprint match",
                "Spreadex race subscription linked to the internal event.",
                new { race.PageId, eligibleCount, eligibleMatchedCount, completeSpreadexBook });
        return (output, displayPrices,
            completeSpreadexBook ? race.IndexPoints : null);
    }

    public SpreadexMatchDiagnostics? GetMatchDiagnostics(long eventId) =>
        _matchDiagnostics.GetValueOrDefault(eventId);

    private static bool IsSyntheticRunner(FeedRunner runner, IReadOnlyList<FeedRunner> runners)
    {
        if (runner.Name.Contains("Favourite", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = runner.Name.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        var names = runners.Where(other => !ReferenceEquals(other, runner))
            .Select(other => BetfairMarketMatcher.NormalizeRunner(other.Name))
            .ToHashSet(StringComparer.Ordinal);
        return parts.All(part => names.Contains(BetfairMarketMatcher.NormalizeRunner(part)));
    }

    public bool TryGetRaceOffSignal(PricingEvent ev, out string reason)
    {
        var spreadexRace = _raceStates.Values
            .Where(x => VenueKey(x.Meeting) == VenueKey(ev.Meeting) &&
                        Math.Abs(x.StartTimeUnix - ev.StartTimeUnix) <= 180)
            .OrderBy(x => Math.Abs(x.StartTimeUnix - ev.StartTimeUnix))
            .FirstOrDefault();
        var key = spreadexRace is null
            ? RaceStateKey(ev.Meeting, ev.StartTimeUnix)
            : RaceStateKey(spreadexRace.Meeting, spreadexRace.StartTimeUnix);
        if (_raceOffSignals.ContainsKey(key))
        {
            reason = _raceOffSignalReasons.GetValueOrDefault(key,
                "Spreadex reported In-Play Now (inplayStatus=N).");
            return true;
        }

        reason = "";
        return false;
    }

    public bool IsRaceOff(PricingEvent ev) => TryGetRaceOffSignal(ev, out _);

    public IReadOnlyList<SpreadexRunnerPrice> GetPrices(string meeting, long startTimeUnix) =>
        _races.Values
            .Where(x => VenueKey(x.Meeting) == VenueKey(meeting) && Math.Abs(x.StartTimeUnix - startTimeUnix) <= 180)
            .OrderBy(x => Math.Abs(x.StartTimeUnix - startTimeUnix))
            .Select(x => x.Prices)
            .FirstOrDefault() ?? [];

    public IReadOnlyList<SpreadexRaceCardPrice> GetRaceCardPrices(string meeting, long startTimeUnix)
    {
        var state = _raceStates.Values
            .Where(x => VenueKey(x.Meeting) == VenueKey(meeting) && Math.Abs(x.StartTimeUnix - startTimeUnix) <= 180)
            .OrderBy(x => Math.Abs(x.StartTimeUnix - startTimeUnix)).FirstOrDefault();
        if (state is null) return [];
        lock (state.Gate) return state.RaceCardMarkets.Values
            .OrderBy(x => x.Order).Select(x => new SpreadexRaceCardPrice(
                x.Key, x.Name, Math.Min(x.Buy, x.Sell), Math.Max(x.Buy, x.Sell),
                x.UpdatedAtUtc, x.Status != ActivePriceStatus)).ToList();
    }

    public IReadOnlyList<FeedNonRunner> GetNonRunners(PricingEvent ev)
    {
        var state = _raceStates.Values
            .Where(x => VenueKey(x.Meeting) == VenueKey(ev.Meeting) &&
                        Math.Abs(x.StartTimeUnix - ev.StartTimeUnix) <= 180)
            .OrderBy(x => Math.Abs(x.StartTimeUnix - ev.StartTimeUnix))
            .FirstOrDefault();
        if (state is null) return [];
        lock (state.Gate)
            return state.NonRunners
                .Select(x => new FeedNonRunner(x.Value, null, x.Key,
                    "Spreadex HorseNonRunnerInfo", $"{x.Key} {x.Value}"))
                .ToList();
    }

    public IReadOnlyList<ProviderRace> GetRaceCatalog() => _raceStates.Values.Select(race =>
    {
        lock (race.Gate)
            return new ProviderRace("SPREADEX", race.PageId.ToString(CultureInfo.InvariantCulture),
                RacingEventNames.CanonicalMeeting(race.Meeting), race.StartTimeUnix,
                race.Runners.Values.Select(x => x.Name).Distinct().ToList(),
                new Dictionary<string, string>
                {
                    ["pricingPageId"] = race.PageId.ToString(CultureInfo.InvariantCulture),
                    ["menuId"] = race.MenuId.ToString(CultureInfo.InvariantCulture)
                });
    }).ToList();

    public string? FindSnapshot(string contains) => _snapshotBodies.Values
        .FirstOrDefault(value => value.Contains(contains, StringComparison.OrdinalIgnoreCase));

    public int RequestFullRefresh()
    {
        lock (_subscriptionGate) _subscriptions.Clear();
        AddModel(MenuModelRef, fetchSnapshot: true);
        AddModel(InPlayEventsModelRef, fetchSnapshot: true);
        AddModel(HorseInPlayGroupModelRef, fetchSnapshot: true);
        foreach (var modelRef in _modelRefs.Keys)
        {
            if (modelRef.StartsWith(AllPanelsPrefix, StringComparison.OrdinalIgnoreCase) ||
                !_initializedModels.ContainsKey(modelRef))
                QueueInitialSnapshot(modelRef);
            else
                QueueSnapshot(modelRef);
        }
        logger.LogInformation("Queued a full Spreadex snapshot refresh and resubscription for {ModelCount} models.",
            _modelRefs.Count);
        return _modelRefs.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never let an authenticated browser launch delay Kestrel binding. A slow
        // navigation/profile recovery is background feed work, not web-host startup.
        await Task.Yield();
        try
        {
            AddModel(MenuModelRef, fetchSnapshot: true);
            AddModel(InPlayEventsModelRef, fetchSnapshot: true);
            AddModel(HorseInPlayGroupModelRef, fetchSnapshot: true);
            await StartBrowserAsync(stoppingToken);
            var persistenceTask = PersistUpdatesAsync(stoppingToken);
            var nextMenuRefresh = DateTime.UtcNow.AddMinutes(1);
            var nextModelReconciliation = DateTime.UtcNow.AddMinutes(1);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= nextMenuRefresh)
                {
                    QueueSnapshot(MenuModelRef);
                    QueueSnapshot(InPlayEventsModelRef);
                    QueueSnapshot(HorseInPlayGroupModelRef);
                    nextMenuRefresh = DateTime.UtcNow.AddMinutes(1);
                }

                if (DateTime.UtcNow >= nextModelReconciliation)
                {
                    ReconcileCurrentModels();
                    nextModelReconciliation = DateTime.UtcNow.AddMinutes(1);
                }

                await RecoverStaleConnectionAsync(stoppingToken);

                if (_initialSnapshotQueue.Reader.TryRead(out var initialModelRef))
                {
                    _queuedInitialSnapshots.TryRemove(initialModelRef, out _);
                    if (!_modelRefs.ContainsKey(initialModelRef)) continue;
                    await FetchAndParseAsync(initialModelRef, stoppingToken);
                    await SubscribeAllAsync();
                    await Task.Delay(500, stoppingToken);
                    continue;
                }

                if (_snapshotQueue.Reader.TryRead(out var modelRef))
                {
                    _queuedSnapshots.TryRemove(modelRef, out _);
                    if (!_modelRefs.ContainsKey(modelRef)) continue;
                    await FetchAndParseAsync(modelRef, stoppingToken);
                    await SubscribeAllAsync();
                    await Task.Delay(500, stoppingToken); // discovery/recovery HTTP is capped at two requests/sec
                    continue;
                }

                await SubscribeAllAsync();
                await Task.Delay(250, stoppingToken);
            }
            await persistenceTask;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Authenticated Spreadex racing subscription service stopped unexpectedly.");
        }
        finally
        {
            if (_context is not null) await _context.DisposeAsync();
            _playwright?.Dispose();
        }
    }

    private async Task StartBrowserAsync(CancellationToken ct)
    {
        _playwright = await Playwright.CreateAsync();
        var profile = @"D:\Temp\racingtrader\spreadex-browser-profile";
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, ".racingtrader-managed"),
            $"RacingTrader/Spreadex{Environment.NewLine}{DateTime.UtcNow:O}", ct);
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(profile, new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "chrome",
            Headless = false,
            ViewportSize = null,
            Args = ["--start-maximized", ManagedBrowserBanner.MarkerArgument("spreadex")]
        });
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        await ManagedBrowserBanner.InstallAsync(_page, "SPREADEX");
        await InstallWebSocketHookAsync(_page);
        ConfigurePage(_page);
        await _page.GotoAsync(RacingUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await ManagedBrowserBanner.ShowAsync(_page, "SPREADEX");
        Interlocked.Exchange(ref _lastInboundTicks, DateTime.UtcNow.Ticks);
        logger.LogInformation("Spreadex racing browser opened. HTTP will discover GB/Irish models; WebSocket subscriptions will carry live prices.");
        await Task.Delay(2000, ct);
    }

    private void ConfigurePage(IPage page)
    {
        page.WebSocket += (_, socket) =>
        {
            if (socket.Url.Contains("zopim", StringComparison.OrdinalIgnoreCase)) return;
            lock (_subscriptionGate) _subscriptions.Clear();
            socket.FrameReceived += (_, frame) =>
            {
                if (!string.IsNullOrWhiteSpace(frame.Text))
                {
                    diagnostics.Write("spreadex-websocket", "frame", new { socket.Url }, frame.Text);
                    RecordInboundMessage();
                    ProcessWebSocketFrame(frame.Text);
                }
            };
            _ = SubscribeAllAsync();
            logger.LogInformation("Spreadex pricing WebSocket connected; resubscribing {Count} racing models.", _modelRefs.Count);
        };
    }

    private void RecordInboundMessage()
    {
        var now = DateTime.UtcNow;
        var previousTicks = Interlocked.Exchange(ref _lastInboundTicks, now.Ticks);
        if (previousTicks <= 0) return;
        var interval = Math.Max(.01, (now - new DateTime(previousTicks, DateTimeKind.Utc)).TotalSeconds);
        var current = Volatile.Read(ref _usualInboundIntervalSeconds);
        Volatile.Write(ref _usualInboundIntervalSeconds, current <= 0 ? interval : current * .9 + interval * .1);
    }

    private bool IsFeedStale(DateTime? lastInbound)
    {
        if (lastInbound is null || _modelRefs.IsEmpty) return false;
        var threshold = Math.Clamp(Volatile.Read(ref _usualInboundIntervalSeconds) * 6, 45, 180);
        return DateTime.UtcNow - lastInbound.Value >= TimeSpan.FromSeconds(threshold);
    }

    private async Task RecoverStaleConnectionAsync(CancellationToken ct)
    {
        var lastInbound = TicksToUtc(Interlocked.Read(ref _lastInboundTicks));
        var lastPrice = TicksToUtc(Interlocked.Read(ref _lastPriceTicks));
        var priceStale = IsWithinExpectedPriceWindow(DateTime.UtcNow) &&
            !_panelRaceByModel.IsEmpty &&
            (lastPrice is null || DateTime.UtcNow - lastPrice.Value >= TimeSpan.FromMinutes(5));
        if ((!IsFeedStale(lastInbound) && !priceStale) || _page is null || _page.IsClosed) return;
        var lastRecovery = TicksToUtc(Interlocked.Read(ref _lastRecoveryTicks));
        if (lastRecovery is not null && DateTime.UtcNow - lastRecovery.Value < TimeSpan.FromMinutes(1)) return;
        if (Interlocked.CompareExchange(ref _recoveryBusy, 1, 0) != 0) return;
        try
        {
            var now = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastRecoveryTicks, now.Ticks);
            Interlocked.Increment(ref _recoveryCount);
            lock (_subscriptionGate) _subscriptions.Clear();
            var recoveryMethod = priceStale && !IsFeedStale(lastInbound)
                ? "price heartbeat watchdog"
                : "WebSocket inbound heartbeat watchdog";
            foreach (var race in _raceStates.Values)
                audit.Write("subscription-loss-detected", null, 2, race.Meeting,
                    $"{race.Meeting} {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), "GMT Standard Time"):HH:mm}",
                    race.StartTimeUnix, "SPREADEX", recoveryMethod,
                    "Spreadex subscription recovery was started for this race.",
                    new { lastInbound, lastPrice, recovery = Volatile.Read(ref _recoveryCount) });
            if (priceStale && !IsFeedStale(lastInbound))
            {
                logger.LogWarning(
                    "Spreadex price watchdog detected no runner-price update since {LastPrice}; refreshing {ModelCount} selection models and resubscribing.",
                    lastPrice, _panelRaceByModel.Count);
                foreach (var modelRef in _panelRaceByModel.Keys) QueueSnapshot(modelRef);
            }
            else
            {
                logger.LogWarning("Spreadex feed watchdog detected no inbound socket traffic since {LastInbound}; reloading and resubscribing.",
                    lastInbound);
                await _page.ReloadAsync(new PageReloadOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });
                await ManagedBrowserBanner.ShowAsync(_page, "SPREADEX");
                Interlocked.Exchange(ref _lastInboundTicks, DateTime.UtcNow.Ticks);
                QueueSnapshot(MenuModelRef);
            }
            await SubscribeAllAsync();
        }
        catch (PlaywrightException ex)
        {
            logger.LogWarning(ex, "Spreadex feed watchdog could not reload the browser page.");
        }
        finally
        {
            Volatile.Write(ref _recoveryBusy, 0);
        }
    }

    private static Task InstallWebSocketHookAsync(IPage page) => page.AddInitScriptAsync(@"
        (() => {
          const NativeWebSocket = window.WebSocket;
          window.__spreadexSockets = [];
          window.WebSocket = function(url, protocols) {
            const ws = protocols ? new NativeWebSocket(url, protocols) : new NativeWebSocket(url);
            window.__spreadexSockets.push(ws);
            return ws;
          };
          window.WebSocket.prototype = NativeWebSocket.prototype;
          window.WebSocket.CONNECTING = NativeWebSocket.CONNECTING;
          window.WebSocket.OPEN = NativeWebSocket.OPEN;
          window.WebSocket.CLOSING = NativeWebSocket.CLOSING;
          window.WebSocket.CLOSED = NativeWebSocket.CLOSED;
          window.__sendSpreadexRaw = msg => {
            let sent = false;
            for (const ws of window.__spreadexSockets) {
              try { if (ws.readyState === 1 && !String(ws.url).includes('zopim')) { ws.send(msg + String.fromCharCode(30)); sent = true; } } catch (_) {}
            }
            return sent;
          };
        })();");

    private async Task SubscribeAllAsync()
    {
        if (_page is null || _page.IsClosed) return;
        foreach (var modelRef in _modelRefs.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            lock (_subscriptionGate) { if (_subscriptions.Contains(modelRef)) continue; }
            var message = new JsonObject
            {
                ["arguments"] = new JsonArray(modelRef),
                ["invocationId"] = Interlocked.Increment(ref _invocationId).ToString(CultureInfo.InvariantCulture),
                ["target"] = "subscribeToUpdates",
                ["type"] = 1
            }.ToJsonString();
            try
            {
                var sent = await _page.EvaluateAsync<bool>("m => window.__sendSpreadexRaw ? window.__sendSpreadexRaw(m) : false", message);
                if (sent) lock (_subscriptionGate) _subscriptions.Add(modelRef);
            }
            catch (PlaywrightException) { return; }
        }
    }

    private void AddModel(string modelRef, bool fetchSnapshot)
    {
        if (string.IsNullOrWhiteSpace(modelRef)) return;
        var added = _modelRefs.TryAdd(modelRef, 0);
        if (fetchSnapshot && (added || !_initializedModels.ContainsKey(modelRef))) QueueInitialSnapshot(modelRef);
    }

    private void QueueInitialSnapshot(string modelRef)
    {
        if (_queuedInitialSnapshots.TryAdd(modelRef, 0)) _initialSnapshotQueue.Writer.TryWrite(modelRef);
    }

    private void QueueSnapshot(string modelRef)
    {
        if (!_initializedModels.ContainsKey(modelRef))
        {
            QueueInitialSnapshot(modelRef);
            return;
        }
        if (_queuedSnapshots.TryAdd(modelRef, 0)) _snapshotQueue.Writer.TryWrite(modelRef);
    }

    private async Task FetchAndParseAsync(string modelRef, CancellationToken ct)
    {
        var result = await FetchSnapshotInBrowserAsync(modelRef);
        ct.ThrowIfCancellationRequested();
        if (!result.Ok)
        {
            logger.LogWarning("Spreadex authenticated snapshot {ModelRef} failed ({Status}).", modelRef, result.Status);
            return;
        }

        Interlocked.Increment(ref _httpSnapshots);
        Interlocked.Exchange(ref _lastHttpTicks, DateTime.UtcNow.Ticks);
        _modelSnapshotUtc[modelRef] = DateTime.UtcNow;
        _snapshotBodies[modelRef] = result.Text;
        diagnostics.Write("spreadex-http", "model-snapshot", new { modelRef }, result.Text);
        ParseSnapshot(modelRef, result.Text);
        _initializedModels[modelRef] = DateTime.UtcNow;
    }

    private async Task<(bool Ok, string Status, string Text)> FetchSnapshotInBrowserAsync(string modelRef)
    {
        if (_page is null || _page.IsClosed) return (false, "browser closed", "");
        try
        {
            var url = ModelUrl + Uri.EscapeDataString(modelRef) + "&_rt=" +
                      DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            var json = await _page.EvaluateAsync<string>(@"async url => {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), 10000);
                try {
                  const response = await fetch(url, { credentials: 'include', cache: 'no-store', signal: controller.signal });
                  return JSON.stringify({ ok: response.ok, status: response.status, text: await response.text() });
                } catch (e) { return JSON.stringify({ ok: false, status: String(e), text: '' }); }
                finally { clearTimeout(timer); }
            }", url);
            var node = JsonNode.Parse(json);
            return (node?["ok"]?.GetValue<bool>() == true, Text(node?["status"]), Text(node?["text"]));
        }
        catch (Exception ex) { return (false, ex.GetType().Name, ""); }
    }

    private void ParseSnapshot(string requestedModelRef, string jsonText)
    {
        try
        {
            var document = JsonNode.Parse(jsonText);
            var model = document?["model"] as JsonObject ?? document as JsonObject;
            if (model is null) return;
            var modelRef = Text(model["modelRef"]?["mref"]);
            if (modelRef.Length == 0) modelRef = requestedModelRef;

            RemoveComponentTargets(modelRef);
            if (modelRef.Equals(MenuModelRef, StringComparison.OrdinalIgnoreCase)) ParseMenu(modelRef, model);
            else if (IsInPlayModel(modelRef)) ParseInPlaySnapshot(modelRef, model);
            else if (modelRef.StartsWith(AllPanelsPrefix, StringComparison.OrdinalIgnoreCase)) ParseMeetingAllPanels(modelRef, model);
            else if (modelRef.StartsWith(PanelSelectionsPrefix, StringComparison.OrdinalIgnoreCase)) ParseSelectionPanel(modelRef, model);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not parse Spreadex racing snapshot {ModelRef}.", requestedModelRef);
        }
    }

    private void ParseMenu(string modelRef, JsonObject model)
    {
        var currentOffKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPageIds = new HashSet<long>();
        var currentAllPanelModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var meetings = 0;
        if (model["items"] is not JsonArray meetingItems) return;

        foreach (var meetingNode in meetingItems.OfType<JsonObject>())
        {
            if (meetingNode["items"] is not JsonArray children) continue;
            var gbIrishChildren = children.OfType<JsonObject>().Where(x => Long(x["racingRegionId"]) == 1).ToList();
            if (gbIrishChildren.Count == 0) continue;
            var all = gbIrishChildren.FirstOrDefault(x => Text(x["name"]).Equals("All", StringComparison.OrdinalIgnoreCase));
            if (all is null || Long(all["pricingPageId"]) <= 0) continue;

            var meeting = RacingEventNames.CanonicalMeeting(Text(meetingNode["name"]));
            if (meeting.Length == 0 || meeting.Equals("Antepost Betting", StringComparison.OrdinalIgnoreCase) ||
                meeting.Equals("Aggregate Markets", StringComparison.OrdinalIgnoreCase)) continue;
            meetings++;
            var allPanelsModel = AllPanelsPrefix + Long(all["pricingPageId"]);
            _allPanelsStartUnix[allPanelsModel] = Long(all["startTimeMs"]) / 1000;
            _allPanelMeetings[allPanelsModel] = meeting;
            currentAllPanelModels.Add(allPanelsModel);
            AddModel(allPanelsModel, fetchSnapshot: true);
            var expectedRaces = 0;
            foreach (var child in gbIrishChildren)
            {
                var match = Regex.Match(Text(child["name"]), @"^\[(\d{1,2}):(\d{2})\]$");
                var pageId = Long(child["pricingPageId"]);
                var startMs = Long(child["startTimeMs"]);
                if (!match.Success || pageId <= 0 || startMs <= 0) continue;
                expectedRaces++;
                currentPageIds.Add(pageId);
                var startUnix = startMs / 1000;
                var state = _raceStates.GetOrAdd(pageId, _ => new RaceState(pageId, Long(child["menuId"]), meeting, startUnix));
                state.Meeting = meeting;
                state.StartTimeUnix = startUnix;
                state.MenuId = Long(child["menuId"]);
                UpdateNonRunners(state, child["contentDetail"]);
                RegisterComponent(modelRef, Text(child["componentRef"]), new ComponentTarget(pageId, "", MarketKind.Menu, false));

                var raceKey = RaceStateKey(meeting, startUnix);
                // Event lifecycle flag shown by Spreadex as "In-Play Now".
                // S means "In-Play Soon" and must not suspend the race.
                var inPlayStatus = Text(child["inplayStatus"]);
                if (inPlayStatus.Length == 0) inPlayStatus = Text(child["inPlayStatus"]);
                if (inPlayStatus.Equals("N", StringComparison.OrdinalIgnoreCase))
                {
                    currentOffKeys.Add(raceKey);
                    _raceOffSignals[raceKey] = DateTime.UtcNow;
                    _raceOffSignalReasons[raceKey] = "Spreadex reported In-Play Now (inplayStatus=N).";
                }
                if (Long(child["fixedOddsStatus"]) == 2 && Long(child["spreadsStatus"]) == 2)
                    _bothMarketsSuspendedSince.TryAdd(raceKey, DateTime.UtcNow);
                else
                    _bothMarketsSuspendedSince.TryRemove(raceKey, out _);
                PublishRace(state);
            }
            _allPanelExpectedRaces[allPanelsModel] = expectedRaces;
        }

        // Price availability codes are deliberately ignored here because
        // Spreadex can suspend and reopen those markets in-play.
        // An N is a one-way lifecycle transition. A later menu snapshot can
        // omit the field entirely, so never erase a confirmed off signal.
        if (meetings > 0 && currentPageIds.Count > 0)
            PruneStaleRacingModels(currentAllPanelModels, currentPageIds);
        Volatile.Write(ref _meetingCount, meetings);
        logger.LogInformation("Spreadex menu discovered {Meetings} GB/Irish meetings and {Races} races.", meetings, _raceStates.Count);
    }

    private void PruneStaleRacingModels(IReadOnlySet<string> currentAllPanelModels, IReadOnlySet<long> currentPageIds)
    {
        var stalePageIds = _raceStates.Keys.Where(pageId => !currentPageIds.Contains(pageId)).ToHashSet();
        foreach (var pageId in stalePageIds)
        {
            if (!_raceStates.TryRemove(pageId, out var race)) continue;
            audit.Write("subscription-removed", null, 2, race.Meeting,
                $"{race.Meeting} {TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix), "GMT Standard Time"):HH:mm}",
                race.StartTimeUnix, "SPREADEX", "coupon reconciliation",
                "Race model was removed because it is no longer present in the current authenticated coupon.",
                new { race.PageId });
            _races.TryRemove(RaceStateKey(race.Meeting, race.StartTimeUnix), out _);
            _queuedPersistence.TryRemove(pageId, out _);
        }

        var staleModels = _allPanelMeetings.Keys
            .Where(modelRef => !currentAllPanelModels.Contains(modelRef))
            .Concat(_panelRaceByModel.Where(owner => stalePageIds.Contains(owner.Value.PageId)).Select(owner => owner.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var staleModel in staleModels) RemoveModel(staleModel);

        logger.LogInformation(
            "Spreadex coupon reconciliation retained {Meetings} meeting models and {Races} current races; removed {StaleRaces} stale races and {StaleModels} stale models.",
            currentAllPanelModels.Count, currentPageIds.Count, stalePageIds.Count, staleModels.Count);
    }

    private void RemoveModel(string modelRef)
    {
        if (modelRef.Equals(MenuModelRef, StringComparison.OrdinalIgnoreCase) || IsInPlayModel(modelRef)) return;
        _modelRefs.TryRemove(modelRef, out _);
        _initializedModels.TryRemove(modelRef, out _);
        _usableSelectionModels.TryRemove(modelRef, out _);
        _modelSnapshotUtc.TryRemove(modelRef, out _);
        _modelDeltaUtc.TryRemove(modelRef, out _);
        _snapshotBodies.TryRemove(modelRef, out _);
        _queuedSnapshots.TryRemove(modelRef, out _);
        _queuedInitialSnapshots.TryRemove(modelRef, out _);
        _panelRaceByModel.TryRemove(modelRef, out _);
        _allPanelsStartUnix.TryRemove(modelRef, out _);
        _allPanelMeetings.TryRemove(modelRef, out _);
        _allPanelExpectedRaces.TryRemove(modelRef, out _);
        _allPanelParsedRaces.TryRemove(modelRef, out _);
        RemoveComponentTargets(modelRef);
        lock (_subscriptionGate) _subscriptions.Remove(modelRef);
    }

    private void ReconcileCurrentModels()
    {
        foreach (var allPanelsModel in _allPanelMeetings.Keys)
        {
            var expected = _allPanelExpectedRaces.GetValueOrDefault(allPanelsModel);
            var parsed = _allPanelParsedRaces.GetValueOrDefault(allPanelsModel);
            if (!_initializedModels.ContainsKey(allPanelsModel) || parsed < expected)
                QueueInitialSnapshot(allPanelsModel);
        }

        foreach (var selectionModel in _panelRaceByModel.Keys)
            if (!_initializedModels.ContainsKey(selectionModel) || !_usableSelectionModels.ContainsKey(selectionModel) ||
                IsSelectionModelStale(selectionModel))
                QueueInitialSnapshot(selectionModel);
    }

    private bool IsSelectionModelStale(string modelRef)
    {
        if (!_panelRaceByModel.TryGetValue(modelRef, out var owner) ||
            !_raceStates.TryGetValue(owner.PageId, out var race)) return true;
        var now = DateTime.UtcNow;
        var start = DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix).UtcDateTime;
        if (start - now > TimeSpan.FromMinutes(60) || now - start > TimeSpan.FromMinutes(10)) return false;
        // A delta proves only that one component moved. A recent, uncached full
        // snapshot is required to prove every runner in the market is current.
        var snapshot = _modelSnapshotUtc.GetValueOrDefault(modelRef);
        return snapshot == default || now - snapshot > TimeSpan.FromMinutes(2);
    }

    private static void UpdateNonRunners(RaceState state, JsonNode? contentDetail)
    {
        if (contentDetail?["contentItems"] is not JsonArray items) return;
        var values = items.OfType<JsonObject>()
            .Where(item => Text(item["name"]).Equals("HorseNonRunnerInfo", StringComparison.OrdinalIgnoreCase))
            .Select(item => Text(item["value"]))
            .Where(value => value.Length > 0)
            .ToList();
        if (values.Count == 0) return;

        var parsed = new Dictionary<int, string>();
        foreach (var value in values)
        foreach (Match match in Regex.Matches(value, @"(?:^|[,;])\s*(\d+)\s+([^,;]+)"))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var cloth))
                parsed[cloth] = match.Groups[2].Value.Split('|', 2)[0].Trim();
        }

        lock (state.Gate)
        {
            state.NonRunners.Clear();
            foreach (var item in parsed) state.NonRunners[item.Key] = item.Value;
        }
    }

    private void ParseMeetingAllPanels(string modelRef, JsonObject model)
    {
        if (model["panels"] is not JsonArray panels) return;
        var parsedPageIds = new HashSet<long>();
        foreach (var panel in panels.OfType<JsonObject>())
        {
            var label = Text(panel["name"]);
            var match = Regex.Match(label, @"^\[(\d{1,2}):(\d{2})\]\s+(.+?)(?:\s+Card\s+-\s+Racecard Markets)?\s*$",
                RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var meeting = match.Groups[3].Value.Trim();
            _allPanelsStartUnix.TryGetValue(modelRef, out var meetingStartUnix);
            var race = FindRace(meeting, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), meetingStartUnix);
            if (race is null) continue;
            parsedPageIds.Add(race.PageId);
            if (_auditedInitialisedRacePages.TryAdd(race.PageId, 0))
                audit.Write("subscription-initialised", null, 2, race.Meeting, label,
                    race.StartTimeUnix, "SPREADEX", "authenticated All Races model snapshot",
                    "Race panel was matched by exact UK date, meeting and scheduled time.",
                    new { race.PageId, modelRef });

            RegisterComponent(modelRef, Text(panel["componentRef"]), new ComponentTarget(race.PageId, "", MarketKind.Standard, false));
            var panelSelections = panel["selectionsContainer"]?["panelSelections"] as JsonArray;
            if (panelSelections is not null)
                ReplaceMarketSnapshot(modelRef, race, panelSelections, MarketKind.Standard, 38);

            if (panel["panelItems"] is not JsonArray panelItems) continue;
            foreach (var item in panelItems.OfType<JsonObject>())
            {
                var itemName = Text(item["name"]);
                UpdateNonRunners(race, item["contentDetail"]);
                // Match the complete three- or four-place settlement ladder.
                // Do not take a three-number suffix from a different market
                // such as 100-60-30-20-10-5 (previously read as 20-10-5).
                var settlementMatch = Regex.Match(itemName,
                    @"(?<![\d.-])(?<points>\d+(?:\.\d+)?(?:-\d+(?:\.\d+)?){2,3})$");
                IReadOnlyList<double>? settlementPoints = null;
                if (settlementMatch.Success)
                    race.IndexPoints = settlementPoints = settlementMatch.Groups["points"].Value
                        .Split('-')
                        .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
                        .ToArray();
                var panelItemId = Long(item["id"]);
                if (panelItemId <= 0) continue;
                if (itemName.Contains("Racecard Market", StringComparison.OrdinalIgnoreCase))
                {
                    var raceCardModel = PanelSelectionsPrefix + panelItemId;
                    _panelRaceByModel[raceCardModel] = (race.PageId, MarketKind.RaceCard);
                    AddModel(raceCardModel, fetchSnapshot: true);
                    continue;
                }
                var winIndexMatch = Regex.Match(itemName, @"(?<total>\d+(?:\.\d+)?)\s+Win Index", RegexOptions.IgnoreCase);
                var isIndex = settlementPoints is { Count: 3 or 4 };
                if (!winIndexMatch.Success && !isIndex) continue;
                if (isIndex) race.IndexMarketName = settlementPoints!.Count == 4
                    ? "50-30-20-10 Index" : "50-25-10 Index";
                if (winIndexMatch.Success && double.TryParse(winIndexMatch.Groups["total"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var declaredTotal))
                    race.WinIndexTotal = declaredTotal;
                var panelModel = PanelSelectionsPrefix + panelItemId;
                _panelRaceByModel[panelModel] = (race.PageId, isIndex ? MarketKind.Index502510 : MarketKind.Hundred);
                AddModel(panelModel, fetchSnapshot: true);
            }

            // The authenticated All Races payload carries bootstrap runner rows for both
            // the 100 Win Index and the place index. Once a dedicated lazy panel has
            // produced usable selections it is authoritative: the bundled rows can lag by
            // hours and must not overwrite a newer panel snapshot/delta.
            if (panelSelections is not null)
            {
                if (!HasUsableSelectionModel(race.PageId, MarketKind.Hundred) &&
                    HasMarketSelections(panelSelections, MarketKind.Hundred, race))
                    ReplaceMarketSnapshot(modelRef, race, panelSelections, MarketKind.Hundred, 3086);
                if (!HasUsableSelectionModel(race.PageId, MarketKind.Index502510) &&
                    HasMarketSelections(panelSelections, MarketKind.Index502510, race))
                    ReplaceMarketSnapshot(modelRef, race, panelSelections, MarketKind.Index502510, 0);
            }
        }
        _allPanelParsedRaces[modelRef] = parsedPageIds.Count;
    }

    private bool HasUsableSelectionModel(long pageId, MarketKind kind) =>
        _panelRaceByModel.Any(pair => pair.Value.PageId == pageId && pair.Value.Kind == kind &&
                                      _usableSelectionModels.ContainsKey(pair.Key));

    private static bool HasMarketSelections(JsonArray selections, MarketKind kind, RaceState race)
    {
        return selections.OfType<JsonObject>().Any(row =>
        {
            var selection = row["selection"] as JsonObject;
            if (selection is null) return false;
            return kind == MarketKind.Index502510
                ? Text(row["selectionType"]?["name"]).Contains(
                    race.IndexMarketName?.Replace(" Index", "") ?? "50-25-10",
                    StringComparison.OrdinalIgnoreCase)
                : kind == MarketKind.Hundred && Long(selection["selectionTypeId"]) == 3086;
        });
    }

    private void ParseSelectionPanel(string modelRef, JsonObject model)
    {
        if (!_panelRaceByModel.TryGetValue(modelRef, out var owner) || !_raceStates.TryGetValue(owner.PageId, out var race))
        {
            logger.LogWarning("Ignored unowned Spreadex index model {ModelRef}; it was not discovered from a meeting All payload.", modelRef);
            return;
        }
        if (model["selections"] is JsonArray raceCardSelections && owner.Kind == MarketKind.RaceCard)
        {
            if (ReplaceRaceCardSnapshot(modelRef, race, raceCardSelections) > 0)
                _usableSelectionModels[modelRef] = DateTime.UtcNow;
            else _usableSelectionModels.TryRemove(modelRef, out _);
        }
        else if (model["selections"] is JsonArray selections &&
                 ReplaceMarketSnapshot(modelRef, race, selections, owner.Kind,
                     owner.Kind == MarketKind.Hundred ? 3086 : 0) > 0)
            _usableSelectionModels[modelRef] = DateTime.UtcNow;
        else
            _usableSelectionModels.TryRemove(modelRef, out _);
    }

    private int ReplaceRaceCardSnapshot(string modelRef, RaceState race, JsonArray selections)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (race.Gate)
        {
            foreach (var row in selections.OfType<JsonObject>())
            {
                if (row["selection"] is not JsonObject selection) continue;
                var typeId = Long(selection["selectionTypeId"]);
                var key = RaceCardKey(typeId);
                if (key is null) continue;
                var name = Text(row["selectionType"]?["name"]);
                if (name.Length == 0) name = Text(selection["name"]);
                var price = selection["price"] as JsonObject;
                var market = race.RaceCardMarkets.GetValueOrDefault(key) ?? new RaceCardState(key, name, RaceCardOrder(typeId));
                market.Name = name;
                market.Buy = Double(price?["buy"]); market.Sell = Double(price?["sell"]);
                market.Status = (int)Long(price?["status"]); market.UpdatedAtUtc = DateTime.UtcNow;
                race.RaceCardMarkets[key] = market; seen.Add(key);
                RegisterComponent(modelRef, Text(price?["componentRef"]), new ComponentTarget(race.PageId, key, MarketKind.RaceCard, true));
                RegisterComponent(modelRef, Text(selection["componentRef"]), new ComponentTarget(race.PageId, key, MarketKind.RaceCard, false));
            }
        }
        if (seen.Count > 0) Interlocked.Exchange(ref _lastPriceTicks, DateTime.UtcNow.Ticks);
        return seen.Count;
    }

    private int ReplaceMarketSnapshot(string modelRef, RaceState race, JsonArray selections, MarketKind kind, long selectionTypeId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (race.Gate)
        {
            foreach (var row in selections.OfType<JsonObject>())
            {
                var selection = row["selection"] as JsonObject;
                if (selection is null) continue;
                var correctType = kind == MarketKind.Index502510
                    ? Text(row["selectionType"]?["name"]).Contains(race.IndexMarketName?.Replace(" Index", "") ?? "50-25-10", StringComparison.OrdinalIgnoreCase)
                    : Long(selection["selectionTypeId"]) == selectionTypeId;
                if (!correctType) continue;
                var name = Text(selection["name"]);
                var runnerKey = BetfairMarketMatcher.NormalizeRunner(name);
                if (runnerKey.Length == 0) continue;
                seen.Add(runnerKey);
                if (!race.Runners.TryGetValue(runnerKey, out var runner))
                    race.Runners[runnerKey] = runner = new RunnerState(name);
                runner.Name = name;
                var price = selection["price"] as JsonObject;
                SetMarket(runner, kind, Double(price?["buy"]), Double(price?["sell"]), (int)Long(price?["status"]), DateTime.UtcNow);
                RegisterComponent(modelRef, Text(price?["componentRef"]), new ComponentTarget(race.PageId, runnerKey, kind, true));
                RegisterComponent(modelRef, Text(selection["componentRef"]), new ComponentTarget(race.PageId, runnerKey, kind, false));
            }

            // A lazy Spreadex model can return an initialised but empty body before its
            // selections are hydrated. Never let that transient response erase the good
            // half of a race-level price bundle.
            if (seen.Count == 0 && kind is MarketKind.Hundred or MarketKind.Index502510)
                return 0;

            foreach (var pair in race.Runners.Where(x => !seen.Contains(x.Key)))
                SetMarket(pair.Value, kind, 0, 0, 210005, DateTime.UtcNow);
        }
        if (seen.Count > 0 && kind is MarketKind.Standard or MarketKind.Hundred or MarketKind.Index502510)
            Interlocked.Exchange(ref _lastPriceTicks, DateTime.UtcNow.Ticks);
        PublishRace(race);
        return seen.Count;
    }

    private void ProcessWebSocketFrame(string rawText)
    {
        foreach (var part in rawText.Split((char)0x1e, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var root = JsonNode.Parse(part);
                if (!Text(root?["target"]).Equals("u", StringComparison.OrdinalIgnoreCase) || root?["arguments"] is not JsonArray args) continue;
                foreach (var argument in args.OfType<JsonObject>())
                {
                    var modelRef = Text(argument["m"]);
                    if (!_modelRefs.ContainsKey(modelRef) || argument["u"] is not JsonArray updates) continue;
                    foreach (var update in updates.OfType<JsonObject>()) ApplyDelta(modelRef, update);
                }
            }
            catch (Exception ex) { logger.LogDebug(ex, "Ignored malformed Spreadex WebSocket frame."); }
        }
    }

    private void ApplyDelta(string modelRef, JsonObject update)
    {
        Interlocked.Increment(ref _webSocketDeltas);
        Interlocked.Exchange(ref _lastDeltaTicks, DateTime.UtcNow.Ticks);
        _modelDeltaUtc[modelRef] = DateTime.UtcNow;
        if (IsInPlayModel(modelRef))
        {
            ParseInPlayNodes(modelRef, update);
            var liveComponentRef = Text(update["_c"]);
            if (liveComponentRef.Length > 0 &&
                _componentTargets.TryGetValue(ComponentKey(modelRef, liveComponentRef), out var liveTarget) &&
                liveTarget.Kind == MarketKind.InPlay && _raceStates.TryGetValue(liveTarget.PageId, out var liveRace))
                MarkRaceInPlay(liveRace, "Spreadex browser moved the race into its live In-Play hierarchy.");
            if (IsStructural(update) || Has(update, "timeGone") || Has(update, "eventName") ||
                Has(update, "pricingPageId")) QueueSnapshot(modelRef);
            return;
        }
        if (modelRef.Equals(MenuModelRef, StringComparison.OrdinalIgnoreCase))
        {
            ApplyMenuInPlaySignals(modelRef, update);
            var menuComponent = Text(update["_c"]);
            if (menuComponent.Length > 0 && (Has(update, "inplayStatus") || Has(update, "inPlayStatus")))
            {
                var inPlayStatus = Text(update["inplayStatus"]);
                if (inPlayStatus.Length == 0) inPlayStatus = Text(update["inPlayStatus"]);
                if (inPlayStatus.Equals("N", StringComparison.OrdinalIgnoreCase))
                {
                    RaceState? menuRace = null;
                    if (_componentTargets.TryGetValue(ComponentKey(modelRef, menuComponent), out var menuTarget) &&
                        menuTarget.Kind == MarketKind.Menu)
                        _raceStates.TryGetValue(menuTarget.PageId, out menuRace);
                    if (menuRace is not null)
                        MarkRaceInPlay(menuRace,
                            "Spreadex race menu reported In-Play Now (inplayStatus=N).");
                }
            }
            QueueSnapshot(modelRef);
            return;
        }

        var componentRef = Text(update["_c"]);
        if (componentRef.Length == 0 || !_componentTargets.TryGetValue(ComponentKey(modelRef, componentRef), out var target) ||
            !_raceStates.TryGetValue(target.PageId, out var race))
        {
            QueueSnapshot(modelRef);
            return;
        }
        if (target.Kind == MarketKind.Menu)
        {
            QueueSnapshot(MenuModelRef);
            return;
        }
        if (target.Kind == MarketKind.RaceCard)
        {
            lock (race.Gate)
            {
                if (!race.RaceCardMarkets.TryGetValue(target.RunnerKey, out var market)) { QueueSnapshot(modelRef); return; }
                var raceCardPrice = target.IsPrice ? update : update["price"] as JsonObject;
                if (raceCardPrice is null) { if (IsStructural(update)) QueueSnapshot(modelRef); return; }
                if (Has(raceCardPrice, "buy")) market.Buy = Double(raceCardPrice["buy"]);
                if (Has(raceCardPrice, "sell")) market.Sell = Double(raceCardPrice["sell"]);
                if (Has(raceCardPrice, "status")) market.Status = (int)Long(raceCardPrice["status"]);
                market.UpdatedAtUtc = DateTime.UtcNow;
            }
            Interlocked.Exchange(ref _lastPriceTicks, DateTime.UtcNow.Ticks);
            if (IsStructural(update)) QueueSnapshot(modelRef);
            return;
        }
        if (target.RunnerKey.Length == 0 || !race.Runners.TryGetValue(target.RunnerKey, out var runner))
        {
            QueueSnapshot(modelRef);
            return;
        }

        var changed = false;
        lock (race.Gate)
        {
            if (target.IsPrice)
            {
                changed = ApplyPriceDelta(runner, target.Kind, update);
            }
            else
            {
                if (Has(update, "name") && Text(update["name"]).Length > 0 && !Text(update["name"]).Equals(runner.Name, StringComparison.Ordinal))
                {
                    runner.Name = Text(update["name"]);
                    changed = true;
                }
                // Spreadex sometimes publishes a runner's changed quote on the
                // selection component rather than the registered price component.
                // Those messages carry the live buy/sell/status inside `price`.
                if (update["price"] is JsonObject nestedPrice)
                    changed = ApplyPriceDelta(runner, target.Kind, nestedPrice) || changed;
                var statusNode = update["selectionStatus"] ?? update["displayStatus"] ?? update["priceStatus"];
                if (statusNode is not null)
                {
                    var status = (int)Long(statusNode);
                    if (status != GetStatus(runner, target.Kind))
                    {
                        SetMarket(runner, target.Kind, GetBuy(runner, target.Kind), GetSell(runner, target.Kind), status, DateTime.UtcNow);
                        changed = true;
                    }
                }
            }
        }
        if (changed) PublishRace(race);
        if (IsStructural(update)) QueueSnapshot(modelRef);
    }

    private void ParseInPlaySnapshot(string modelRef, JsonObject model) => ParseInPlayNodes(modelRef, model);

    private void ParseInPlayNodes(string modelRef, JsonNode node)
    {
        if (node is JsonObject item)
        {
            if (Long(item["eventTypeId"]) == 28)
            {
                var pageId = Long(item["pricingPageId"]);
                var race = pageId > 0 && _raceStates.TryGetValue(pageId, out var exactRace)
                    ? exactRace
                    : PreviousRaceFromInPlayMeeting(item);
                if (race is not null)
                {
                    RegisterComponent(modelRef, Text(item["componentRef"]),
                        new ComponentTarget(race.PageId, "", MarketKind.InPlay, false));
                    MarkRaceInPlay(race,
                        "Spreadex browser In-Play hierarchy advanced the meeting to its next race.");
                }
            }
            foreach (var child in item.Select(pair => pair.Value).Where(value => value is not null))
                ParseInPlayNodes(modelRef, child!);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(value => value is not null))
                ParseInPlayNodes(modelRef, child!);
        }
    }

    private RaceState? PreviousRaceFromInPlayMeeting(JsonObject item)
    {
        var meeting = Regex.Replace(Text(item["eventName"]), @"\s*\(\d+\)\s*$", "").Trim();
        var nextMatch = Regex.Match(Text(item["timeGone"]), @"\[(\d{1,2}):(\d{2})\]");
        if (meeting.Length == 0 || !nextMatch.Success) return null;
        var nextMinutes = int.Parse(nextMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                          int.Parse(nextMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        var today = UkDate(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var races = _raceStates.Values
            .Where(race => VenueKey(race.Meeting) == VenueKey(meeting))
            .Where(race => UkDate(race.StartTimeUnix) == today)
            .OrderBy(race => race.StartTimeUnix)
            .ToList();
        var nextIndex = races.FindIndex(race => Math.Abs(TimeOfDayMinutes(race.StartTimeUnix) - nextMinutes) <= 1);
        return nextIndex > 0 ? races[nextIndex - 1] : null;
    }

    private void MarkRaceInPlay(RaceState race, string reason)
    {
        if (DateTime.UtcNow < DateTimeOffset.FromUnixTimeSeconds(race.StartTimeUnix).UtcDateTime) return;
        var key = RaceStateKey(race.Meeting, race.StartTimeUnix);
        if (!_raceOffSignals.TryAdd(key, DateTime.UtcNow)) return;
        logger.LogInformation("Spreadex In-Play hierarchy reported race off for {Meeting} {StartTimeUnix}.",
            race.Meeting, race.StartTimeUnix);
        _raceOffSignalReasons[key] = reason;
        _ = feedRepository.QueueSpreadexRepriceAsync(race.Meeting, race.StartTimeUnix, CancellationToken.None);
    }

    private void ApplyMenuInPlaySignals(string modelRef, JsonNode node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array.Where(value => value is not null))
                ApplyMenuInPlaySignals(modelRef, child!);
            return;
        }
        if (node is not JsonObject item) return;

        var status = Text(item["inplayStatus"]);
        if (status.Length == 0) status = Text(item["inPlayStatus"]);
        if (status.Equals("N", StringComparison.OrdinalIgnoreCase))
        {
            var componentRef = Text(item["_c"]);
            if (componentRef.Length == 0) componentRef = Text(item["componentRef"]);
            RaceState? race = null;
            if (componentRef.Length > 0 &&
                _componentTargets.TryGetValue(ComponentKey(modelRef, componentRef), out var target) &&
                target.Kind == MarketKind.Menu)
                _raceStates.TryGetValue(target.PageId, out race);

            // Structural menu deltas can contain the complete race item rather
            // than the registered component reference. Only accept an explicit
            // [HH:mm] race row with its own start timestamp. Never infer a race
            // from the meeting-wide All/Daily Markets N flag.
            if (race is null &&
                Regex.IsMatch(Text(item["name"]), @"^\[\d{1,2}:\d{2}\]$") &&
                Long(item["startTimeMs"]) > 0)
            {
                var startUnix = Long(item["startTimeMs"]) / 1000;
                var exactTimeCandidates = _raceStates.Values
                    .Where(candidate => Math.Abs(candidate.StartTimeUnix - startUnix) <= 60)
                    .OrderBy(candidate => Math.Abs(candidate.StartTimeUnix - startUnix))
                    .ToList();
                // Different meetings commonly share off times. An unbound
                // structural delta is only safe when its full timestamp maps
                // to one and only one known race.
                race = exactTimeCandidates.Count == 1 ? exactTimeCandidates[0] : null;
            }
            if (race is not null)
                MarkRaceInPlay(race,
                    "Spreadex race WebSocket reported In-Play Now (inplayStatus=N).");
        }

        foreach (var child in item.Select(pair => pair.Value).Where(value => value is not null))
            ApplyMenuInPlaySignals(modelRef, child!);
    }

    private bool ApplyPriceDelta(RunnerState runner, MarketKind kind, JsonObject price)
    {
        var buy = Has(price, "buy") ? Double(price["buy"]) : GetBuy(runner, kind);
        var sell = Has(price, "sell") ? Double(price["sell"]) : GetSell(runner, kind);
        var status = Has(price, "status") ? (int)Long(price["status"]) : GetStatus(runner, kind);
        var changed = buy != GetBuy(runner, kind) || sell != GetSell(runner, kind) || status != GetStatus(runner, kind);
        if (changed)
        {
            SetMarket(runner, kind, buy, sell, status, DateTime.UtcNow);
            if (kind is MarketKind.Standard or MarketKind.Hundred or MarketKind.Index502510)
                Interlocked.Exchange(ref _lastPriceTicks, DateTime.UtcNow.Ticks);
        }
        return changed;
    }

    private void RegisterComponent(string modelRef, string componentRef, ComponentTarget target)
    {
        if (componentRef.Length == 0) return;
        var key = ComponentKey(modelRef, componentRef);
        _componentTargets[key] = target;
        var refs = _componentRefsByModel.GetOrAdd(modelRef, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (refs) refs.Add(key);
    }

    private static string ComponentKey(string modelRef, string componentRef) => $"{modelRef}\u001f{componentRef}";

    private void RemoveComponentTargets(string modelRef)
    {
        if (!_componentRefsByModel.TryRemove(modelRef, out var refs)) return;
        lock (refs) foreach (var componentRef in refs) _componentTargets.TryRemove(componentRef, out _);
    }

    private RaceState? FindRace(string meeting, int hour, int minute, long meetingStartUnix = 0)
    {
        // All Races models can contain identical venue/time labels on adjacent
        // days. The model start timestamp is the date authority; without it,
        // the panel must not be attached to a race.
        if (meetingStartUnix <= 0) return null;
        var meetingKey = VenueKey(meeting);
        var meetingDate = UkDate(meetingStartUnix);
        return _raceStates.Values
            .Where(x => VenueKey(x.Meeting) == meetingKey)
            .Where(x => UkDate(x.StartTimeUnix) == meetingDate)
            .Where(x => Math.Abs(TimeOfDayMinutes(x.StartTimeUnix) - (hour * 60 + minute)) <= 2)
            .OrderBy(x => Math.Abs(x.StartTimeUnix - meetingStartUnix))
            .FirstOrDefault();
    }

    private void PublishRace(RaceState race)
    {
        List<SpreadexRunnerPrice> prices;
        lock (race.Gate)
        {
            var quotedIndexTotal = race.Runners.Values
                .Where(runner => runner.HundredBuy > 0)
                .Sum(runner => runner.HundredBuy);
            var usePreciseIndex = quotedIndexTotal > 0;
            var marketTotal = usePreciseIndex && race.WinIndexTotal > 0 ? race.WinIndexTotal
                : usePreciseIndex ? quotedIndexTotal : 0;
            prices = race.Runners.Values.Select(runner =>
            {
                // The meeting-level panelSelections are not a reliable fixed-odds fallback;
                // for some races they are the 50-25-10 index (for example 28-31). Never
                // expose those as the 100 Win Index while its own subscription is loading.
                var first = usePreciseIndex ? runner.HundredBuy : 0;
                var second = usePreciseIndex ? runner.HundredSell : 0;
                var offer = Math.Max(first, second);
                var bid = Math.Min(first, second);
                var updated = usePreciseIndex ? runner.HundredUpdatedUtc : default;
                var indexOffer = Math.Max(runner.IndexBuy, runner.IndexSell);
                var hundredOffer = Math.Max(runner.HundredBuy, runner.HundredSell);
                // A place index includes at least the winner contribution (50
                // points versus 100 in the win index). Reject partial/reset
                // deltas such as 0.1-1 for a 20% win chance, even if a stale
                // status flag still says active.
                var coherentIndex = hundredOffer <= 0 || indexOffer + .0001 >= hundredOffer * .5;
                // 210003 is also used for ordinary pre-race market suspensions. The quote
                // remains the latest valid Spreadex book and must stay visible/usable; the
                // event-level in-play suspension latch controls whether updates are sent on.
                var publishIndex = coherentIndex && indexOffer > 0;
                return new SpreadexRunnerPrice(
                    runner.Name, offer, bid,
                    offer > 0 && marketTotal > 0
                        ? RacingMarketCalculator.SnapToOddsLadder(marketTotal / offer) : 0,
                    updated == default ? DateTime.UtcNow : updated,
                    publishIndex && runner.IndexBuy > 0 ? runner.IndexBuy : null,
                    publishIndex && runner.IndexSell > 0 ? runner.IndexSell : null,
                    publishIndex && runner.IndexUpdatedUtc != default ? runner.IndexUpdatedUtc : null,
                    publishIndex ? race.IndexMarketName : null,
                    publishIndex && runner.IndexStatus == 210003,
                    runner.HundredStatus == 210003);
            }).ToList();
        }
        _races[$"{VenueKey(race.Meeting)}:{race.StartTimeUnix}"] = new(
            race.PageId, race.Meeting, race.StartTimeUnix, prices, race.IndexPoints);
        if (_queuedPersistence.TryAdd(race.PageId, 0)) _persistenceQueue.Writer.TryWrite(race.PageId);
    }

    private async Task PersistUpdatesAsync(CancellationToken cancellationToken)
    {
        await foreach (var pageId in _persistenceQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (!_raceStates.TryGetValue(pageId, out var state)) continue;
                var key = $"{VenueKey(state.Meeting)}:{state.StartTimeUnix}";
                if (!_races.TryGetValue(key, out var race) || race.Prices.Count == 0) continue;
                // Persist the provider quote immediately so the trader display
                // cannot miss an insert while event discovery and repricing are
                // contending for the same event rows. The queued worker then
                // recalculates the balanced book and publishes all markets as
                // one logical update.
                await feedRepository.StoreSpreadexPricesImmediateAsync(
                    race.Meeting, race.StartTimeUnix, race.Prices, cancellationToken);
                await feedRepository.QueueSpreadexRepriceAsync(
                    race.Meeting, race.StartTimeUnix, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not immediately persist Spreadex prices for {Meeting} {StartTimeUnix}.",
                    _raceStates.TryGetValue(pageId, out var failed) ? failed.Meeting : "unknown",
                    failed?.StartTimeUnix ?? 0);
            }
            finally { _queuedPersistence.TryRemove(pageId, out _); }
        }
    }

    private static void SetMarket(RunnerState runner, MarketKind kind, double buy, double sell, int status, DateTime updatedUtc)
    {
        if (kind == MarketKind.Hundred)
        {
            if (Math.Max(buy, sell) > 0)
            {
                runner.HundredBuy = buy;
                runner.HundredSell = sell;
                runner.HundredUpdatedUtc = updatedUtc;
            }
            runner.HundredStatus = status;
        }
        else if (kind == MarketKind.Index502510)
        {
            if (Math.Max(buy, sell) > 0)
            {
                runner.IndexBuy = buy;
                runner.IndexSell = sell;
                runner.IndexUpdatedUtc = updatedUtc;
            }
            runner.IndexStatus = status;
        }
        else
        {
            runner.StandardBuy = buy;
            runner.StandardSell = sell;
            runner.StandardStatus = status;
            runner.StandardUpdatedUtc = updatedUtc;
        }
    }

    private static double GetBuy(RunnerState runner, MarketKind kind) => kind switch { MarketKind.Hundred => runner.HundredBuy, MarketKind.Index502510 => runner.IndexBuy, _ => runner.StandardBuy };
    private static double GetSell(RunnerState runner, MarketKind kind) => kind switch { MarketKind.Hundred => runner.HundredSell, MarketKind.Index502510 => runner.IndexSell, _ => runner.StandardSell };
    private static int GetStatus(RunnerState runner, MarketKind kind) => kind switch { MarketKind.Hundred => runner.HundredStatus, MarketKind.Index502510 => runner.IndexStatus, _ => runner.StandardStatus };
    private static bool Has(JsonObject node, string name) => node[name] is not null;
    private static bool IsStructural(JsonObject node) => node["added"] is not null || node["removed"] is not null ||
        node["allRefs"] is not null || node["_p"] is not null || node["isVisible"] is not null;
    private static bool IsInPlayModel(string modelRef) =>
        modelRef.Equals(InPlayEventsModelRef, StringComparison.OrdinalIgnoreCase) ||
        modelRef.Equals(HorseInPlayGroupModelRef, StringComparison.OrdinalIgnoreCase);
    private static int TimeOfDayMinutes(long unix) => (int)TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
        DateTimeOffset.FromUnixTimeSeconds(unix), "GMT Standard Time").TimeOfDay.TotalMinutes;
    private static DateTime UkDate(long unix) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
        DateTimeOffset.FromUnixTimeSeconds(unix), "GMT Standard Time").Date;
    private bool IsWithinExpectedPriceWindow(DateTime utcNow)
    {
        var now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        var ukNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "GMT Standard Time");
        if (ukNow.TimeOfDay < TimeSpan.FromHours(8)) return false;

        var latestRaceUnix = _raceStates.Values
            .Where(race => UkDate(race.StartTimeUnix) == ukNow.Date)
            .Select(race => race.StartTimeUnix)
            .DefaultIfEmpty(0)
            .Max();
        if (latestRaceUnix <= 0) return false;

        // Races can start late and Spreadex may continue trading in play.
        var windowEndUtc = DateTimeOffset.FromUnixTimeSeconds(latestRaceUnix)
            .AddMinutes(30)
            .UtcDateTime;
        return utcNow <= windowEndUtc;
    }

    private static DateTime? TicksToUtc(long ticks) => ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
    private static string RaceStateKey(string meeting, long startUnix) => $"{VenueKey(meeting)}:{startUnix}";
    private static string VenueKey(string value) => RacingEventNames.MeetingKey(value);
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
    private static long Long(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Double(JsonNode? node) => double.TryParse(Text(node), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? RaceCardKey(long typeId) => typeId switch
    {
        6707 => "double-number", 6608 => "favourite-performance", 528 => "sp-winner",
        15084 => "sp-2-places", 15085 => "sp-3-places", 8819 => "race-squared-number",
        6708 => "race-multi-mules", 10615 => "double-draw", _ => null
    };
    private static int RaceCardOrder(long typeId) => typeId switch
    { 6707 => 1, 6608 => 2, 528 => 3, 15084 => 4, 15085 => 5, 8819 => 6, 6708 => 7, 10615 => 8, _ => 99 };

    private enum MarketKind { Menu, InPlay, Standard, Hundred, Index502510, RaceCard }
    private sealed record ComponentTarget(long PageId, string RunnerKey, MarketKind Kind, bool IsPrice);
    private sealed record SpreadexRace(long PageId, string Meeting, long StartTimeUnix,
        IReadOnlyList<SpreadexRunnerPrice> Prices, IReadOnlyList<double>? IndexPoints);
    private sealed class RaceState(long pageId, long menuId, string meeting, long startTimeUnix)
    {
        public long PageId { get; } = pageId;
        public long MenuId { get; set; } = menuId;
        public string Meeting { get; set; } = meeting;
        public long StartTimeUnix { get; set; } = startTimeUnix;
        public double WinIndexTotal { get; set; } = 100;
        public IReadOnlyList<double>? IndexPoints { get; set; }
        public string? IndexMarketName { get; set; }
        public object Gate { get; } = new();
        public Dictionary<string, RunnerState> Runners { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> NonRunners { get; } = new();
        public Dictionary<string, RaceCardState> RaceCardMarkets { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class RaceCardState(string key, string name, int order)
    {
        public string Key { get; } = key; public string Name { get; set; } = name; public int Order { get; } = order;
        public double Buy { get; set; } public double Sell { get; set; } public int Status { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
    private sealed class RunnerState(string name)
    {
        public string Name { get; set; } = name;
        public double HundredBuy { get; set; }
        public double HundredSell { get; set; }
        public int HundredStatus { get; set; }
        public DateTime HundredUpdatedUtc { get; set; }
        public double StandardBuy { get; set; }
        public double StandardSell { get; set; }
        public int StandardStatus { get; set; }
        public DateTime StandardUpdatedUtc { get; set; }
        public double IndexBuy { get; set; }
        public double IndexSell { get; set; }
        public int IndexStatus { get; set; }
        public DateTime IndexUpdatedUtc { get; set; }
    }
}
