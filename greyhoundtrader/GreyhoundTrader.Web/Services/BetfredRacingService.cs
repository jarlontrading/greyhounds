using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RacingTrader.Web.Services;

internal sealed record BetfredSubscriptionStatus(
    bool BrowserConnected,
    string ManagedBrowserId,
    string BrowserProfile,
    int DiscoveredEvents,
    int ParsedRaces,
    int MatchedRaces,
    long FeedRequests,
    long Snapshots,
    long PriceUpdates,
    long HttpErrors,
    double RequestsPerMinute,
    double? AverageRequestIntervalSeconds,
    DateTime? LastDiscoveryUtc,
    DateTime? LastRequestUtc,
    DateTime? LastResponseUtc,
    DateTime? LastSnapshotUtc,
    DateTime? LastPriceUtc,
    long? LastResponseEventId,
    string PollingStrategy,
    DateTime? LastCouponRefreshUtc,
    long SocketDeltaWatermark,
    long SocketDeltasReceived,
    long SocketDeltasApplied,
    long SocketDeltasIgnored,
    long SocketDeltasUnmapped,
    DateTime? LastSocketDeltaUtc,
    string? LastError);

internal sealed class BetfredRacingService(
    RacingFeedRepository repository,
    R2SilkStorage? silkStorage,
    RacingDiagnosticCapture diagnostics,
    RaceAuditTrail audit,
    IHttpClientFactory httpClientFactory,
    ILogger<BetfredRacingService> logger) : BackgroundService
{
    private const string ManagedBrowserId = "GreyhoundTrader/Betfred";
    private const string BrowserProfile = @"D:\Temp\greyhoundtrader\betfred-pricing-browser-profile";
    private const string BridgeBaseUrl = "http://127.0.0.1:5278";
    private const string Gateway = "https://content-presentation-gateway.betfred.com";
    private const string RacingInfoUrl = Gateway + "/sport/70/racing-next-events/6?language_code=en&franchise_id=3&tz_offset=60";
    private const int MaximumRequestsPerPass = 6;
    private static readonly TimeZoneInfo UkTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly Regex EventLinkRegex = new(
        @"/sports/greyhound-racing/event/(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<long, byte> _eventIds = new();
    private readonly ConcurrentDictionary<long, BetfredRaceMetadata> _metadata = new();
    private readonly ConcurrentDictionary<long, BetfredRace> _races = new();
    private readonly ConcurrentDictionary<long, long> _lastPollTicks = new();
    private readonly ConcurrentDictionary<long, long> _lastSocketDeltaTicksByEvent = new();
    private readonly ConcurrentDictionary<long, string> _storedHashes = new();
    private readonly ConcurrentDictionary<long, byte> _auditedMatches = new();
    private readonly ConcurrentDictionary<long, string> _raceOffSignals = new();
    private readonly ConcurrentDictionary<long, byte> _auditedOffSignals = new();
    private readonly ConcurrentQueue<long> _requestTimes = new();
    private volatile bool _bridgeConnected;
    private long _feedRequests;
    private long _snapshots;
    private long _priceUpdates;
    private long _httpErrors;
    private long _lastDiscoveryTicks;
    private long _lastRequestTicks;
    private long _lastResponseTicks;
    private long _lastSnapshotTicks;
    private long _lastPriceTicks;
    private long _lastResponseEventId;
    private double _averageRequestIntervalSeconds;
    private volatile string? _lastError;
    private volatile bool _geoRestricted;
    private DateOnly? _lastCouponRefreshUkDate;
    private DateTime _nextCouponRefreshAttemptUtc = DateTime.MinValue;
    private long _lastCouponRefreshTicks;
    private long _socketDeltaWatermark;
    private long _socketDeltasReceived;
    private long _socketDeltasApplied;
    private long _socketDeltasIgnored;
    private long _socketDeltasUnmapped;
    private long _lastSocketDeltaTicks;
    private readonly bool _dailyCouponRefreshEnabled = string.Equals(
        Environment.GetEnvironmentVariable("GREYHOUNDTRADER_BETFRED_DAILY_REFRESH_ENABLED"),
        "true", StringComparison.OrdinalIgnoreCase);

    internal BetfredOffRaceIdentity? GetOffRaceIdentity(long eventId)
    {
        if (_metadata.TryGetValue(eventId, out var metadata))
            return new(eventId, 4, metadata.Meeting, metadata.StartTimeUnix);
        if (_races.TryGetValue(eventId, out var race))
            return new(eventId, 4, race.Meeting, race.StartTimeUnix);
        return null;
    }

    public BetfredSubscriptionStatus Status => new(
        _bridgeConnected && !_geoRestricted,
        ManagedBrowserId, BrowserProfile,
        _eventIds.Count, _races.Count, _storedHashes.Count,
        Interlocked.Read(ref _feedRequests), Interlocked.Read(ref _snapshots),
        Interlocked.Read(ref _priceUpdates), Interlocked.Read(ref _httpErrors),
        RequestsPerMinute(),
        Volatile.Read(ref _averageRequestIntervalSeconds) is > 0 and var interval ? interval : null,
        TicksToUtc(Interlocked.Read(ref _lastDiscoveryTicks)),
        TicksToUtc(Interlocked.Read(ref _lastRequestTicks)),
        TicksToUtc(Interlocked.Read(ref _lastResponseTicks)),
        TicksToUtc(Interlocked.Read(ref _lastSnapshotTicks)),
        TicksToUtc(Interlocked.Read(ref _lastPriceTicks)),
        Interlocked.Read(ref _lastResponseEventId) is > 0 and var eventId ? eventId : null,
        "20m window: initial snapshot; 30s HTTP polling, tightening to 3s in final 5m; WebSocket odds push",
        TicksToUtc(Interlocked.Read(ref _lastCouponRefreshTicks)),
        Interlocked.Read(ref _socketDeltaWatermark),
        Interlocked.Read(ref _socketDeltasReceived),
        Interlocked.Read(ref _socketDeltasApplied),
        Interlocked.Read(ref _socketDeltasIgnored),
        Interlocked.Read(ref _socketDeltasUnmapped),
        TicksToUtc(Interlocked.Read(ref _lastSocketDeltaTicks)),
        _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Browser recovery must not hold port 5187 closed while Chrome starts.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartBridgeAsync(stoppingToken);
                var nextDiscovery = DateTime.MinValue;
                var nextRacingStatus = DateTime.MinValue;
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (await RefreshCouponForNewUkDayIfDueAsync(stoppingToken))
                    {
                        nextDiscovery = DateTime.MinValue;
                        nextRacingStatus = DateTime.MinValue;
                    }
                    await PollPageStateAsync(stoppingToken);
                    if (DateTime.UtcNow >= nextRacingStatus)
                    {
                        await PollRacingStatusesAsync(stoppingToken);
                        // This is only the next-races discovery/status snapshot. Odds arrive by
                        // WebSocket, with the per-event HTTP request acting as a 60-second fallback.
                        nextRacingStatus = DateTime.UtcNow.AddSeconds(15);
                    }
                    if (DateTime.UtcNow >= nextDiscovery || _eventIds.IsEmpty)
                    {
                        await DiscoverAsync(stoppingToken);
                        nextDiscovery = DateTime.UtcNow.AddMinutes(5);
                    }

                    var deltaUpdates = await DrainSocketDeltasAsync(stoppingToken);
                    var snapshots = await PollDueEventsAsync(stoppingToken);
                    if (snapshots > 0 || deltaUpdates > 0) await MatchAndStoreAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _bridgeConnected = false;
                logger.LogWarning(ex, "Betfred racing capture lost its browser bridge; retrying in five seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> DrainSocketDeltasAsync(CancellationToken ct)
    {
        try
        {
            var after = Interlocked.Read(ref _socketDeltaWatermark);
            var json = await GetBridgeTextAsync($"/api/betfred/socket-deltas?after={after}", ct);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null) return 0;
            var watermark = ReadLong(root, "watermark") ?? after;
            var deltas = root["deltas"] as JsonArray;
            var applied = 0;
            if (deltas is not null)
            {
                foreach (var node in deltas.OfType<JsonObject>().OrderBy(item => ReadLong(item, "sequence") ?? 0))
                {
                    Interlocked.Increment(ref _socketDeltasReceived);
                    var sequence = ReadLong(node, "sequence") ?? 0;
                    var entity = ReadString(node, "entity") ?? string.Empty;
                    var eventId = ReadLong(node, "betfredEventId") ?? 0;
                    if (!entity.Equals("Odds", StringComparison.OrdinalIgnoreCase))
                    {
                        // Event state is retained for diagnosis until each field has been
                        // proven against Betfred's visible Race is Running state.
                        if (entity.Equals("Events", StringComparison.OrdinalIgnoreCase))
                            diagnostics.Write("betfred-socket", "event-delta", new
                            {
                                sequence, betfredEventId = eventId,
                                isLive = ReadBool(node, "isLive"), ended = ReadBool(node, "ended"),
                                resultsEntered = ReadBool(node, "resultsEntered")
                            }, node.ToJsonString());
                        Interlocked.Increment(ref _socketDeltasIgnored);
                        continue;
                    }

                    var marketName = ReadString(node, "marketName");
                    var selectionName = ReadString(node, "selectionName");
                    if (eventId <= 0 || string.IsNullOrWhiteSpace(selectionName) || !IsWinnerMarket(marketName))
                    {
                        Interlocked.Increment(ref _socketDeltasUnmapped);
                        continue;
                    }
                    if (!_races.TryGetValue(eventId, out var race))
                    {
                        Interlocked.Increment(ref _socketDeltasUnmapped);
                        continue;
                    }

                    var normalized = BetfairMarketMatcher.NormalizeRunner(selectionName);
                    var runnerIndex = race.Runners.ToList().FindIndex(item =>
                        BetfairMarketMatcher.NormalizeRunner(item.Name) == normalized);
                    if (runnerIndex < 0)
                    {
                        Interlocked.Increment(ref _socketDeltasUnmapped);
                        continue;
                    }
                    var existing = race.Runners[runnerIndex];
                    var frozen = ReadBool(node, "frozen");
                    var enabled = ReadBool(node, "enabled");
                    var price = ReadDouble(node, "decimalPrice");
                    var effectivePrice = frozen == true || enabled == false ? null : price ?? existing.Price;
                    var runners = race.Runners.ToList();
                    runners[runnerIndex] = existing with { Price = effectivePrice, RawJson = node.ToJsonString() };
                    _races[eventId] = race with { Runners = runners };
                    _lastSocketDeltaTicksByEvent[eventId] = DateTime.UtcNow.Ticks;
                    applied++;
                    Interlocked.Increment(ref _socketDeltasApplied);
                    Interlocked.Exchange(ref _lastSocketDeltaTicks, DateTime.UtcNow.Ticks);
                    diagnostics.Write("betfred-socket", "winner-delta-applied", new
                    {
                        sequence, betfredEventId = eventId, selectionName,
                        previousPrice = existing.Price, price = effectivePrice, frozen, enabled
                    }, node.ToJsonString());
                }
            }
            Interlocked.Exchange(ref _socketDeltaWatermark, Math.Max(after, watermark));
            return applied;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or JsonException)
        {
            _lastError = $"Betfred socket delta drain: {ex.Message}";
            logger.LogDebug(ex, "Betfred socket delta drain failed; HTTP polling remains active.");
            return 0;
        }
    }

    private async Task<bool> RefreshCouponForNewUkDayIfDueAsync(CancellationToken ct)
    {
        // Kept behind an explicit release flag until the matching capture
        // endpoint is deployed.
        if (!_dailyCouponRefreshEnabled) return false;
        var utcNow = DateTime.UtcNow;
        var ukNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, UkTimeZone);
        var ukDate = DateOnly.FromDateTime(ukNow);
        if (ukNow.TimeOfDay < TimeSpan.FromHours(8) ||
            _lastCouponRefreshUkDate == ukDate ||
            utcNow < _nextCouponRefreshAttemptUtc)
            return false;

        try
        {
            var client = httpClientFactory.CreateClient("Bet365Capture");
            using var response = await client.PostAsync(
                BridgeBaseUrl + "/api/betfred/refresh-coupon", null, ct);
            response.EnsureSuccessStatusCode();

            _lastCouponRefreshUkDate = ukDate;
            _nextCouponRefreshAttemptUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _lastCouponRefreshTicks, DateTime.UtcNow.Ticks);

            // Drop yesterday's provider ids and state only after the browser
            // confirms a successful refresh. The immediate discovery/status
            // pass repopulates today's card.
            _eventIds.Clear();
            _metadata.Clear();
            _races.Clear();
        _lastPollTicks.Clear();
            _lastSocketDeltaTicksByEvent.Clear();
            _storedHashes.Clear();
            _raceOffSignals.Clear();
            _auditedMatches.Clear();
            _auditedOffSignals.Clear();
            diagnostics.Write("betfred-browser", "daily-coupon-refreshed", new
            {
                ukDate = ukDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                refreshedAtUtc = DateTime.UtcNow
            });
            logger.LogInformation("Refreshed the Betfred all-races coupon for UK date {UkDate}.", ukDate);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            _nextCouponRefreshAttemptUtc = utcNow.AddMinutes(5);
            _lastError = $"Daily Betfred coupon refresh failed: {ex.Message}";
            logger.LogWarning(ex, "Daily Betfred coupon refresh failed; retrying in five minutes.");
            return false;
        }
    }

    public bool TryGetRaceOffSignal(PricingEvent racingEvent, out string reason)
    {
        foreach (var signal in _raceOffSignals)
        {
            if (!_metadata.TryGetValue(signal.Key, out var metadata)) continue;
            if (Math.Abs(metadata.StartTimeUnix - racingEvent.StartTimeUnix) > 180) continue;
            if (RacingEventNames.MeetingKey(metadata.Meeting) != RacingEventNames.MeetingKey(racingEvent.Meeting)) continue;
            reason = signal.Value;
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private async Task PollPageStateAsync(CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("Bet365Capture");
            // Keep the labelled browser on Betfred's all-races coupon. The matching
            // racing-by-date response carries every event id and raceStatus, so there
            // is no need to navigate the visible tab through individual races.
            using var response = await client.GetAsync(BridgeBaseUrl + "/api/betfred/page-state", ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            _bridgeConnected = response.IsSuccessStatusCode;
            if (!response.IsSuccessStatusCode) return;
            var state = JsonNode.Parse(json) as JsonObject;
            if (state is null) return;
            var eventId = ReadLong(state, "eventId");
            var running = state?["raceRunning"]?.GetValue<bool>() ?? false;
            // An event id is normally absent on the coupon. Exact event attribution
            // is deliberately handled by PollRacingStatusesAsync (raceStatus=16).
            if (!running || eventId is null or <= 0) return;

            _raceOffSignals[eventId.Value] = $"Betfred page reported Race is Running (event {eventId.Value}).";
            diagnostics.Write("betfred-browser", "race-running", new
            {
                betfredEventId = eventId.Value,
                observedAtUtc = state?["observedAtUtc"]?.GetValue<string>(),
                statusTexts = state?["statusTexts"]?.ToJsonString()
            }, json);
            if (_auditedOffSignals.TryAdd(eventId.Value, 0))
                logger.LogInformation("Betfred page reported Race is Running for event {EventId}.", eventId.Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogDebug(ex, "Betfred page-state probe failed; event API polling continues.");
        }
    }

    private async Task PollRacingStatusesAsync(CancellationToken ct)
    {
        try
        {
            var json = await GetBridgePayloadAsync("/api/betfred/racing-info", null, ct);
            if (string.IsNullOrWhiteSpace(json)) return;
            _lastError = null;
            foreach (var metadata in ParseMetadata(json))
            {
                _metadata[metadata.EventId] = metadata;
                _eventIds.TryAdd(metadata.EventId, 0);
                if (metadata.RaceStatus != 16) continue;
                _raceOffSignals[metadata.EventId] =
                    $"Betfred racing status reported Race is Running (raceStatus=16, event {metadata.EventId}).";
                if (_auditedOffSignals.TryAdd(metadata.EventId, 0))
                    logger.LogInformation(
                        "Betfred racing status 16 reported Race is Running for {Meeting} event {EventId}.",
                        metadata.Meeting, metadata.EventId);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            logger.LogDebug(ex, "Betfred racing-status refresh failed; DOM and other suspension signals remain active.");
        }
    }

    private async Task StartBridgeAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("Bet365Capture");
        using var response = await client.GetAsync($"{BridgeBaseUrl}/api/betfred/status", ct);
        response.EnsureSuccessStatusCode();
        _bridgeConnected = true;
        _lastError = null;
        logger.LogInformation("Betfred capture is using the labelled Betfred tab in the managed browser context.");
    }

    private async Task DiscoverAsync(CancellationToken ct)
    {
        try
        {
            var discoveryJson = await GetBridgeTextAsync("/api/betfred/discovery", ct);
            var ids = EventLinkRegex.Matches(discoveryJson)
                .Select(match => long.TryParse(match.Groups["id"].Value, out var id) ? id : 0)
                .Where(id => id > 0).Distinct().ToList();
            foreach (var id in ids) _eventIds.TryAdd(id, 0);

            string? infoJson = null;
            try
            {
                infoJson = await GetBridgePayloadAsync("/api/betfred/racing-info", null, ct);
            }
            catch (InvalidOperationException ex)
            {
                diagnostics.Write("betfred-browser", "racing-info-unavailable",
                    new { error = ex.Message, idsFromLinks = ids.Count }, "");
            }
            if (!string.IsNullOrWhiteSpace(infoJson))
            {
                foreach (var item in ParseMetadata(infoJson))
                {
                    _metadata[item.EventId] = item;
                    _eventIds.TryAdd(item.EventId, 0);
                }
                diagnostics.Write("betfred-browser", "racing-info",
                    new { idsFromLinks = ids.Count, metadataEvents = _metadata.Count }, infoJson);
            }

            Interlocked.Exchange(ref _lastDiscoveryTicks, DateTime.UtcNow.Ticks);
            if (!_geoRestricted) _lastError = null;
            diagnostics.Write("betfred-browser", "discovery",
                new { eventCount = _eventIds.Count, metadataCount = _metadata.Count,
                    bridge = BridgeBaseUrl, responseLength = discoveryJson.Length },
                string.Join(',', _eventIds.Keys.OrderBy(id => id)));
            logger.LogInformation("Betfred discovered {Count} racing events; {MetadataCount} have dated metadata.",
                _eventIds.Count, _metadata.Count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            _lastError = ex.Message;
            logger.LogWarning(ex, "Betfred discovery failed; retained events will continue polling.");
        }
    }

    private async Task<int> PollDueEventsAsync(CancellationToken ct)
    {
        if (!_bridgeConnected || _geoRestricted) return 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nowTicks = DateTime.UtcNow.Ticks;
        var due = _eventIds.Keys
            .Where(id => IsDue(id, now, nowTicks))
            .OrderBy(id => _metadata.TryGetValue(id, out var metadata)
                ? Math.Abs(metadata.StartTimeUnix - now)
                : long.MaxValue)
            .Take(MaximumRequestsPerPass)
            .ToList();
        if (due.Count == 0) return 0;

        var results = await Task.WhenAll(due.Select(id => FetchEventAsync(id, ct)));
        return results.Count(value => value);
    }

    private bool IsDue(long eventId, long nowUnix, long nowTicks)
    {
        var lastTicks = _lastPollTicks.GetValueOrDefault(eventId);
        var elapsed = lastTicks <= 0 ? TimeSpan.MaxValue :
            new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(lastTicks, DateTimeKind.Utc);
        if (!_metadata.TryGetValue(eventId, out var metadata)) return false;
        var untilStart = metadata.StartTimeUnix - nowUnix;
        // Prices are only required close to the off. Keep a delayed race alive
        // until Betfred supplies an explicit lifecycle/OFF signal, but do not
        // spend event calls on the rest of the day's card.
        if (untilStart > 1200 || untilStart < -900 || _raceOffSignals.ContainsKey(eventId))
            return false;
        if (!_races.ContainsKey(eventId)) return true;
        var lastSocketTicks = _lastSocketDeltaTicksByEvent.GetValueOrDefault(eventId);
        var socketQuiet = lastSocketTicks <= 0 ? TimeSpan.MaxValue :
            new DateTime(nowTicks, DateTimeKind.Utc) - new DateTime(lastSocketTicks, DateTimeKind.Utc);
        // The fixed-price show often opens shortly before the scheduled off. Socket
        // deltas are preferred, but the bridge may not receive them on every session;
        // a one-minute fallback can therefore miss the whole show. Poll tightly in
        // the final five minutes so a complete Betfred book is stored and queued for
        // repricing while the race is still open.
        var interval = untilStart <= 300 && untilStart >= -30
            ? TimeSpan.FromSeconds(3)
            : TimeSpan.FromSeconds(30);
        return elapsed >= interval && socketQuiet >= interval;
    }

    private async Task<bool> FetchEventAsync(long eventId, CancellationToken ct)
    {
        _lastPollTicks[eventId] = DateTime.UtcNow.Ticks;
        var url = $"{Gateway}/event/{eventId.ToString(CultureInfo.InvariantCulture)}?language_code=en&franchise_id=3&tz_offset=60";
        try
        {
            var json = await GetBridgePayloadAsync($"/api/betfred/event/{eventId}", eventId, ct);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var metadata = _metadata.GetValueOrDefault(eventId);
            var parsed = ParseRace(eventId, json, metadata);
            if (parsed is null)
            {
                diagnostics.Write("betfred-browser", "unparsed-event",
                    new { eventId, metadataKnown = metadata is not null }, json);
                return false;
            }
            _metadata[eventId] = new(parsed.BetfredEventId, parsed.Meeting, parsed.StartTimeUnix);
            _races[eventId] = parsed;
            if (parsed.IsLive || parsed.ResultsEntered || parsed.Ended)
            {
                var lifecycle = parsed.IsLive ? "isLive=true" :
                    parsed.ResultsEntered ? "resultsEntered=true" : "ended=true";
                _raceOffSignals[eventId] =
                    $"Betfred event API reported terminal race lifecycle ({lifecycle}, event {eventId}).";
                if (_auditedOffSignals.TryAdd(eventId, 0))
                    logger.LogInformation(
                        "Betfred event API reported {Lifecycle} for {Meeting} event {EventId}.",
                        lifecycle, parsed.Meeting, eventId);
            }
            Interlocked.Increment(ref _snapshots);
            Interlocked.Exchange(ref _lastSnapshotTicks, DateTime.UtcNow.Ticks);
            diagnostics.Write("betfred-browser", "event-snapshot",
                new { betfredEventId = eventId, parsed.Meeting, parsed.StartTimeUnix, runners = parsed.Runners.Count }, json);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            Interlocked.Increment(ref _httpErrors);
            _lastError = ex.Message;
            logger.LogDebug(ex, "Betfred event API request {EventId} failed.", eventId);
            return false;
        }
    }

    private async Task<string> GetBridgeTextAsync(string path, CancellationToken ct)
    {
        RecordRequest();
        var client = httpClientFactory.CreateClient("Bet365Capture");
        using var response = await client.GetAsync(BridgeBaseUrl + path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        _bridgeConnected = response.IsSuccessStatusCode;
        Interlocked.Exchange(ref _lastResponseTicks, DateTime.UtcNow.Ticks);
        if (!response.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref _httpErrors);
            throw new InvalidOperationException($"Betfred bridge returned HTTP {(int)response.StatusCode}: {text}");
        }
        return text;
    }

    private async Task<string?> GetBridgePayloadAsync(string path, long? eventId, CancellationToken ct)
    {
        var envelopeText = await GetBridgeTextAsync(path, ct);
        var envelope = JsonNode.Parse(envelopeText) as JsonObject;
        var ok = envelope?["ok"]?.GetValue<bool>() ?? false;
        var status = envelope?["status"]?.GetValue<int>() ?? 0;
        var body = envelope?["body"]?.GetValue<string>();
        if (!ok) throw new InvalidOperationException($"Betfred browser API returned HTTP {status}.");
        if (eventId is > 0) Interlocked.Exchange(ref _lastResponseEventId, eventId.Value);
        return body;
    }

    private void RecordRequest()
    {
        var now = DateTime.UtcNow;
        var previous = Interlocked.Exchange(ref _lastRequestTicks, now.Ticks);
        Interlocked.Increment(ref _feedRequests);
        _requestTimes.Enqueue(now.Ticks);
        PruneRequestTimes(now.Ticks);
        if (previous <= 0) return;
        var seconds = Math.Max(.01, (now - new DateTime(previous, DateTimeKind.Utc)).TotalSeconds);
        var current = Volatile.Read(ref _averageRequestIntervalSeconds);
        Volatile.Write(ref _averageRequestIntervalSeconds, current <= 0 ? seconds : current * .9 + seconds * .1);
    }

    private double RequestsPerMinute()
    {
        var now = DateTime.UtcNow.Ticks;
        PruneRequestTimes(now);
        return _requestTimes.Count;
    }

    private void PruneRequestTimes(long nowTicks)
    {
        var cutoff = nowTicks - TimeSpan.FromMinutes(1).Ticks;
        while (_requestTimes.TryPeek(out var ticks) && ticks < cutoff) _requestTimes.TryDequeue(out _);
    }

    private async Task MatchAndStoreAsync(CancellationToken ct)
    {
        var events = await repository.GetDisplayProviderEventsAsync(ct);
        foreach (var racingEvent in events)
        {
            var candidates = _races.Values
                .Where(race => Math.Abs(race.StartTimeUnix - racingEvent.StartTimeUnix) <= 180)
                .Where(race => RacingEventNames.MeetingKey(race.Meeting) == RacingEventNames.MeetingKey(racingEvent.Meeting))
                .ToList();
            if (candidates.Count == 0) continue;
            var providerRunners = await repository.GetRaceWinnerRunnersForProviderAsync(racingEvent.EventId, ct);
            if (providerRunners.Count < 2) continue;
            var match = candidates.Select(race => new
                {
                    Race = race,
                    Fingerprint = RaceRunnerFingerprint.Compare(
                        providerRunners.Select(runner => runner.Name), race.Runners.Select(runner => runner.Name))
                })
                .Where(candidate => candidate.Fingerprint.IsMatch)
                .OrderByDescending(candidate => candidate.Fingerprint.Coverage)
                .ThenBy(candidate => Math.Abs(candidate.Race.StartTimeUnix - racingEvent.StartTimeUnix))
                .FirstOrDefault();
            if (match is null) continue;

            var pricesByName = match.Race.Runners.ToDictionary(
                runner => BetfairMarketMatcher.NormalizeRunner(runner.Name), StringComparer.Ordinal);
            var mapped = providerRunners.Select(runner =>
            {
                pricesByName.TryGetValue(BetfairMarketMatcher.NormalizeRunner(runner.Name), out var quote);
                return (Runner: runner, Price: quote?.Price, SilkUrl: quote?.SilkUrl,
                    RawJson: quote?.RawJson ?? "{}");
            }).ToList();
            var hash = string.Join('|', mapped.OrderBy(item => item.Runner.CompetitorId)
                .Select(item => $"{item.Runner.CompetitorId}:{item.Price?.ToString("0.########", CultureInfo.InvariantCulture) ?? "-"}:{item.SilkUrl ?? "-"}"));
            if (_storedHashes.TryGetValue(racingEvent.EventId, out var previous) && previous == hash) continue;

            var updated = DateTime.UtcNow;
            await repository.StoreDisplayProviderPricesAsync(racingEvent, "BETFRED",
                mapped.Select(item => (item.Runner, item.Price, item.RawJson)).ToList(), updated, ct);
            await repository.QueueEventRepriceAsync(racingEvent.EventId, ct);
            var cachedSilks = new List<(long CompetitorId, string AssetUrl)>();
            foreach (var item in mapped.Where(item => racingEvent.SportId == 2 && !string.IsNullOrWhiteSpace(item.SilkUrl)))
            {
                var publicUrl = silkStorage is null ? null : await silkStorage.CacheBetfredSilkAsync(
                    racingEvent.EventId, item.Runner.CompetitorId, item.SilkUrl!, ct);
                if (!string.IsNullOrWhiteSpace(publicUrl))
                    cachedSilks.Add((item.Runner.CompetitorId, publicUrl));
            }
            if (await repository.StorePreferredProviderSilksAsync(racingEvent.EventId, cachedSilks, ct))
                await repository.QueueEventRepriceAsync(racingEvent.EventId, ct);
            _storedHashes[racingEvent.EventId] = hash;
            Interlocked.Increment(ref _priceUpdates);
            Interlocked.Exchange(ref _lastPriceTicks, updated.Ticks);
            if (_auditedMatches.TryAdd(racingEvent.EventId, 0))
                await audit.WriteAsync("provider-match", racingEvent.EventId, racingEvent.SportId,
                    racingEvent.Meeting, racingEvent.Name, racingEvent.StartTimeUnix, "BETFRED",
                    "date+meeting+off-time+runner-fingerprint",
                    "Betfred greyhound event linked for show pricing and primary OFF.",
                    new { match.Race.BetfredEventId, match.Fingerprint }, ct);
            await audit.WriteAsync("price-update", racingEvent.EventId, racingEvent.SportId,
                racingEvent.Meeting, racingEvent.Name, racingEvent.StartTimeUnix, "BETFRED",
                "managed browser event API", "Betfred Winner prices stored for complete-show-book selection.",
                new { priced = mapped.Count(item => item.Price is > 1), runners = mapped.Count }, ct);
        }
    }

    private static IReadOnlyList<BetfredRaceMetadata> ParseMetadata(string json)
    {
        var root = JsonNode.Parse(json);
        if (root is null) return [];
        var result = new Dictionary<long, BetfredRaceMetadata>();
        foreach (var item in Descendants(root).OfType<JsonObject>())
        {
            var id = ReadLong(item, "eventId", "eventID", "id");
            if (id is null or <= 0) continue;
            var country = ReadString(item, "countryName", "country");
            if (!string.Equals(country, "UK & Ireland", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadBool(item, "isSpecial") == true) continue;
            var meeting = ReadString(item, "tournamentName", "meetingName", "courseName", "venueName");
            var start = ReadStartTime(item);
            if (string.IsNullOrWhiteSpace(meeting) || start is null) continue;
            var raceInfo = item["raceInfo"] as JsonObject;
            var raceStatus = raceInfo is null ? null : ReadInt(raceInfo, "raceStatus");
            result[id.Value] = new(id.Value, meeting.Trim(), start.Value, raceStatus);
        }
        return result.Values.ToList();
    }

    private static BetfredRace? ParseRace(long expectedEventId, string json, BetfredRaceMetadata? knownMetadata)
    {
        var root = JsonNode.Parse(json);
        if (root is null) return null;
        var objects = Descendants(root).OfType<JsonObject>().ToList();
        var metadata = knownMetadata ?? objects.Select(item =>
            {
                var id = ReadLong(item, "eventId", "eventID", "id");
                var meeting = ReadString(item, "tournamentName", "meetingName", "courseName", "venueName");
                var start = ReadStartTime(item);
                return id == expectedEventId && !string.IsNullOrWhiteSpace(meeting) && start is not null
                    ? new BetfredRaceMetadata(expectedEventId, meeting.Trim(), start.Value)
                    : null;
            }).FirstOrDefault(value => value is not null);
        if (metadata is null) return null; // Never link a race without dated meeting/off-time confirmation.

        var eventObject = objects.FirstOrDefault(item =>
            ReadLong(item, "eventId", "eventID", "id") == expectedEventId &&
            !string.IsNullOrWhiteSpace(ReadString(item, "tournamentName", "meetingName", "courseName", "venueName")));
        var isLive = eventObject is not null && ReadBool(eventObject, "isLive") == true;
        var resultsEntered = eventObject is not null && ReadBool(eventObject, "resultsEntered") == true;
        var ended = eventObject is not null && ReadBool(eventObject, "ended") == true;

        var runners = objects
            .Where(item => ReadLong(item, "eventId", "eventID") == expectedEventId)
            .Where(item => IsWinnerMarket(ReadString(item, "marketName", "marketTitle")))
            .Select(item =>
            {
                var name = ReadString(item, "teamName", "selectionName", "runnerName")?.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Favourite", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Unnamed", StringComparison.OrdinalIgnoreCase)) return null;
                var frozen = ReadBool(item, "frozen") == true || ReadBool(item, "enabled") == false;
                // Betfred supplies a uniform display placeholder (commonly 4.5) before a
                // fixed-price show opens. It must never displace the genuine BetsAPI book.
                var onlySp = item["racingInfo"] is JsonObject racingInfo && ReadBool(racingInfo, "onlySP") == true;
                var price = ReadDouble(item, "oddValue", "decimalPrice", "price");
                if (price is null or <= 1)
                {
                    var numerator = ReadDouble(item, "numerator");
                    var denominator = ReadDouble(item, "denominator");
                    if (numerator is not null && denominator is > 0) price = 1d + numerator.Value / denominator.Value;
                }
                var teamPlayersId = ReadLong(item, "teamPlayersId", "winnerTeamPlayersId");
                var silkUrl = teamPlayersId is > 0
                    ? $"https://assets.oddsking.com/prod/jockey-silks/{expectedEventId}-{teamPlayersId.Value}.png"
                    : null;
                return new BetfredRunner(name, !onlySp && !frozen && price is > 1 ? price : null, silkUrl, item.ToJsonString());
            })
            .Where(item => item is not null)
            .Cast<BetfredRunner>()
            .GroupBy(item => BetfairMarketMatcher.NormalizeRunner(item.Name), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Price.HasValue).First())
            .ToList();
        return runners.Count < 2 ? null : new(expectedEventId, metadata.Meeting, metadata.StartTimeUnix,
            runners, isLive, resultsEntered, ended);
    }

    private static bool IsWinnerMarket(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        return normalized is "winner" or "race winner" or "win" or "to win" or "win or each way";
    }

    private static IEnumerable<JsonNode> Descendants(JsonNode node)
    {
        yield return node;
        if (node is JsonObject obj)
            foreach (var child in obj.Select(pair => pair.Value).Where(value => value is not null))
                foreach (var nested in Descendants(child!)) yield return nested;
        else if (node is JsonArray array)
            foreach (var child in array.Where(value => value is not null))
                foreach (var nested in Descendants(child!)) yield return nested;
    }

    private static string? ReadString(JsonObject item, params string[] names)
    {
        foreach (var name in names)
            if (item[name] is JsonValue value && value.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result))
                return result;
        return null;
    }

    private static long? ReadLong(JsonObject item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item[name] is not JsonValue value) continue;
            if (value.TryGetValue<long>(out var number)) return number;
            if (value.TryGetValue<int>(out var integer)) return integer;
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static int? ReadInt(JsonObject item, params string[] names)
    {
        var value = ReadLong(item, names);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    private static double? ReadDouble(JsonObject item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item[name] is not JsonValue value) continue;
            if (value.TryGetValue<double>(out var number)) return number;
            if (value.TryGetValue<decimal>(out var decimalNumber)) return (double)decimalNumber;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return null;
    }

    private static bool? ReadBool(JsonObject item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item[name] is not JsonValue value) continue;
            if (value.TryGetValue<bool>(out var result)) return result;
            if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out result)) return result;
        }
        return null;
    }

    private static long? ReadStartTime(JsonObject item)
    {
        foreach (var name in new[] { "dateStart", "startTime", "startDate", "eventStartTime", "eventDate", "offTime" })
        {
            if (item[name] is not JsonValue value) continue;
            if (value.TryGetValue<long>(out var unix))
            {
                if (unix > 10_000_000_000) unix /= 1000;
                if (unix > 1_000_000_000) return unix;
            }
            if (value.TryGetValue<string>(out var text) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return parsed.ToUnixTimeSeconds();
        }
        return null;
    }

    private static bool IsEventApiUrl(string url) =>
        url.StartsWith(Gateway + "/event/", StringComparison.OrdinalIgnoreCase);

    private static DateTime? TicksToUtc(long ticks) => ticks <= 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    private sealed record BrowserFetchResult(int Status, bool Ok, string Text);
    private sealed record BetfredRaceMetadata(long EventId, string Meeting, long StartTimeUnix, int? RaceStatus = null);
    private sealed record BetfredRace(long BetfredEventId, string Meeting, long StartTimeUnix,
        IReadOnlyList<BetfredRunner> Runners, bool IsLive, bool ResultsEntered, bool Ended);
    private sealed record BetfredRunner(string Name, double? Price, string? SilkUrl, string RawJson);
}
