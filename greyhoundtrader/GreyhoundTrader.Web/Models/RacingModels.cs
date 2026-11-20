namespace RacingTrader.Web.Models;

public sealed record MeetingSummary(
    int SportId,
    string Name,
    int EventCount,
    int SubscribedEventCount,
    bool Subscribed,
    long FirstStartUnix,
    long LastStartUnix);

public sealed record EventSummary(
    long EventId,
    int SportId,
    string Meeting,
    string Name,
    long StartTimeUnix,
    string Status,
    bool Subscribed,
    bool Active,
    bool Display,
    bool InProgress,
    int BetsApiPricedRunnerCount)
{
    public bool BetsApiPriced => BetsApiPricedRunnerCount > 0;
}

public sealed record MarketColumn(
    long MarketId,
    string Name,
    int DisplayOrder,
    bool Active,
    bool Display);

public sealed record PriceCell(
    long MarketId,
    string MarketName,
    double Price,
    double PreviousPrice,
    bool Active,
    bool Display,
    string? Result,
    string? Rule4TimeUtc,
    double Rule4Deduction);

public sealed record PriceHistoryPoint(DateTime RecordedAtUtc, double Price);

public sealed record BetfairRunnerPrice(
    string MarketId,
    string RunnerStatus,
    double? BestBackPrice,
    double? BestLayPrice,
    double? LastTradedPrice,
    string MarketStatus,
    bool InPlay,
    bool IsDelayed,
    int BetDelay,
    DateTime CapturedAtUtc);

public sealed record RunnerRow(
    long SelectionId,
    long? CompetitorId,
    string Identifier,
    string Name,
    string? AssetUrl,
    double? BetsApiPrice,
    DateTime? BetsApiUpdatedAtUtc,
    double? BetfredPrice,
    DateTime? BetfredUpdatedAtUtc,
    double? Bet365DirectPrice,
    DateTime? Bet365DirectUpdatedAtUtc,
    double? BetfredIndex502510,
    double? BetfredIndex50302010,
    double? SpreadexPrice,
    double? SpreadexBuy,
    double? SpreadexIndex502510Buy,
    double? SpreadexIndex502510Sell,
    DateTime? SpreadexIndex502510UpdatedAtUtc,
    string? SpreadexIndexMarketName,
    DateTime? SpreadexUpdatedAtUtc,
    string? SelectedPriceSource,
    string? LadderSource,
    double? ModelSourcePrice,
    int? LadderMoves,
    BetfairRunnerPrice? Betfair,
    bool Active,
    bool Display,
    bool Withdrawn,
    string? Result,
    int? FinishingPosition,
    string? ResultStatus,
    string? Rule4TimeUtc,
    double Rule4Deduction,
    IReadOnlyDictionary<string, PriceCell> Prices,
    IReadOnlyList<PriceHistoryPoint> PriceHistory,
    double? TimeformForecastPrice,
    DateTime? TimeformForecastUpdatedAtUtc,
    double? BetAhoyPrice = null,
    DateTime? BetAhoyUpdatedAtUtc = null);

public sealed record DerivedSelection(
    long SelectionId,
    string Identifier,
    string Name,
    double Price,
    double PreviousPrice,
    bool Active,
    bool Display,
    bool Withdrawn,
    string? Result);

public sealed record DerivedMarket(
    long MarketId,
    string Name,
    int DisplayOrder,
    bool Active,
    bool Display,
    IReadOnlyList<DerivedSelection> Selections);

public sealed record EventBoard(
    EventSummary Event,
    IReadOnlyList<MarketColumn> Markets,
    IReadOnlyList<RunnerRow> Runners,
    IReadOnlyList<DerivedMarket> DerivedMarkets,
    double TotalRule4Deduction,
    EventMarketConfiguration MarketConfiguration,
    IReadOnlyList<MarketRule4Adjustment> MarketAdjustments,
    IReadOnlyList<RaceCardSpreadPrice> RaceCardSpreads);

public sealed record RaceCardSpreadPrice(
    string Key,
    string Name,
    double? Bid,
    double? Offer,
    double? Midpoint,
    string HelpText,
    double? SpreadexBid = null,
    double? SpreadexOffer = null,
    DateTime? SpreadexUpdatedAtUtc = null);

public sealed record MeetingRaceCardSpreadBoard(
    string Meeting,
    IReadOnlyList<MeetingRaceCardSpreadRace> Races,
    IReadOnlyList<RaceCardSpreadPrice> Aggregate);

public sealed record MeetingRaceCardSpreadRace(
    long EventId,
    string EventName,
    long StartTimeUnix,
    string Status,
    IReadOnlyList<RaceCardSpreadPrice> Markets);

public sealed record MeetingTrapChallengeBoard(
    string Meeting,
    string Status,
    string Message,
    int RaceCount,
    int PricedRaceCount,
    int ResultedRaceCount,
    double TieProbability,
    IReadOnlyList<TrapChallengeSelection> Selections);

public sealed record TrapChallengeSelection(
    int Trap,
    int Winners,
    double? Probability,
    double? FairPrice);

public sealed record EventMarketConfiguration(
    long EventId,
    bool Top3Enabled,
    bool Insure2Enabled,
    bool Insure3Enabled,
    bool Insure4Enabled,
    IReadOnlyList<BettingWithoutMarket> BettingWithoutMarkets,
    IReadOnlyList<MatchBetPair> MatchBets,
    IReadOnlyList<ForecastPair> Forecasts);

public sealed record BettingWithoutMarket(
    long Id,
    long EventId,
    long ExcludedSelectionId,
    string ExcludedRunnerName,
    int MarketSuffix,
    int DisplayOrder,
    bool Enabled,
    bool Voided,
    string? VoidReason);

public sealed record MatchBetPair(
    long Id,
    long EventId,
    long RunnerASelectionId,
    string RunnerAName,
    long RunnerBSelectionId,
    string RunnerBName,
    int MarketSuffix,
    int DisplayOrder,
    bool Enabled,
    bool Voided,
    string? VoidReason);

public sealed record ForecastPair(
    long Id,
    long EventId,
    long RunnerASelectionId,
    string RunnerAName,
    long RunnerBSelectionId,
    string RunnerBName,
    int MarketSuffix,
    int DisplayOrder,
    bool Enabled,
    bool Voided,
    string? VoidReason);

public sealed record MarketRule4Adjustment(
    long MarketId,
    string MarketName,
    long WithdrawnSelectionId,
    string RunnerName,
    double PriceAtWithdrawal,
    double Deduction,
    DateTime AppliedAtUtc,
    string AdjustmentType);

public sealed record SubscriptionRequest(bool Subscribed);
public sealed record WithdrawalRequest(string? Reason);
public sealed record ReplacementRequest(string Name, double? Price);
public sealed record UpdateEventMarketsRequest(
    bool Top3Enabled,
    bool Insure2Enabled,
    bool Insure3Enabled,
    bool Insure4Enabled);
public sealed record CreateMatchBetRequest(long RunnerASelectionId, long RunnerBSelectionId);
public sealed record CreateForecastRequest(long RunnerASelectionId, long RunnerBSelectionId);
public sealed record CreateBettingWithoutRequest(long ExcludedSelectionId);
public sealed record GreyhoundLineMarketRequest(bool MultiTrapsEnabled, bool TripleTrapsEnabled);

public sealed record RunnerActionReceipt(
    long EventId,
    long SelectionId,
    string Action,
    string RunnerName,
    string? ReplacementName,
    double PriceAtAction,
    double Rule4Deduction,
    DateTime ActionTimeUtc,
    IReadOnlyList<Rule4MarketPrice>? MarketPrices = null);

public sealed record Rule4MarketPrice(string MarketName, double LastPrice, double Deduction);

public sealed record IndexMarketToHide(long EventId, long MarketId, string MarketName, int MarketTypeId, int DisplayOrder);

public sealed record ActivityUpdate(
    long Id,
    string ActivityType,
    int? SportId,
    long? EventId,
    string? Meeting,
    string? EventName,
    string Message,
    DateTime CreatedAtUtc);

public sealed record FeedLastUpdate(
    string Source,
    long? EventId,
    string? RaceName,
    DateTime? UpdatedAtUtc);

public sealed record ProviderPriceComparisonRow(
    long EventId,
    string Meeting,
    string EventName,
    long StartTimeUnix,
    string EventStatus,
    long SelectionId,
    long? CompetitorId,
    string Identifier,
    string RunnerName,
    bool Withdrawn,
    double? Bet365BrowserPrice,
    DateTime? Bet365BrowserUpdatedAtUtc,
    DateTime? Bet365BrowserObservedAtUtc,
    double? BetsApiPrice,
    DateTime? BetsApiUpdatedAtUtc,
    DateTime? BetsApiObservedAtUtc,
    double? BetfredPrice,
    DateTime? BetfredUpdatedAtUtc,
    DateTime? BetfredObservedAtUtc,
    double? SpreadexPrice,
    double? SpreadexBuy,
    double? SpreadexSell,
    DateTime? SpreadexUpdatedAtUtc,
    double? PriceDifference,
    double? AbsolutePriceDifference,
    double? ImpliedProbabilityDifferencePoints,
    double? UpdateLagSeconds,
    DateTime? LatestComparisonUpdateUtc,
    double? BetAhoy = null);

public sealed record IndexArbRow(
    long EventId,
    string RaceName,
    long StartTimeUnix,
    string MarketName,
    long? CompetitorId,
    string Identifier,
    string RunnerName,
    string? AssetUrl,
    double RtBid,
    double RtOffer,
    double SpreadexBid,
    double SpreadexOffer,
    double ArbPoints,
    DateTime? SpreadexUpdatedAtUtc);
