using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RacingTrader.Web.Services;

internal sealed class Bet365CaptureBridge(
    IHttpClientFactory clients,
    RaceAuditTrail audit,
    ILogger<Bet365CaptureBridge> logger) : BackgroundService
{
    private const string BaseUrl = "http://127.0.0.1:5378";
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<long, FeedEvent> _events = new();
    private readonly ConcurrentDictionary<long, FeedEvent> _eventIdentities = new();
    private readonly ConcurrentDictionary<long, FeedPriceSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<long, byte> _changedEventIds = new();
    private readonly ConcurrentQueue<long> _priorityEventIds = new();
    private readonly ConcurrentDictionary<long, DateTime> _raceOffEvents = new();
    private readonly ConcurrentDictionary<long, byte> _auditedSnapshots = new();
    private DateTime _nextDiscoveryUtc = DateTime.MinValue;
    private DateTime _lastFailureAuditUtc = DateTime.MinValue;

    public IReadOnlyList<FeedEvent> Events => _events.Values.ToList();
    public bool HasEvents => !_events.IsEmpty;
    public void RequestFullRefresh() => _nextDiscoveryUtc = DateTime.MinValue;
    public IReadOnlyList<long> DrainChangedEventIds()
    {
        var changed = new List<long>();
        while (_priorityEventIds.TryDequeue(out var priorityEventId))
            if (_changedEventIds.TryRemove(priorityEventId, out _)) changed.Add(priorityEventId);
        foreach (var eventId in _changedEventIds.Keys)
            if (_changedEventIds.TryRemove(eventId, out _)) changed.Add(eventId);
        return changed.Distinct().ToList();
    }
    public void Prioritize(IEnumerable<long> eventIds)
    {
        foreach (var eventId in eventIds.Where(id => id > 0).Distinct())
        {
            _changedEventIds[eventId] = 0;
            _priorityEventIds.Enqueue(eventId);
        }
    }
    public FeedPriceSnapshot? Snapshot(long eventId) => _snapshots.GetValueOrDefault(eventId);
    public bool IsRaceOff(long eventId) => _raceOffEvents.ContainsKey(eventId);
    public async Task RefreshCapturedCardsAsync(CancellationToken cancellationToken)
    {
        var http = clients.CreateClient("Bet365Capture");
        await RefreshSnapshotsAsync(http, cancellationToken);
    }
    public async Task<JsonNode?> WalkMeetingAsync(string meeting, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient("Bet365Capture");
        var meetings = JsonNode.Parse(await http.GetStringAsync(BaseUrl + "/api/racing/meetings", cancellationToken)) as JsonArray;
        var target = meetings?.OfType<JsonObject>().FirstOrDefault(item =>
            BetfairMarketMatcher.NormalizeMeeting(Text(item["name"])) ==
            BetfairMarketMatcher.NormalizeMeeting(meeting));
        var meetingId = Text(target?["meetingId"]);
        if (meetingId.Length == 0)
            throw new InvalidOperationException($"{meeting} was not found in the Bet365 greyhound meetings.");
        using var response = await http.PostAsync(
            BaseUrl + "/api/racing/meetings/" + Uri.EscapeDataString(meetingId) + "/walk", null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        await RefreshSnapshotsAsync(http, cancellationToken);
        _nextDiscoveryUtc = DateTime.MinValue;
        return JsonNode.Parse(body);
    }
    public FeedPriceSnapshot? Snapshot(PricingEvent racingEvent)
    {
        if (_eventIdentities.TryGetValue(racingEvent.EventId, out var exact) && IsSameRace(exact, racingEvent))
            return Snapshot(racingEvent.EventId);
        var providerEvent = MatchProviderEvent(racingEvent);
        return providerEvent is null ? null : Snapshot(providerEvent.EventId);
    }
    public bool IsRaceOff(PricingEvent racingEvent)
    {
        if (_eventIdentities.TryGetValue(racingEvent.EventId, out var exact) &&
            IsSameRace(exact, racingEvent) && IsRaceOff(racingEvent.EventId))
            return true;
        var providerEvent = MatchProviderEvent(racingEvent);
        return providerEvent is not null && IsRaceOff(providerEvent.EventId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var http = clients.CreateClient("Bet365Capture");
                if (DateTime.UtcNow >= _nextDiscoveryUtc)
                {
                    await DiscoverAndSubscribeAsync(http, stoppingToken);
                    _nextDiscoveryUtc = DateTime.UtcNow + DiscoveryInterval;
                }
                await RefreshSnapshotsAsync(http, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bet365 browser bridge cycle failed.");
                if (DateTime.UtcNow - _lastFailureAuditUtc >= TimeSpan.FromMinutes(1))
                {
                    _lastFailureAuditUtc = DateTime.UtcNow;
                    foreach (var race in _events.Values)
                        await audit.WriteAsync("subscription-loss-detected", race.EventId, race.SportId,
                            race.Meeting, race.Name, race.StartTimeUnix, "BET365-BROWSER",
                            "capture bridge polling failure", ex.Message,
                            new { exception = ex.GetType().Name }, stoppingToken);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task DiscoverAndSubscribeAsync(HttpClient http, CancellationToken ct)
    {
        // Import the cards already held by the browser service before attempting
        // its comparatively slow discovery refresh. A stuck /refresh must never
        // prevent SP-only runner cards from reaching Greyhound Trader.
        await RefreshSnapshotsAsync(http, ct);

        try
        {
            using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            refreshTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await http.PostAsync(BaseUrl + "/api/racing/refresh", null, refreshTimeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Bet365 racing discovery refresh timed out; continuing with already captured cards.");
        }

        await RefreshSnapshotsAsync(http, ct);

        var subscribedRoot = JsonNode.Parse(
            await http.GetStringAsync(BaseUrl + "/api/racing/subscribed", ct)) as JsonArray;
        var subscribedIds = subscribedRoot?.OfType<JsonObject>()
            .Select(x => Text(x["meetingId"]))
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var meetings = JsonNode.Parse(
            await http.GetStringAsync(BaseUrl + "/api/racing/meetings", ct)) as JsonArray;
        foreach (var meeting in meetings?.OfType<JsonObject>() ?? [])
        {
            if (!Text(meeting["region"]).Equals("UK & Ireland", StringComparison.OrdinalIgnoreCase)) continue;
            var eligible = (meeting["races"] as JsonArray)?.OfType<JsonObject>()
                .Any(r => InTradingWindow(Long(r["startTimeUnix"]))) == true;
            if (!eligible) continue;

            var id = Text(meeting["meetingId"]);
            if (id.Length == 0 || subscribedIds.Contains(id)) continue;
            await http.PostAsync(
                BaseUrl + "/api/racing/meetings/" + Uri.EscapeDataString(id) + "/subscribe", null, ct);
            subscribedIds.Add(id);
            await RefreshSnapshotsAsync(http, ct);
            foreach (var race in (meeting["races"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var scheduled = Long(race["startTimeUnix"]);
                if (!InTradingWindow(scheduled)) continue;
                await audit.WriteAsync("subscription-requested", Long(race["eventId"]), 4,
                    Text(meeting["name"]), Text(race["name"]), scheduled, "BET365-BROWSER",
                    "meeting WebSocket subscription", "Bet365 meeting subscription requested.",
                    new { meetingId = id }, ct);
            }
        }
    }

    private async Task RefreshSnapshotsAsync(HttpClient http, CancellationToken ct)
    {
        var inPlayRoot = JsonNode.Parse(
            await http.GetStringAsync(BaseUrl + "/api/racing/inplay", ct)) as JsonArray;
        foreach (var item in inPlayRoot?.OfType<JsonObject>() ?? [])
        {
            var inPlayEventId = Long(item["eventId"]);
            if (inPlayEventId > 0 && Bool(item["isRaceOff"]))
                _raceOffEvents[inPlayEventId] = DateTime.UtcNow;
        }

        var root = JsonNode.Parse(await http.GetStringAsync(BaseUrl + "/api/racing/subscribed", ct)) as JsonArray;
        var seenEventIds = new HashSet<long>();
        foreach (var meeting in root?.OfType<JsonObject>() ?? [])
        foreach (var race in (meeting["races"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var eventId = Long(race["eventId"]);
            if (eventId <= 0) continue;
            var scheduled = ParseBet365Time(Text(race["scheduledStart"]));
            if (scheduled <= 0) continue;
            seenEventIds.Add(eventId);
            var meetingName = Text(race["meeting"]);
            var runners = (race["selections"] as JsonArray)?.OfType<JsonObject>()
                .Where(x => !Text(x["name"]).Contains("Favourite", StringComparison.OrdinalIgnoreCase))
                // Greyhound selections currently carry trap numbers but no Bet365
                // silk id. The trap number is the stable identifier required here.
                .Where(x => Long(x["draw"]) is >= 1 and <= 6)
                .GroupBy(x => BetfairMarketMatcher.NormalizeRunner(Text(x["name"])))
                .Where(x => x.Key.Length > 0)
                .Select(x => x.First())
                .Select(x => new FeedRunner(
                    Long(x["selectionId"]), Text(x["name"]), (int)Long(x["draw"]), Text(x["price"]),
                    Double(x["decimalPrice"]), Double(x["decimalPrice"]) > 1,
                    Bool(x["nonRunner"]), Text(x["silkId"]), x.ToJsonString(),
                    SourceUpdatedAtUtc: Date(x["lastUpdatedUtc"]))).ToList() ?? [];
            if (runners.Count < 2) continue;
            var previous = _snapshots.GetValueOrDefault(eventId);
            var updated = runners.Select(x => x.SourceUpdatedAtUtc).Where(x => x.HasValue)
                .Select(x => x!.Value).DefaultIfEmpty().Max();
            var changed = previous is null || string.Join('|', previous.Runners.Select(x => $"{x.CompetitorId}:{x.Price:0.####}:{x.ExplicitNonRunner}")) !=
                string.Join('|', runners.Select(x => $"{x.CompetitorId}:{x.Price:0.####}:{x.ExplicitNonRunner}"));
            if (updated == default) updated = changed ? DateTime.UtcNow : previous?.UpdatedAtUtc ?? DateTime.UtcNow;
            var timeStatus = IsRaceOff(eventId) ? 1 : 0;
            _events[eventId] = new FeedEvent(eventId, 4, 0, meetingName,
                RacingEventNames.Canonical(meetingName, scheduled), scheduled, timeStatus,
                new DateTimeOffset(updated).ToUnixTimeSeconds(), new DateTimeOffset(updated).ToUnixTimeSeconds(),
                race.ToJsonString());
            _eventIdentities[eventId] = _events[eventId];
            _snapshots[eventId] = new FeedPriceSnapshot("BET365_DIRECT", updated, DateTime.UtcNow, runners,
                runners.Where(x => x.ExplicitNonRunner)
                    .Select(x => new FeedNonRunner(x.Name, x.CompetitorId, x.Identifier, "Bet365 browser non-runner", x.RawJson)).ToList());
            if (_auditedSnapshots.TryAdd(eventId, 0))
                await audit.WriteAsync("subscription-initialised", eventId, 4, meetingName,
                    _events[eventId].Name, scheduled, "BET365-BROWSER",
                    "subscribed full-race snapshot",
                    "First complete Bet365 browser runner snapshot received.",
                    new { runnerCount = runners.Count, updated }, ct);
            // A newly completed direct book must be priced immediately as well as later
            // websocket changes. Meetings are imported after each subscription, keeping
            // this priority batch bounded to the newly available card.
            if (changed) _changedEventIds[eventId] = 0;
        }
        foreach (var eventId in _events.Keys.Where(id => !seenEventIds.Contains(id)).ToList())
        {
            _events.TryRemove(eventId, out _);
            if (!_raceOffEvents.ContainsKey(eventId)) _snapshots.TryRemove(eventId, out _);
        }
        logger.LogInformation("Bet365 browser bridge holds {EventCount} races and {SnapshotCount} price snapshots.",
            _events.Count, _snapshots.Count);
    }

    private FeedEvent? MatchProviderEvent(PricingEvent racingEvent) =>
        _events.Values
            .Where(x => IsSameRace(x, racingEvent))
            .OrderBy(x => Math.Abs(x.StartTimeUnix - racingEvent.StartTimeUnix))
            .FirstOrDefault();

    private static bool IsSameRace(FeedEvent providerEvent, PricingEvent racingEvent) =>
        providerEvent.SportId == racingEvent.SportId &&
        Math.Abs(providerEvent.StartTimeUnix - racingEvent.StartTimeUnix) <= 90 &&
        BetfairMarketMatcher.NormalizeMeeting(providerEvent.Meeting) ==
        BetfairMarketMatcher.NormalizeMeeting(racingEvent.Meeting);

    private static bool InTradingWindow(long unix)
    {
        if (unix <= 0) return false;
        var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.FromUnixTimeSeconds(unix), "GMT Standard Time");
        var today = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "GMT Standard Time").Date;
        return local.Date >= today && local.Date <= today.AddDays(1) &&
               local.TimeOfDay >= TimeSpan.FromHours(11) && local.TimeOfDay <= TimeSpan.FromHours(22);
    }
    private static long ParseBet365Time(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local)) return 0;
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"));
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }
    private static string Text(JsonNode? n) => n?.ToString() ?? "";
    private static long Long(JsonNode? n) => long.TryParse(Text(n), out var x) ? x : 0;
    private static double Double(JsonNode? n) => double.TryParse(Text(n), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : 0;
    private static bool Bool(JsonNode? n) => bool.TryParse(Text(n), out var x) && x;
    private static DateTime? Date(JsonNode? n) => DateTime.TryParse(Text(n), null, DateTimeStyles.RoundtripKind, out var x) ? x.ToUniversalTime() : null;
}
