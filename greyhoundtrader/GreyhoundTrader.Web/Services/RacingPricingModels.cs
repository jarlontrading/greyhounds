using System.Text.Json.Serialization;

namespace RacingTrader.Web.Services;

internal sealed record FeedEvent(
    long EventId,
    int SportId,
    long LeagueId,
    string Meeting,
    string Name,
    long StartTimeUnix,
    int TimeStatus,
    long UpdatedAtUnix,
    long OddsUpdatedAtUnix,
    string RawJson);

internal sealed record FeedRunner(
    long CompetitorId,
    string Name,
    int Identifier,
    string PriceText,
    double Price,
    bool HasPrice,
    bool ExplicitNonRunner,
    string? SilkId,
    string RawJson,
    double? PricingPrice = null,
    DateTime? SourceUpdatedAtUtc = null,
    string? PricingSource = null)
{
    public double EffectivePrice => PricingPrice is > 1 ? PricingPrice.Value : Price;
}

internal sealed record FeedNonRunner(
    string Name,
    long? SourceRunnerId,
    int? Identifier,
    string Reason,
    string RawJson);

internal sealed record GreyhoundTrapObservation(
    long Id,
    long EventId,
    int TrapNumber,
    string Source,
    long CompetitorId,
    string RunnerName,
    string? PriceText,
    double? DecimalPrice,
    bool HasPrice,
    bool ExplicitNonRunner,
    bool IsVacant,
    string ChangeType,
    DateTime SourceUpdatedAtUtc,
    DateTime ObservedAtUtc,
    string? RawJson);

internal sealed record TrapLineGuidePoint(
    double Line,
    double UnderProbability,
    double UnderPrice,
    double OverProbability,
    double OverPrice,
    bool PickEm);

internal sealed record TrapLineMarketGuide(
    string Name,
    string Formula,
    double PickEmLine,
    IReadOnlyList<TrapLineGuidePoint> Lines);

internal sealed record FeedPriceSnapshot(
    string Source,
    DateTime UpdatedAtUtc,
    DateTime ObservedAtUtc,
    IReadOnlyList<FeedRunner> Runners,
    IReadOnlyList<FeedNonRunner> NonRunners)
{
    public int PricedRunnerCount => Runners.Count(x => x.HasPrice && x.Price > 1);
    public bool IsUsable => Runners.Count > 1 && PricedRunnerCount > 1;
}

internal sealed record FeedPriceComparison(
    FeedPriceSnapshot? Selected,
    FeedPriceSnapshot? Direct,
    FeedPriceSnapshot? BetsApi);

internal sealed record RunnerFingerprint(
    int ExpectedCount,
    int ActualCount,
    int MatchedCount,
    double Coverage,
    int RequiredMatches)
{
    public bool HasEnoughEvidence => ExpectedCount >= 2 && ActualCount >= 2;
    public bool IsMatch => HasEnoughEvidence &&
                           MatchedCount >= RequiredMatches &&
                           Coverage >= RaceRunnerFingerprint.MinimumCoverage;
}

internal static class RaceRunnerFingerprint
{
    internal const double MinimumCoverage = .70;

    public static RunnerFingerprint Compare(IEnumerable<string> expectedNames, IEnumerable<string> actualNames)
    {
        var expected = Normalize(expectedNames);
        var actual = Normalize(actualNames);
        var largestSet = Math.Max(expected.Count, actual.Count);
        var matched = expected.Intersect(actual).Count();
        var required = largestSet < 2
            ? 2
            : Math.Max(Math.Min(3, largestSet), (int)Math.Ceiling(largestSet * MinimumCoverage));
        return new RunnerFingerprint(
            expected.Count,
            actual.Count,
            matched,
            largestSet == 0 ? 0 : (double)matched / largestSet,
            required);
    }

    private static HashSet<string> Normalize(IEnumerable<string> names) =>
        names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !name.Contains("Favourite", StringComparison.OrdinalIgnoreCase))
            .Select(BetfairMarketMatcher.NormalizeRunner)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}

internal sealed record PricingEvent(
    long EventId,
    int SportId,
    string Meeting,
    string Name,
    long StartTimeUnix,
    string Status,
    long OddsUpdatedAtUnix,
    bool ForceRefresh = false);

internal sealed record ProviderRunner(
    long CompetitorId,
    string Name,
    int Identifier);

internal sealed class PricingRunner
{
    public long EventId { get; init; }
    public long MarketId { get; init; }
    public long SelectionId { get; init; }
    public long? CompetitorId { get; init; }
    public string? MarketName { get; init; }
    public required string Name { get; init; }
    public required string Identifier { get; init; }
    public double Price { get; set; }
    public double PreviousPrice { get; set; }
    public bool Active { get; set; }
    public bool Display { get; set; }
    public bool Scratch { get; set; }
    public string? AssetUrl { get; init; }
    public string? Result { get; set; }
    public string? Rule4Time { get; set; }
    public double Rule4Deduction { get; set; }
    // spuno_selections.line - the authoritative handicap/product line. Settlement must read
    // it from here rather than parsing it back out of the culture-formatted display name.
    public double Line { get; set; }
}

internal sealed record ProviderIndexPrice(
    long? CompetitorId,
    string Name,
    double? Index502510,
    double? Index50302010);

internal sealed record PricingConfiguration(
    bool Top3Enabled,
    bool Insure2Enabled,
    bool Insure3Enabled,
    bool Insure4Enabled,
    bool MultiTrapsEnabled,
    bool TripleTrapsEnabled,
    double? MultiTrapsLine,
    double? TripleTrapsLine,
    IReadOnlyList<PricingBettingWithout> BettingWithoutMarkets,
    IReadOnlyList<PricingMatchBet> MatchBets,
    IReadOnlyList<PricingForecast> Forecasts);

internal sealed record PricingBettingWithout(
    long ExcludedSelectionId,
    int MarketSuffix,
    int DisplayOrder,
    bool Enabled,
    bool Voided);

internal sealed record PricingMatchBet(
    long RunnerASelectionId,
    long RunnerBSelectionId,
    int MarketSuffix,
    bool Enabled,
    bool Voided);

internal sealed record PricingForecast(
    long RunnerASelectionId,
    long RunnerBSelectionId,
    int MarketSuffix,
    bool Enabled,
    bool Voided);

internal sealed record GeneratedMarket(
    long MarketId,
    string Name,
    int MarketTypeId,
    int DisplayOrder,
    bool Active,
    bool Display,
    IReadOnlyList<GeneratedSelection> Selections);

internal sealed class GeneratedSelection
{
    [JsonPropertyName("eventId")] public long EventId { get; init; }
    [JsonPropertyName("marketName")] public required string MarketName { get; init; }
    [JsonPropertyName("selectionId")] public long SelectionId { get; init; }
    [JsonPropertyName("competitorId")] public long? CompetitorId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("identifier")] public required string Identifier { get; init; }
    [JsonPropertyName("priceBack")] public double PriceBack { get; set; }
    [JsonPropertyName("priceBackLast")] public double PriceBackLast { get; set; }
    [JsonPropertyName("priceLay")] public double PriceLay { get; set; }
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("line")] public double Line { get; init; }
    [JsonPropertyName("active")] public int Active { get; set; }
    [JsonPropertyName("display")] public int Display { get; set; }
    [JsonPropertyName("scratch")] public int Scratch { get; set; }
    [JsonPropertyName("rule4time")] public string? Rule4Time { get; set; }
    [JsonPropertyName("rule4deduction")] public double Rule4Deduction { get; set; }
    [JsonPropertyName("nonRunnerTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NonRunnerTime { get; set; }
    [JsonPropertyName("nonRunnerPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NonRunnerPrice { get; set; }
    [JsonPropertyName("assetUrl")] public string? AssetUrl { get; init; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("marketId")] public long MarketId { get; init; }
}

internal sealed record GeneratedEventSnapshot(PricingEvent Event, IReadOnlyList<GeneratedMarket> Markets);

internal static class RacingResultCodes
{
    public const string Void = "99";
}
