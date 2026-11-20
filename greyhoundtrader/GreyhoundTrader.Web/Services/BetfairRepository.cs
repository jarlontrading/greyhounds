using MySqlConnector;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetfairRepository
{
    private readonly string _connectionString;

    public BetfairRepository(ExternalSettings settings)
    {
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = settings.MySqlServer,
            UserID = settings.MySqlUser,
            Password = settings.MySqlPassword,
            Database = settings.RacingDatabase,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 8,
            DefaultCommandTimeout = 20,
            MaximumPoolSize = 1,
            ConnectionIdleTimeout = 30
        }.ConnectionString;
    }

    private MySqlConnection OpenConnection() => new(_connectionString);

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS web_betfair_event_links (
              event_id BIGINT NOT NULL PRIMARY KEY,
              sport_id INT NOT NULL,
              meeting VARCHAR(160) NOT NULL,
              event_name VARCHAR(240) NOT NULL,
              start_time_utc DATETIME(6) NOT NULL,
              betfair_market_id VARCHAR(32) NULL,
              betfair_event_id VARCHAR(32) NULL,
              betfair_event_name VARCHAR(240) NULL,
              betfair_venue VARCHAR(160) NULL,
              betfair_market_name VARCHAR(160) NULL,
              match_status VARCHAR(32) NOT NULL,
              match_score DOUBLE NOT NULL DEFAULT 0,
              matched_runners INT NOT NULL DEFAULT 0,
              internal_runners INT NOT NULL DEFAULT 0,
              match_detail VARCHAR(500) NULL,
              last_catalogue_utc DATETIME(6) NOT NULL,
              updated_at_utc DATETIME(6) NOT NULL,
              UNIQUE KEY uq_web_betfair_market (betfair_market_id),
              KEY ix_web_betfair_status_start (match_status, start_time_utc)
            );

            CREATE TABLE IF NOT EXISTS web_betfair_runner_links (
              event_id BIGINT NOT NULL,
              internal_selection_id BIGINT NOT NULL,
              internal_identifier VARCHAR(24) NOT NULL,
              internal_name VARCHAR(240) NOT NULL,
              betfair_selection_id BIGINT NOT NULL,
              betfair_name VARCHAR(240) NOT NULL,
              match_score DOUBLE NOT NULL,
              updated_at_utc DATETIME(6) NOT NULL,
              PRIMARY KEY (event_id, internal_selection_id),
              UNIQUE KEY uq_web_betfair_runner (event_id, betfair_selection_id)
            );

            CREATE TABLE IF NOT EXISTS web_betfair_prices (
              event_id BIGINT NOT NULL,
              betfair_market_id VARCHAR(32) NOT NULL,
              betfair_selection_id BIGINT NOT NULL,
              runner_status VARCHAR(32) NOT NULL,
              best_back_price DOUBLE NULL,
              best_back_size DOUBLE NULL,
              best_lay_price DOUBLE NULL,
              best_lay_size DOUBLE NULL,
              last_traded_price DOUBLE NULL,
              market_status VARCHAR(32) NOT NULL,
              in_play TINYINT(1) NOT NULL,
              is_delayed TINYINT(1) NOT NULL,
              bet_delay INT NOT NULL,
              market_version BIGINT NOT NULL,
              last_match_time_utc DATETIME(6) NULL,
              captured_at_utc DATETIME(6) NOT NULL,
              PRIMARY KEY (event_id, betfair_selection_id),
              KEY ix_web_betfair_prices_captured (captured_at_utc)
            );

            CREATE TABLE IF NOT EXISTS web_betfair_state (
              state_id TINYINT NOT NULL PRIMARY KEY,
              last_catalogue_utc DATETIME(6) NULL,
              last_price_utc DATETIME(6) NULL,
              last_error VARCHAR(1000) NULL,
              updated_at_utc DATETIME(6) NOT NULL
            );
            INSERT IGNORE INTO web_betfair_state(state_id, updated_at_utc) VALUES(1, UTC_TIMESTAMP(6));
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BetfairEventCandidate>> GetCandidatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.event_id,e.sport_id,e.meeting,e.ev_name,e.start_time,
                   s.selection_id,s.identifier,s.name
            FROM spuno_events e
            LEFT JOIN spuno_selections s
              ON s.event_id=e.event_id AND s.market_name='Race Winner'
             AND s.active<>0 AND s.display<>0 AND s.scratch=0
            WHERE e.send_to_sp<>0
              AND e.sport_id IN (2,4)
              AND e.start_time BETWEEN UTC_TIMESTAMP() - INTERVAL 2 HOUR AND UTC_TIMESTAMP() + INTERVAL 48 HOUR
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('3','resulted','post game')
            ORDER BY e.start_time,e.event_id,CAST(NULLIF(s.identifier,'') AS UNSIGNED),s.name;
            """;
        var accumulators = new Dictionary<long, CandidateAccumulator>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = reader.GetInt64(0);
            if (!accumulators.TryGetValue(eventId, out var item))
            {
                item = new CandidateAccumulator(eventId, reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4));
                accumulators[eventId] = item;
            }
            if (!reader.IsDBNull(5))
                item.Runners.Add(new BetfairInternalRunner(reader.GetInt64(5), reader.GetString(6), reader.GetString(7)));
        }
        return accumulators.Values.Select(x => x.ToModel()).ToList();
    }

    public async Task SaveMatchesAsync(IReadOnlyList<BetfairEventMatch> matches, CancellationToken cancellationToken)
    {
        if (matches.Count == 0) return;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var match in matches)
        {
            await using (var command = new MySqlCommand("""
                INSERT INTO web_betfair_event_links(
                  event_id,sport_id,meeting,event_name,start_time_utc,betfair_market_id,betfair_event_id,
                  betfair_event_name,betfair_venue,betfair_market_name,match_status,match_score,
                  matched_runners,internal_runners,match_detail,last_catalogue_utc,updated_at_utc)
                VALUES(@eventId,@sportId,@meeting,@eventName,@start,@marketId,@betfairEventId,
                  @betfairEventName,@venue,@marketName,@status,@score,@matched,@internal,@detail,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE sport_id=VALUES(sport_id),meeting=VALUES(meeting),event_name=VALUES(event_name),
                  start_time_utc=VALUES(start_time_utc),betfair_market_id=VALUES(betfair_market_id),
                  betfair_event_id=VALUES(betfair_event_id),betfair_event_name=VALUES(betfair_event_name),
                  betfair_venue=VALUES(betfair_venue),betfair_market_name=VALUES(betfair_market_name),
                  match_status=VALUES(match_status),match_score=VALUES(match_score),matched_runners=VALUES(matched_runners),
                  internal_runners=VALUES(internal_runners),match_detail=VALUES(match_detail),
                  last_catalogue_utc=UTC_TIMESTAMP(6),updated_at_utc=UTC_TIMESTAMP(6);
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("@eventId", match.InternalEvent.EventId);
                command.Parameters.AddWithValue("@sportId", match.InternalEvent.SportId);
                command.Parameters.AddWithValue("@meeting", match.InternalEvent.Meeting);
                command.Parameters.AddWithValue("@eventName", match.InternalEvent.Name);
                command.Parameters.AddWithValue("@start", match.InternalEvent.StartTimeUtc);
                command.Parameters.AddWithValue("@marketId", match.BetfairMarket?.MarketId is { } marketId ? marketId : DBNull.Value);
                command.Parameters.AddWithValue("@betfairEventId", match.BetfairMarket?.EventId is { } betfairEventId ? betfairEventId : DBNull.Value);
                command.Parameters.AddWithValue("@betfairEventName", match.BetfairMarket?.EventName is { } betfairEventName ? betfairEventName : DBNull.Value);
                command.Parameters.AddWithValue("@venue", match.BetfairMarket?.Venue is { } venue ? venue : DBNull.Value);
                command.Parameters.AddWithValue("@marketName", match.BetfairMarket?.MarketName is { } marketName ? marketName : DBNull.Value);
                command.Parameters.AddWithValue("@status", match.Status);
                command.Parameters.AddWithValue("@score", match.Score);
                command.Parameters.AddWithValue("@matched", match.RunnerMatches.Count);
                command.Parameters.AddWithValue("@internal", match.InternalEvent.Runners.Count);
                command.Parameters.AddWithValue("@detail", match.Detail is null ? DBNull.Value : match.Detail);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = new MySqlCommand("DELETE FROM web_betfair_runner_links WHERE event_id=@eventId;", connection, transaction))
            {
                delete.Parameters.AddWithValue("@eventId", match.InternalEvent.EventId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var runner in match.RunnerMatches)
            {
                await using var command = new MySqlCommand("""
                    INSERT INTO web_betfair_runner_links(event_id,internal_selection_id,internal_identifier,internal_name,
                      betfair_selection_id,betfair_name,match_score,updated_at_utc)
                    VALUES(@eventId,@internalId,@identifier,@internalName,@betfairId,@betfairName,@score,UTC_TIMESTAMP(6));
                    """, connection, transaction);
                command.Parameters.AddWithValue("@eventId", match.InternalEvent.EventId);
                command.Parameters.AddWithValue("@internalId", runner.InternalSelectionId);
                command.Parameters.AddWithValue("@identifier", runner.InternalIdentifier);
                command.Parameters.AddWithValue("@internalName", runner.InternalName);
                command.Parameters.AddWithValue("@betfairId", runner.BetfairSelectionId);
                command.Parameters.AddWithValue("@betfairName", runner.BetfairName);
                command.Parameters.AddWithValue("@score", runner.Score);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await UpdateStateAsync(connection, transaction, "last_catalogue_utc", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BetfairMarketLink>> GetLinkedMarketsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT link.event_id,link.betfair_market_id,link.match_status
            FROM web_betfair_event_links link
            JOIN spuno_events event ON event.event_id=link.event_id
            WHERE link.match_status='MATCHED' AND link.betfair_market_id IS NOT NULL
              AND event.send_to_sp<>0
              AND event.start_time BETWEEN UTC_TIMESTAMP() - INTERVAL 2 HOUR AND UTC_TIMESTAMP() + INTERVAL 24 HOUR
              AND COALESCE(NULLIF(event.status,''),'0') NOT IN ('3','resulted','post game')
            ORDER BY event.start_time;
            """;
        var output = new List<BetfairMarketLink>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new BetfairMarketLink(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return output;
    }

    public async Task SaveBooksAsync(
        IReadOnlyList<BetfairMarketLink> links,
        IReadOnlyList<BetfairMarketBook> books,
        CancellationToken cancellationToken)
    {
        var eventByMarket = links.ToDictionary(x => x.MarketId, x => x.EventId, StringComparer.Ordinal);
        if (books.Count == 0 || eventByMarket.Count == 0) return;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var book in books)
        {
            if (!eventByMarket.TryGetValue(book.MarketId, out var eventId)) continue;
            foreach (var runner in book.Runners)
            {
                await using var command = new MySqlCommand("""
                    INSERT INTO web_betfair_prices(event_id,betfair_market_id,betfair_selection_id,runner_status,
                      best_back_price,best_back_size,best_lay_price,best_lay_size,last_traded_price,
                      market_status,in_play,is_delayed,bet_delay,market_version,last_match_time_utc,captured_at_utc)
                    VALUES(@eventId,@marketId,@selectionId,@runnerStatus,@back,@backSize,@lay,@laySize,@ltp,
                      @marketStatus,@inPlay,@delayed,@betDelay,@version,@lastMatch,UTC_TIMESTAMP(6))
                    ON DUPLICATE KEY UPDATE runner_status=VALUES(runner_status),best_back_price=VALUES(best_back_price),
                      best_back_size=VALUES(best_back_size),best_lay_price=VALUES(best_lay_price),best_lay_size=VALUES(best_lay_size),
                      last_traded_price=VALUES(last_traded_price),market_status=VALUES(market_status),in_play=VALUES(in_play),
                      is_delayed=VALUES(is_delayed),bet_delay=VALUES(bet_delay),market_version=VALUES(market_version),
                      last_match_time_utc=VALUES(last_match_time_utc),captured_at_utc=UTC_TIMESTAMP(6);
                    """, connection, transaction);
                command.Parameters.AddWithValue("@eventId", eventId);
                command.Parameters.AddWithValue("@marketId", book.MarketId);
                command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
                command.Parameters.AddWithValue("@runnerStatus", runner.Status);
                command.Parameters.AddWithValue("@back", Db(runner.BestBackPrice));
                command.Parameters.AddWithValue("@backSize", Db(runner.BestBackSize));
                command.Parameters.AddWithValue("@lay", Db(runner.BestLayPrice));
                command.Parameters.AddWithValue("@laySize", Db(runner.BestLaySize));
                command.Parameters.AddWithValue("@ltp", Db(runner.LastTradedPrice));
                command.Parameters.AddWithValue("@marketStatus", book.Status);
                command.Parameters.AddWithValue("@inPlay", book.InPlay ? 1 : 0);
                command.Parameters.AddWithValue("@delayed", book.IsMarketDataDelayed ? 1 : 0);
                command.Parameters.AddWithValue("@betDelay", book.BetDelay);
                command.Parameters.AddWithValue("@version", book.Version);
                command.Parameters.AddWithValue("@lastMatch", book.LastMatchTimeUtc is null ? DBNull.Value : book.LastMatchTimeUtc);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await UpdateStateAsync(connection, transaction, "last_price_utc", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BetfairBiasReference>> GetBiasReferencesAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT runner.internal_selection_id,runner.internal_identifier,runner.internal_name,
                   price.best_back_price,price.best_lay_price,price.last_traded_price,
                   price.is_delayed,price.in_play,price.market_status,price.captured_at_utc
            FROM web_betfair_runner_links runner
            JOIN web_betfair_event_links event_link ON event_link.event_id=runner.event_id AND event_link.match_status='MATCHED'
            JOIN web_betfair_prices price
              ON price.event_id=runner.event_id AND price.betfair_selection_id=runner.betfair_selection_id
            WHERE runner.event_id=@eventId AND price.runner_status='ACTIVE';
            """;
        var output = new List<BetfairBiasReference>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new BetfairBiasReference(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                NullableDouble(reader, 3), NullableDouble(reader, 4), NullableDouble(reader, 5),
                reader.GetBoolean(6), reader.GetBoolean(7), reader.GetString(8), reader.GetDateTime(9)));
        return output;
    }

    public async Task<IReadOnlyList<BetfairMatchSummary>> GetMatchesAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id,sport_id,meeting,event_name,start_time_utc,betfair_market_id,betfair_event_name,
                   betfair_venue,match_status,match_score,matched_runners,internal_runners,updated_at_utc,match_detail
            FROM web_betfair_event_links
            ORDER BY start_time_utc DESC LIMIT @limit;
            """;
        var output = new List<BetfairMatchSummary>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new BetfairMatchSummary(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetDouble(9), reader.GetInt32(10),
                reader.GetInt32(11), reader.GetDateTime(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        return output;
    }

    public async Task<BetfairIntegrationStatus> GetStatusAsync(ExternalSettings settings, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
              SUM(match_status='MATCHED'),SUM(match_status IN ('PARTIAL','MARKET_MATCHED')),SUM(match_status='UNMATCHED')
            FROM web_betfair_event_links;
            SELECT last_catalogue_utc,last_price_utc,last_error FROM web_betfair_state WHERE state_id=1;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var matched = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var partial = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        var unmatched = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new BetfairIntegrationStatus(settings.BetfairEnabled, settings.BetfairConfigured, settings.BetfairAuthenticationMode, settings.BetfairBiasEnabled,
            settings.BetfairOrderMode, settings.BetfairMaximumPriceAgeSeconds, matched, partial, unmatched,
            reader.IsDBNull(0) ? null : reader.GetDateTime(0), reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task RecordErrorAsync(string message, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE web_betfair_state SET last_error=@message,updated_at_utc=UTC_TIMESTAMP(6) WHERE state_id=1;
            """, connection);
        command.Parameters.AddWithValue("@message", message.Length <= 1000 ? message : message[..1000]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateStateAsync(MySqlConnection connection, MySqlTransaction transaction,
        string column, string? error, CancellationToken cancellationToken)
    {
        if (column is not ("last_catalogue_utc" or "last_price_utc")) throw new ArgumentOutOfRangeException(nameof(column));
        await using var command = new MySqlCommand($"UPDATE web_betfair_state SET {column}=UTC_TIMESTAMP(6),last_error=@error,updated_at_utc=UTC_TIMESTAMP(6) WHERE state_id=1;", connection, transaction);
        command.Parameters.AddWithValue("@error", error is null ? DBNull.Value : error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object Db(double? value) => value is null ? DBNull.Value : value.Value;
    private static double? NullableDouble(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private sealed class CandidateAccumulator(long eventId, int sportId, string meeting, string name, DateTime start)
    {
        public List<BetfairInternalRunner> Runners { get; } = [];
        public BetfairEventCandidate ToModel() => new(eventId, sportId, meeting, name, DateTime.SpecifyKind(start, DateTimeKind.Utc), Runners);
    }
}
