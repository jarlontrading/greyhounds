using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace RacingTrader.Web.Services;

internal sealed record ResultCandidate(long EventId, int SportId, string Meeting, string Name, long StartTimeUnix);
internal sealed record ResultRunner(string Name, int? Position, string Status, string? Identifier, string? FinishDistance = null);
internal sealed record ResultPage(
    long EventId,
    int SportId,
    string SourceUrl,
    IReadOnlyList<ResultRunner> Runners,
    int? ExpectedRunnerCount,
    bool IsFinal,
    bool IsVoid = false);

internal sealed class RacingResultClient(
    HttpClient httpClient,
    GreyhoundDistanceMarkets distances,
    ILogger<RacingResultClient> logger)
{
    private const string HorseFastUrl = "https://www.sportinglife.com/racing/fast-results";
    private const string HorseResultsUrl = "https://www.sportinglife.com/racing/results";
    private const string DogFastUrl = "https://www.sportinglife.com/greyhounds/fast-results";
    private const string DogRacecardsUrl = "https://www.sportinglife.com/greyhounds/racecards";
    private const string DogResultsUrl = "https://www.sportinglife.com/greyhounds/results";
    private readonly HtmlParser _parser = new();

    public async Task<IReadOnlyList<ResultPage>> FindResultsAsync(IReadOnlyList<ResultCandidate> candidates, CancellationToken cancellationToken)
    {
        var output = new Dictionary<long, ResultPage>();
        foreach (var sportGroup in candidates.GroupBy(x => x.SportId))
        {
            var indexUrls = sportGroup.Key == 2
                ? new List<string> { HorseFastUrl, HorseResultsUrl }
                : new List<string> { DogFastUrl, DogRacecardsUrl, DogResultsUrl };
            if (sportGroup.Key == 4)
            {
                // The undated greyhound indexes roll over at midnight. Include
                // the dated archive for every candidate day so late/missed
                // evening results can still be recovered on the following day.
                indexUrls.AddRange(sportGroup
                    .Select(candidate => DateTimeOffset.FromUnixTimeSeconds(candidate.StartTimeUnix)
                        .ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    .Distinct(StringComparer.Ordinal)
                    .Select(date => $"{DogResultsUrl}/{date}"));
            }
            var links = new List<IElement>();
            foreach (var indexUrl in indexUrls)
            {
                try
                {
                    var indexDocument = await GetDocumentAsync(indexUrl, cancellationToken);
                    if (sportGroup.Key == 2 && indexUrl == HorseFastUrl)
                    {
                        foreach (var fastResult in ParseHorseFastResults(indexDocument, sportGroup))
                            AddBestResult(output, fastResult);
                    }
                    links.AddRange(sportGroup.Key == 2
                        // Sporting Life now uses the race title/time itself as
                        // the result link on some meetings instead of rendering
                        // a child link whose text is exactly "Full Result".
                        // IsMatchingCard still enforces the full date, meeting
                        // and off-time identity before ParseHorseAsync is called.
                        ? indexDocument.QuerySelectorAll("a[href*='/racing/results/']")
                        : indexDocument.QuerySelectorAll("a[href*='/greyhounds/racecards/']")
                            .Where(x => x.GetAttribute("href")?.Contains("/racecard/", StringComparison.OrdinalIgnoreCase) == true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not read Sporting Life result index {IndexUrl}.", indexUrl);
                }
            }
            foreach (var candidate in sportGroup)
            {
                // The distance worker has already matched the exact Sporting Life
                // card by event, venue, off time and runners. Reuse that URL for
                // results because the results index can omit an entire evening
                // card after midnight.
                var knownCardUrl = candidate.SportId == 4
                    ? distances.Get(candidate.EventId)?.SourceUrl
                    : null;
                var sourceUrls = Enumerable.Repeat(knownCardUrl, string.IsNullOrWhiteSpace(knownCardUrl) ? 0 : 1)
                    .Concat(links.Where(link => IsMatchingCard(link, candidate))
                    .Select(x => x.GetAttribute("href"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? x : "https://www.sportinglife.com" + x))
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var sourceUrl in sourceUrls)
                {
                    try
                    {
                        var result = candidate.SportId == 2
                            ? await ParseHorseAsync(candidate, sourceUrl, cancellationToken)
                            : await ParseDogAsync(candidate, sourceUrl, cancellationToken);
                        if (result.Runners.Count == 0) continue;
                        AddBestResult(output, result);
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Could not read Sporting Life result for event {EventId} from {SourceUrl}.", candidate.EventId, sourceUrl);
                    }
                }
            }
        }
        return output.Values.ToList();
    }

    private static IReadOnlyList<ResultPage> ParseHorseFastResults(
        IDocument document,
        IEnumerable<ResultCandidate> candidates)
    {
        var candidateList = candidates.ToList();
        var output = new List<ResultPage>();
        foreach (var card in document.QuerySelectorAll("div[class*='FastResultWrapper']"))
        {
            var time = Clean(card.QuerySelector(".time-short")?.TextContent);
            var header = Clean(card.QuerySelector("[class*='HeaderTimeCourseText']")?.TextContent);
            var course = header;
            if (!string.IsNullOrWhiteSpace(time) &&
                course.StartsWith(time, StringComparison.OrdinalIgnoreCase))
                course = course[time.Length..].Trim();
            var candidate = candidateList.FirstOrDefault(item =>
            {
                var expected = DateTimeOffset.FromUnixTimeSeconds(item.StartTimeUnix).ToLocalTime();
                return expected.Date == DateTimeOffset.Now.Date &&
                       expected.ToString("HH:mm", CultureInfo.InvariantCulture) == time &&
                       RacingEventNames.MeetingKey(item.Meeting)
                           .Equals(RacingEventNames.MeetingKey(course), StringComparison.OrdinalIgnoreCase);
            });
            if (candidate is null) continue;

            var runners = new List<ResultRunner>();
            foreach (var row in card.QuerySelectorAll("div[class*='RideRow']"))
            {
                var positionText = Clean(row.QuerySelector("[class*='RidePosition']")?.TextContent);
                var positionMatch = Regex.Match(positionText, @"\d+");
                if (!positionMatch.Success ||
                    !int.TryParse(positionMatch.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position) ||
                    position <= 0)
                    continue;
                var name = Clean(row.QuerySelector("[class*='RideHorseName'] a")?.TextContent);
                name = Regex.Replace(name, @"\s*\([^)]*\)\s*$", "");
                var cloth = Regex.Match(
                    Clean(row.QuerySelector("[class*='RideNumber']")?.TextContent), @"\d+").Value;
                if (!string.IsNullOrWhiteSpace(name))
                    runners.Add(new(name, position, "Finished", cloth));
            }
            if (runners.All(x => x.Position != 1)) continue;

            int? expectedRunnerCount = null;
            foreach (var cell in card.QuerySelectorAll("td"))
            {
                if (!Clean(cell.TextContent).Equals("Runners:", StringComparison.OrdinalIgnoreCase)) continue;
                var match = Regex.Match(Clean(cell.NextElementSibling?.TextContent), @"\d+");
                if (match.Success && int.TryParse(match.Value, out var count)) expectedRunnerCount = count;
                break;
            }
            var sourceHref = card.QuerySelector(
                "a[href*='/racing/results/'], a[href*='/racing/racecards/']")?.GetAttribute("href");
            // The fast-results page itself has no trustworthy day marker. A
            // venue/time match can therefore select a cached or previous-day
            // card. Sporting Life initially links a fast result back to its
            // dated racecard, then replaces that with the dated full-result
            // URL. Either link safely confirms the candidate's exact date.
            if (string.IsNullOrWhiteSpace(sourceHref)) continue;
            var expectedDate = DateTimeOffset.FromUnixTimeSeconds(candidate.StartTimeUnix)
                .ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var hrefDate = Regex.Match(sourceHref, @"/(?<date>\d{4}-\d{2}-\d{2})/");
            if (!hrefDate.Success ||
                !hrefDate.Groups["date"].Value.Equals(expectedDate, StringComparison.Ordinal))
                continue;
            var sourceUrl = sourceHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? sourceHref
                : "https://www.sportinglife.com" + sourceHref;
            output.Add(new(
                candidate.EventId,
                candidate.SportId,
                sourceUrl,
                runners.OrderBy(x => x.Position).ToList(),
                expectedRunnerCount,
                false));
        }
        return output;
    }

    private static void AddBestResult(
        IDictionary<long, ResultPage> results,
        ResultPage candidate)
    {
        if (!results.TryGetValue(candidate.EventId, out var current))
        {
            results[candidate.EventId] = candidate;
            return;
        }
        var currentDepth = current.Runners.Count(x => x.Position is > 0);
        var candidateDepth = candidate.Runners.Count(x => x.Position is > 0);
        if (candidate.IsFinal && !current.IsFinal ||
            candidate.IsFinal == current.IsFinal && candidateDepth > currentDepth)
            results[candidate.EventId] = candidate;
    }

    private async Task<ResultPage> ParseHorseAsync(ResultCandidate candidate, string url, CancellationToken cancellationToken)
    {
        var document = await GetDocumentAsync(url, cancellationToken);
        var bodyText = Clean(document.Body?.TextContent);
        var expectedMatch = Regex.Match(bodyText, @"\b(\d+)\s+Runners?\b", RegexOptions.IgnoreCase);
        int? expectedRunnerCount = expectedMatch.Success && int.TryParse(expectedMatch.Groups[1].Value, out var expected)
            ? expected
            : null;
        var isFinal = bodyText.Contains("Weighed In", StringComparison.OrdinalIgnoreCase);
        var runners = new List<ResultRunner>();
        foreach (var row in document.QuerySelectorAll(".hr-racing-runner-position-container"))
        {
            // Prefer Sporting Life's stable test attributes. The generated
            // styled-component class names changed in August 2026 and left the
            // full-result page visible to users but empty to this parser.
            var positionText = Clean(
                row.QuerySelector("[data-test-id='position-no']")?.TextContent ??
                row.QuerySelector("[class*='StyledRunnerPositionContainer']")?.TextContent);
            var positionMatch = Regex.Match(positionText, @"\d+");
            int? position = positionMatch.Success && int.TryParse(positionMatch.Value, out var parsed) ? parsed : null;
            var name = Clean(
                row.QuerySelector("a[href*='/racing/profiles/horse/']")?.TextContent ??
                row.QuerySelector("[class*='StyledHorseName'] a")?.TextContent);
            name = Regex.Replace(name, @"\s*\([^)]*\)\s*$", "");
            var cloth = Regex.Match(Clean(
                row.QuerySelector("[data-testid='saddle-cloth-no']")?.TextContent ??
                row.QuerySelector("[class*='StyledSaddleClothNo']")?.TextContent), @"\d+").Value;
            if (!string.IsNullOrWhiteSpace(name)) runners.Add(new(name, position, position.HasValue ? "Finished" : positionText, cloth));
        }
        return new(candidate.EventId, candidate.SportId, url, runners, expectedRunnerCount, isFinal);
    }

    private async Task<ResultPage> ParseDogAsync(ResultCandidate candidate, string url, CancellationToken cancellationToken)
    {
        var document = await GetDocumentAsync(url, cancellationToken);
        var bodyText = Clean(document.Body?.TextContent);
        // Sporting Life occasionally emits a greyhound URL whose date segment is
        // one day behind the date rendered on the card. Validate the rendered
        // race date before reading positions so accepting that broken slug cannot
        // match a same-time race from the wrong day.
        var renderedDate = Regex.Match(bodyText,
            @"\b(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\w*\s+(?<day>\d{1,2})\s+(?<month>[A-Za-z]+)\s+(?<year>\d{4})\b",
            RegexOptions.IgnoreCase);
        if (renderedDate.Success &&
            DateTime.TryParseExact(
                $"{renderedDate.Groups["day"].Value} {renderedDate.Groups["month"].Value} {renderedDate.Groups["year"].Value}",
                "d MMMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pageDate) &&
            pageDate.Date != DateTimeOffset.FromUnixTimeSeconds(candidate.StartTimeUnix).ToLocalTime().Date)
            return new(candidate.EventId, candidate.SportId, url, [], null, false);
        var nextData = document.QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if (bodyText.Contains("Final Result", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(nextData))
        {
            try
            {
                using var noRaceJson = JsonDocument.Parse(nextData);
                var noRaceRuns = noRaceJson.RootElement.GetProperty("props").GetProperty("pageProps")
                    .GetProperty("race").GetProperty("runs").EnumerateArray()
                    .Where(run => run.TryGetProperty("run_status", out var status) &&
                                  status.GetString()?.Equals("RUNNER", StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();
                var isNoRace = noRaceRuns.Count > 0 && noRaceRuns.All(run =>
                    run.TryGetProperty("run_description", out var description) &&
                    Clean(description.GetString()).Contains("NoRace", StringComparison.OrdinalIgnoreCase));
                if (isNoRace)
                {
                    var voidRunners = noRaceRuns.Select(run =>
                    {
                        var trap = run.TryGetProperty("cloth_number", out var trapValue) && trapValue.TryGetInt32(out var parsedTrap)
                            ? parsedTrap.ToString(CultureInfo.InvariantCulture) : null;
                        var name = run.TryGetProperty("greyhound", out var greyhound) &&
                                   greyhound.TryGetProperty("name", out var nameValue)
                            ? Clean(nameValue.GetString()) : $"Trap {trap}";
                        return new ResultRunner(name, null, "No Race", trap);
                    }).Where(runner => !string.IsNullOrWhiteSpace(runner.Name)).ToList();
                    return new(candidate.EventId, candidate.SportId, url, voidRunners,
                        voidRunners.Count, true, true);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not inspect Sporting Life no-race state for event {EventId}.", candidate.EventId);
            }
        }
        // Sporting Life's adjacent elements can render as "5 ranWinning time"
        // in TextContent, so a trailing word boundary is not reliable here.
        var ranMatch = Regex.Match(bodyText, @"\b(\d+)\s+ran", RegexOptions.IgnoreCase);
        int? expectedRunnerCount = ranMatch.Success && int.TryParse(ranMatch.Groups[1].Value, out var ran) && ran > 0 ? ran : null;
        var isFinal = expectedRunnerCount.HasValue &&
            bodyText.Contains("Final Result", StringComparison.OrdinalIgnoreCase);
        // Racecard rows are displayed in trap order before a result exists. Treating
        // that order as finishing positions can settle the wrong dog provisionally.
        if (!isFinal)
            return new(candidate.EventId, candidate.SportId, url, [], expectedRunnerCount, false);

        // The visual result has two numeric columns: finishing position first,
        // then trap number. Its runner wrappers remain in trap order, so using
        // DOM row order as the finish order incorrectly produces a 1-2-3-4-5-6
        // result. Sporting Life embeds the authoritative finish_position and
        // cloth_number fields in __NEXT_DATA__; use those explicit fields.
        var runners = new List<ResultRunner>();
        try
        {
            if (!string.IsNullOrWhiteSpace(nextData))
            {
                using var json = JsonDocument.Parse(nextData);
                var race = json.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("race");
                var structuredRuns = race.GetProperty("runs").EnumerateArray().ToList();
                var starters = structuredRuns.Where(run =>
                    run.TryGetProperty("run_status", out var status) &&
                    status.GetString()?.Equals("RUNNER", StringComparison.OrdinalIgnoreCase) == true).ToList();
                expectedRunnerCount = starters.Count > 0 ? starters.Count : expectedRunnerCount;
                foreach (var run in starters)
                {
                    if (!run.TryGetProperty("finish_position", out var positionValue) ||
                        !positionValue.TryGetInt32(out var position) || position <= 0 ||
                        !run.TryGetProperty("cloth_number", out var trapValue) ||
                        !trapValue.TryGetInt32(out var trap) || trap <= 0 ||
                        !run.TryGetProperty("greyhound", out var greyhound) ||
                        !greyhound.TryGetProperty("name", out var nameValue)) continue;
                    var name = Clean(nameValue.GetString());
                    var finishDistance = run.TryGetProperty("finish_distance", out var distanceValue)
                        ? Clean(distanceValue.GetString()) : null;
                    if (!string.IsNullOrWhiteSpace(name))
                        runners.Add(new(name, position, "Finished", trap.ToString(CultureInfo.InvariantCulture), finishDistance));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not parse structured Sporting Life greyhound result for event {EventId}.", candidate.EventId);
        }
        // Never infer positions from DOM ordering. If the structured payload is
        // absent, leave this source unresolved so BetsAPI/Betfred can safely win.
        // Tied runners occupy consecutive finishing slots, so the first three places are
        // accounted for when at least `place` runners finished at or above each place -
        // 1,2,2 and 1,1,3 are both complete podiums. Requiring exactly one runner per place
        // treated every dead heat as an incomplete result.
        var hasTopThree = Enumerable.Range(1, 3).All(
            place => runners.Count(x => x.Position is int position && position > 0 && position <= place) >= place);
        return new(candidate.EventId, candidate.SportId, url, runners,
            hasTopThree && runners.Count < expectedRunnerCount ? null : expectedRunnerCount, hasTopThree);
    }

    private async Task<IDocument> GetDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return await _parser.ParseDocumentAsync(html, cancellationToken);
    }

    private static bool IsMatchingCard(IElement link, ResultCandidate candidate)
    {
        var expectedStart = DateTimeOffset.FromUnixTimeSeconds(candidate.StartTimeUnix).ToLocalTime();
        var expectedDate = expectedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var expectedTime = expectedStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        var meeting = Normalize(candidate.Meeting);
        var rawHref = link.GetAttribute("href") ?? "";
        var hrefDate = Regex.Match(rawHref, @"/(?<date>\d{4}-\d{2}-\d{2})/");
        // Result indexes contain multiple days and meetings commonly reuse the
        // same off times. Never permit a venue/time match to cross a race date.
        var previousDate = expectedStart.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!hrefDate.Success ||
            !hrefDate.Groups["date"].Value.Equals(expectedDate, StringComparison.Ordinal) &&
            !hrefDate.Groups["date"].Value.Equals(previousDate, StringComparison.Ordinal))
            return false;
        var href = Normalize(rawHref);
        var linkText = Normalize(link.TextContent);
        if (Regex.IsMatch(linkText, @"\b\d{1,2}:\d{2}\b"))
            return linkText.Contains(expectedTime) && href.Contains(meeting, StringComparison.OrdinalIgnoreCase);
        IElement? current = link;
        // Only inspect the link's own race header. Climbing into a meeting/day container
        // can make one race match a different time at the same venue.
        for (var depth = 0; depth < 4 && current is not null; depth++, current = current.ParentElement)
        {
            var text = Normalize(current.TextContent);
            if (text.Contains(expectedTime, StringComparison.OrdinalIgnoreCase) && text.Contains(meeting, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var eventText = Normalize(candidate.Name);
        return eventText.Contains(expectedTime) && Normalize(link.ParentElement?.TextContent).Contains(meeting, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) => Regex.Replace(Clean(value).Replace(" EVE ", " ", StringComparison.OrdinalIgnoreCase), @"[^a-zA-Z0-9:]+", " ").Trim();
    private static string Clean(string? value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
}

internal sealed class BetfredResultClient(
    IHttpClientFactory httpClientFactory,
    RacingFeedRepository feedRepository,
    ILogger<BetfredResultClient> logger)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly Dictionary<long, DateTime> _nextPollUtc = [];

    public void ResetPolling() => _nextPollUtc.Clear();

    public async Task<IReadOnlyList<ResultPage>> FindResultsAsync(
        IReadOnlyList<ResultCandidate> candidates, CancellationToken cancellationToken, bool requireReadiness = true)
    {
        var now = DateTime.UtcNow;
        var due = candidates
            .Where(candidate => candidate.SportId == 4 && candidate.StartTimeUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                (!_nextPollUtc.TryGetValue(candidate.EventId, out var next) || next <= now))
            // Do not cap this list: a fixed newest-first cap can permanently
            // starve earlier suspended races during a busy card.
            .OrderBy(candidate => candidate.StartTimeUnix)
            .ToList();
        foreach (var candidate in due) _nextPollUtc[candidate.EventId] = now.Add(PollInterval);

        var output = new List<ResultPage>();
        foreach (var candidate in due)
        {
            var betfredEventId = await feedRepository.GetBetfredEventIdAsync(candidate.EventId, cancellationToken);
            if (betfredEventId is null) continue;
            try
            {
                var client = httpClientFactory.CreateClient("Bet365Capture");
                // Betfred exposes resultsEntered when the coupon/event has the
                // finishing-post/full-result state. Do not navigate the visible
                // browser into an event until that signal is present.
                if (requireReadiness)
                {
                    using var readinessResponse = await client.GetAsync(
                        $"http://127.0.0.1:5278/api/betfred/event/{betfredEventId.Value}", cancellationToken);
                    if (!readinessResponse.IsSuccessStatusCode) continue;
                    await using var readinessStream = await readinessResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var readinessEnvelope = await JsonDocument.ParseAsync(readinessStream, cancellationToken: cancellationToken);
                    if (!readinessEnvelope.RootElement.TryGetProperty("body", out var bodyValue) ||
                        bodyValue.ValueKind != JsonValueKind.String ||
                        !IsBetfredResultReady(bodyValue.GetString())) continue;
                }

                using var response = await client.GetAsync(
                    $"http://127.0.0.1:5278/api/betfred/winning-post/{betfredEventId.Value}", cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!json.RootElement.TryGetProperty("resultRows", out var rows) || rows.ValueKind != JsonValueKind.Array)
                    continue;
                var runners = new List<ResultRunner>();
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("position", out var positionValue) || !positionValue.TryGetInt32(out var position) ||
                        !row.TryGetProperty("trap", out var trapValue) || !trapValue.TryGetInt32(out var trap) ||
                        !row.TryGetProperty("name", out var nameValue)) continue;
                    var name = nameValue.GetString()?.Trim();
                    if (position <= 0 || trap <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                    runners.Add(new(name, position, "Finished", trap.ToString(CultureInfo.InvariantCulture)));
                }
                // De-duplicate on the runner, not the position. Grouping by position alone
                // discarded the second runner of a dead heat, and which one survived came
                // down to row order - the dropped co-winner then settled as a loser.
                runners = runners.GroupBy(runner => (runner.Position, runner.Identifier))
                    .Select(group => group.First())
                    .OrderBy(runner => runner.Position).ToList();
                if (runners.Count < 3 || !Enumerable.Range(1, 3).All(
                        place => runners.Count(runner => runner.Position is int position && position > 0 && position <= place) >= place))
                    continue;
                output.Add(new ResultPage(candidate.EventId, 4,
                    $"https://www.betfred.com/sports/greyhound-racing/event/{betfredEventId.Value}",
                    runners, runners.Count >= 5 ? runners.Count : null, true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Betfred Winning Post lookup failed for event {EventId}.", candidate.EventId);
            }
        }
        return output;
    }

    private static bool IsBetfredResultReady(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("events", out var events) &&
                   events.ValueKind == JsonValueKind.Array &&
                   events.EnumerateArray().Any(item =>
                       item.TryGetProperty("resultsEntered", out var entered) && entered.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class RacingResultWorker(
    RacingResultRepository repository,
    RacingResultClient client,
    BetsApiResultClient betsApiResultClient,
    BetfredResultClient betfredResultClient,
    RacingPublisher publisher,
    TrapChallengePublishingWorker trapChallengePublisher,
    RacingActivityLog activity,
    RaceAuditTrail audit,
    ILogger<RacingResultWorker> logger) : BackgroundService
{
    private readonly Dictionary<long, DateTime> _finalResultReplayDueUtc = new();
    private readonly HashSet<long> _finalResultReplayCompleted = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var candidates = await repository.GetCandidatesAsync(stoppingToken);
                // Betfred's Winning Post can be populated before the external
                // results sites. Poll it first so a complete top-three result is
                // published as soon as Betfred marks resultsEntered.
                var betfredResults = await betfredResultClient.FindResultsAsync(candidates, stoppingToken);
                var betfredEventIds = betfredResults.Where(x => x.IsFinal).Select(x => x.EventId).ToHashSet();
                var sportingLifeResults = await client.FindResultsAsync(
                    candidates.Where(x => !betfredEventIds.Contains(x.EventId)).ToList(), stoppingToken);
                // BetsAPI is polled every 30 seconds only for races still absent
                // from Betfred and Sporting Life in this cycle.
                var sportingLifeEventIds = sportingLifeResults.Where(x => x.IsFinal).Select(x => x.EventId).ToHashSet();
                var betsApiResults = await betsApiResultClient.FindResultsAsync(
                    candidates.Where(x => !betfredEventIds.Contains(x.EventId) &&
                                          !sportingLifeEventIds.Contains(x.EventId)).ToList(), stoppingToken);
                var results = betfredResults.Concat(sportingLifeResults).Concat(betsApiResults).ToList();
                var settledEventIds = new HashSet<long>();
                foreach (var result in results)
                {
                    var candidate = candidates.FirstOrDefault(item => item.EventId == result.EventId);
                    var isBetsApi = result.SourceUrl.Contains("api.b365api.com", StringComparison.OrdinalIgnoreCase);
                    var isBetfred = result.SourceUrl.Contains("betfred.com", StringComparison.OrdinalIgnoreCase);
                    await audit.WriteAsync(result.IsFinal ? "final-result-received" : "fast-result-received",
                        result.EventId, result.SportId, candidate?.Meeting, candidate?.Name,
                        candidate?.StartTimeUnix ?? 0, isBetsApi ? "BETSAPI" : isBetfred ? "BETFRED" : "SPORTINGLIFE",
                        isBetsApi ? "bet365/result raw" : isBetfred ? "Winning Post event page" : result.IsFinal ? "full result page" : "fast results page",
                        isBetsApi
                            ? "Top-three trap placings returned by the dedicated BetsAPI result endpoint."
                            : isBetfred
                            ? "Betfred Winning Post returned the trap-number finishing order."
                            : "Result matched by exact race date, meeting, time and runner evidence.",
                        new
                        {
                            result.SourceUrl, result.ExpectedRunnerCount,
                            placings = result.Runners.Where(runner => runner.Position is > 0)
                                .Select(runner => new { runner.Name, runner.Position, runner.Status, runner.Identifier })
                        }, stoppingToken);
                    if (result.IsFinal &&
                        !_finalResultReplayCompleted.Contains(result.EventId) &&
                        !_finalResultReplayDueUtc.ContainsKey(result.EventId))
                        _finalResultReplayDueUtc[result.EventId] = DateTime.UtcNow.AddSeconds(60);
                    var settled = await repository.SettleAsync(result, stoppingToken);
                    if (settled is null) continue;
                    settledEventIds.Add(result.EventId);
                    if (result.SportId == 4 && candidate is not null)
                        await trapChallengePublisher.PublishMeetingAsync(
                            candidate.Meeting, candidate.StartTimeUnix, stoppingToken);
                    await publisher.PublishAsync(settled, stoppingToken);
                    var resultKind=result.IsFinal?"final-result":"provisional-result";
                    var positionCount=result.Runners.Count(x=>x.Position is>0);
                    await activity.WriteAsync(resultKind,
                        $"{(result.IsFinal?"Final":"Fast")} result published with {positionCount} placings.",
                        result.SportId, result.EventId, settled.Event.Meeting, settled.Event.Name, stoppingToken);
                    await audit.WriteAsync(result.IsFinal ? "final-result-published" : "provisional-result-published",
                        result.EventId, result.SportId, settled.Event.Meeting, settled.Event.Name,
                        settled.Event.StartTimeUnix, "RACINGTRADER", "result settlement and RabbitMQ publishing",
                        "Result stored, markets settled and updated payloads sent.",
                        new { positionCount, result.SourceUrl }, stoppingToken);
                    logger.LogInformation(
                        "{ResultKind} result with {RunnerCount} placings settled and published for event {EventId}.",
                        result.IsFinal?"Final":"Provisional",positionCount,result.EventId);
                }
                // Forecast pairs can be added or rebuilt after the ordinary race
                // result transaction. Once a confirmed first and second are stored,
                // settle any remaining forecast selections automatically instead of
                // relying on the manual repair endpoint.
                foreach (var eventId in await repository.GetRecentForecastRepairEventIdsAsync(stoppingToken))
                {
                    var forecastSnapshot = await repository.RepairForecastsAsync(eventId, stoppingToken);
                    if (forecastSnapshot is null) continue;
                    await publisher.PublishAsync(forecastSnapshot, stoppingToken);
                    await activity.WriteAsync("forecast-result",
                        "Forecast result repaired automatically from the stored confirmed first and second.",
                        forecastSnapshot.Event.SportId, eventId, forecastSnapshot.Event.Meeting,
                        forecastSnapshot.Event.Name, stoppingToken);
                    await audit.WriteAsync("forecast-result-repaired", eventId,
                        forecastSnapshot.Event.SportId, forecastSnapshot.Event.Meeting,
                        forecastSnapshot.Event.Name, forecastSnapshot.Event.StartTimeUnix,
                        "RACINGTRADER", "stored first/second automatic repair",
                        "Previously unset forecast selections were settled and republished.",
                        cancellationToken: stoppingToken);
                }
                foreach (var eventId in _finalResultReplayDueUtc
                             .Where(item => item.Value <= DateTime.UtcNow)
                             .Select(item => item.Key)
                             .ToList())
                {
                    try
                    {
                        var replaySnapshot = await repository.BuildStoredResultSnapshotAsync(eventId, stoppingToken);
                        if (replaySnapshot is null)
                        {
                            // The candidate was not safely settled (for example,
                            // it never received an OFF signal). Remove this due
                            // entry; a later confirmed result observation can
                            // schedule a fresh replay if stored state then exists.
                            _finalResultReplayDueUtc.Remove(eventId);
                            continue;
                        }

                        await publisher.PublishAsync(replaySnapshot, stoppingToken);
                        _finalResultReplayDueUtc.Remove(eventId);
                        _finalResultReplayCompleted.Add(eventId);
                        await activity.WriteAsync("final-result-replayed",
                            "Complete stored result replayed 60 seconds after confirmation.",
                            replaySnapshot.Event.SportId, eventId, replaySnapshot.Event.Meeting,
                            replaySnapshot.Event.Name, stoppingToken);
                        await audit.WriteAsync("final-result-replayed", eventId,
                            replaySnapshot.Event.SportId, replaySnapshot.Event.Meeting,
                            replaySnapshot.Event.Name, replaySnapshot.Event.StartTimeUnix,
                            "RACINGTRADER", "60-second complete-result replay",
                            "All stored result markets and selections were rebuilt and republished.",
                            cancellationToken: stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _finalResultReplayDueUtc[eventId] = DateTime.UtcNow.AddSeconds(15);
                        logger.LogWarning(ex,
                            "Complete result replay failed for event {EventId}; retrying in 15 seconds.", eventId);
                    }
                }
                // Late evening Sporting Life results can cross the date boundary.
                // Keep retrying for a full day before the terminal no-result
                // safeguard is allowed to void/close an event.
                var staleCutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
                foreach (var candidate in candidates.Where(x => x.StartTimeUnix <= staleCutoff && !settledEventIds.Contains(x.EventId)))
                {
                    var closed = await repository.CloseUnresultedAsync(candidate.EventId, stoppingToken);
                    if (closed is null) continue;
                    await publisher.PublishAsync(closed, stoppingToken);
                    await audit.WriteAsync("closed-without-result", candidate.EventId, candidate.SportId,
                        candidate.Meeting, candidate.Name, candidate.StartTimeUnix,
                        "RACINGTRADER", "24-hour stale-result safeguard",
                        "Race closed without a trustworthy result.", cancellationToken: stoppingToken);
                    await activity.WriteAsync("closed-no-result",
                        "Old race closed after no trustworthy final result was available.",
                        candidate.SportId, candidate.EventId, candidate.Meeting, candidate.Name, stoppingToken);
                    logger.LogWarning("Closed stale event {EventId} without a result after the result lookup found no trustworthy final.", candidate.EventId);
                }
            }
            catch (OperationCanceledException ex) when (!stoppingToken.IsCancellationRequested)
            {
                // HttpClient represents its own request timeout as a cancelled task.
                // It is a failed polling cycle, not a request to stop the host.
                logger.LogWarning(ex, "Hosted result cycle timed out; it will retry.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Hosted result cycle failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
