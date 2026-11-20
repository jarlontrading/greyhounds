namespace RacingTrader.Web.Services;

internal sealed record BetfairMarketCatalogue(
    string MarketId,
    string MarketName,
    string EventId,
    string EventName,
    string? Venue,
    DateTime StartTimeUtc,
    string EventTypeId,
    IReadOnlyList<BetfairCatalogueRunner> Runners);

internal sealed record BetfairCatalogueRunner(
    long SelectionId,
    string Name,
    int SortPriority,
    string? ClothNumber);

internal sealed record BetfairMarketBook(
    string MarketId,
    string Status,
    bool InPlay,
    bool IsMarketDataDelayed,
    int BetDelay,
    long Version,
    DateTime? LastMatchTimeUtc,
    IReadOnlyList<BetfairBookRunner> Runners);

internal sealed record BetfairBookRunner(
    long SelectionId,
    string Status,
    double? BestBackPrice,
    double? BestBackSize,
    double? BestLayPrice,
    double? BestLaySize,
    double? LastTradedPrice);

internal sealed record BetfairInternalRunner(
    long SelectionId,
    string Identifier,
    string Name);

internal sealed record BetfairEventCandidate(
    long EventId,
    int SportId,
    string Meeting,
    string Name,
    DateTime StartTimeUtc,
    IReadOnlyList<BetfairInternalRunner> Runners);

internal sealed record BetfairRunnerMatch(
    long InternalSelectionId,
    string InternalIdentifier,
    string InternalName,
    long BetfairSelectionId,
    string BetfairName,
    double Score);

internal sealed record BetfairEventMatch(
    BetfairEventCandidate InternalEvent,
    BetfairMarketCatalogue? BetfairMarket,
    string Status,
    double Score,
    IReadOnlyList<BetfairRunnerMatch> RunnerMatches,
    string? Detail);

internal sealed record BetfairMarketLink(
    long EventId,
    string MarketId,
    string Status);

internal sealed record BetfairBiasReference(
    long InternalSelectionId,
    string Identifier,
    string Name,
    double? BestBackPrice,
    double? BestLayPrice,
    double? LastTradedPrice,
    bool IsDelayed,
    bool InPlay,
    string MarketStatus,
    DateTime CapturedAtUtc);

internal sealed record BetfairIntegrationStatus(
    bool Enabled,
    bool Configured,
    string AuthenticationMode,
    bool BiasEnabled,
    string OrderMode,
    int MaximumPriceAgeSeconds,
    int MatchedEvents,
    int PartialEvents,
    int UnmatchedEvents,
    DateTime? LastCatalogueUtc,
    DateTime? LastPriceUtc,
    string? LastError);

internal sealed record BetfairMatchSummary(
    long EventId,
    int SportId,
    string Meeting,
    string EventName,
    DateTime StartTimeUtc,
    string? BetfairMarketId,
    string? BetfairEventName,
    string? BetfairVenue,
    string MatchStatus,
    double MatchScore,
    int MatchedRunners,
    int InternalRunners,
    DateTime UpdatedAtUtc,
    string? Detail);
