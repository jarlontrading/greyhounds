using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RacingTrader.Web.Services;

internal sealed partial class BetfairMarketMatcher
{
    private static readonly HashSet<string> CountrySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "gb", "ire", "fr", "usa", "aus", "nz", "ger", "ity", "spa", "saf", "arg", "brz", "jpn",
        // Greyhound reserves are displayed as "Dog Name (RES)" so the trader and Sportuno
        // can see a reserve ran. Providers report the bare name, so the suffix must be
        // invisible to every name comparison - fingerprint matching, Betfair pairing and
        // the non-runner matcher all normalise through here.
        "res"
    };

    public IReadOnlyList<BetfairEventMatch> Match(
        IReadOnlyList<BetfairEventCandidate> events,
        IReadOnlyList<BetfairMarketCatalogue> markets)
    {
        var output = new List<BetfairEventMatch>();
        var usedMarkets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var racingEvent in events.OrderBy(x => x.StartTimeUtc))
        {
            var candidates = markets
                .Where(market => !usedMarkets.Contains(market.MarketId) && IsSameSport(racingEvent.SportId, market.EventTypeId))
                .Select(market => (Market: market, Score: MarketScore(racingEvent, market)))
                .Where(x => x.Score >= 70)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => Math.Abs((x.Market.StartTimeUtc - racingEvent.StartTimeUtc).TotalSeconds))
                .ToList();

            if (candidates.Count == 0)
            {
                output.Add(new BetfairEventMatch(racingEvent, null, "UNMATCHED", 0, [], "No WIN market matched meeting and off-time."));
                continue;
            }

            var best = candidates[0];
            var runnerMatches = MatchRunners(racingEvent.Runners, best.Market.Runners);
            var requiredRunners = racingEvent.Runners.Count;
            var trusted = requiredRunners >= 2 && runnerMatches.Count == requiredRunners;
            var status = trusted ? "MATCHED" : requiredRunners == 0 ? "MARKET_MATCHED" : "PARTIAL";
            var coverage = requiredRunners == 0 ? 0 : (double)runnerMatches.Count / requiredRunners;
            var score = Math.Min(100, best.Score + coverage * 10);
            var detail = trusted
                ? null
                : requiredRunners == 0
                    ? "Market matched; waiting for internal runners before price bias can be trusted."
                    : $"Matched {runnerMatches.Count} of {requiredRunners} active internal runners.";
            output.Add(new BetfairEventMatch(racingEvent, best.Market, status, score, runnerMatches, detail));
            usedMarkets.Add(best.Market.MarketId);
        }
        return output;
    }

    private static double MarketScore(BetfairEventCandidate racingEvent, BetfairMarketCatalogue market)
    {
        var seconds = Math.Abs((market.StartTimeUtc - racingEvent.StartTimeUtc).TotalSeconds);
        if (seconds > 300) return 0;
        var meeting = NormalizeMeeting(racingEvent.Meeting);
        var venue = NormalizeMeeting(market.Venue ?? "");
        var eventName = NormalizeMeeting(market.EventName);
        var nameScore = meeting.Length > 0 && venue == meeting ? 60
            : meeting.Length > 0 && (venue.Contains(meeting, StringComparison.Ordinal) || eventName.Contains(meeting, StringComparison.Ordinal)) ? 52
            : TokenSimilarity(meeting, venue.Length > 0 ? venue : eventName) >= .75 ? 45
            : 0;
        if (nameScore == 0) return 0;
        var timeScore = 40 * Math.Max(0, 1 - seconds / 300);
        return nameScore + timeScore;
    }

    private static IReadOnlyList<BetfairRunnerMatch> MatchRunners(
        IReadOnlyList<BetfairInternalRunner> internalRunners,
        IReadOnlyList<BetfairCatalogueRunner> betfairRunners)
    {
        var output = new List<BetfairRunnerMatch>();
        var used = new HashSet<long>();
        foreach (var runner in internalRunners)
        {
            var normalized = NormalizeRunner(runner.Name);
            var candidates = betfairRunners.Where(candidate => !used.Contains(candidate.SelectionId)).Select(candidate =>
            {
                var candidateName = NormalizeRunner(candidate.Name);
                var exact = normalized.Length > 0 && normalized == candidateName;
                var identifierMatch = Integer(runner.Identifier) is int internalNumber && Integer(candidate.ClothNumber) == internalNumber;
                var similarity = TokenSimilarity(normalized, candidateName);
                var score = exact ? 1.0 : identifierMatch && similarity >= .8 ? .9 : similarity >= .94 ? .85 : 0;
                return (Runner: candidate, Score: score);
            }).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Runner.SortPriority).ToList();
            if (candidates.Count == 0) continue;
            var best = candidates[0];
            used.Add(best.Runner.SelectionId);
            output.Add(new BetfairRunnerMatch(runner.SelectionId, runner.Identifier, runner.Name,
                best.Runner.SelectionId, best.Runner.Name, best.Score));
        }
        return output;
    }

    internal static string NormalizeMeeting(string value)
    {
        var normalized = NormalizeText(value);
        normalized = MeetingDateSuffix().Replace(normalized, " ");
        normalized = MeetingSessionSuffix().Replace(normalized, " ");
        return CollapseSpaces(normalized);
    }

    internal static string NormalizeRunner(string value)
    {
        var normalized = LeadingRunnerNumber().Replace(value ?? "", "");
        normalized = CountrySuffix().Replace(normalized, match =>
            CountrySuffixes.Contains(match.Groups[1].Value) ? "" : match.Value);
        return NormalizeText(normalized);
    }

    private static string NormalizeText(string value)
    {
        var decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            // Providers inconsistently retain possessive apostrophes in runner names
            // (for example "Sara's" versus "Saras"). They must not split the token.
            if (character is '\'' or '\u2018' or '\u2019' or '\u02BC') continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return CollapseSpaces(builder.ToString());
    }

    private static string CollapseSpaces(string value) => MultipleSpaces().Replace(value, " ").Trim();

    private static double TokenSimilarity(string left, string right)
    {
        if (left == right && left.Length > 0) return 1;
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }

    private static int? Integer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var result) ? result : null;
    }

    private static bool IsSameSport(int sportId, string eventTypeId) =>
        sportId == 2 && eventTypeId == "7" || sportId == 4 && eventTypeId == "4339";

    [GeneratedRegex(@"^\s*\d+[a-zA-Z]?\s*[.\-:]?\s*")]
    private static partial Regex LeadingRunnerNumber();
    [GeneratedRegex(@"\(([A-Za-z]{2,4})\)\s*$")]
    private static partial Regex CountrySuffix();
    [GeneratedRegex(@"\b(?:mon|tue|wed|thu|fri|sat|sun)?\s*\d{1,2}(?:st|nd|rd|th)?\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)(?:ember|uary|ch|il|e|y|ust|tember|ober)?\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingDateSuffix();
    [GeneratedRegex(@"\s+(?:eve|am|pm)$", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingSessionSuffix();
    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpaces();
}
