using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class RacingFeedClient(HttpClient httpClient, ExternalSettings settings, Bet365CaptureBridge capture,
    RacingDiagnosticCapture diagnostics, ILogger<RacingFeedClient> logger)
{
    private const string OriginalInPlayEventUrl = "https://jarlon.onlinegamblingtech.com/api/v1/event?evId=";
    private const string DirectFeedRoot = @"C:\Temp\b365-direct\events";
    private readonly Dictionary<long, (DateTime RefreshAfterUtc, FeedPriceSnapshot Snapshot)> _betsApiCache = [];

    public async Task<string> GetFeedResponseAsync(long eventId, bool raw, CancellationToken cancellationToken)
    {
        var rawQuery = raw ? "&raw=1" : string.Empty;
        var url = $"https://api.b365api.com/v4/bet365/prematch?token={Uri.EscapeDataString(settings.B365ApiToken)}&FI={eventId}{rawQuery}";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        diagnostics.Write("betsapi", "prematch-response", new { eventId, raw, status = (int)response.StatusCode }, body);
        return body;
    }

    public async Task<string> GetUpcomingFeedResponseAsync(int sportId, int page, CancellationToken cancellationToken)
    {
        var safePage = Math.Clamp(page, 1, 20);
        var url = $"https://api.b365api.com/v1/bet365/upcoming?token={Uri.EscapeDataString(settings.B365ApiToken)}&per_page=100&page={safePage}&sport_id={sportId}&skip_esports=1";
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        diagnostics.Write("betsapi", "upcoming-response",
            new { sportId, page = safePage, status = (int)response.StatusCode }, body);
        return body;
    }

    public async Task<IReadOnlyList<FeedEvent>> GetEventsAsync(int sportId, CancellationToken cancellationToken)
    {
        var discoveryStart = DateTimeOffset.UtcNow.AddHours(-4).ToUnixTimeSeconds();
        var discoveryEnd = DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeSeconds();
        var captured = capture.Events
            .Where(x => x.SportId == sportId &&
                        x.StartTimeUnix >= discoveryStart &&
                        x.StartTimeUnix <= discoveryEnd)
            .ToList();
        if (captured.Count > 0) return captured;
        var direct = TryGetDirectEvents(sportId);
        if (direct.Count > 0)
        {
            logger.LogDebug("Using direct Bet365 discovery for sport {SportId} ({EventCount} events).", sportId, direct.Count);
            return direct;
        }

        var output = (await GetBetsApiEventsAsync(sportId, cancellationToken))
            .ToDictionary(item => item.EventId);
        return output.Values.ToList();
    }

    public async Task<IReadOnlyList<FeedEvent>> GetBetsApiEventsAsync(
        int sportId, CancellationToken cancellationToken)
    {
        var output = new Dictionary<long, FeedEvent>();
        for (var page = 1; page <= 4; page++)
        {
            var path = $"https://api.b365api.com/v1/bet365/upcoming?token={Uri.EscapeDataString(settings.B365ApiToken)}&per_page=100&page={page}&sport_id={sportId}&skip_esports=1";
            var root = await GetJsonAsync(path, cancellationToken);
            var results = root?["results"] as JsonArray;
            if (results is null || results.Count == 0) break;
            foreach (var item in results.OfType<JsonObject>())
            {
                var parsed = ParseEvent(item, sportId);
                if (parsed is not null) output[parsed.EventId] = parsed;
            }
            if (results.Count < 100) break;
        }

        try
        {
            // Match the original racing engine: use the actual Bet365 in-play
            // list, then confirm race-off from default_open_right below.
            var path = $"https://api.b365api.com/v1/bet365/inplay?sport_id={sportId}&token={Uri.EscapeDataString(settings.B365ApiToken)}";
            var root = await GetJsonAsync(path, cancellationToken);
            foreach (var item in (root?["results"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var parsed = ParseEvent(item, sportId, forceInPlay: true);
                // In-play is an update channel, never an event-discovery channel.
                // Only apply it to the exact event already seen in pre-event discovery.
                if (parsed is not null && output.ContainsKey(parsed.EventId))
                    output[parsed.EventId] = parsed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not refresh in-play event list for sport {SportId}", sportId);
        }
        if (sportId == 2)
        {
            var implausible = output.Values.GroupBy(x => x.Meeting, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.Where(item => group.Count(other => Math.Abs(other.StartTimeUnix - item.StartTimeUnix) <= 120) >= 3))
                .Select(x => x.EventId).ToHashSet();
            foreach (var eventId in implausible)
            {
                logger.LogError("Rejected implausible horse event {EventId}: at least three races at the same meeting were within two minutes.", eventId);
                output.Remove(eventId);
            }
        }
        return output.Values.ToList();
    }

    private static IReadOnlyList<FeedEvent> TryGetDirectEvents(int sportId)
    {
        var path = Path.Combine(DirectFeedRoot, $"discovery-{sportId}.json");
        if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(10)) return [];

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray items) return [];
            return items.OfType<JsonObject>().Select(item =>
            {
                var eventId = Long(item["EventId"]);
                var itemSportId = (int)Long(item["SportId"]);
                var start = Long(item["StartTimeUnix"]);
                var meeting = Text(item["Meeting"]);
                return eventId > 0 && itemSportId == sportId && start > 0
                    ? new FeedEvent(eventId, itemSportId, Long(item["LeagueId"]), meeting, RacingEventNames.Canonical(meeting, start),
                        start, (int)Long(item["TimeStatus"]), Long(item["UpdatedAtUnix"]), Long(item["OddsUpdatedAtUnix"]), item.ToJsonString())
                    : null;
            }).Where(item => item is not null).Cast<FeedEvent>().ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<FeedPriceComparison> GetRunnerSnapshotsAsync(PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        var browser = capture.Snapshot(racingEvent);
        var direct = TryGetDirectSnapshot(racingEvent);
        FeedPriceSnapshot? betsApi = null;
        try
        {
            betsApi = await GetBetsApiSnapshotAsync(racingEvent, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own request timeout as cancellation. That is
            // a provider miss, so continue with the Bet365 runner scaffold rather
            // than abandoning this race and leaving an empty card.
            logger.LogDebug(ex, "BetsAPI price snapshot timed out for event {EventId}; using available runner metadata.",
                racingEvent.EventId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "BetsAPI price snapshot unavailable for event {EventId}.", racingEvent.EventId);
        }

        var freshestDirect = new[] { browser, direct }
            .Where(x => x?.IsUsable == true)
            .OrderByDescending(x => x!.UpdatedAtUtc)
            .FirstOrDefault();
        // Browser/direct Bet365 remains valuable for event and runner metadata, but
        // its prices are not reliable enough to drive the model. BetsAPI is the only
        // Bet365 price source. If it is unavailable, retain the browser runners as a
        // zero-priced scaffold so Spreadex can still match and price the race.
        var selected = SelectBetsApiPricingOrMetadata(freshestDirect, betsApi);
        if (selected is not null)
            logger.LogDebug("Using {Source} snapshot dated {UpdatedAtUtc:o} for event {EventId}.",
                selected.Source, selected.UpdatedAtUtc, racingEvent.EventId);
        return new FeedPriceComparison(selected, browser ?? direct, betsApi);
    }

    internal static FeedPriceSnapshot? SelectBetsApiPricingOrMetadata(
        FeedPriceSnapshot? direct,
        FeedPriceSnapshot? betsApi)
    {
        if (IsCompleteFixedPriceBook(betsApi))
            return betsApi! with
            {
                Runners = betsApi.Runners.Select(runner => runner with { PricingSource = "BETSAPI" }).ToList()
            };

        if (IsCompleteFixedPriceBook(direct))
            return direct! with
            {
                Source = "BET365_DIRECT",
                Runners = direct.Runners.Select(runner => runner with
                {
                    PricingPrice = runner.Price,
                    PricingSource = "BET365_DIRECT"
                }).ToList()
            };

        var metadata = betsApi is { Runners.Count: > 1 } ? betsApi
            : direct is { Runners.Count: > 1 } ? direct
            : null;
        if (metadata is null) return null;

        return metadata with
        {
            Source = metadata.Source == "BETSAPI" ? "BETSAPI_METADATA" : "BET365_METADATA",
            Runners = metadata.Runners.Select(runner => runner with
            {
                PriceText = string.Empty,
                Price = 0,
                // This means the runner is participating, not that Bet365 supplied
                // a usable price. Spreadex replaces PricingPrice when matched.
                HasPrice = !runner.ExplicitNonRunner,
                PricingPrice = null,
                PricingSource = null
            }).ToList()
        };
    }

    private static bool IsCompleteFixedPriceBook(FeedPriceSnapshot? snapshot)
    {
        if (snapshot is null) return false;
        var runners = snapshot.Runners
            .Where(runner => !runner.Name.Contains("Favourite", StringComparison.OrdinalIgnoreCase))
            .GroupBy(runner => runner.Identifier)
            .Select(group => group.OrderBy(runner => runner.ExplicitNonRunner).First())
            .ToList();
        var active = runners.Where(runner => !runner.ExplicitNonRunner).ToList();
        return active.Count >= 2 &&
               runners.All(runner => runner.Identifier is >= 1 and <= 6) &&
               runners.Select(runner => runner.Identifier).Distinct().Count() == runners.Count &&
               active.All(runner => runner.HasPrice && runner.Price > 1);
    }

    public IReadOnlyList<long> GetFreshDirectEventIds()
    {
        var browserEventIds = capture.DrainChangedEventIds();
        if (!Directory.Exists(DirectFeedRoot)) return browserEventIds;
        var cutoff = DateTime.UtcNow.AddSeconds(-10);
        return browserEventIds.Concat(Directory.EnumerateFiles(DirectFeedRoot, "*.json")
            .Where(path => !Path.GetFileName(path).StartsWith("discovery-", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.GetLastWriteTimeUtc(path) >= cutoff)
            .Select(path => long.TryParse(Path.GetFileNameWithoutExtension(path), out var eventId) ? eventId : 0)
            .Where(eventId => eventId > 0))
            .Distinct()
            .ToList();
    }

    private async Task<FeedPriceSnapshot?> GetBetsApiSnapshotAsync(PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_betsApiCache.TryGetValue(racingEvent.EventId, out var cached) && cached.RefreshAfterUtc > now)
            return cached.Snapshot;

        var root = await GetJsonAsync(
            $"https://api.b365api.com/v3/bet365/prematch?token={Uri.EscapeDataString(settings.B365ApiToken)}&FI={racingEvent.EventId}",
            cancellationToken);
        var parsed = ParsePrematchRunners(root);
        var runners = parsed.Runners;
        if (runners.Count < 2) return null;
        var updatedAt = MainUpdatedAt(root) ??
            runners.Select(x => x.SourceUpdatedAtUtc).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
        if (updatedAt == default) updatedAt = LatestUpdatedAt(root) ?? now;
        var snapshot = new FeedPriceSnapshot("BETSAPI", updatedAt, now, runners, parsed.NonRunners);
        _betsApiCache[racingEvent.EventId] = (now.AddSeconds(racingEvent.Status == "1" ? 1 : 10), snapshot);
        return snapshot;
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        diagnostics.Write(url.Contains("onlinegamblingtech", StringComparison.OrdinalIgnoreCase)
                ? "bet365-event-api" : "betsapi",
            "http-response",
            new { url = RedactToken(url), status = (int)response.StatusCode }, body);
        return JsonNode.Parse(body);
    }

    private static string RedactToken(string url) =>
        Regex.Replace(url, @"(?<=token=)[^&]+", "[redacted]", RegexOptions.IgnoreCase);

    public async Task<bool?> IsRaceOffAsync(PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        if (capture.IsRaceOff(racingEvent)) return true;
        var eventId = racingEvent.EventId;
        try
        {
            var root = await GetJsonAsync(OriginalInPlayEventUrl + eventId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            var value = Long(root?["eventData"]?["default_open_right"]);
            if (value == 1) return true;
            logger.LogInformation("Bet365 in-play event {EventId} was not race-off in the original event model (default_open_right={Value}).", eventId, value);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // This matches the original racing code: failure to read the event
            // model exits without closing or suspending the race.
            logger.LogWarning(ex, "Could not confirm race-off for Bet365 event {EventId}; leaving it active.", eventId);
            return null;
        }
    }

    private static FeedEvent? ParseEvent(JsonObject item, int fallbackSportId, bool forceInPlay = false)
    {
        var id = Long(item["id"]);
        var start = Long(item["time"]);
        if (id <= 0 || start <= 0) return null;
        var sport = (int)Long(item["sport_id"]);
        if (sport == 0) sport = fallbackSportId;
        var league = item["league"] as JsonObject;
        var meeting = Text(league?["name"]);
        if (string.IsNullOrWhiteSpace(meeting)) meeting = "Unassigned";
        var name = RacingEventNames.Canonical(meeting, start);
        return new FeedEvent(id, sport, Long(league?["id"]), meeting, name, start,
            forceInPlay ? 1 : (int)Long(item["time_status"]), Long(item["updated_at"]), Long(item["odds_updated_at"]), item.ToJsonString());
    }

    private static PrematchFeedData ParsePrematchRunners(JsonNode? root)
    {
        var candidates = new List<(int Score, string Path, JsonArray Odds)>();
        Visit(root, "", candidates);
        var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault().Odds;
        var runners = best is null
            ? []
            : best.OfType<JsonObject>().Select(ParsePrematchRunner).Where(x => x is not null).Cast<FeedRunner>()
                .GroupBy(x => x.CompetitorId).Select(x => x.First()).OrderBy(x => x.Identifier).ToList();
        var nonRunners = new Dictionary<string, FeedNonRunner>(StringComparer.Ordinal);
        VisitNonRunners(root, "", nonRunners);
        foreach (var runner in runners.Where(x => x.ExplicitNonRunner))
            AddNonRunner(nonRunners, new FeedNonRunner(runner.Name, null, runner.Identifier,
                "Explicit participant non-runner flag", runner.RawJson));
        return new PrematchFeedData(runners, nonRunners.Values.ToList());
    }

    private static void Visit(JsonNode? node, string path, List<(int Score, string Path, JsonArray Odds)> candidates)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = path + "." + property.Key;
                if (property.Key.Equals("odds", StringComparison.OrdinalIgnoreCase) && property.Value is JsonArray odds)
                    candidates.Add((ScoreOdds(odds, childPath), childPath, odds));
                Visit(property.Value, childPath, candidates);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) Visit(child, path, candidates);
    }

    private static int ScoreOdds(JsonArray odds, string path)
    {
        var score = path.Contains(".main.sp.", StringComparison.OrdinalIgnoreCase) ? 30 : 0;
        if (path.Contains("fixed", StringComparison.OrdinalIgnoreCase) || path.Contains("racecard", StringComparison.OrdinalIgnoreCase)) score += 20;
        foreach (var item in odds.OfType<JsonObject>().Take(20))
        {
            if (!string.IsNullOrWhiteSpace(Text(item["NA"]))) score += 2;
            if (Long(item["ID"]) > 0) score += 2;
            if (Long(item["PN"]) > 0) score += 5;
            if (!string.IsNullOrWhiteSpace(Text(item["OD"]))) score += 3;
            if (!string.IsNullOrWhiteSpace(Text(item["JN"]))) score++;
        }
        return score;
    }

    private static FeedRunner? ParsePrematchRunner(JsonObject item)
    {
        var name = Text(item["NA"]).Trim();
        if (name.Equals("Favourite", StringComparison.OrdinalIgnoreCase) || name.Equals("2nd Favourite", StringComparison.OrdinalIgnoreCase)) return null;
        var id = Long(item["ID"]);
        var identifier = (int)Long(item["PN"]);
        if (id <= 0 || identifier <= 0 || string.IsNullOrWhiteSpace(name)) return null;
        var priceText = Text(item["OD"]);
        var explicitNr = IsExplicitNonRunner(item);
        var hasPrice = TryPrice(priceText, out var price);
        return new FeedRunner(id, name, identifier, priceText, price, hasPrice && !explicitNr, explicitNr, Text(item["SI"]), item.ToJsonString(),
            SourceUpdatedAtUtc: UnixDateTime(item["updated_at"]));
    }

    private static void VisitNonRunners(JsonNode? node, string path, Dictionary<string, FeedNonRunner> output)
    {
        if (node is JsonObject item)
        {
            var name = Text(item["NA"]).Trim();
            var sourceRunnerId = Long(item["DI"]);
            var itemId = Long(item["ID"]);
            var identifier = (int)Long(item["PN"]);
            var inRacingMainMarket = path.Contains(".main.sp.", StringComparison.OrdinalIgnoreCase) ||
                                     path.Contains(".win_each_way.sp.", StringComparison.OrdinalIgnoreCase);
            var inNonRunnerMarket = path.Contains("non_runner", StringComparison.OrdinalIgnoreCase) ||
                                    path.Contains("nonrunner", StringComparison.OrdinalIgnoreCase);
            var isMinimalNonRunnerEntry = inRacingMainMarket && sourceRunnerId > 0 &&
                                          Long(item["ID"]) == 0 && identifier == 0 &&
                                          string.IsNullOrWhiteSpace(Text(item["OD"]));
            if (!string.IsNullOrWhiteSpace(name) &&
                !name.Equals("Favourite", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("2nd Favourite", StringComparison.OrdinalIgnoreCase) &&
                (((inRacingMainMarket || inNonRunnerMarket) && IsExplicitNonRunner(item)) ||
                 isMinimalNonRunnerEntry))
            {
                AddNonRunner(output, new FeedNonRunner(
                    name,
                    sourceRunnerId > 0 ? sourceRunnerId : itemId > 0 ? itemId : null,
                    identifier > 0 ? identifier : null,
                    IsExplicitNonRunner(item) ? "Explicit participant non-runner flag" : "Bet365 Non Runners market",
                    item.ToJsonString()));
            }
            foreach (var property in item)
                VisitNonRunners(property.Value, path + "." + property.Key, output);
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                VisitNonRunners(array[index], $"{path}[{index}]", output);
        }
    }

    private static bool IsExplicitNonRunner(JsonObject item)
    {
        var price = Text(item["OD"]);
        var exclusion = Text(item["EX"]);
        var nonRunner = Text(item["NR"]);
        var jockey = Text(item["JN"]);
        return price.Equals("NR", StringComparison.OrdinalIgnoreCase) ||
               exclusion.Equals("Non Runner", StringComparison.OrdinalIgnoreCase) ||
               jockey.Equals("Non Runner", StringComparison.OrdinalIgnoreCase) ||
               nonRunner.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               nonRunner.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               nonRunner.Equals("Non Runner", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddNonRunner(Dictionary<string, FeedNonRunner> output, FeedNonRunner nonRunner)
    {
        var key = BetfairMarketMatcher.NormalizeRunner(nonRunner.Name);
        if (key.Length == 0) return;
        if (!output.TryGetValue(key, out var existing) || existing.SourceRunnerId is null)
            output[key] = nonRunner;
    }

    private static DateTime? MainUpdatedAt(JsonNode? root)
    {
        if (root?["results"] is not JsonArray { Count: > 0 } results || results[0] is not JsonObject result)
            return null;
        return UnixDateTime(result["main"]?["updated_at"]);
    }

    private static FeedPriceSnapshot? TryGetDirectSnapshot(PricingEvent racingEvent)
    {
        var path = Path.Combine(DirectFeedRoot, $"{racingEvent.EventId}.json");
        if (!File.Exists(path)) return null;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null || Long(root["EventId"]) != racingEvent.EventId || Long(root["SportId"]) != racingEvent.SportId) return null;
            if (root["Selections"] is not JsonObject selections) return null;

            var output = new List<FeedRunner>();
            foreach (var selection in selections.Select(x => x.Value).OfType<JsonObject>())
            {
                if (Bool(selection["IsFavourite"])) continue;
                var id = Long(selection["SelectionId"]);
                var name = Text(selection["Name"]).Trim();
                var identifier = (int)Long(selection["Draw"]);
                if (identifier <= 0)
                {
                    var digits = new string(Text(selection["BettingSelection"]).Where(char.IsDigit).ToArray());
                    int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out identifier);
                }

                var priceText = Text(selection["Price"]);
                var explicitNonRunner = priceText.Equals("NR", StringComparison.OrdinalIgnoreCase);
                var suspended = Bool(selection["Suspended"]);
                var hasPrice = TryPrice(priceText, out var price) && !suspended && !explicitNonRunner;
                if (id <= 0 || identifier <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                output.Add(new FeedRunner(id, name, identifier, priceText, price, hasPrice, explicitNonRunner,
                    Text(selection["SilkId"]), selection.ToJsonString(),
                    SourceUpdatedAtUtc: IsoDateTime(selection["LastUpdatedUtc"])));
            }
            if (output.Count < 2) return null;
            var updatedAt = output.Select(x => x.SourceUpdatedAtUtc).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
            if (updatedAt == default) updatedAt = File.GetLastWriteTimeUtc(path);
            var ordered = output.OrderBy(x => x.Identifier).ToList();
            var nonRunners = ordered.Where(x => x.ExplicitNonRunner)
                .Select(x => new FeedNonRunner(x.Name, null, x.Identifier, "Direct feed non-runner", x.RawJson)).ToList();
            return new FeedPriceSnapshot("DIRECT", updatedAt, DateTime.UtcNow, ordered, nonRunners);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<FeedRunner> ParseLiveRunners(JsonNode? root)
    {
        var markets = root?["eventData"]?["markets"] as JsonArray;
        if (markets is null) return [];
        JsonArray? best = null;
        var bestScore = int.MinValue;
        foreach (var market in markets.OfType<JsonObject>())
        {
            var odds = market["Odds"] as JsonArray;
            if (odds is null) continue;
            var score = odds.OfType<JsonObject>().Take(12).Sum(item =>
                (Long(item["cloth_number"]) > 0 ? 5 : 0) + (!string.IsNullOrWhiteSpace(Text(item["odds"])) ? 3 : 0) + (!string.IsNullOrWhiteSpace(Text(item["name"])) ? 1 : 0));
            if (Text(market["name"]).Contains("win", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (score > bestScore) { bestScore = score; best = odds; }
        }
        if (best is null) return [];
        return best.OfType<JsonObject>().Select(item =>
        {
            var name = Text(item["name"]);
            var id = Long(item["id"]);
            var cloth = (int)Long(item["cloth_number"]);
            var priceText = Text(item["odds"]);
            var hasPrice = TryPrice(priceText, out var price);
            var suspended = Long(item["suspended"]) != 0;
            return id > 0 && cloth > 0 && !string.IsNullOrWhiteSpace(name)
                ? new FeedRunner(id, name, cloth, priceText, price, hasPrice && !suspended, false, Text(item["image_id"]), item.ToJsonString()) : null;
        }).Where(x => x is not null).Cast<FeedRunner>().OrderBy(x => x.Identifier).ToList();
    }

    internal static bool TryPrice(string text, out double price)
    {
        price = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Split(':')[0].Trim();
        if (value.Equals("EVS", StringComparison.OrdinalIgnoreCase) || value.Equals("EVENS", StringComparison.OrdinalIgnoreCase)) { price = 2; return true; }
        var parts = value.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
        { price = 1 + numerator / denominator; return price > 1; }
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out price) && price > 1;
    }

    private sealed record PrematchFeedData(
        IReadOnlyList<FeedRunner> Runners,
        IReadOnlyList<FeedNonRunner> NonRunners);

    private static long Long(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static bool Bool(JsonNode? node) => bool.TryParse(Text(node), out var value) ? value : Long(node) != 0;
    private static string Text(JsonNode? node) => node?.ToString() ?? "";
    private static DateTime? UnixDateTime(JsonNode? node)
    {
        var value = Long(node);
        return value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime : null;
    }
    private static DateTime? IsoDateTime(JsonNode? node) =>
        DateTime.TryParse(Text(node), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value) ? value : null;
    private static DateTime? LatestUpdatedAt(JsonNode? node)
    {
        long latest = 0;
        void VisitUpdated(JsonNode? current)
        {
            if (current is JsonObject obj)
            {
                foreach (var property in obj)
                {
                    if (property.Key.Equals("updated_at", StringComparison.OrdinalIgnoreCase))
                        latest = Math.Max(latest, Long(property.Value));
                    VisitUpdated(property.Value);
                }
            }
            else if (current is JsonArray array)
                foreach (var child in array) VisitUpdated(child);
        }
        VisitUpdated(node);
        return latest > 0 ? DateTimeOffset.FromUnixTimeSeconds(latest).UtcDateTime : null;
    }
}
