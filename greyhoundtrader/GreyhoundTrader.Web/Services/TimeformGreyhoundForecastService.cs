using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

public sealed partial class TimeformGreyhoundForecastService(
    IHttpClientFactory clients,
    ILogger<TimeformGreyhoundForecastService> logger) : BackgroundService
{
    private const string IndexUrl = "https://www.timeform.com/greyhound-racing/racecards";
    private static readonly TimeZoneInfo UkZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London");
    private readonly ConcurrentDictionary<string, ForecastRace> _races = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loadedUrls = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _loadedDate;
    private DateTime? _lastRefreshUtc;
    private string? _lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var nextRefresh = TimeSpan.FromMinutes(30);
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                nextRefresh = TimeSpan.FromMinutes(2);
                logger.LogWarning(ex, "Could not refresh Timeform greyhound forecasts; retrying shortly.");
            }
            await Task.Delay(nextRefresh, stoppingToken);
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, UkZone).Date);
        if (_loadedDate != today)
        {
            _loadedDate = today;
            _races.Clear();
            _loadedUrls.Clear();
        }

        var http = clients.CreateClient("TimeformGreyhounds");
        var html = await http.GetStringAsync(IndexUrl, cancellationToken);
        var cards = RacecardLinkRegex().Matches(html).Select(match => new RacecardLink(
                WebUtility.HtmlDecode(match.Groups["url"].Value),
                match.Groups["track"].Value,
                match.Groups["time"].Value,
                match.Groups["date"].Value))
            .Where(card => card.Date == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .DistinctBy(card => card.Url, StringComparer.OrdinalIgnoreCase)
            .Where(card => !_loadedUrls.ContainsKey(card.Url))
            .ToList();

        using var gate = new SemaphoreSlim(8, 8);
        await Task.WhenAll(cards.Select(async card =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var page = await http.GetStringAsync(Absolute(card.Url), cancellationToken);
                var race = Parse(card, page);
                if (race is null) return;
                _races[Key(race.Meeting, race.StartTimeUnix)] = race;
                _loadedUrls[card.Url] = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Timeform forecast unavailable for {Track} {Time}.", card.Track, card.Time);
            }
            finally { gate.Release(); }
        }));
        _lastRefreshUtc = DateTime.UtcNow;
        _lastError = null;
        logger.LogInformation("Timeform forecast cache holds {RaceCount} races ({NewCount} loaded this pass).",
            _races.Count, cards.Count);
    }

    internal IReadOnlyList<RunnerRow> Enrich(EventSummary racingEvent, IReadOnlyList<RunnerRow> runners)
    {
        var forecast = Match(racingEvent.Meeting, racingEvent.StartTimeUnix);
        if (forecast is null) return runners;
        return runners.Select(runner => runner with
        {
            TimeformForecastPrice = forecast.Prices.GetValueOrDefault(NormalizeRunner(runner.Name)),
            TimeformForecastUpdatedAtUtc = forecast.CapturedAtUtc
        }).ToList();
    }

    internal IReadOnlyList<TrapChallengeRaceInput> ApplyTrapChallengeFallback(
        string meeting, IReadOnlyList<TrapChallengeRaceInput> races)
    {
        return races.Select(race =>
        {
            var entries = race.Runners
                .GroupBy(runner => runner.Trap)
                .Select(group => group
                    .OrderBy(runner => runner.Scratch || IsVacant(runner.Name))
                    .ThenByDescending(runner => EffectivePrice(runner) > 1)
                    .First())
                .ToList();
            var live = entries.Where(runner => !runner.Scratch && !IsVacant(runner.Name)).ToList();
            if (live.Count >= 2 && live.All(runner => EffectivePrice(runner) > 1)) return race;
            var forecast = Match(meeting, race.StartTimeUnix);
            if (forecast is null || IsResulted(race.Status) || forecast.Runners.Count < 2)
                return race;
            return race with
            {
                // Timeform's racecard supplies the trap mapping as well as the tissue.
                // Replacing the incomplete input also drops vacant/non-runner traps.
                Runners = forecast.Runners.Select(runner => new TrapChallengeRunnerInput(
                    runner.Trap, runner.Name, runner.Price, runner.Price, false, null, null, null)).ToList()
            };
        }).ToList();
    }

    internal object Health() => new
    {
        source = "TIMEFORM_FORECAST",
        races = _races.Count,
        loadedDate = _loadedDate == default ? null : _loadedDate.ToString("yyyy-MM-dd"),
        lastRefreshUtc = _lastRefreshUtc,
        lastError = _lastError
    };

    private ForecastRace? Match(string meeting, long startTimeUnix)
    {
        var exact = _races.GetValueOrDefault(Key(meeting, startTimeUnix));
        if (exact is not null) return exact;
        var normalizedMeeting = NormalizeMeeting(meeting);
        return _races.Values.Where(race => race.Meeting == normalizedMeeting &&
                Math.Abs(race.StartTimeUnix - startTimeUnix) <= 90)
            .OrderBy(race => Math.Abs(race.StartTimeUnix - startTimeUnix)).FirstOrDefault();
    }

    private static ForecastRace? Parse(RacecardLink card, string html)
    {
        var block = ForecastRegex().Match(html);
        if (!block.Success) return null;
        var text = WebUtility.HtmlDecode(TagRegex().Replace(block.Groups["body"].Value, " "));
        var prices = ForecastPriceRegex().Matches(text).Select(match => new
            {
                Name = NormalizeRunner(match.Groups["name"].Value.Trim().TrimEnd(',')),
                Price = DecimalPrice(match.Groups["odds"].Value)
            })
            .Where(item => item.Name.Length > 0 && item.Price > 1)
            .GroupBy(item => item.Name).ToDictionary(group => group.Key, group => group.First().Price,
                StringComparer.Ordinal);
        var runners = RaceRunnerRegex().Matches(html).Select(match => new
            {
                Trap = int.Parse(match.Groups["trap"].Value, CultureInfo.InvariantCulture),
                Name = WebUtility.HtmlDecode(TagRegex().Replace(match.Groups["name"].Value, " ")).Trim()
            })
            .Select(item => new ForecastRunner(item.Trap, item.Name,
                prices.GetValueOrDefault(NormalizeRunner(item.Name))))
            .Where(item => item.Trap is >= 1 and <= 6 && item.Price > 1)
            .DistinctBy(item => item.Trap)
            .ToList();
        if (prices.Count < 2 || !DateTime.TryParseExact(card.Date + card.Time, "yyyy-MM-ddHHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), UkZone);
        return new ForecastRace(NormalizeMeeting(card.Track), new DateTimeOffset(utc).ToUnixTimeSeconds(),
            prices, runners, DateTime.UtcNow, Absolute(card.Url));
    }

    private static double DecimalPrice(string text)
    {
        if (text.Equals("EVS", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("EVENS", StringComparison.OrdinalIgnoreCase)) return 2;
        var parts = text.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var n) &&
               double.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0
            ? 1 + n / d : 0;
    }

    private static string Absolute(string url) => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? url : "https://www.timeform.com" + (url.StartsWith('/') ? url : "/" + url);
    private static string Key(string meeting, long unix) => $"{NormalizeMeeting(meeting)}:{unix / 60}";
    private static string NormalizeMeeting(string value)
    {
        var meeting = value.Replace('-', ' ').Trim();
        foreach (var suffix in new[] { " EVE", " AM", " PM" })
            if (meeting.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                meeting = meeting[..^suffix.Length];
        return NormalizeRunner(meeting);
    }
    private static string NormalizeRunner(string value) => new(value.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static bool IsVacant(string name) => name.Trim().StartsWith("Vacant", StringComparison.OrdinalIgnoreCase) ||
                                                  name.Trim().Equals("Reserve", StringComparison.OrdinalIgnoreCase);
    private static bool IsResulted(string status) => status.Trim().ToUpperInvariant() is
        "3" or "RESULTED" or "FINISHED" or "CLOSED" or "SETTLED";
    private static double EffectivePrice(TrapChallengeRunnerInput runner) =>
        runner.CurrentPrice > 1 ? runner.CurrentPrice : runner.PreviousPrice > 1 ? runner.PreviousPrice : 0;

    private sealed record RacecardLink(string Url, string Track, string Time, string Date);
    private sealed record ForecastRace(string Meeting, long StartTimeUnix,
        IReadOnlyDictionary<string, double> Prices, IReadOnlyList<ForecastRunner> Runners,
        DateTime CapturedAtUtc, string Url);
    private sealed record ForecastRunner(int Trap, string Name, double Price);

    [GeneratedRegex("href=\"(?<url>[^\"]*/greyhound-racing/racecards/(?<track>[^/]+)/(?<time>\\d{4})/(?<date>\\d{4}-\\d{2}-\\d{2})/\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RacecardLinkRegex();
    [GeneratedRegex("<p[^>]*class=\"[^\"]*rpf-betting-forecast[^\"]*\"[^>]*>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ForecastRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
    [GeneratedRegex("(?<odds>\\d+/\\d+|EVS|EVENS)\\s+(?<name>[^,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ForecastPriceRegex();
    [GeneratedRegex("<tbody[^>]*class=\"[^\"]*\\brpb\\b[^\"]*\"[^>]*data-trap=\"(?<trap>[1-6])\"[^>]*>.*?<a[^>]*class=\"[^\"]*\\brpb-greyhound\\b[^\"]*\"[^>]*>(?<name>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RaceRunnerRegex();
}
