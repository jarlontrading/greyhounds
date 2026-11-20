using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetsApiResultClient(
    IHttpClientFactory httpClientFactory,
    ExternalSettings settings,
    ILogger<BetsApiResultClient> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<long, DateTime> _nextPollUtc = [];

    public void ResetPolling(long eventId) => _nextPollUtc.Remove(eventId);

    public async Task<IReadOnlyList<ResultPage>> FindResultsAsync(
        IReadOnlyList<ResultCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var due = candidates
            .Where(x => x.SportId == 4 &&
                        (!_nextPollUtc.TryGetValue(x.EventId, out var next) || next <= now))
            .OrderByDescending(x => x.StartTimeUnix)
            .ToList();
        foreach (var candidate in due)
            _nextPollUtc[candidate.EventId] = now.Add(PollInterval);

        var output = new List<ResultPage>();
        foreach (var batch in due.Chunk(10))
        {
            var ids = string.Join(',', batch.Select(x => x.EventId));
            var requestUrl = $"https://api.b365api.com/v1/bet365/result?token={Uri.EscapeDataString(settings.B365ApiToken)}&event_id={ids}&raw=1";
            try
            {
                using var response = await httpClientFactory.CreateClient("BetsApiResults")
                    .GetAsync(requestUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!json.RootElement.TryGetProperty("results", out var results) ||
                    results.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in results.EnumerateArray())
                {
                    if (!TryReadEventId(item, out var eventId) ||
                        !item.TryGetProperty("body", out var bodyElement) ||
                        bodyElement.ValueKind != JsonValueKind.String)
                        continue;
                    var page = Parse(eventId, bodyElement.GetString() ?? "");
                    if (page is not null) output.Add(page);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "BetsAPI result lookup failed for greyhound events {EventIds}.", ids);
            }
        }

        var liveIds = candidates.Select(x => x.EventId).ToHashSet();
        foreach (var oldId in _nextPollUtc.Keys.Where(x => !liveIds.Contains(x)).ToList())
            _nextPollUtc.Remove(oldId);
        return output;
    }

    internal static ResultPage? Parse(long eventId, string body)
    {
        var runners = new List<ResultRunner>();
        var namesByTrap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in body.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(field => field.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
            fields.TryGetValue("PN", out var cardTrap);
            fields.TryGetValue("NA", out var cardRawName);
            var cardName = CleanRunnerName(cardRawName);
            if (!string.IsNullOrWhiteSpace(cardTrap) && !string.IsNullOrWhiteSpace(cardName))
                namesByTrap[cardTrap] = cardName;
            if (!fields.TryGetValue("EX", out var placing)) continue;
            // Dead-heated placings are returned as e.g. "2nd DH". Keep every
            // tied runner: dropping the second row left the race provisional
            // forever and therefore blocked the meeting Trap Challenge.
            var positionMatch = Regex.Match(placing, @"^(?<position>[1-3])(?:st|nd|rd)(?:\s+DH)?$", RegexOptions.IgnoreCase);
            if (!positionMatch.Success ||
                !int.TryParse(positionMatch.Groups["position"].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var position))
                continue;
            fields.TryGetValue("PN", out var trap);
            fields.TryGetValue("NA", out var rawName);
            var name = CleanRunnerName(rawName);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(trap)) continue;
            runners.Add(new ResultRunner(name, position, "Finished", trap));
        }

        // BetsAPI sometimes exposes only the first two explicit EX rows while the
        // complete finishing order is still present in the Tricast result (for
        // example 6-5-4). Accept the missing third only when the tricast's first
        // two traps agree with the explicit winner and runner-up. This keeps the
        // fallback deterministic and prevents an unrelated dividend from being
        // mistaken for a result.
        if (!runners.Any(runner => runner.Position == 3))
        {
            var tricast = Regex.Match(body,
                @"\|PA;NA=(?<traps>[1-6](?:-[1-6]){2});",
                RegexOptions.IgnoreCase);
            var traps = tricast.Success
                ? tricast.Groups["traps"].Value.Split('-', StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (traps.Length == 3 &&
                runners.Any(runner => runner.Position == 1 && runner.Identifier == traps[0]) &&
                runners.Any(runner => runner.Position == 2 && runner.Identifier == traps[1]) &&
                namesByTrap.TryGetValue(traps[2], out var thirdName))
                runners.Add(new ResultRunner(thirdName, 3, "Finished", traps[2]));
        }

        runners = runners
            .GroupBy(x => $"{x.Identifier}\u001f{x.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.Position)
            .ToList();
        var depth = runners.Select(x => x.Position ?? 0).DefaultIfEmpty(0).Max();
        // A dead heat for 1st returns two runners on position 1. The parser above already
        // keeps both ("2nd DH"); this gate used to throw the whole page away, so the race
        // never settled from any source and was swept up by the 24-hour stale close.
        if (depth == 0 || runners.Count(x => x.Position == 1) < 1) return null;
        var isCompletePodium = runners.Count >= 3 &&
            runners.All(x => x.Position is >= 1 and <= 3) &&
            (runners.Any(x => x.Position == 3) || runners.Count(x => x.Position == 2) >= 2);
        return new ResultPage(eventId, 4,
            $"https://api.b365api.com/v1/bet365/result?event_id={eventId}&raw=1",
            runners, null, isCompletePodium);
    }

    private static string CleanRunnerName(string? rawName)
    {
        var wrapped = Regex.Match(rawName ?? "",
            @"^Trap\s+\d+\s*\((?<name>.+)\)$", RegexOptions.IgnoreCase)
            .Groups["name"].Value.Trim();
        return string.IsNullOrWhiteSpace(wrapped) ? rawName?.Trim() ?? "" : wrapped;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string? Identifier, string Name)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();
        public bool Equals((string? Identifier, string Name) x, (string? Identifier, string Name) y) =>
            string.Equals(x.Identifier, y.Identifier, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string? Identifier, string Name) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Identifier ?? ""),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    private static bool TryReadEventId(JsonElement item, out long eventId)
    {
        eventId = 0;
        if (!item.TryGetProperty("id", out var id)) return false;
        return id.ValueKind == JsonValueKind.Number
            ? id.TryGetInt64(out eventId)
            : id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out eventId);
    }
}
