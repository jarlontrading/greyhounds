using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace RacingTrader.Web.Services;

internal sealed record DistanceCard(string Url, int Metres, DateTime? OffUtc,
    string? RawMargin, decimal? Margin, IReadOnlyList<string> RunnerNames);

internal sealed class GreyhoundDistanceClient(HttpClient http)
{
    private readonly HtmlParser _parser = new();
    private readonly Dictionary<string, (DateTime Expires, (string Url, string Time)[] Links)> _indexes = new();
    private readonly Dictionary<string, (DateTime Expires, string Data)> _cards = new();

    public async Task<DistanceCard?> FindAsync(PricingEvent ev, string? knownUrl, CancellationToken ct)
    {
        var day = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimeUnix).UtcDateTime.ToString("yyyy-MM-dd");
        string[] urls;
        if (!string.IsNullOrEmpty(knownUrl)) urls = [knownUrl];
        else
        {
            if (!_indexes.TryGetValue(day, out var index) || index.Expires <= DateTime.UtcNow)
            {
                if (string.CompareOrdinal(day, DateTime.UtcNow.ToString("yyyy-MM-dd")) > 0) return null;
                var indexUrl = day == DateTime.UtcNow.ToString("yyyy-MM-dd")
                    ? "https://www.sportinglife.com/greyhounds/racecards"
                    : $"https://www.sportinglife.com/greyhounds/results/{day}";
                var html = await http.GetStringAsync(indexUrl, ct);
                var doc = await _parser.ParseDocumentAsync(html, ct);
                var found = doc.QuerySelectorAll("a[href*='/racecard/']")
                    .Select(a => (Uri: new Uri(new Uri("https://www.sportinglife.com"), a.GetAttribute("href")!),
                        Time: a.QuerySelector(".time-short")?.TextContent.Trim() ?? ""))
                    .Where(link => link.Uri.Host == "www.sportinglife.com" && link.Uri.AbsolutePath.Contains($"/greyhounds/racecards/{day}/"))
                    .Select(link => (link.Uri.AbsoluteUri, link.Time)).Distinct().ToArray();
                index = (DateTime.UtcNow.AddMinutes(2), found);
                _indexes[day] = index;
            }
            var localTime = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ev.StartTimeUnix),
                TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("HH:mm");
            urls = index.Links.Where(link => (link.Time.Length == 0 || link.Time == localTime) &&
                GreyhoundDistanceMarkets.TrackKey(new Uri(link.Url).Segments[^3].Trim('/')) == GreyhoundDistanceMarkets.TrackKey(ev.Meeting))
                .Select(link => link.Url).ToArray();
        }
        foreach (var url in urls)
        {
            if (!_cards.TryGetValue(url, out var cached) || cached.Expires <= DateTime.UtcNow)
            {
                var doc = await _parser.ParseDocumentAsync(await http.GetStringAsync(url, ct), ct);
                var data = doc.QuerySelector("script#__NEXT_DATA__")?.TextContent;
                if (string.IsNullOrWhiteSpace(data)) continue;
                cached = (DateTime.UtcNow.AddSeconds(10), data);
                _cards[url] = cached;
            }
            var card = Parse(cached.Data, url, ev);
            if (card is not null) return card;
        }
        return null;
    }

    internal static DistanceCard? Parse(string data, string url, PricingEvent ev)
    {
        using var json = JsonDocument.Parse(data);
        var race = json.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("race");
        var summary = race.GetProperty("race_summary");
        string? Field(string key) => summary.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        // The structured source time and off_time are UTC, even when the visible card uses BST.
        if (!DateTimeOffset.TryParseExact($"{Field("date")} {Field("time")}", "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) ||
            start.ToUnixTimeSeconds() != ev.StartTimeUnix || ev.SportId != 4 ||
            GreyhoundDistanceMarkets.TrackKey(Field("course_name") ?? "") != GreyhoundDistanceMarkets.TrackKey(ev.Meeting) ||
            !int.TryParse(Field("distance"), out var metres) || metres <= 0) return null;
        DateTime? off = DateTimeOffset.TryParseExact($"{Field("date")} {Field("off_time")}", "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var actual) ? actual.UtcDateTime : null;
        var runs = race.GetProperty("runs").EnumerateArray().Where(run =>
            run.TryGetProperty("run_status", out var status) && status.GetString() == "RUNNER").ToArray();
        var names = runs.Select(run => run.GetProperty("greyhound").GetProperty("name").GetString() ?? "").ToArray();
        int Position(JsonElement run) => run.TryGetProperty("finish_position", out var value) && value.TryGetInt32(out var pos) ? pos : 0;
        string? raw = null;
        decimal? margin = null;
        if (Field("race_stage") == "Final Result")
        {
            var winners = runs.Count(run => Position(run) == 1);
            var seconds = runs.Where(run => Position(run) == 2).ToArray();
            if (winners > 1) { raw = "dead heat"; margin = 0m; }
            else if (winners == 1 && seconds.Length == 1 && seconds[0].TryGetProperty("finish_distance", out var distance))
            {
                raw = distance.ValueKind == JsonValueKind.String ? distance.GetString() : null;
                margin = GreyhoundDistanceMarkets.ParseMargin(raw);
            }
        }
        return new(url, metres, off, raw, margin, names);
    }
}

internal sealed class GreyhoundDistanceWorker(GreyhoundDistanceMarkets distances, GreyhoundDistanceClient client,
    RacingFeedRepository repository, RacingPublisher publisher, ILogger<GreyhoundDistanceWorker> logger) : BackgroundService
{
    // Bumped by hand when the pricing *shape* changes. It no longer stands in for the
    // contents of the price table: the publication signature also carries the table's own
    // fingerprint, so regenerating fixed-margin-markets-min50.txt is visible rather than
    // silently re-settling bets struck against the previous bands.
    private const string PriceVersion = "fractional-v1";
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the host finish starting and bind the HTTP port before the first
        // potentially large backfill sweep begins.
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { distances.LastError = null; await PollAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { distances.LastError = ex.Message; logger.LogError(ex, "Distance market polling failed."); }
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var events = await repository.GetDistanceEventsAsync(ct);
        foreach (var ev in events)
        {
            try
            {
                var now = DateTime.UtcNow;
                var saved = distances.Get(ev.EventId);
                // Backfills never enter latency averages. Keep pending observations across restarts.
                var interval = saved?.PublishedSignature is null ||
                    !saved.PublishedSignature.Contains(':' + PriceVersion + ':', StringComparison.Ordinal)
                    ? TimeSpan.Zero
                    : saved.Margin.HasValue ? TimeSpan.FromMinutes(5)
                    : ev.StartTimeUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600 ? TimeSpan.FromMinutes(5)
                    : now - DateTimeOffset.FromUnixTimeSeconds(ev.StartTimeUnix).UtcDateTime > TimeSpan.FromHours(2)
                        ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
                if (saved is not null && now - saved.CheckedUtc < interval) continue;
                var card = await client.FindAsync(ev, saved?.SourceUrl, ct);
                if (card is null) continue;
                if (!await repository.DistanceRunnerNamesMatchAsync(ev.EventId, card.RunnerNames, ct)) continue;
                if (saved is not null && saved.Metres != card.Metres)
                    throw new InvalidDataException($"Race distance changed for {ev.EventId}; preserving the original market for review.");
                now = DateTime.UtcNow;
                // Once a table has been published there are bets against those exact bands
                // and labels, and settlement re-reads the bands from whatever table is
                // loaded now. Regenerating the price file - the intended maintenance action
                // - would therefore settle an "over 2.5" bet against a 3.5 boundary with the
                // selection name still reading 2.5. Keep the published table and raise it.
                var currentTable = distances.Find(ev.Meeting, card.Metres) ?? saved?.Prices;
                var table = currentTable;
                if (saved?.PublishedSignature is not null && saved.Prices is not null &&
                    currentTable is not null && currentTable.Fingerprint != saved.Prices.Fingerprint)
                {
                    table = saved.Prices;
                    logger.LogError(
                        "Distance price table for {Track} {Metres}m changed after event {EventId} was published ({Published} -> {Current}); keeping the published bands. Settle or void the open markets before regenerating again.",
                        ev.Meeting, card.Metres, ev.EventId, saved.Prices.Fingerprint, currentTable.Fingerprint);
                }
                var firstSeen = saved?.FirstSeenUtc ?? now;
                var off = card.OffUtc ?? saved?.ActualOffUtc;
                var missing = card.Margin is null && saved?.Margin is null ? now : saved?.LastMissingUtc;
                var marginSeen = card.Margin.HasValue ? saved?.MarginSeenUtc ?? now : saved?.MarginSeenUtc;
                var timed = off.HasValue && firstSeen < off && missing >= off && marginSeen >= missing;
                var observation = new DistanceObservation(ev.EventId, ev.Meeting, ev.StartTimeUnix, card.Url, card.Metres,
                    firstSeen, now, off, missing, marginSeen, card.RawMargin ?? saved?.RawMargin,
                    card.Margin ?? saved?.Margin, saved?.TimedSample == true || timed,
                    table, saved?.PublishedSignature);
                distances.Put(observation);
                await distances.SaveAsync(ct);
                if (observation.Prices is null) continue;
                var signature = $"{ev.Status}:{observation.Margin?.ToString(CultureInfo.InvariantCulture) ?? "pending"}:{PriceVersion}:{observation.Prices.Fingerprint}";
                if (signature == observation.PublishedSignature) continue;
                var snapshot = await repository.SaveDistanceMarketAsync(ev.EventId, distances, ct);
                if (snapshot is null) continue;
                await publisher.PublishAsync(snapshot, ct);
                distances.Put(observation with { PublishedSignature = signature });
                await distances.SaveAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { distances.LastError = ex.Message; logger.LogWarning(ex, "Distance observation failed for {EventId}.", ev.EventId); }
        }
        distances.LastPollUtc = DateTime.UtcNow;
    }
}
