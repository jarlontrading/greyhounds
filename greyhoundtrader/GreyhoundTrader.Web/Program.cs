using RacingTrader.Web.Configuration;
using RacingTrader.Web.Models;
using RacingTrader.Web.Services;
using System.Net;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);
var serviceUrl = builder.Configuration["RacingTrader:Url"] ?? "http://127.0.0.1:5187";
var hostedWorkersEnabled = !builder.Configuration.GetValue<bool>("RacingTrader:DisableHostedWorkers");
var betfredWorkerEnabled = hostedWorkersEnabled &&
    builder.Configuration.GetValue<bool>("RacingTrader:EnableBetfredWorker");
var publishTargets = RacingPublisher.ParseExchangePrefixes(builder.Configuration);
builder.WebHost.UseUrls(serviceUrl);
builder.Logging.AddFilter("System.Net.Http.HttpClient.RacingFeedClient", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.RacingResultClient", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.BetsApiInPlay", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.BetsApiResults", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.Bet365Capture", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.TimeformGreyhounds", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient.GreyhoundDistanceClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddProblemDetails();
// Kestrel must bind independently of slow browser/feed hosted-service startup.
// Without concurrent startup, one delayed worker leaves the public tunnel with
// a running connector but no origin listener on port 5287.
builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
builder.Services.AddSingleton<ExternalSettings>();
builder.Services.AddSingleton<RacingRepository>();
// The distance state is loaded by its own worker. Do not make construction of
// every race-pricing repository wait for that file-backed service.
builder.Services.AddSingleton(_ => new RacingMarketCalculator());
builder.Services.AddSingleton<GreyhoundDistanceMarkets>();
builder.Services.AddSingleton<TrapChallengeService>();
builder.Services.AddHttpClient("TimeformGreyhounds", client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/126.0 Safari/537.36");
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = CreateIpv4Handler();
    handler.ConnectTimeout = TimeSpan.FromSeconds(20);
    return handler;
});
builder.Services.AddSingleton<TimeformGreyhoundForecastService>();
builder.Services.AddHttpClient<GreyhoundDistanceClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<RacingFeedRepository>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("FeedRepositoryStartup");
    log.LogInformation("Resolving feed settings.");
    var settings = sp.GetRequiredService<ExternalSettings>();
    log.LogInformation("Resolving market calculator.");
    var calculator = sp.GetRequiredService<RacingMarketCalculator>();
    log.LogInformation("Constructing feed repository.");
    return new RacingFeedRepository(settings, calculator,
        sp.GetRequiredService<ILogger<RacingFeedRepository>>());
});
builder.Services.AddSingleton<CanonicalEventService>();
builder.Services.AddSingleton<RacingPublisher>();
builder.Services.AddSingleton<RacingActivityLog>();
builder.Services.AddSingleton<RacingFeedDiagnostics>();
builder.Services.AddSingleton<RacingDiagnosticCapture>();
builder.Services.AddSingleton<RaceAuditTrail>();
builder.Services.AddSingleton<RaceTerminalGate>();
builder.Services.AddSingleton<BetfairRepository>();
builder.Services.AddSingleton<BetfairMarketMatcher>();
builder.Services.AddSingleton<BetfairBiasService>();
builder.Services.AddSingleton<BetfairSessionManager>();
builder.Services.AddHttpClient<RacingFeedClient>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(CreateIpv4Handler);
builder.Services.AddHttpClient("BetsApiInPlay", client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(CreateIpv4Handler);
builder.Services.AddSingleton<BetsApiInPlayMonitor>();
builder.Services.AddHttpClient("BetsApiResults", client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(CreateIpv4Handler);
builder.Services.AddSingleton<BetsApiResultClient>();
builder.Services.AddSingleton<BetfredResultClient>();
builder.Services.AddHttpClient("Bet365Capture", client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("BetfredSuspensionService", client => client.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddHttpClient("SilkImages", client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Bet365CaptureBridge>();
builder.Services.AddSingleton<OnDemandBet365Capture>();
builder.Services.AddSingleton<BetfredSuspensionClient>();
builder.Services.AddSingleton<BetAhoyFeed>();
builder.Services.AddSingleton<R2SilkStorage>();
builder.Services.AddSingleton<SpreadexRacingService>();
builder.Services.AddSingleton<BetfredRacingService>(sp =>
{
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("BetfredStartup");
    log.LogInformation("Resolving Betfred repository.");
    var feedRepository = sp.GetRequiredService<RacingFeedRepository>();
    // Silk caching is presentation work and must not hold OFF protection.
    R2SilkStorage? silkStorage = null;
    log.LogInformation("Resolving Betfred diagnostics.");
    var diagnosticCapture = sp.GetRequiredService<RacingDiagnosticCapture>();
    log.LogInformation("Resolving Betfred audit trail.");
    var auditTrail = sp.GetRequiredService<RaceAuditTrail>();
    log.LogInformation("Resolving Betfred HTTP factory and logger.");
    return new BetfredRacingService(feedRepository, silkStorage, diagnosticCapture,
        auditTrail, sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<ILogger<BetfredRacingService>>());
});
builder.Services.AddHttpClient<BetfairClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<RacingResultRepository>();
builder.Services.AddHttpClient<RacingResultClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
if (hostedWorkersEnabled)
{
    // Resolve and start workers after Kestrel has been allowed to bind. A browser or
    // external-feed constructor must never leave a live process with no HTTP listener.
    builder.Services.AddSingleton<RacingSuspensionWorker>();
    builder.Services.AddSingleton<RacingFeedWorker>();
    builder.Services.AddSingleton<RacingResultWorker>();
    // The repository limits this worker to the live settlement window. Do not widen that
    // query into a historical backfill here: hundreds of serial Sporting Life requests
    // previously starved suspension and status traffic.
    builder.Services.AddSingleton<GreyhoundDistanceWorker>();
    builder.Services.AddSingleton<TrapChallengePublishingWorker>();
    builder.Services.AddSingleton<WithdrawalDetectionService>();
    // Resolve every worker after the host can bind. Constructing the direct OFF
    // worker graph has previously left a live dotnet process with no listener.
    builder.Services.AddHostedService<DeferredHostedService<BetsApiInPlayMonitor>>();
    builder.Services.AddHostedService<DeferredHostedService<BetfredSuspensionClient>>();
    if (betfredWorkerEnabled)
        builder.Services.AddHostedService<DeferredHostedService<BetfredRacingService>>();
    builder.Services.AddHostedService<DeferredHostedService<RacingSuspensionWorker>>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<BetAhoyFeed>());
    builder.Services.AddHostedService<DeferredHostedService<RacingDiagnosticCapture>>();
    builder.Services.AddHostedService<DeferredHostedService<RacingFeedWorker>>();
    builder.Services.AddHostedService<DeferredHostedService<RacingResultWorker>>();
    builder.Services.AddHostedService<DeferredHostedService<GreyhoundDistanceWorker>>();
    // Absent in this fork, so no greyhound non-runner was ever actioned automatically.
    // Dogs are admitted on a confirmed feed non-runner flag only - see the sport_id guard
    // in ApplyPendingHorseWithdrawalsAsync.
    builder.Services.AddHostedService<DeferredHostedService<WithdrawalDetectionService>>();
    builder.Services.AddHostedService<DeferredHostedService<TrapChallengePublishingWorker>>();
    builder.Services.AddHostedService<DeferredHostedService<TimeformGreyhoundForecastService>>();
}

var app = builder.Build();
app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/distance-review", (GreyhoundDistanceMarkets distances) => Results.Ok(distances.Review()));

var repository = app.Services.GetRequiredService<RacingRepository>();
var skipStartupSchema = app.Configuration.GetValue("RacingTrader:SkipStartupSchema", false);
try
{
    // PROD intentionally skips the slow legacy schema/maintenance pass.
    if (!skipStartupSchema)
    {
        await repository.EnsureRuntimeFeatureSchemaAsync(app.Lifetime.ApplicationStopping);
        await app.Services.GetRequiredService<CanonicalEventService>().InitialiseAsync(app.Lifetime.ApplicationStopping);
        await repository.InitialiseAsync(app.Lifetime.ApplicationStopping);
        if (app.Configuration.GetValue("RacingTrader:RunStartupCleanup", false))
        {
            await app.Services.GetRequiredService<RacingFeedRepository>()
                .RemoveSyntheticFavouriteSelectionsAsync(app.Lifetime.ApplicationStopping);
            await app.Services.GetRequiredService<RacingFeedRepository>()
                .RemoveDuplicateAndCombinationSelectionsAsync(app.Lifetime.ApplicationStopping);
        }
        await repository.RepairStaleBulkWithdrawalsAsync("Nottingham", app.Lifetime.ApplicationStopping);
        await repository.RepairZeroRule4DeductionsAsync(app.Lifetime.ApplicationStopping);
        var betfairRepository = app.Services.GetRequiredService<BetfairRepository>();
        await betfairRepository.InitialiseAsync(app.Lifetime.ApplicationStopping);
    }
    else
    {
        app.Logger.LogWarning("Startup schema and maintenance checks skipped because RacingTrader:SkipStartupSchema is enabled.");
    }
}
catch (Exception ex)
{
    app.Logger.LogError(ex,
        "Database startup checks failed. The web interface will start so configuration and connectivity can be diagnosed.");
}

// Construct the shared pricing/suspension graph on one thread before Kestrel
// accepts monitor and browser requests. Those requests resolve the same
// singletons; racing them against concurrent hosted-service construction has
// caused DI lock cycles with a live web process but no suspension heartbeat.
_ = app.Services.GetRequiredService<RacingMarketCalculator>();
_ = app.Services.GetRequiredService<RacingFeedRepository>();
_ = app.Services.GetRequiredService<BetfredRacingService>();
_ = app.Services.GetRequiredService<Bet365CaptureBridge>();
_ = app.Services.GetRequiredService<RacingSuspensionWorker>();

app.MapGet("/api/disk-space", () =>
{
    try
    {
        var drive = new DriveInfo(@"C:\");
        var free = drive.AvailableFreeSpace;
        var total = drive.TotalSize;
        var percent = total > 0 ? 100d * free / total : 0;
        var level = free < 5L * 1024 * 1024 * 1024 || percent < 5 ? "critical"
            : free < 20L * 1024 * 1024 * 1024 || percent < 10 ? "warning" : "ok";
        return Results.Ok(new { drive = "C:", freeBytes = free, totalBytes = total,
            freePercent = percent, level, checkedAtUtc = DateTime.UtcNow });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.Ok(new { drive = "C:", level = "unavailable", checkedAtUtc = DateTime.UtcNow });
    }
});

app.MapGet("/api/status", (ExternalSettings settings) => Results.Ok(new
{
    service = "Greyhound Trader Web",
    environment = string.Join(" + ", publishTargets.Select(x => x == "DEMO" ? "UAT" : x)),
    productionPublishing = publishTargets.Contains("PROD"),
    uatPublishing = publishTargets.Contains("DEMO"),
    marketVisibility = new
    {
        uat = "all eligible markets",
        production = "all eligible markets except 50-25-10 Index and 50-30-20-10 Index"
    },
    hostedFeed = new { enabled = hostedWorkersEnabled, betfredEnabled = betfredWorkerEnabled,
        sports = new[] { 4 }, labels = new[] { "greyhounds" }, desktopProcessRequired = false,
        preLivePollSeconds = 10, livePollSeconds = 1, deltaPublishing = true, hostedResults = true },
    database = settings.RacingDatabase,
    settingsFile = settings.SourcePath,
    automaticRule4 = new
    {
        explicitFeedNonRunner = "immediate",
        isolatedSuspensionObservations = 3,
        isolatedSuspensionMinimumSeconds = 20,
        maximumSimultaneousCandidates = 2,
        requiresOtherActiveRunners = true
    },
    priceHistory = new { market = "Race Winner", retentionDays = 7, recordsChangesOnly = true },
    betfair = new
    {
        enabled = settings.BetfairEnabled,
        configured = settings.BetfairConfigured,
        authenticationMode = settings.BetfairAuthenticationMode,
        delayedFeed = true,
        biasEnabled = settings.BetfairBiasEnabled,
        biasWeight = settings.BetfairBiasWeight,
        allowInPlayBias = settings.BetfairAllowInPlayBias,
        orderMode = settings.BetfairOrderMode
    },
    utc = DateTime.UtcNow
}));

app.MapGet("/api/betfair/status", async (ExternalSettings settings, BetfairRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetStatusAsync(settings, ct)));

app.MapGet("/api/spreadex/status", (SpreadexRacingService spreadex) => Results.Ok(spreadex.Status));
app.MapGet("/api/betfred/status", (BetfredRacingService betfred) => Results.Ok(betfred.Status));
app.MapGet("/api/betfred-suspension-service/status", (BetfredSuspensionClient service) => Results.Ok(service.Status));
app.MapPost("/api/admin/betfred/link", async (
    long eventId, long betfredEventId, RacingFeedRepository repository, CancellationToken ct) =>
    Results.Ok(new { eventId, betfredEventId, linked = await repository.StoreBetfredEventLinkAsync(eventId, betfredEventId, ct) }));
app.MapGet("/api/betfred-off/status", (BetfredRacingService betfred) =>
{
    var status = betfred.Status;
    return Results.Ok(new
    {
        enabled = betfredWorkerEnabled,
        browserConnected = status.BrowserConnected,
        pageAvailable = status.BrowserConnected,
        status.ManagedBrowserId,
        status.BrowserProfile,
        couponUrl = "https://www.betfred.com/sports/greyhound-racing?tab=today",
        startedAtUtc = status.LastDiscoveryUtc,
        lastObservedAtUtc = status.LastResponseUtc,
        raceCardsObserved = status.DiscoveredEvents,
        runningSignals = 0,
        status.LastError
    });
});
app.MapGet("/api/spreadex/meeting-health", (SpreadexRacingService spreadex) =>
    Results.Ok(spreadex.GetMeetingHealth()));
app.MapGet("/api/race-audit", async (DateOnly? date, long? eventId, RaceAuditTrail audit, CancellationToken ct) =>
{
    var ukToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "GMT Standard Time"));
    return Results.Ok(await audit.ReadAsync(date ?? ukToday, eventId, ct));
});
app.MapGet("/api/spreadex/match-diagnostics", (long eventId, SpreadexRacingService spreadex) =>
    spreadex.GetMatchDiagnostics(eventId) is { } diagnostics
        ? Results.Ok(diagnostics)
        : Results.NotFound());
app.MapGet("/api/spreadex/prices", (string meeting, long startTimeUnix, SpreadexRacingService spreadex) =>
    Results.Ok(spreadex.GetPrices(meeting, startTimeUnix)));
app.MapGet("/api/spreadex/racecard-prices", (string meeting, long startTimeUnix, SpreadexRacingService spreadex) =>
    Results.Ok(spreadex.GetRaceCardPrices(meeting, startTimeUnix)));
app.MapGet("/api/spreadex/non-runners", (string meeting, long startTimeUnix, SpreadexRacingService spreadex) =>
    Results.Ok(spreadex.GetNonRunners(new PricingEvent(0, 2, meeting, meeting, startTimeUnix, "0", 0))));
app.MapGet("/api/spreadex/100-index-history", async (long eventId, long? competitorId, int? limit,
    RacingFeedRepository feedRepository, CancellationToken ct) => Results.Ok(
        await feedRepository.GetSpreadexHundredHistoryAsync(eventId, competitorId, limit ?? 200, ct)));
app.MapPost("/api/admin/hide-index-markets", async (RacingRepository repo, RacingPublisher publisher, CancellationToken ct) =>
{
    var markets = await repo.SetAllIndexMarketsHiddenAsync(ct);
    await publisher.PublishHiddenIndexMarketsAsync(markets, ct);
    return Results.Ok(new { hidden = markets.Count, futurePublishing = false });
});
app.MapGet("/api/spreadex/snapshot", (string contains, SpreadexRacingService spreadex) =>
    spreadex.FindSnapshot(contains) is { } snapshot ? Results.Text(snapshot, "application/json") : Results.NotFound());

app.MapPost("/api/admin/refresh-feeds", async (
    SpreadexRacingService spreadex, Bet365CaptureBridge capture,
    RacingFeedRepository feedRepo, RacingActivityLog activity, CancellationToken ct) =>
{
    var spreadexModels = spreadex.RequestFullRefresh();
    capture.RequestFullRefresh();
    var queuedEvents = await feedRepo.QueueAllActiveEventsRepriceAsync(ct);
    await activity.WriteAsync("subscription",
        $"Full feed refresh requested: {spreadexModels} Spreadex models and {queuedEvents} subscribed events queued.",
        cancellationToken: ct);
    return Results.Accepted(value: new { spreadexModels, queuedEvents });
});

app.MapPost("/api/admin/bet365/walk-meeting/{meeting}", async (
    string meeting, Bet365CaptureBridge capture, OnDemandBet365Capture onDemandCapture, RacingRepository repo,
    RacingFeedRepository feedRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    await onDemandCapture.StartAsync(ct);
    try
    {
    var events = await repo.GetMeetingEventsAsync(4, meeting, true, ct);
    foreach (var racingEvent in events)
        await feedRepo.QueueEventRepriceAsync(racingEvent.EventId, ct);
    capture.Prioritize(events.Select(racingEvent => racingEvent.EventId));
    var result = await capture.WalkMeetingAsync(meeting, ct);
    var cardsCreated = 0;
    foreach (var racingEvent in events)
    {
        if ((await feedRepo.GetRaceWinnerRunnerNamesAsync(racingEvent.EventId, ct)).Count >= 2) continue;
        var snapshot = capture.Snapshot(racingEvent.EventId);
        if (snapshot is null) continue;
        var runners = snapshot.Runners
            .Where(runner => !runner.ExplicitNonRunner &&
                             !BetfairMarketMatcher.NormalizeRunner(runner.Name)
                                 .Contains("vacant", StringComparison.Ordinal))
            .ToList();
        if (runners.Count < 2) continue;
        var pricingEvent = new PricingEvent(racingEvent.EventId, racingEvent.SportId,
            racingEvent.Meeting, racingEvent.Name, racingEvent.StartTimeUnix,
            racingEvent.Status, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var suspended = await feedRepo.ApplyPricesAsync(pricingEvent, runners, null,
            new HashSet<long>(), new HashSet<string>(), ct, createSuspendedCard: true);
        if (suspended is null) continue;
        await publisher.PublishAsync(suspended, ct);
        cardsCreated++;
    }
    await activity.WriteAsync("bet365-meeting-walk",
        $"On-demand Bet365 walk completed for {meeting}; {events.Count} races queued for repricing and {cardsCreated} missing cards created suspended. Capture browser closed after import.",
        4, meeting: meeting, cancellationToken: ct);
    return Results.Ok(new { meeting, queuedEvents = events.Count, cardsCreated, capture = result });
    }
    finally
    {
        await onDemandCapture.StopAsync();
    }
});

app.MapGet("/api/betfair/matches", async (int? limit, BetfairRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetMatchesAsync(limit ?? 100, ct)));

app.MapGet("/api/activity", async (int? limit, RacingActivityLog activity, CancellationToken ct) =>
    Results.Ok(await activity.GetRecentAsync(limit ?? 100, ct)));
app.MapGet("/api/activity/rule4", async (int? limit, RacingActivityLog activity, CancellationToken ct) =>
    Results.Ok(await activity.GetRecentRule4Async(limit ?? 100, ct)));
app.MapPost("/api/admin/rule4/repair-zero", async (RacingRepository repo, CancellationToken ct) =>
    Results.Ok(new { repaired = await repo.RepairZeroRule4DeductionsAsync(ct) }));
app.MapPost("/api/admin/events/{eventId:long}/rule4/repair-zero", async (
    long eventId, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.RepairZeroRule4DeductionsForEventAsync(eventId, ct)));
app.MapGet("/api/suspension-monitor", async (RacingActivityLog activity, CancellationToken ct) =>
    Results.Ok(await activity.GetSuspensionMonitorAsync(ct)));
app.MapGet("/api/suspension-sentinel/status", (RacingSuspensionWorker sentinel) =>
    Results.Ok(sentinel.Status));
app.MapGet("/api/feed-last-updates", async (RacingActivityLog activity, CancellationToken ct) =>
    Results.Ok(await activity.GetFeedLastUpdatesAsync(ct)));
app.MapGet("/api/feed-diagnostics", (RacingFeedDiagnostics diagnostics) =>
    Results.Ok(diagnostics.Snapshot()));
app.MapGet("/api/missing-betsapi-events", async (RacingActivityLog activity, CancellationToken ct) =>
    Results.Ok(await activity.GetMissingBetsApiEventsAsync(ct)));
app.MapGet("/api/provider-link-issues", async (CanonicalEventService canonical, CancellationToken ct) =>
    Results.Ok(await canonical.GetProviderLinkIssuesAsync(ct)));
app.MapGet("/api/provider-price-comparison", async (
    bool? includeTomorrow, RacingRepository repo, BetAhoyFeed betAhoy, CancellationToken ct) =>
{
    var rows = await repo.GetProviderPriceComparisonAsync(includeTomorrow ?? false, ct);
    return Results.Ok(rows.Select(row => row with
    {
        BetAhoy = row.Withdrawn ? null : betAhoy.GetQuote(row.Meeting, row.StartTimeUnix, row.RunnerName)?.Price
    }));
});
app.MapGet("/api/index-arbs", async (RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetIndexArbsAsync(ct)));

app.MapGet("/api/feed/pre-event", async (
    int? sportId, int? page, RacingFeedClient feed, CancellationToken ct) =>
{
    var requestedSportId = sportId ?? 2;
    if (requestedSportId is not (2 or 4))
        return Results.BadRequest("sportId must be 2 (horses) or 4 (greyhounds).");

    var response = await feed.GetUpcomingFeedResponseAsync(requestedSportId, page ?? 1, ct);
    return Results.Text(response, "application/json");
});

app.MapGet("/api/meetings", async (int sportId, RacingRepository repo, CancellationToken ct) =>
{
    if (sportId is not (2 or 4)) return Results.BadRequest("sportId must be 2 (horses) or 4 (greyhounds).");
    return Results.Ok(await repo.GetMeetingsAsync(sportId, ct));
});

app.MapGet("/api/meetings/{sportId:int}/{meeting}/events", async (
    int sportId, string meeting, bool? subscribedOnly, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetMeetingEventsAsync(sportId, meeting, subscribedOnly ?? true, ct)));

app.MapGet("/api/meetings/4/{meeting}/trap-challenge", async (
    string meeting, long? startTimeUnix, TrapChallengeService trapChallenge, CancellationToken ct) =>
    Results.Ok(await trapChallenge.GetAsync(meeting, ct, startTimeUnix)));

app.MapGet("/api/trap-challenges/publishing", async (
    RacingRepository repo, TrapChallengeService trapChallenge, CancellationToken ct) =>
{
    var output = new List<object>();
    foreach (var meeting in (await repo.GetMeetingsAsync(4, ct)).Where(x => x.Subscribed && x.EventCount >= 2))
    {
        var snapshot = await trapChallenge.BuildPublishingSnapshotAsync(meeting.Name, meeting.FirstStartUnix, ct);
        if (snapshot is null) continue;
        var market = snapshot.Markets.Single();
        output.Add(new
        {
            meeting = meeting.Name,
            eventId = snapshot.Event.EventId,
            eventName = snapshot.Event.Name,
            startTimeUnix = snapshot.Event.StartTimeUnix,
            eventStatus = snapshot.Event.Status,
            marketId = market.MarketId,
            marketName = market.Name,
            marketActive = market.Active,
            selections = market.Selections.Select(x => new { x.SelectionId, x.Identifier, x.Name, price = x.PriceBack, x.Result })
        });
    }
    return Results.Ok(output);
});

app.MapPost("/api/admin/trap-challenges/republish", async (
    RacingRepository repo, TrapChallengeService trapChallenge, RacingPublisher publisher, CancellationToken ct) =>
{
    var published = new List<object>();
    foreach (var meeting in (await repo.GetMeetingsAsync(4, ct)).Where(x => x.Subscribed && x.EventCount >= 2))
    {
        var snapshot = await trapChallenge.BuildPublishingSnapshotAsync(meeting.Name, meeting.FirstStartUnix, ct);
        if (snapshot is null) continue;
        await publisher.PublishAsync(snapshot, ct);
        published.Add(new { meeting = meeting.Name, eventId = snapshot.Event.EventId,
            marketId = snapshot.Markets.Single().MarketId, status = snapshot.Event.Status });
    }
    return Results.Ok(new { count = published.Count, published });
});
app.MapPost("/api/admin/trap-challenges/{meeting}/republish", async (
    string meeting, long startTimeUnix, TrapChallengeService trapChallenge,
    RacingPublisher publisher, CancellationToken ct) =>
{
    // A completed daytime meeting can fall outside the general meetings query
    // before its final result is repaired. Allow that meeting's stable challenge
    // event to be rebuilt and published directly from its stored race results.
    var snapshot = await trapChallenge.BuildPublishingSnapshotAsync(meeting, startTimeUnix, ct);
    if (snapshot is null) return Results.NotFound(new { meeting, startTimeUnix });
    await publisher.PublishAsync(snapshot, ct);
    var market = snapshot.Markets.Single();
    return Results.Ok(new
    {
        meeting,
        eventId = snapshot.Event.EventId,
        marketId = market.MarketId,
        status = snapshot.Event.Status,
        results = market.Selections.Select(selection => new
        {
            selection.Identifier,
            selection.Name,
            selection.Result
        })
    });
});
app.MapPost("/api/admin/trap-challenges/{eventId:long}/close", async (
    long eventId, string meeting, string eventName, long startTimeUnix,
    RacingPublisher publisher, CancellationToken ct) =>
{
    await publisher.PublishTrapChallengeClosureAsync(eventId, meeting, eventName, startTimeUnix, ct);
    return Results.Ok(new { eventId, closed = true });
});

app.MapGet("/api/events/chronological", async (
    int sportId, bool? subscribedOnly, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetChronologicalEventsAsync(sportId, subscribedOnly ?? true, ct)));

app.MapGet("/api/meetings/racecard-spreads", async (
    string meeting, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetMeetingRaceCardSpreadsAsync(meeting, ct)));

app.MapPost("/api/meetings/{sportId:int}/{meeting}/republish", async (
    int sportId, string meeting, RacingFeedRepository feedRepo, CancellationToken ct) =>
    Results.Ok(new { meeting, queuedEvents = await feedRepo.QueueMeetingRepublishAsync(sportId, meeting, ct) }));

app.MapPost("/api/admin/events/{eventId:long}/reprice", async (
    long eventId, RacingFeedRepository feedRepo, CancellationToken ct) =>
    await feedRepo.QueueEventRepriceAsync(eventId, ct)
        ? Results.Accepted(value: new { eventId, queued = true })
        : Results.NotFound());

app.MapPost("/api/admin/events/republish", async (
    long[] eventIds, RacingFeedRepository feedRepo, RacingPublisher publisher, CancellationToken ct) =>
{
    var events = await feedRepo.GetEventsForPublishingAsync(eventIds, ct);
    var creations = await feedRepo.ClaimInitialEventPublicationsAsync(events, ct);
    try
    {
        await publisher.PublishEventsAsync(creations, ct);
    }
    catch
    {
        await feedRepo.ReleaseInitialEventPublicationClaimsAsync(
            creations.Select(x => x.EventId), CancellationToken.None);
        throw;
    }
    return Results.Ok(new { requested = eventIds.Distinct().Count(), published = creations.Count, eventIds = creations.Select(x => x.EventId) });
});

app.MapGet("/api/betsapi/meetings", async (RacingFeedClient feed, RacingRepository repo, CancellationToken ct) =>
{
    try
    {
        var events = await feed.GetBetsApiEventsAsync(4, ct);
        IReadOnlyDictionary<string, MeetingSummary> stored;
        try
        {
            stored = (await repo.GetMeetingsAsync(4, ct))
                .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is MySqlConnector.MySqlException or TimeoutException)
        {
            app.Logger.LogWarning(ex, "Greyhound subscription database is unavailable; returning BetsAPI meetings unsubscribed.");
            stored = new Dictionary<string, MeetingSummary>(StringComparer.OrdinalIgnoreCase);
        }
        var meetings = events
            .GroupBy(item => item.Meeting, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                stored.TryGetValue(group.Key, out var subscription);
                return new MeetingSummary(
                    4, group.Key, group.Count(), subscription?.SubscribedEventCount ?? 0,
                    subscription?.Subscribed ?? false, group.Min(item => item.StartTimeUnix),
                    group.Max(item => item.StartTimeUnix));
            })
            .OrderBy(item => item.FirstStartUnix)
            .ThenBy(item => item.Name)
            .ToList();
        return Results.Ok(meetings);
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogWarning(ex, "BetsAPI greyhound meeting discovery failed; serving stored meetings.");
        try
        {
            return Results.Ok(await repo.GetMeetingsAsync(4, ct));
        }
        catch (Exception databaseException) when (databaseException is MySqlConnector.MySqlException or TimeoutException)
        {
            app.Logger.LogError(databaseException, "Stored greyhound meetings are also unavailable.");
            return Results.Problem(
                title: "Greyhound meetings unavailable",
                detail: "Neither the live meeting feed nor the stored meeting list is currently available.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
});

app.MapPost("/api/admin/events/{eventId:long}/republish-current", async (
    long eventId, RacingFeedRepository feedRepo, RacingPublisher publisher, CancellationToken ct) =>
{
    var snapshot = (await feedRepo.BuildCurrentSnapshotsForPublishingAsync(ct))
        .FirstOrDefault(item => item.Event.EventId == eventId);
    if (snapshot is null) return Results.NotFound(new { eventId });
    await publisher.PublishAsync(snapshot, ct);
    var publishedMarkets = snapshot.Markets.Where(market => market.MarketTypeId is not (1010 or 1011)).ToList();
    return Results.Ok(new
    {
        eventId,
        markets = publishedMarkets.Count,
        selections = publishedMarkets.Sum(market => market.Selections.Count),
        republished = true
    });
});

app.MapPost("/api/admin/events/hide-empty-shells", async (
    long[] eventIds, RacingFeedRepository feedRepo, RacingPublisher publisher, CancellationToken ct) =>
{
    var events = await feedRepo.GetEventsForPublishingAsync(eventIds.Distinct().ToList(), ct);
    await publisher.PublishHiddenEventShellsAsync(events, ct);
    return Results.Ok(new { hidden = events.Count, eventIds = events.Select(item => item.EventId) });
});

app.MapPost("/api/admin/markets/republish-all", async (
    RacingFeedRepository feedRepo, RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var snapshots = await feedRepo.BuildCurrentSnapshotsForPublishingAsync(ct);
    var marketCount = 0;
    var selectionCount = 0;
    foreach (var snapshot in snapshots)
    {
        await publisher.PublishAsync(snapshot, ct);
        marketCount += snapshot.Markets.Count(x => x.MarketTypeId is not (1010 or 1011));
        selectionCount += snapshot.Markets
            .Where(x => x.MarketTypeId is not (1010 or 1011))
            .Sum(x => x.Selections.Count);
    }
    await activity.WriteAsync("publishing",
        $"Full Sportuno resend completed: {snapshots.Count} events, {marketCount} markets and {selectionCount} selections.",
        cancellationToken: ct);
    return Results.Ok(new { events = snapshots.Count, markets = marketCount, selections = selectionCount });
});

app.MapPost("/api/admin/events/suspend", async (
    long[] eventIds, RacingFeedRepository feedRepo, RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var events = await feedRepo.GetEventsForPublishingAsync(eventIds, ct);
    var suspended = new List<long>();
    foreach (var ev in events)
    {
        var snapshot = await feedRepo.SuspendRaceAsync(ev, ct);
        if (snapshot is null)
        {
            var shell = await feedRepo.SuspendEventShellAsync(ev, ct);
            await publisher.PublishEventsAsync([shell], ct);
        }
        else await publisher.PublishAsync(snapshot, ct);
        await activity.WriteAsync("suspension", "Missed overdue event suspended administratively.",
            ev.SportId, ev.EventId, ev.Meeting, ev.Name, ct);
        suspended.Add(ev.EventId);
    }
    return Results.Ok(new { requested = eventIds.Distinct().Count(), suspended = suspended.Count, eventIds = suspended });
});

app.MapPost("/api/admin/events/terminal-repair", async (
    long[] eventIds, RacingFeedRepository feedRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var events = await feedRepo.GetEventsForTerminalRepairAsync(eventIds, ct);
    var repaired = new List<long>();
    foreach (var ev in events)
    {
        var snapshot = await feedRepo.SuspendRaceAsync(ev with { Status = "2" }, ct);
        if (snapshot is null)
        {
            await publisher.PublishEventsAsync([ev with { Status = "2" }], ct);
        }
        else
        {
            await publisher.PublishAsync(snapshot, ct);
        }
        repaired.Add(ev.EventId);
    }
    await activity.WriteAsync("terminal-repair",
        $"Terminal state resent for {repaired.Count} historical greyhound races.",
        4, cancellationToken: ct);
    return Results.Ok(new { requested = eventIds.Distinct().Count(), repaired = repaired.Count, eventIds = repaired });
});

app.MapPost("/api/admin/forecasts/repair-recent", async (
    RacingResultRepository resultRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var candidates = await resultRepo.GetRecentForecastRepairEventIdsAsync(ct);
    var repaired = new List<long>();
    foreach (var eventId in candidates)
    {
        var snapshot = await resultRepo.RepairForecastsAsync(eventId, ct);
        if (snapshot is null) continue;
        await publisher.PublishAsync(snapshot, ct);
        await activity.WriteAsync("forecast-result",
            "Forecast result repaired from the stored confirmed first and second.",
            snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
        repaired.Add(eventId);
    }
    return Results.Ok(new { candidates = candidates.Count, repaired = repaired.Count, eventIds = repaired });
});

app.MapPost("/api/admin/results/revalidate-recent", async (
    bool? allToday,
    RacingResultRepository resultRepo, RacingResultClient resultClient,
    RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var candidates = await resultRepo.GetCandidatesAsync(ct, allToday == true);
    var results = await resultClient.FindResultsAsync(candidates, ct);
    var republished = new List<long>();
    foreach (var result in results)
    {
        var snapshot = await resultRepo.SettleAsync(result, ct);
        if (snapshot is null) continue;
        await publisher.PublishAsync(snapshot, ct);
        await activity.WriteAsync(result.IsFinal ? "final-result" : "provisional-result",
            "Result manually revalidated and republished from the latest Sporting Life data.",
            snapshot.Event.SportId, result.EventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
        republished.Add(result.EventId);
    }
    return Results.Ok(new
    {
        candidates = candidates.Count,
        resultsFound = results.Count,
        republished = republished.Count,
        eventIds = republished
    });
});

app.MapPost("/api/admin/results/revalidate-betfred", async (
    RacingResultRepository resultRepo, BetfredResultClient resultClient,
    RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var candidates = await resultRepo.GetCandidatesAsync(ct);
    resultClient.ResetPolling();
    var results = await resultClient.FindResultsAsync(candidates, ct);
    var republished = new List<long>();
    foreach (var result in results)
    {
        var snapshot = await resultRepo.SettleAsync(result, ct);
        if (snapshot is null) continue;
        await publisher.PublishAsync(snapshot, ct);
        await activity.WriteAsync("final-result",
            "Result revalidated and published from Betfred Winning Post.",
            snapshot.Event.SportId, result.EventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
        republished.Add(result.EventId);
    }
    return Results.Ok(new { candidates = candidates.Count, resultsFound = results.Count,
        republished = republished.Count, eventIds = republished });
});

app.MapPost("/api/admin/results/revalidate-betfred/{eventId:long}", async (
    long eventId, RacingResultRepository resultRepo, BetfredResultClient resultClient,
    RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var candidate = await resultRepo.GetCandidateAsync(eventId, ct);
    if (candidate is null) return Results.NotFound(new { eventId });
    resultClient.ResetPolling();
    var result = (await resultClient.FindResultsAsync([candidate], ct, requireReadiness: false)).SingleOrDefault();
    if (result is null) return Results.NotFound(new { eventId, message = "No Betfred result available." });
    var snapshot = await resultRepo.SettleAsync(result, ct)
        ?? await resultRepo.PromoteStoredResultAsync(result, ct);
    if (snapshot is null) return Results.Conflict(new { eventId, message = "Betfred result failed validation." });
    await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("final-result-corrected",
        "Stored result corrected and republished from Betfred Winning Post.",
        snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
    return Results.Ok(new { eventId, result = result.Runners.Select(x => new { x.Position, trap = x.Identifier, x.Name }) });
});

app.MapPost("/api/admin/results/revalidate-betsapi/{eventId:long}", async (
    long eventId, RacingResultRepository resultRepo, BetsApiResultClient resultClient,
    RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var candidate = await resultRepo.GetCandidateAsync(eventId, ct);
    if (candidate is null) return Results.NotFound(new { eventId });
    resultClient.ResetPolling(eventId);
    var result = (await resultClient.FindResultsAsync([candidate], ct)).SingleOrDefault();
    if (result is null) return Results.NotFound(new { eventId, message = "No complete BetsAPI result available." });
    var snapshot = await resultRepo.SettleAsync(result, ct)
        ?? await resultRepo.PromoteStoredResultAsync(result, ct);
    if (snapshot is null) return Results.Conflict(new { eventId, message = "BetsAPI result failed validation." });
    await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("final-result-corrected",
        "Stored result corrected and republished from the BetsAPI result feed.",
        snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
    return Results.Ok(new { eventId, result = result.Runners.Select(x => new { x.Position, trap = x.Identifier, x.Name }) });
});

app.MapPost("/api/admin/results/recompute-stored/{eventId:long}", async (
    long eventId, RacingResultRepository resultRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var snapshot = await resultRepo.RecomputeStoredResultAsync(eventId, ct);
    if (snapshot is null) return Results.NotFound(new { eventId, message = "No stored result required recomputation." });
    await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("final-result-corrected",
        "Stored result markets recomputed with the current settlement rules and republished.",
        snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
    return Results.Ok(new { eventId, republished = true });
});

app.MapPost("/api/admin/results/republish-stored-recent", async (
    RacingResultRepository resultRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var eventIds = await resultRepo.GetRecentStoredResultEventIdsAsync(ct);
    var republished = new List<long>();
    foreach (var eventId in eventIds)
    {
        var snapshot = await resultRepo.BuildStoredResultSnapshotAsync(eventId, ct);
        if (snapshot is null) continue;
        await publisher.PublishAsync(snapshot, ct);
        await activity.WriteAsync("result-republished",
            "Stored verified result event, markets and selections republished to Sportuno.",
            snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
        republished.Add(eventId);
    }
    return Results.Ok(new { candidates = eventIds.Count, republished = republished.Count, eventIds = republished });
});

app.MapPost("/api/admin/results/{eventId:long}/republish-stored", async (
    long eventId, RacingResultRepository resultRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var snapshot = await resultRepo.BuildStoredResultSnapshotAsync(eventId, ct);
    if (snapshot is null) return Results.NotFound(new { eventId, error = "No stored verified result." });
    await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("result-republished",
        "Stored verified result event, markets and selections republished to Sportuno.",
        snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
    return Results.Ok(new { eventId, republished = true });
});

app.MapPost("/api/admin/results/{eventId:long}/republish-provisional", async (
    long eventId, RacingResultRepository resultRepo, RacingPublisher publisher,
    RacingActivityLog activity, CancellationToken ct) =>
{
    var snapshot = await resultRepo.BuildStoredProvisionalSnapshotAsync(eventId, ct);
    if (snapshot is null) return Results.NotFound(new { eventId, error = "No stored provisional result." });
    await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("provisional-result-republished",
        "Stored known result markets and selections republished to Sportuno; unknown placings remain unset.",
        snapshot.Event.SportId, eventId, snapshot.Event.Meeting, snapshot.Event.Name, ct);
    return Results.Ok(new { eventId, status = snapshot.Event.Status, markets = snapshot.Markets.Count, republished = true });
});

app.MapPost("/api/admin/results/{eventId:long}/revert-false", async (
    long eventId, RacingResultRepository resultRepo, RacingFeedRepository feedRepo,
    RacingPublisher publisher, RacingActivityLog activity, CancellationToken ct) =>
{
    var racingEvent = await resultRepo.RevertFalseResultToSuspendedAsync(eventId, ct);
    if (racingEvent is null) return Results.NotFound();
    // Repair the externally visible event first; rebuilding the large child
    // payload must not delay removal of a false result in Sportuno.
    await publisher.PublishEventsAsync([racingEvent with { Status = "2" }], ct);
    var snapshot = await feedRepo.SuspendRaceAsync(racingEvent, ct);
    if (snapshot is not null) await publisher.PublishAsync(snapshot, ct);
    await activity.WriteAsync("result-reverted",
        "Incorrect result removed; event restored and republished as suspended.",
        racingEvent.SportId, eventId, racingEvent.Meeting, racingEvent.Name, ct);
    return Results.Ok(new { eventId, status = "2", republished = snapshot is not null });
});

app.MapPost("/api/admin/hide-synthetic-favourites", async (
    RacingFeedRepository feedRepo, CancellationToken ct) =>
    Results.Ok(new { hidden = await feedRepo.HideSyntheticFavouriteSelectionsAsync(ct) }));

app.MapPost("/api/admin/suppress-implausible-horse-events", async (
    RacingFeedRepository feedRepo, RacingPublisher publisher, CancellationToken ct) =>
{
    var events = await feedRepo.SuppressImplausibleHorseEventsAsync(ct);
    foreach (var ev in events) await publisher.PublishEventsAsync([ev], ct);
    return Results.Ok(events.Select(x => new { x.EventId, x.Meeting, x.Name }));
});

app.MapPut("/api/meetings/{sportId:int}/{meeting}/subscription", async (
    int sportId, string meeting, SubscriptionRequest request, RacingRepository repo,
    RacingActivityLog activity, RaceAuditTrail audit, CancellationToken ct) =>
{
    if (sportId is not (2 or 4)) return Results.BadRequest("sportId must be 2 or 4.");
    await repo.SetMeetingSubscriptionAsync(sportId, meeting, request.Subscribed, ct);
    await activity.WriteAsync("subscription", $"{meeting} {(request.Subscribed ? "subscribed" : "unsubscribed")} manually.",
        sportId, meeting: meeting, cancellationToken: ct);
    foreach (var racingEvent in await repo.GetMeetingEventsAsync(sportId, meeting, false, ct))
        await audit.WriteAsync(request.Subscribed ? "trader-subscription-enabled" : "trader-subscription-disabled",
            racingEvent.EventId, racingEvent.SportId, racingEvent.Meeting, racingEvent.Name,
            racingEvent.StartTimeUnix, "RACINGTRADER", "manual meeting subscription",
            $"Meeting subscription {(request.Subscribed ? "enabled" : "disabled")} by the user.",
            cancellationToken: ct);
    return Results.NoContent();
});

app.MapGet("/api/events/{eventId:long}", async (
    long eventId, RacingRepository repo, BetAhoyFeed betAhoy, TimeformGreyhoundForecastService timeform, CancellationToken ct) =>
{
    var board = await repo.GetEventBoardAsync(eventId, ct);
    return board is null ? Results.NotFound() : Results.Ok(board with
    {
        Runners = AddBetAhoyQuotes(board.Event, timeform.Enrich(board.Event, board.Runners), betAhoy)
    });
});
static IReadOnlyList<RunnerRow> AddBetAhoyQuotes(EventSummary race, IReadOnlyList<RunnerRow> runners, BetAhoyFeed feed)
    => runners.Select(runner =>
    {
        var quote = runner.Withdrawn ? null : feed.GetQuote(race.Meeting, race.StartTimeUnix, runner.Name);
        return runner with { BetAhoyPrice = quote?.Price, BetAhoyUpdatedAtUtc = quote?.ObservedAtUtc };
    }).ToArray();

app.MapGet("/api/timeform/forecast-health", (TimeformGreyhoundForecastService timeform) =>
    Results.Ok(timeform.Health()));
app.MapPost("/api/admin/timeform/refresh", async (
    TimeformGreyhoundForecastService timeform, CancellationToken ct) =>
{
    await timeform.RefreshAsync(ct);
    return Results.Ok(timeform.Health());
});

app.MapGet("/api/events/{eventId:long}/feed", async (
    long eventId, bool? raw, RacingFeedClient feed, CancellationToken ct) =>
{
    var payload = await feed.GetFeedResponseAsync(eventId, raw ?? true, ct);
    return Results.Text(payload, "application/json");
});

app.MapGet("/api/events/{eventId:long}/trap-history", async (
    long eventId, RacingFeedRepository feedRepository, CancellationToken ct) =>
    Results.Ok(await feedRepository.GetTrapHistoryAsync(eventId, ct)));

app.MapGet("/api/greyhound-line-market-guide", (RacingMarketCalculator calculator) =>
    Results.Ok(calculator.BuildEvenGreyhoundLineGuide()));

app.MapGet("/api/events/{eventId:long}/feed/direct", (long eventId, ExternalSettings settings) =>
{
    var url = $"https://api.b365api.com/v3/bet365/prematch?token={Uri.EscapeDataString(settings.B365ApiToken)}&FI={eventId}";
    return Results.Redirect(url);
});

app.MapGet("/api/events/{eventId:long}/market-configuration", async (
    long eventId, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.GetEventMarketConfigurationAsync(eventId, ct)));

app.MapPut("/api/events/{eventId:long}/greyhound-line-markets", async (
    long eventId, GreyhoundLineMarketRequest request, RacingFeedRepository feedRepository,
    CancellationToken ct) =>
{
    await feedRepository.SetGreyhoundLineMarketsAsync(eventId, request.MultiTrapsEnabled,
        request.TripleTrapsEnabled, ct);
    return Results.NoContent();
});

app.MapPut("/api/events/{eventId:long}/market-configuration", async (
    long eventId, UpdateEventMarketsRequest request, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.SaveEventMarketConfigurationAsync(eventId, request, ct)));

app.MapPost("/api/events/{eventId:long}/match-bets", async (
    long eventId, CreateMatchBetRequest request, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.AddMatchBetPairAsync(eventId, request, ct)));

app.MapPost("/api/events/{eventId:long}/forecasts", async (
    long eventId, CreateForecastRequest request, RacingRepository repo,
    RacingFeedRepository feedRepo, RacingPublisher publisher, CancellationToken ct) =>
{
    var forecast = await repo.AddForecastPairAsync(eventId, request, ct);
    var snapshot = await feedRepo.BuildConfiguredMarketForPublishingAsync(
        eventId, forecast.MarketSuffix, ct);
    if (snapshot is not null) await publisher.PublishAsync(snapshot, ct);
    return Results.Ok(forecast);
});

app.MapPost("/api/events/{eventId:long}/betting-without", async (
    long eventId, CreateBettingWithoutRequest request, RacingRepository repo, CancellationToken ct) =>
    Results.Ok(await repo.AddBettingWithoutMarketAsync(eventId, request, ct)));

app.MapDelete("/api/events/{eventId:long}/betting-without/{id:long}", async (
    long eventId, long id, RacingRepository repo, CancellationToken ct) =>
{
    await repo.DeleteBettingWithoutMarketAsync(eventId, id, ct);
    return Results.NoContent();
});

app.MapDelete("/api/events/{eventId:long}/match-bets/{pairId:long}", async (
    long eventId, long pairId, RacingRepository repo, CancellationToken ct) =>
{
    await repo.DeleteMatchBetPairAsync(eventId, pairId, ct);
    return Results.NoContent();
});

app.MapDelete("/api/events/{eventId:long}/forecasts/{pairId:long}", async (
    long eventId, long pairId, RacingRepository repo, CancellationToken ct) =>
{
    await repo.DeleteForecastPairAsync(eventId, pairId, ct);
    return Results.NoContent();
});

app.MapPost("/api/events/{eventId:long}/runners/{selectionId:long}/withdraw", async (
    long eventId, long selectionId, WithdrawalRequest request, RacingRepository repo, RacingActivityLog activity, CancellationToken ct) =>
{
    var receipt = await repo.WithdrawHorseAsync(eventId, selectionId, request.Reason, "WEB", ct);
    var details = string.Join("; ", receipt.MarketPrices?.Select(item =>
        $"{item.MarketName}: {item.LastPrice:0.00} => {item.Deduction:P0}") ?? []);
    // 4 (greyhounds): this app only ever withdraws dogs, and the hardcoded 2 filed every
    // Rule 4 entry under horse racing in the activity log.
    await activity.WriteAsync("rule4",
        $"{receipt.RunnerName}: winner {receipt.PriceAtAction:0.00}, R4 {receipt.Rule4Deduction:P0}||{details}",
        4, eventId, eventName: receipt.RunnerName, cancellationToken: ct);
    return Results.Ok(receipt);
});

app.MapPost("/api/events/{eventId:long}/runners/{selectionId:long}/replace", async (
    long eventId, long selectionId, ReplacementRequest request, RacingRepository repo, RacingActivityLog activity, CancellationToken ct) =>
{
    var receipt = await repo.ReplaceGreyhoundAsync(eventId, selectionId, request.Name, request.Price, ct);
    await activity.WriteAsync("replacement", $"{receipt.RunnerName} replaced by {receipt.ReplacementName}.",
        4, eventId, eventName: receipt.ReplacementName, cancellationToken: ct);
    return Results.Ok(receipt);
});

app.MapFallbackToFile("index.html");
app.Run();

static SocketsHttpHandler CreateIpv4Handler() => new()
{
    ConnectTimeout = TimeSpan.FromSeconds(3),
    PooledConnectionLifetime = TimeSpan.FromSeconds(30),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(10),
    ConnectCallback = async (context, cancellationToken) =>
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }
};
