using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

/// <summary>
/// Polls the compact BetsAPI in-play filter independently of discovery and
/// pricing. Appearance here is an explicit provider lifecycle signal; it is
/// never used to create an event.
/// </summary>
internal sealed class BetsApiInPlayMonitor(
    IHttpClientFactory httpClientFactory,
    ExternalSettings settings,
    RacingDiagnosticCapture diagnostics,
    ILogger<BetsApiInPlayMonitor> logger) : BackgroundService
{
    private volatile FeedEvent[] _inPlay = [];
    private readonly ConcurrentDictionary<long, DateTime> _nextEventChecks = new();
    private readonly ConcurrentDictionary<long, byte> _confirmedOff = new();
    private readonly ConcurrentDictionary<long, byte> _checksInFlight = new();
    private CancellationToken _stoppingToken;

    public (bool IsOff, string Reason) TryGetRaceOffSignal(PricingEvent racingEvent)
    {
        var meetingKey = RacingEventNames.MeetingKey(racingEvent.Meeting);
        var match = _inPlay.FirstOrDefault(item =>
            item.EventId == racingEvent.EventId ||
            (RacingEventNames.MeetingKey(item.Meeting) == meetingKey &&
             Math.Abs(item.StartTimeUnix - racingEvent.StartTimeUnix) <= 120));
        // The compact in-play filter is useful discovery evidence but it is not
        // complete for every UK/Irish race. Once the scheduled time has passed,
        // directly check the known BetsAPI event id even when it never appeared
        // in the compact filter. RacingSuspensionWorker only calls this method at
        // or after the scheduled time, so this does not create an early suspend.
        var providerEventId = match?.EventId ?? racingEvent.EventId;

        if (_confirmedOff.ContainsKey(providerEventId))
            return (true, $"BetsAPI event model confirmed RACE_OFF (RO=1, event {providerEventId}).");

        var now = DateTime.UtcNow;
        if (_nextEventChecks.TryGetValue(providerEventId, out var nextCheck) && nextCheck > now)
            return (false, string.Empty);
        _nextEventChecks[providerEventId] = now.AddSeconds(3);
        if (_checksInFlight.TryAdd(providerEventId, 0))
            _ = CheckEventAsync(providerEventId, match is not null, _stoppingToken);
        return (false, string.Empty);
    }

    private async Task CheckEventAsync(long providerEventId, bool compactFilterMatch, CancellationToken cancellationToken)
    {
        try
        {
            var token = Uri.EscapeDataString(settings.B365ApiToken);
            var url = $"https://api.b365api.com/v1/bet365/event?FI={providerEventId}&token={token}";
            using var response = await httpClientFactory.CreateClient("BetsApiInPlay")
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            diagnostics.Write("betsapi-inplay", "event-race-off-check",
                new { eventId = providerEventId, compactFilterMatch, status = (int)response.StatusCode }, body);
            var root = JsonNode.Parse(body);
            var eventNode = FindEventNode(root?["results"]);
            if (Text(eventNode?["RO"]) != "1")
                return;

            _confirmedOff[providerEventId] = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not confirm BetsAPI RACE_OFF for event {EventId}.", providerEventId);
        }
        finally { _checksInFlight.TryRemove(providerEventId, out _); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ukNow = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "GMT Standard Time");
                if (ukNow.TimeOfDay < TimeSpan.FromHours(10.5) || ukNow.TimeOfDay > TimeSpan.FromHours(22.5))
                {
                    _inPlay = [];
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var token = Uri.EscapeDataString(settings.B365ApiToken);
                var url = $"https://api.b365api.com/v1/bet365/inplay_filter?sport_id=4&token={token}";
                using var response = await httpClientFactory.CreateClient("BetsApiInPlay")
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(stoppingToken);
                diagnostics.Write("betsapi-inplay", "compact-filter-response",
                    new { status = (int)response.StatusCode }, body);

                var parsed = new List<FeedEvent>();
                foreach (var item in (JsonNode.Parse(body)?["results"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var eventId = Long(item["id"]);
                    var start = Long(item["time"]);
                    var meeting = Text(item["league"]?["name"]);
                    if (string.IsNullOrWhiteSpace(meeting)) meeting = Text(item["home"]?["name"]);
                    if (eventId <= 0 || start <= 0 || string.IsNullOrWhiteSpace(meeting)) continue;
                    parsed.Add(new FeedEvent(eventId, 4, Long(item["league"]?["id"]), meeting,
                        RacingEventNames.Canonical(meeting, start), start, 1,
                        Long(item["updated_at"]), Long(item["odds_updated_at"]), item.ToJsonString()));
                }
                _inPlay = parsed.ToArray();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh compact BetsAPI in-play filter.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private static long Long(JsonNode? node) => long.TryParse(node?.ToString(), out var value) ? value : 0;
    private static string Text(JsonNode? node) => node?.ToString()?.Trim() ?? string.Empty;

    private static JsonObject? FindEventNode(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            if (Text(item["type"]).Equals("EV", StringComparison.OrdinalIgnoreCase)) return item;
            foreach (var child in item.Select(pair => pair.Value))
                if (FindEventNode(child) is { } found) return found;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                if (FindEventNode(child) is { } found) return found;
        }
        return null;
    }
}
