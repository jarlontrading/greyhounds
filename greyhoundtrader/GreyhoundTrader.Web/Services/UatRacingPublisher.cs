using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using RacingTrader.Web.Configuration;
using RacingTrader.Web.Models;
using RabbitMQ.Client;

namespace RacingTrader.Web.Services;

internal sealed class RacingPublisher(
    ExternalSettings settings,
    IConfiguration configuration,
    RacingDiagnosticCapture diagnostics,
    RaceAuditTrail audit,
    ILogger<RacingPublisher> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, byte> _terminalEvents = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _eventGates = new();
    private readonly ConcurrentDictionary<long, GeneratedEventSnapshot> _latestOpenSnapshots = new();
    private readonly ConcurrentDictionary<string, byte> _auditedOpenChildMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _exchangePrefixes = ParseExchangePrefixes(configuration);
    private IConnection? _connection;
    private IChannel? _channel;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(GeneratedEventSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Event.Status is "2" or "3")
            _terminalEvents[snapshot.Event.EventId] = 0;
        var eventGate = _eventGates.GetOrAdd(snapshot.Event.EventId, _ => new SemaphoreSlim(1, 1));
        await eventGate.WaitAsync(cancellationToken);
        try
        {
            var terminal = snapshot.Event.Status is "2" or "3";
            if (terminal) _terminalEvents[snapshot.Event.EventId] = 0;
            else
            {
                if (_terminalEvents.ContainsKey(snapshot.Event.EventId)) return;
                // Distance markets can arrive separately after their racecard is matched.
                // Keep the other children available for the explicit OFF cascade.
                _latestOpenSnapshots.AddOrUpdate(snapshot.Event.EventId, snapshot, (_, previous) =>
                    new(snapshot.Event, previous.Markets.Concat(snapshot.Markets)
                        .GroupBy(market => market.MarketId).Select(group => group.Last()).ToList()));
            }
            foreach (var prefix in _exchangePrefixes)
            {
                // Sportuno consumes events, markets and selections from three
                // independent exchanges. Always establish the open parent event
                // in this same publish operation before sending either child tier.
                if (!terminal)
                {
                    await PublishEventAsync(snapshot.Event, prefix, null, cancellationToken);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
                await PublishChildrenToEnvironmentAsync(snapshot, prefix, cancellationToken);
            }
            // Events, markets and selections are consumed from separate RabbitMQ
            // exchanges. A terminal child batch must therefore finish with the
            // authoritative terminal event, otherwise a slower child consumer can
            // make the parent visible again after it was suspended/resulted.
            if (terminal)
            {
                foreach (var prefix in _exchangePrefixes)
                    await PublishEventAsync(snapshot.Event, prefix, null, cancellationToken);
                _latestOpenSnapshots.TryRemove(snapshot.Event.EventId, out _);
            }
        }
        finally
        {
            eventGate.Release();
        }
    }

    public async Task PublishHiddenIndexMarketsAsync(IEnumerable<IndexMarketToHide> markets, CancellationToken cancellationToken)
    {
        foreach (var market in markets)
        foreach (var prefix in _exchangePrefixes)
            await SendAsync($"{prefix}markets", new Dictionary<string, object?>
            {
                ["messageType"] = "market", ["messageTimeStampUTC"] = Timestamp(), ["marketId"] = market.MarketId,
                ["eventID"] = market.EventId, ["marketName"] = market.MarketName, ["marketTypeId"] = market.MarketTypeId,
                ["displayOrder"] = market.DisplayOrder, ["active"] = 0, ["display"] = 0
            }, cancellationToken);
    }

    public async Task PublishEventsAsync(IEnumerable<PricingEvent> events, CancellationToken cancellationToken)
    {
        foreach (var ev in events)
        {
            if (ev.Status is "2" or "3")
                _terminalEvents[ev.EventId] = 0;
            var eventGate = _eventGates.GetOrAdd(ev.EventId, _ => new SemaphoreSlim(1, 1));
            await eventGate.WaitAsync(cancellationToken);
            try
            {
                var terminal = ev.Status is "2" or "3";
                foreach (var prefix in _exchangePrefixes)
                    // Event-shell publishing must remain small and predictable.
                    // Full terminal children are rebuilt and sent by the queued
                    // suspension/result follow-up path.
                    await PublishEventAsync(ev, prefix, null, cancellationToken);
                if (terminal) _latestOpenSnapshots.TryRemove(ev.EventId, out _);
            }
            finally
            {
                eventGate.Release();
            }
        }
    }

    public async Task PublishHiddenEventShellsAsync(
        IEnumerable<PricingEvent> events, CancellationToken cancellationToken)
    {
        foreach (var ev in events)
        {
            var eventGate = _eventGates.GetOrAdd(ev.EventId, _ => new SemaphoreSlim(1, 1));
            await eventGate.WaitAsync(cancellationToken);
            try
            {
                foreach (var prefix in _exchangePrefixes)
                {
                    await SendAsync($"{prefix}events", new Dictionary<string, object?>
                    {
                        ["messageType"] = "event", ["messageTimeStampUTC"] = Timestamp(),
                        ["eventId"] = ev.EventId, ["sportId"] = ev.SportId, ["eventName"] = ev.Name,
                        ["startDate"] = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimeUnix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                        ["unixStartDate"] = ev.StartTimeUnix, ["competition"] = ev.Meeting,
                        ["active"] = 0, ["display"] = 0, ["inplay"] = 0
                    }, cancellationToken, ev.EventId);
                }
            }
            finally
            {
                eventGate.Release();
            }
        }
    }

    public Task PublishTrapChallengeClosureAsync(
        long eventId, string meeting, string eventName, long startTimeUnix,
        CancellationToken cancellationToken)
    {
        var marketId = checked(eventId * 10_000 + TrapChallengeService.MarketSuffix);
        var selections = Enumerable.Range(1, 6).Select(trap => new GeneratedSelection
        {
            EventId = eventId,
            MarketName = "Trap Challenge",
            SelectionId = checked(marketId * 100 + trap),
            Name = $"Trap {trap}",
            Identifier = trap.ToString(),
            PriceBack = 1,
            PriceBackLast = 1,
            PriceLay = 1,
            Active = 0,
            Display = 0,
            Scratch = 0,
            MarketId = marketId
        }).ToList();
        var snapshot = new GeneratedEventSnapshot(
            new PricingEvent(eventId, 4, meeting, eventName, startTimeUnix, "2",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            [new GeneratedMarket(marketId, "Trap Challenge", TrapChallengeService.MarketTypeId,
                10, false, false, selections)]);
        return PublishAsync(snapshot, cancellationToken);
    }

    private async Task PublishEventAsync(
        PricingEvent ev,
        string exchangePrefix,
        GeneratedEventSnapshot? latestOpenSnapshot,
        CancellationToken cancellationToken)
    {
        var terminal = ev.Status is "2" or "3";
        if (terminal) _terminalEvents[ev.EventId] = 0;
        else if (_terminalEvents.ContainsKey(ev.EventId)) return;
        var message = new Dictionary<string, object?>
        {
            ["messageType"] = "event", ["messageTimeStampUTC"] = Timestamp(), ["eventId"] = ev.EventId,
            ["sportId"] = ev.SportId, ["eventName"] = ev.Name,
            ["startDate"] = DateTimeOffset.FromUnixTimeSeconds(ev.StartTimeUnix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["unixStartDate"] = ev.StartTimeUnix, ["competition"] = ev.Meeting,
            ["active"] = ev.Status is "2" or "3" ? 0 : 1,
            ["display"] = ShouldDisplay(ev) ? 1 : 0,
            ["inplay"] = ev.Status is "1" or "2" ? 1 : 0
        };
        await SendAsync($"{exchangePrefix}events", message, cancellationToken, ev.EventId, terminal);
        await audit.WriteAsync(terminal ? "terminal-event-message-sent" : "creation-message-sent",
            ev.EventId, ev.SportId, ev.Meeting, ev.Name, ev.StartTimeUnix,
            "RACINGTRADER", $"RabbitMQ {exchangePrefix}events",
            terminal ? "Terminal event message sent." : "Initial/open event message sent.",
            new
            {
                exchangePrefix, status = ev.Status, active = terminal ? 0 : 1,
                display = ShouldDisplay(ev) ? 1 : 0, inplay = ev.Status is "1" or "2" ? 1 : 0
            }, cancellationToken);
        if (terminal && latestOpenSnapshot is not null)
            await PublishCachedSuspensionChildrenAsync(
                latestOpenSnapshot, exchangePrefix, cancellationToken);
        diagnostics.Write("publisher-event", terminal ? "suspension" : "creation", new
        {
            exchangePrefix, ev.EventId, ev.SportId, ev.Meeting, ev.Name, ev.Status,
            active = terminal ? 0 : 1,
            display = ShouldDisplay(ev) ? 1 : 0,
            inplay = ev.Status is "1" or "2" ? 1 : 0
        });
    }

    private async Task PublishCachedSuspensionChildrenAsync(
        GeneratedEventSnapshot snapshot,
        string exchangePrefix,
        CancellationToken cancellationToken)
    {
        var marketCount = 0;
        var selectionCount = 0;
        foreach (var market in snapshot.Markets)
        {
            if (market.MarketTypeId is 1010 or 1011) continue;
            await SendAsync($"{exchangePrefix}markets", new Dictionary<string, object?>
            {
                ["messageType"] = "market", ["messageTimeStampUTC"] = Timestamp(),
                ["marketId"] = market.MarketId, ["eventID"] = snapshot.Event.EventId,
                ["marketName"] = market.Name, ["marketTypeId"] = market.MarketTypeId,
                ["displayOrder"] = market.DisplayOrder, ["active"] = 0, ["display"] = 0
            }, cancellationToken, snapshot.Event.EventId, terminalMessage: true);
            marketCount++;
            if (market.Selections.Count == 0) continue;
            var selections = market.Selections.Select(selection => new GeneratedSelection
            {
                EventId = selection.EventId,
                MarketName = selection.MarketName,
                SelectionId = selection.SelectionId,
                CompetitorId = selection.CompetitorId,
                Name = selection.Name,
                Identifier = selection.Identifier,
                PriceBack = Math.Max(1d, selection.PriceBack),
                PriceBackLast = Math.Max(1d, selection.PriceBackLast),
                PriceLay = selection.PriceLay,
                Index = selection.Index,
                Line = selection.Line,
                Active = 0,
                Display = 0,
                Scratch = selection.Scratch,
                Rule4Time = selection.Rule4Time,
                Rule4Deduction = selection.Rule4Deduction,
                NonRunnerTime = selection.NonRunnerTime,
                NonRunnerPrice = selection.NonRunnerPrice,
                AssetUrl = selection.AssetUrl,
                Result = selection.Result,
                MarketId = selection.MarketId
            }).ToList();
            await SendAsync($"{exchangePrefix}selections", new Dictionary<string, object?>
            {
                ["messageType"] = "selection", ["messageTimeStampUTC"] = Timestamp(),
                ["eventId"] = snapshot.Event.EventId, ["sportId"] = snapshot.Event.SportId,
                ["marketId"] = market.MarketId, ["selections"] = selections
            }, cancellationToken, snapshot.Event.EventId, terminalMessage: true);
            selectionCount += selections.Count;
        }
        diagnostics.Write("publisher-event", "cached-terminal-children", new
        {
            exchangePrefix, snapshot.Event.EventId, marketCount, selectionCount
        });
    }

    private async Task PublishChildrenToEnvironmentAsync(
        GeneratedEventSnapshot snapshot,
        string exchangePrefix,
        CancellationToken cancellationToken)
    {
        var ev = snapshot.Event;
        var terminal = ev.Status is "2" or "3";
        var eventVisible = ShouldDisplay(ev);
        var marketCount = 0;
        var selectionCount = 0;
        var markets = snapshot.Markets
            .Where(market => market.MarketTypeId is not (1010 or 1011))
            .ToList();

        // Phase 1: create/update every market. Do not interleave selection
        // batches: their consumer can otherwise outrun a later market message.
        foreach (var market in markets)
        {
            if (!terminal && _terminalEvents.ContainsKey(ev.EventId)) return;
            var display = ShouldDisplayMarket(eventVisible, market);
            await SendAsync($"{exchangePrefix}markets", new Dictionary<string, object?>
            {
                ["messageType"] = "market", ["messageTimeStampUTC"] = Timestamp(), ["marketId"] = market.MarketId,
                ["eventID"] = ev.EventId, ["marketName"] = market.Name, ["marketTypeId"] = market.MarketTypeId,
                ["displayOrder"] = market.DisplayOrder, ["active"] = market.Active ? 1 : 0, ["display"] = display ? 1 : 0
            }, cancellationToken, ev.EventId, terminal);
            marketCount++;
        }

        // Exchanges have separate consumers, so allow the market tier to be
        // observed before publishing any selections for those markets.
        if (markets.Count > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        // Phase 2: publish all selection batches only after all parent markets.
        foreach (var market in markets)
        {
            if (!terminal && _terminalEvents.ContainsKey(ev.EventId)) return;
            if (market.Selections.Count == 0) continue;
            var display = ShouldDisplayMarket(eventVisible, market);
            var publishedSelections = PrepareSelectionsForPublishing(market, false, display);
            await SendAsync($"{exchangePrefix}selections", new Dictionary<string, object?>
            {
                ["messageType"] = "selection", ["messageTimeStampUTC"] = Timestamp(), ["eventId"] = ev.EventId,
                ["sportId"] = ev.SportId, ["marketId"] = market.MarketId,
                ["selections"] = publishedSelections
            }, cancellationToken, ev.EventId, terminal);
            selectionCount += publishedSelections.Count;
        }
        if (terminal || _auditedOpenChildMessages.TryAdd($"{exchangePrefix}:{ev.EventId}", 0))
            await audit.WriteAsync(terminal ? "terminal-children-messages-sent" : "initial-market-messages-sent",
                ev.EventId, ev.SportId, ev.Meeting, ev.Name, ev.StartTimeUnix,
                "RACINGTRADER", $"RabbitMQ {exchangePrefix}markets/selections",
                "Market and selection payloads sent.",
                new { exchangePrefix, marketCount, selectionCount, status = ev.Status }, cancellationToken);
    }

    private static IReadOnlyList<GeneratedSelection> PrepareSelectionsForPublishing(
        GeneratedMarket market, bool isIndexMarket, bool marketVisible)
    {
        if (marketVisible && (!isIndexMarket || market.Selections.All(x => x.PriceBack >= 1d && x.PriceBackLast >= 1d)))
            return market.Selections;

        return market.Selections.Select(selection => new GeneratedSelection
        {
            EventId = selection.EventId,
            MarketName = selection.MarketName,
            SelectionId = selection.SelectionId,
            CompetitorId = selection.CompetitorId,
            Name = selection.Name,
            Identifier = selection.Identifier,
            PriceBack = Math.Max(1d, selection.PriceBack),
            PriceBackLast = Math.Max(1d, selection.PriceBackLast),
            PriceLay = selection.PriceLay,
            Index = selection.Index,
            Line = selection.Line,
            Active = selection.Active,
            Display = marketVisible ? selection.Display : 0,
            Scratch = selection.Scratch,
            Rule4Time = selection.Rule4Time,
            Rule4Deduction = selection.Rule4Deduction,
            NonRunnerTime = selection.NonRunnerTime,
            NonRunnerPrice = selection.NonRunnerPrice,
            AssetUrl = selection.AssetUrl,
            Result = selection.Result,
            MarketId = selection.MarketId
        }).ToList();
    }

    internal static bool ShouldDisplayMarket(bool eventVisible, GeneratedMarket market) =>
        eventVisible && market.Display;

    private static bool ShouldDisplay(PricingEvent ev)
    {
        // A subscribed race may receive complete prices well before race day.
        // Keep every open event/market visible as soon as it is published;
        // terminal suspension/result messages still hide it.
        return ev.Status is not ("2" or "3");
    }

    private async Task SendAsync(string exchange, object message, CancellationToken cancellationToken,
        long? eventId = null, bool terminalMessage = false)
    {
        if (message is Dictionary<string, object?> payload) AddTrapChallengeSinglesOnly(payload);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Recheck after acquiring the actual channel lock. A stale price
            // message may have queued before suspension but must never publish
            // after the terminal event has claimed this ID.
            if (!terminalMessage && eventId is { } id && _terminalEvents.ContainsKey(id)) return;
            await EnsureConnectedAsync(cancellationToken);
            await _channel!.BasicPublishAsync(exchange, "", false, body, cancellationToken);
            if (terminalMessage)
                diagnostics.Write("publisher-payload", "terminal-published",
                    new { exchange, eventId }, Encoding.UTF8.GetString(body));
        }
        catch
        {
            await DisposeConnectionAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    internal static void AddTrapChallengeSinglesOnly(Dictionary<string, object?> message)
    {
        // Apply at the wire boundary so hidden shells, terminal messages and
        // cached suspension children cannot accidentally omit the restriction.
        var isEvent = message.GetValueOrDefault("messageType") is "event"
            && message.GetValueOrDefault("sportId") is int sportId && sportId == 4
            && message.GetValueOrDefault("eventName") is string name
            && (name.EndsWith(" Meeting", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(" Trap Challenge", StringComparison.OrdinalIgnoreCase));
        var isMarket = message.GetValueOrDefault("messageType") is "market"
            && message.GetValueOrDefault("marketTypeId") is int marketTypeId
            && marketTypeId == TrapChallengeService.MarketTypeId;
        if (isEvent || isMarket) message["singlesOnly"] = true;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection?.IsOpen == true && _channel?.IsOpen == true) return;
        await DisposeConnectionAsync();
        var uriText = settings.RabbitMqUrlSp.StartsWith("amqp://", StringComparison.OrdinalIgnoreCase)
            ? "amqps://" + settings.RabbitMqUrlSp[7..] : settings.RabbitMqUrlSp;
        var targetLabel = string.Join("-", _exchangePrefixes);
        var factory = new ConnectionFactory { Uri = new Uri(uriText), ClientProvidedName = $"RacingTrader.Web-{targetLabel}" };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        logger.LogInformation("Connected web racing publisher to {Targets} exchanges.", string.Join(", ", _exchangePrefixes));
    }

    private async Task DisposeConnectionAsync()
    {
        if (_channel is not null) { try { await _channel.DisposeAsync(); } catch { } _channel = null; }
        if (_connection is not null) { try { await _connection.DisposeAsync(); } catch { } _connection = null; }
    }
    public async ValueTask DisposeAsync() { await DisposeConnectionAsync(); _gate.Dispose(); }
    private static string Timestamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

    internal static string[] ParseExchangePrefixes(IConfiguration configuration)
    {
        var configured = configuration["RacingTrader:PublishTargets"] ?? "DISABLED";
        if (configured.Equals("DISABLED", StringComparison.OrdinalIgnoreCase))
            return [];
        var prefixes = configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (prefixes.Any(x => x is not ("DEMO" or "PROD")))
            throw new InvalidOperationException(
                "RacingTrader:PublishTargets must be DISABLED or contain only DEMO and/or PROD.");
        return prefixes;
    }
}
