using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetfairClient(
    HttpClient httpClient,
    ExternalSettings settings,
    BetfairSessionManager sessionManager)
{
    private const string Endpoint = "https://api.betfair.com/exchange/betting/json-rpc/v1";
    private long _requestId;

    public async Task<IReadOnlyList<BetfairMarketCatalogue>> GetWinMarketsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var result = await CallAsync("listMarketCatalogue", new
        {
            filter = new
            {
                eventTypeIds = new[] { "7", "4339" },
                marketTypeCodes = new[] { "WIN" },
                marketStartTime = new { from = fromUtc.ToString("O"), to = toUtc.ToString("O") }
            },
            marketProjection = new[] { "EVENT", "EVENT_TYPE", "MARKET_START_TIME", "RUNNER_DESCRIPTION" },
            sort = "FIRST_TO_START",
            maxResults = 1000
        }, cancellationToken);

        if (result is not JsonArray markets) return [];
        return markets.OfType<JsonObject>().Select(ParseCatalogue).Where(x => x is not null).Cast<BetfairMarketCatalogue>().ToList();
    }

    public async Task<IReadOnlyList<BetfairMarketBook>> GetMarketBooksAsync(
        IReadOnlyList<string> marketIds,
        CancellationToken cancellationToken)
    {
        if (marketIds.Count == 0) return [];
        var output = new List<BetfairMarketBook>();
        foreach (var batch in marketIds.Distinct(StringComparer.Ordinal).Chunk(20))
        {
            var result = await CallAsync("listMarketBook", new
            {
                marketIds = batch,
                priceProjection = new
                {
                    priceData = new[] { "EX_BEST_OFFERS" },
                    exBestOffersOverrides = new { bestPricesDepth = 1, rollupModel = "STAKE", rollupLimit = 1 }
                }
            }, cancellationToken);
            if (result is JsonArray books)
                output.AddRange(books.OfType<JsonObject>().Select(ParseBook).Where(x => x is not null).Cast<BetfairMarketBook>());
        }
        return output;
    }

    private async Task<JsonNode?> CallAsync(string operation, object parameters, CancellationToken cancellationToken)
    {
        if (!settings.BetfairConfigured)
            throw new InvalidOperationException("Betfair integration is not configured.");

        var payload = new
        {
            jsonrpc = "2.0",
            method = $"SportsAPING/v1.0/{operation}",
            @params = parameters,
            id = Interlocked.Increment(ref _requestId)
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sessionToken = await sessionManager.GetSessionTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.TryAddWithoutValidation("X-Application", settings.BetfairAppKey);
            request.Headers.TryAddWithoutValidation("X-Authentication", sessionToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
            var envelope = root is JsonArray array ? array.FirstOrDefault() as JsonObject : root as JsonObject;
            if (envelope?["error"] is not JsonNode error)
                return envelope?["result"];

            var errorText = error.ToJsonString();
            if (attempt == 0 && IsSessionError(errorText))
            {
                sessionManager.InvalidateSession();
                continue;
            }
            throw new InvalidOperationException($"Betfair {operation} failed: {errorText}.");
        }

        throw new InvalidOperationException($"Betfair {operation} failed after session renewal.");
    }

    private static bool IsSessionError(string error) =>
        error.Contains("INVALID_SESSION_INFORMATION", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("NO_SESSION", StringComparison.OrdinalIgnoreCase);

    private static BetfairMarketCatalogue? ParseCatalogue(JsonObject market)
    {
        var marketId = Text(market["marketId"]);
        var eventNode = market["event"] as JsonObject;
        var eventTypeNode = market["eventType"] as JsonObject;
        if (marketId.Length == 0 || !TryDate(market["marketStartTime"], out var start)) return null;
        var runners = (market["runners"] as JsonArray)?.OfType<JsonObject>().Select(runner =>
        {
            var metadata = runner["metadata"] as JsonObject;
            return new BetfairCatalogueRunner(
                Long(runner["selectionId"]),
                Text(runner["runnerName"]),
                (int)Long(runner["sortPriority"]),
                TextOrNull(metadata?["CLOTH_NUMBER"]));
        }).Where(x => x.SelectionId > 0 && x.Name.Length > 0).ToList() ?? [];
        return new BetfairMarketCatalogue(
            marketId,
            Text(market["marketName"]),
            Text(eventNode?["id"]),
            Text(eventNode?["name"]),
            TextOrNull(eventNode?["venue"]),
            start,
            Text(eventTypeNode?["id"]),
            runners);
    }

    private static BetfairMarketBook? ParseBook(JsonObject market)
    {
        var marketId = Text(market["marketId"]);
        if (marketId.Length == 0) return null;
        var runners = (market["runners"] as JsonArray)?.OfType<JsonObject>().Select(runner =>
        {
            var exchange = runner["ex"] as JsonObject;
            var back = (exchange?["availableToBack"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var lay = (exchange?["availableToLay"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            return new BetfairBookRunner(
                Long(runner["selectionId"]),
                Text(runner["status"]),
                DoubleOrNull(back?["price"]),
                DoubleOrNull(back?["size"]),
                DoubleOrNull(lay?["price"]),
                DoubleOrNull(lay?["size"]),
                DoubleOrNull(runner["lastPriceTraded"]));
        }).Where(x => x.SelectionId > 0).ToList() ?? [];
        return new BetfairMarketBook(
            marketId,
            Text(market["status"]),
            Boolean(market["inplay"]),
            Boolean(market["isMarketDataDelayed"]),
            (int)Long(market["betDelay"]),
            Long(market["version"]),
            TryDate(market["lastMatchTime"], out var matchTime) ? matchTime : null,
            runners);
    }

    private static string Text(JsonNode? node) => node?.ToString().Trim() ?? "";
    private static string? TextOrNull(JsonNode? node) => Text(node) is { Length: > 0 } value ? value : null;
    private static long Long(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double? DoubleOrNull(JsonNode? node) => double.TryParse(Text(node), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool Boolean(JsonNode? node) => bool.TryParse(Text(node), out var value) && value;
    private static bool TryDate(JsonNode? node, out DateTime value) => DateTime.TryParse(Text(node), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
}
