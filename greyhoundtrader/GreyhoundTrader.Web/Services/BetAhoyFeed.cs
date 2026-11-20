using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace RacingTrader.Web.Services;

internal sealed class BetAhoyFeed(IConfiguration configuration, ILogger<BetAhoyFeed> logger) : BackgroundService
{
    private sealed record Race(string Meeting, long Start, string Status, DateTime? OffUtc = null);
    private sealed record Outcome(string RaceId, string Runner);
    internal sealed record Quote(double Price, DateTime ObservedAtUtc);
    private sealed record Offer(string OutcomeId, bool Available, Quote? Quote);
    private readonly ConcurrentDictionary<string, Race> _races = new();
    private readonly ConcurrentDictionary<string, Outcome> _outcomes = new();
    private readonly ConcurrentDictionary<string, Offer> _offers = new();
    private readonly object _gate = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("BetAhoy:Enabled", false)) return;
        await Task.Yield();
        using var playwright = await Playwright.CreateAsync();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "chrome", Headless = true });
                var page = await browser.NewPageAsync();
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                page.Close += (_, _) => disconnected.TrySetResult();
                page.WebSocket += (_, socket) =>
                {
                    if (!Uri.TryCreate(socket.Url, UriKind.Absolute, out var url) ||
                        url.Scheme != "wss" || url.Host != "sportsapi.betahoy.co.uk") return;
                    lock (_gate) { _races.Clear(); _outcomes.Clear(); _offers.Clear(); }
                    socket.FrameReceived += (_, frame) =>
                    {
                        try { lock (_gate) Read(frame.Text); }
                        catch (Exception ex) { logger.LogDebug(ex, "Ignored unreadable BetAhoy frame."); }
                    };
                    socket.Close += (_, _) => disconnected.TrySetResult();
                };
                await page.GotoAsync("https://www.betahoy.co.uk/sports/greyhounds-27/all-0/location",
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
                await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromMinutes(30), ct));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning(ex, "BetAhoy browser disconnected."); }
            finally { lock (_gate) { _races.Clear(); _outcomes.Clear(); _offers.Clear(); } }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private void Read(string? text)
    {
        if (text is null || JsonNode.Parse(text) is not JsonArray frame ||
            !int.TryParse(frame[0]?.ToString(), out var code) || code is not (50 or 68)) return;
        var nodes = frame.Skip(code == 68 ? 4 : 3).SelectMany(Objects).ToArray();
        var now = DateTime.UtcNow;
        foreach (var o in nodes.Where(o => Value(o, "_type") == "TOURNAMENT"))
        {
            if (Value(o, "sportId") != "27" || !long.TryParse(Value(o, "startTime"), out var ms)) continue;
            var id = Value(o, "id");
            var name = Value(o, "name");
            if (id.Length == 0 || name.Length < 7 || !TimeOnly.TryParseExact(name[..5], "HH:mm", out _)) continue;
            _races.TryAdd(id, new Race(name[6..].Trim(), ms / 1000, Value(o, "statusName")));
        }
        foreach (var o in nodes.Where(o => Value(o, "_type") == "OUTCOME" && Value(o, "typeName") == "Race Winner"))
            if (_races.ContainsKey(Value(o, "eventId")))
                _outcomes[Value(o, "id")] = new Outcome(Value(o, "eventId"), Value(o, "paramParticipantName1"));
        foreach (var o in nodes.Where(o => Value(o, "_type") == "BETTING_OFFER"))
            if (_outcomes.ContainsKey(Value(o, "outcomeId")))
                _offers[Value(o, "id")] = new Offer(Value(o, "outcomeId"), Available(o), ReadQuote(o, now));

        if (code != 68) return;
        foreach (var o in nodes)
        {
            var id = Value(o, "id");
            var type = Value(o, "entityType");
            var change = Value(o, "changeType");
            if (type == "BETTING_OFFER" && change == "DELETE") { _offers.TryRemove(id, out _); continue; }
            if (change != "UPDATE" || o["changedProperties"] is not JsonObject fields) continue;
            if (type == "TOURNAMENT" && _races.TryGetValue(id, out var race) && Value(fields, "statusName") is { Length: > 0 } status)
            {
                var off = race.Status == "Pending" && status == "In Progress" && Value(fields, "statusId") == "2" ? now : race.OffUtc;
                _races[id] = race with { Status = status, OffUtc = off };
            }
            if (type == "BETTING_OFFER" && _offers.TryGetValue(id, out var offer))
            {
                var available = fields.ContainsKey("isAvailable") ? Available(fields) : offer.Available;
                var quote = !available ? null : fields.ContainsKey("odds") ? ReadQuote(fields, now) : offer.Quote;
                _offers[id] = offer with { Available = available, Quote = quote };
            }
        }
    }

    private static bool Available(JsonObject o) =>
        bool.TryParse(Value(o, "isAvailable"), out var available) && available;

    private static Quote? ReadQuote(JsonObject o, DateTime now)
    {
        if (bool.TryParse(Value(o, "isAvailable"), out var available) && !available ||
            !double.TryParse(Value(o, "odds"), System.Globalization.CultureInfo.InvariantCulture, out var price) ||
            !double.IsFinite(price) || price <= 1) return null;
        return new(price, now);
    }

    private static string Value(JsonObject? node, string key) => node?[key]?.ToString() ?? "";
    private static IEnumerable<JsonObject> Objects(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            yield return obj;
            foreach (var entry in obj)
                foreach (var child in Objects(entry.Value)) yield return child;
        }
        else if (value is JsonArray arr)
            foreach (var item in arr)
                foreach (var child in Objects(item)) yield return child;
    }

    private KeyValuePair<string, Race>? Match(string meeting, long start)
    {
        var key = RacingEventNames.MeetingKey(meeting);
        var matches = _races.Where(r => r.Value.Start == start &&
            RacingEventNames.MeetingKey(r.Value.Meeting) == key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool TryGetOff(PricingEvent race, out string reason)
    {
        reason = "";
        if (!configuration.GetValue("BetAhoy:UseOff", false)) return false;
        var match = Match(race.Meeting, race.StartTimeUnix);
        if (match is not { } item || item.Value.OffUtc is not { } off ||
            off < DateTime.UtcNow.AddMinutes(-2)) return false;
        reason = $"BetAhoy reported In Progress for race {item.Key} at {off:O}.";
        return true;
    }

    public Quote? GetQuote(string meeting, long start, string runner)
    {
        if (Match(meeting, start) is not { } race || race.Value.Status != "Pending") return null;
        var name = BetfairMarketMatcher.NormalizeRunner(runner);
        var quotes = _offers.Values.Where(o => o.Quote is { } quote && quote.ObservedAtUtc >= DateTime.UtcNow.AddMinutes(-2) &&
            _outcomes.TryGetValue(o.OutcomeId, out var outcome) && outcome.RaceId == race.Key &&
            BetfairMarketMatcher.NormalizeRunner(outcome.Runner) == name).Take(2).ToArray();
        return quotes.Length == 1 ? quotes[0].Quote : null;
    }

    public IReadOnlyList<FeedRunner> ApplyFallback(PricingEvent race, IReadOnlyList<FeedRunner> runners)
    {
        if (!configuration.GetValue("BetAhoy:UseFallbackPrices", false)) return runners;
        var field = runners.Where(r => !r.ExplicitNonRunner).ToArray();
        if (field.Length < 2 || field.Any(r => (r.HasPrice && r.EffectivePrice > 1) || r.PricingPrice is > 1))
            return runners;
        lock (_gate)
        {
            if (Match(race.Meeting, race.StartTimeUnix) is not { } matched) return runners;
            var expected = field.Select(r => BetfairMarketMatcher.NormalizeRunner(r.Name)).ToHashSet();
            var actual = _outcomes.Values.Where(o => o.RaceId == matched.Key)
                .Select(o => BetfairMarketMatcher.NormalizeRunner(o.Runner)).ToHashSet();
            if (expected.Count != field.Length || !expected.SetEquals(actual)) return runners;
            var quotes = field.ToDictionary(r => r.CompetitorId, r => GetQuote(race.Meeting, race.StartTimeUnix, r.Name));
            if (quotes.Values.Any(q => q is null)) return runners;
            return runners.Select(r => r.ExplicitNonRunner ? r : r with
            {
                HasPrice = true, PricingPrice = quotes[r.CompetitorId]!.Price,
                PricingSource = "BETAHOY", SourceUpdatedAtUtc = quotes[r.CompetitorId]!.ObservedAtUtc
            }).ToArray();
        }
    }
}