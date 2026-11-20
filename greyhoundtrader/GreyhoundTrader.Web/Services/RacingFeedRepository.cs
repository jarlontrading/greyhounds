using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using MySqlConnector;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed partial class RacingFeedRepository
{
    private readonly string _connectionString;
    private readonly RacingMarketCalculator _calculator;
    private readonly SemaphoreSlim _trapHistorySchemaLock = new(1, 1);
    private volatile bool _trapHistorySchemaReady;
    private readonly SemaphoreSlim _lineMarketSchemaLock = new(1, 1);
    private volatile bool _lineMarketSchemaReady;
    private readonly ILogger<RacingFeedRepository> _logger;

    public RacingFeedRepository(ExternalSettings settings, RacingMarketCalculator calculator,
        ILogger<RacingFeedRepository> logger)
    {
        _calculator = calculator;
        _logger = logger;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = settings.MySqlServer, UserID = settings.MySqlUser, Password = settings.MySqlPassword,
            Database = settings.RacingDatabase, SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 8, DefaultCommandTimeout = 20, AllowUserVariables = true,
            MaximumPoolSize = 4, ConnectionIdleTimeout = 30
        }.ConnectionString;
    }

    private MySqlConnection OpenConnection() => new(_connectionString);

    /// <summary>
    /// Applies innodb_lock_wait_timeout to this session and restores the server default
    /// when disposed. MySqlConnection.Dispose returns the PHYSICAL connection to the pool
    /// without resetting session variables, so a bare "SET SESSION" leaked a 1-2 second
    /// lock timeout onto all four pooled connections permanently, against a server default
    /// of 50. Declare the scope AFTER the connection and BEFORE any transaction so it is
    /// disposed while the connection is still open.
    /// </summary>
    private sealed class SessionLockWaitTimeout(MySqlConnection connection) : IAsyncDisposable
    {
        public static async Task<SessionLockWaitTimeout> ApplyAsync(
            MySqlConnection connection, int seconds, CancellationToken cancellationToken)
        {
            // seconds is an int supplied by the call site; no user input reaches this.
            await using var command = new MySqlCommand(
                $"SET SESSION innodb_lock_wait_timeout={seconds};", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new SessionLockWaitTimeout(connection);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var reset = new MySqlCommand(
                    "SET SESSION innodb_lock_wait_timeout=DEFAULT;", connection);
                await reset.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (MySqlException)
            {
                // Only fails on an already-broken connection, which MySqlConnector discards
                // rather than pools. Never let it mask an exception unwinding through here.
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetRaceWinnerRunnerNamesAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT name
            FROM spuno_selections
            WHERE event_id=@eventId
              AND MOD(market_id,10000)=9001
              AND COALESCE(name,'')<>''
              AND LOWER(name) NOT LIKE '%favourite%';
            """;
        var names = new List<string>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    public async Task<IReadOnlyList<ProviderRunner>> GetRaceWinnerRunnersForProviderAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT competitor_id,MAX(name),
                   MIN(CAST(COALESCE(NULLIF(identifier,''),'0') AS UNSIGNED))
            FROM spuno_selections
            WHERE event_id=@eventId
              AND MOD(market_id,10000)=9001
              AND competitor_id IS NOT NULL AND competitor_id>0
              AND COALESCE(name,'')<>''
              AND LOWER(name) NOT LIKE '%favourite%'
            GROUP BY competitor_id
            ORDER BY MIN(CAST(COALESCE(NULLIF(identifier,''),'9999') AS UNSIGNED)),MAX(name);
            """;
        var runners = new List<ProviderRunner>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            runners.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return runners;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetDisplayProviderEventsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,
                   COALESCE(NULLIF(status,''),'0'),0,0
            FROM spuno_events
            WHERE sport_id=4 AND send_to_sp<>0
              AND start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 36 HOUR)
              AND COALESCE(NULLIF(status,''),'0') NOT IN ('3','resulted','post game')
            ORDER BY start_time_unix,event_id;
            """;
        var events = new List<PricingEvent>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetBoolean(7)));
        return events;
    }

    public async Task StoreDisplayProviderPricesAsync(
        PricingEvent racingEvent,
        string source,
        IReadOnlyList<(ProviderRunner Runner, double? Price, string RawJson)> prices,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (prices.Count == 0) return;
        // The upstream event builder can temporarily leave duplicate Race Winner
        // selection rows for the same competitor. Provider prices are keyed by
        // event/competitor/source, so collapse those rows before writing rather
        // than letting one duplicate abort the provider's entire capture loop.
        var uniquePrices = prices
            .GroupBy(item => item.Runner.CompetitorId)
            .Select(group => group
                .OrderByDescending(item => item.Price is > 1)
                .ThenBy(item => item.Runner.Identifier)
                .First())
            .ToList();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var snapshot = new MySqlCommand("""
            INSERT INTO web_race_winner_source_snapshots
              (event_id,source_name,source_updated_at_utc,observed_at_utc,runner_count,priced_runner_count,is_selected)
            VALUES(@eventId,@source,@updated,@updated,@runnerCount,@pricedCount,0)
            ON DUPLICATE KEY UPDATE source_updated_at_utc=VALUES(source_updated_at_utc),
              observed_at_utc=VALUES(observed_at_utc),runner_count=VALUES(runner_count),
              priced_runner_count=VALUES(priced_runner_count),is_selected=0;
            """, connection, transaction))
        {
            snapshot.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            snapshot.Parameters.AddWithValue("@source", source);
            snapshot.Parameters.AddWithValue("@updated", updatedAtUtc);
            snapshot.Parameters.AddWithValue("@runnerCount", uniquePrices.Count);
            snapshot.Parameters.AddWithValue("@pricedCount", uniquePrices.Count(x => x.Price is > 1));
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        const string priceSql = """
            INSERT INTO web_race_winner_source_prices
              (event_id,competitor_id,source_name,selection_name,identifier,price_text,decimal_price,has_price,
               source_updated_at_utc,observed_at_utc,raw_json)
            VALUES(@eventId,@competitorId,@source,@name,@identifier,@priceText,@price,@hasPrice,@updated,@updated,@raw)
            ON DUPLICATE KEY UPDATE selection_name=VALUES(selection_name),identifier=VALUES(identifier),
              price_text=VALUES(price_text),decimal_price=VALUES(decimal_price),has_price=VALUES(has_price),
              source_updated_at_utc=VALUES(source_updated_at_utc),observed_at_utc=VALUES(observed_at_utc),
              raw_json=VALUES(raw_json);
            """;
        foreach (var item in uniquePrices)
        {
            await using var price = new MySqlCommand(priceSql, connection, transaction);
            price.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            price.Parameters.AddWithValue("@competitorId", item.Runner.CompetitorId);
            price.Parameters.AddWithValue("@source", source);
            price.Parameters.AddWithValue("@name", item.Runner.Name);
            price.Parameters.AddWithValue("@identifier", item.Runner.Identifier);
            price.Parameters.AddWithValue("@priceText", item.Price is > 1 ? item.Price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
            price.Parameters.AddWithValue("@price", item.Price is > 1 ? item.Price.Value : DBNull.Value);
            price.Parameters.AddWithValue("@hasPrice", item.Price is > 1 ? 1 : 0);
            price.Parameters.AddWithValue("@updated", updatedAtUtc);
            price.Parameters.AddWithValue("@raw", item.RawJson);
            await price.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> StorePreferredProviderSilksAsync(
        long eventId,
        IReadOnlyList<(long CompetitorId, string AssetUrl)> silks,
        CancellationToken cancellationToken)
    {
        var unique = silks
            .Where(item => item.CompetitorId > 0 &&
                           Uri.TryCreate(item.AssetUrl, UriKind.Absolute, out var uri) &&
                           uri.Scheme == Uri.UriSchemeHttps &&
                           uri.Host.Equals("images.jarlontrading.co.uk", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.CompetitorId)
            .Select(group => group.First())
            .ToList();
        if (unique.Count == 0) return false;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE spuno_selections
            SET asset_url=@asset
            WHERE event_id=@eventId AND competitor_id=@competitorId
              AND COALESCE(asset_url,'')<>@asset;
            """;
        var changed = 0;
        foreach (var item in unique)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@competitorId", item.CompetitorId);
            command.Parameters.AddWithValue("@asset", item.AssetUrl);
            changed += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed > 0;
    }

    public async Task<int> RemoveSyntheticFavouriteSelectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var removed = 0;
        var commands = new[]
        {
            "DELETE FROM web_market_adjustments WHERE LOWER(runner_name) LIKE '%favourite%';",
            "DELETE FROM web_activity_log WHERE activity_type='rule4' AND LOWER(message) LIKE '%favourite%';",
            """
            DELETE h FROM web_price_history h
            JOIN spuno_selections s ON s.event_id=h.event_id AND s.selection_id=h.selection_id
            WHERE LOWER(s.name) LIKE '%favourite%';
            """,
            """
            DELETE st FROM web_runner_state st
            JOIN spuno_selections s ON s.event_id=st.event_id AND s.selection_id=st.selection_id
            WHERE LOWER(s.name) LIKE '%favourite%';
            """,
            """
            DELETE c FROM web_non_runner_candidates c
            JOIN spuno_selections s ON s.event_id=c.event_id AND s.selection_id=c.selection_id
            WHERE LOWER(s.name) LIKE '%favourite%';
            """,
            "DELETE FROM web_runner_actions WHERE LOWER(COALESCE(old_name,'')) LIKE '%favourite%' OR LOWER(COALESCE(new_name,'')) LIKE '%favourite%';",
            "DELETE FROM web_race_winner_source_prices WHERE LOWER(selection_name) LIKE '%favourite%';",
            "DELETE FROM web_spreadex_runner_prices WHERE LOWER(runner_name) LIKE '%favourite%';",
            "DELETE FROM spuno_selections WHERE LOWER(name) LIKE '%favourite%';",
            "DELETE FROM em_b365_selections_raw WHERE LOWER(selection_name) LIKE '%favourite%';"
        };
        foreach (var sql in commands)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<int> RemoveDuplicateAndCombinationSelectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var removed = 0;
        var commands = new[]
        {
            """
            CREATE TEMPORARY TABLE tmp_invalid_racing_selections (
              event_id BIGINT NOT NULL, market_id BIGINT NOT NULL, selection_id BIGINT NOT NULL,
              runner_name VARCHAR(200) NOT NULL,
              PRIMARY KEY(event_id,market_id,selection_id)
            ) ENGINE=InnoDB;
            """,
            """
            INSERT IGNORE INTO tmp_invalid_racing_selections
            SELECT s.event_id,s.market_id,s.selection_id,s.name
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id=s.event_id
            WHERE e.sport_id=2
              AND e.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 DAY)
              AND LOWER(s.name) LIKE '% or %';
            """,
            """
            INSERT IGNORE INTO tmp_invalid_racing_selections
            SELECT duplicate.event_id,duplicate.market_id,duplicate.selection_id,duplicate.name
            FROM spuno_selections duplicate
            JOIN spuno_events event ON event.event_id=duplicate.event_id
            JOIN spuno_selections keeper
              ON keeper.event_id=duplicate.event_id
             AND keeper.market_id=duplicate.market_id
             AND LOWER(TRIM(keeper.name))=LOWER(TRIM(duplicate.name))
             AND (
               CAST(NULLIF(keeper.identifier,'') AS UNSIGNED) < CAST(NULLIF(duplicate.identifier,'') AS UNSIGNED)
               OR (
                 CAST(NULLIF(keeper.identifier,'') AS UNSIGNED) = CAST(NULLIF(duplicate.identifier,'') AS UNSIGNED)
                 AND keeper.selection_id < duplicate.selection_id
               )
             )
            WHERE event.sport_id=2
              AND event.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 DAY);
            """,
            """
            DELETE a FROM web_market_adjustments a
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=a.event_id AND bad.selection_id=a.withdrawn_selection_id;
            """,
            """
            DELETE h FROM web_price_history h
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=h.event_id AND bad.selection_id=h.selection_id;
            """,
            """
            DELETE st FROM web_runner_state st
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=st.event_id AND bad.selection_id=st.selection_id;
            """,
            """
            DELETE c FROM web_non_runner_candidates c
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=c.event_id AND bad.selection_id=c.selection_id;
            """,
            """
            DELETE a FROM web_runner_actions a
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=a.event_id AND bad.selection_id=a.selection_id;
            """,
            "DELETE FROM web_activity_log WHERE activity_type='rule4' AND LOWER(message) LIKE '% or %';",
            """
            DELETE s FROM spuno_selections s
            JOIN tmp_invalid_racing_selections bad
              ON bad.event_id=s.event_id AND bad.market_id=s.market_id AND bad.selection_id=s.selection_id;
            """,
            "DROP TEMPORARY TABLE tmp_invalid_racing_selections;"
        };
        foreach (var sql in commands)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            removed += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<IReadOnlyList<PricingEvent>> SuppressNonCanonicalDuplicateEventsAsync(CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT duplicate.event_id,duplicate.sport_id,duplicate.meeting,duplicate.ev_name,
                   duplicate.start_time_unix,COALESCE(NULLIF(duplicate.status,''),'0')
            FROM spuno_events duplicate
            JOIN racing_canonical_events canonical
              ON canonical.sport_id=duplicate.sport_id
             AND LOWER(REPLACE(REPLACE(canonical.meeting,' ',''),'-',''))=
                 LOWER(REPLACE(REPLACE(duplicate.meeting,' ',''),'-',''))
             AND ABS(canonical.start_time_unix-duplicate.start_time_unix)<=90
             AND canonical.event_id<>duplicate.event_id
            WHERE duplicate.sport_id=2
              AND duplicate.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 1 DAY)
              AND (duplicate.send_to_sp<>0 OR duplicate.active<>0 OR duplicate.display<>0);
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var duplicates = new List<PricingEvent>();
        await using (var find = new MySqlCommand(findSql, connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                duplicates.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), "3", 0));
        foreach (var duplicate in duplicates)
        {
            await using var suppress = new MySqlCommand("""
                UPDATE spuno_events SET status='3',send_to_sp=0,active=0,display=0,in_progress=0 WHERE event_id=@eventId;
                UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId;
                UPDATE spuno_selections SET active=0,display=0 WHERE event_id=@eventId;
                UPDATE em_events_raw SET time_status=3,status=3,sendto_sp=0,active=0,display=0 WHERE betsapi_event_id=@eventId;
                """, connection);
            suppress.Parameters.AddWithValue("@eventId", duplicate.EventId);
            await suppress.ExecuteNonQueryAsync(cancellationToken);
        }
        return duplicates;
    }

    public async Task<int> HideSyntheticFavouriteSelectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE spuno_selections s
            JOIN spuno_events e ON e.event_id=s.event_id
            SET s.active=0,s.display=0
            WHERE e.sport_id=2 AND LOWER(s.name) LIKE '%favourite%'
              AND (s.active<>0 OR s.display<>0);
            """, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertEventsAsync(IReadOnlyList<FeedEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;
        const string sql = """
            INSERT INTO em_events_raw (
              betsapi_event_id, sport_id, league_id, league_name,
              home_id, home_name, away_id, away_name,
              starts_at_unix, starts_at_utc, time_status, ss, our_event_id, r_id,
              updated_at_unix, updated_at_utc, odds_updated_at_unix, odds_updated_at_utc,
              extra_n, raw_json, last_seen_utc, last_message, status,
              sendto_sp, active, display, spuno_odds_updated_at_unix, spuno_odds_updated_at_utc
            ) VALUES (
              @id,@sport,@leagueId,@meeting,NULL,@name,NULL,NULL,
              @start,FROM_UNIXTIME(@start),@timeStatus,NULL,NULL,NULL,
              NULLIF(@updated,0),IF(@updated>0,FROM_UNIXTIME(@updated),NULL),
              NULLIF(@oddsUpdated,0),IF(@oddsUpdated>0,FROM_UNIXTIME(@oddsUpdated),NULL),
              NULL,@raw,UTC_TIMESTAMP(),NULL,@timeStatus,0,0,0,NULL,NULL
            ) ON DUPLICATE KEY UPDATE
              sport_id=VALUES(sport_id), league_id=VALUES(league_id), league_name=VALUES(league_name),
              home_name=VALUES(home_name), starts_at_unix=VALUES(starts_at_unix), starts_at_utc=VALUES(starts_at_utc),
              time_status=IF(em_events_raw.time_status IN (2,3),em_events_raw.time_status,VALUES(time_status)),
              status=IF(em_events_raw.status IN (2,3),em_events_raw.status,VALUES(status)),
              updated_at_unix=VALUES(updated_at_unix), updated_at_utc=VALUES(updated_at_utc),
              odds_updated_at_unix=VALUES(odds_updated_at_unix), odds_updated_at_utc=VALUES(odds_updated_at_utc),
              raw_json=VALUES(raw_json), last_seen_utc=UTC_TIMESTAMP();
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var item in events)
        {
            var subscribed = await IsMeetingSubscribedAsync(connection, transaction, item.SportId, item.Meeting, cancellationToken);
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@id", item.EventId);
            command.Parameters.AddWithValue("@sport", item.SportId);
            command.Parameters.AddWithValue("@leagueId", item.LeagueId > 0 ? item.LeagueId : DBNull.Value);
            command.Parameters.AddWithValue("@meeting", item.Meeting);
            command.Parameters.AddWithValue("@name", item.Name);
            command.Parameters.AddWithValue("@start", item.StartTimeUnix);
            command.Parameters.AddWithValue("@timeStatus", item.TimeStatus);
            command.Parameters.AddWithValue("@updated", item.UpdatedAtUnix);
            command.Parameters.AddWithValue("@oddsUpdated", item.OddsUpdatedAtUnix);
            command.Parameters.AddWithValue("@raw", item.RawJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var stageEvent = new MySqlCommand("""
                INSERT INTO spuno_events (
                  event_id,sport_id,meeting,ev_name,start_time,start_time_unix,
                  status,send_to_sp,active,display,in_progress
                ) VALUES (
                  @id,@sport,@meeting,@name,FROM_UNIXTIME(@start),@start,
                  @status,@subscribed,@subscribed,@display,
                  0
                )
                ON DUPLICATE KEY UPDATE
                  sport_id=VALUES(sport_id),meeting=VALUES(meeting),ev_name=VALUES(ev_name),
                  start_time=VALUES(start_time),start_time_unix=VALUES(start_time_unix),
                  send_to_sp=VALUES(send_to_sp),
                  active=IF(COALESCE(NULLIF(spuno_events.status,''),'0') IN ('2','3','resulted','post game'),0,VALUES(active)),
                  display=IF(COALESCE(NULLIF(spuno_events.status,''),'0') IN ('2','3','resulted','post game'),
                             0,VALUES(display));
                """, connection, transaction);
            stageEvent.Parameters.AddWithValue("@id", item.EventId);
            stageEvent.Parameters.AddWithValue("@sport", item.SportId);
            stageEvent.Parameters.AddWithValue("@meeting", item.Meeting);
            stageEvent.Parameters.AddWithValue("@name", item.Name);
            stageEvent.Parameters.AddWithValue("@start", item.StartTimeUnix);
            stageEvent.Parameters.AddWithValue("@status", item.TimeStatus.ToString());
            stageEvent.Parameters.AddWithValue("@subscribed", subscribed ? 1 : 0);
            stageEvent.Parameters.AddWithValue("@display", subscribed ? 1 : 0);
            await stageEvent.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var syncEvents = new MySqlCommand("""
            UPDATE spuno_events se
            JOIN em_events_raw raw ON raw.betsapi_event_id = se.event_id
            SET se.sport_id=raw.sport_id,
                se.meeting=COALESCE(NULLIF(raw.league_name,''),'Unassigned'),
                se.ev_name=COALESCE(NULLIF(raw.home_name,''),NULLIF(raw.away_name,''),se.ev_name),
                se.start_time=FROM_UNIXTIME(raw.starts_at_unix),
                se.start_time_unix=raw.starts_at_unix,
                se.status=CASE
                    WHEN COALESCE(NULLIF(se.status,''),'0') IN ('2','3','resulted','post game') THEN se.status
                    -- Discovery may tell us that an upstream event is status 2,
                    -- but copying that directly would silently stop pricing
                    -- without publishing the required terminal event. Only the
                    -- in-play transition is staged here; suspension is owned by
                    -- RacingSuspensionWorker after an explicit provider signal.
                    WHEN raw.starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                         AND raw.time_status=1 THEN '1'
                    ELSE se.status
                END
            WHERE raw.sport_id IN (2,4)
              AND raw.last_seen_utc >= UTC_TIMESTAMP() - INTERVAL 1 MINUTE;
            """, connection, transaction))
            await syncEvents.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsMeetingSubscribedAsync(
        MySqlConnection connection, MySqlTransaction transaction, int sportId, string meeting,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT EXISTS(
              SELECT 1 FROM web_competition_subscriptions
              WHERE sport_id=@sportId AND competition_key=@competitionKey AND subscribed=1
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("@sportId", sportId);
        command.Parameters.AddWithValue("@competitionKey", CompetitionKey(meeting));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string CompetitionKey(string meeting)
    {
        var value = meeting.Trim();
        if (value.EndsWith(" EVE", StringComparison.OrdinalIgnoreCase)) value = value[..^4].Trim();
        else if (value.EndsWith(" AM", StringComparison.OrdinalIgnoreCase) ||
                 value.EndsWith(" PM", StringComparison.OrdinalIgnoreCase)) value = value[..^3].Trim();
        return value.ToLowerInvariant();
    }

    private static bool IsInTodaysUkTradingWindow(long startTimeUnix)
    {
        var start = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.FromUnixTimeSeconds(startTimeUnix), "GMT Standard Time");
        var today = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.UtcNow, "GMT Standard Time").Date;
        return start.Date == today &&
               start.TimeOfDay >= TimeSpan.FromHours(11) &&
               start.TimeOfDay <= TimeSpan.FromHours(22);
    }

    public async Task<IReadOnlyList<PricingEvent>> GetPriceCandidatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT se.event_id, se.sport_id, se.meeting, se.ev_name, se.start_time_unix,
                   CASE
                       WHEN se.status = '2' THEN '2'
                       WHEN raw.time_status = 1 THEN '1'
                       ELSE COALESCE(NULLIF(se.status,''), CAST(raw.time_status AS CHAR), '0')
                   END AS event_status,
                   GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,0)) AS odds_updated,
                   (COALESCE(raw.spuno_odds_updated_at_unix,0) < GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,1))
                    OR NOT EXISTS (SELECT 1 FROM spuno_selections fs WHERE fs.event_id=se.event_id AND fs.market_name='Race Winner')
                    OR EXISTS (
                        SELECT 1 FROM spuno_selections fs
                        WHERE fs.event_id=se.event_id AND fs.market_name='Race Winner'
                        GROUP BY fs.event_id
                        HAVING SUM(fs.scratch=0 AND fs.active<>0 AND fs.display<>0 AND fs.price_back>1 AND fs.price_back<1000) >= 2
                           AND SUM(CASE WHEN fs.scratch=0 AND fs.active<>0 AND fs.display<>0 AND fs.price_back>1 AND fs.price_back<1000
                                        THEN 1/fs.price_back ELSE 0 END) NOT BETWEEN 0.98 AND 1.02
                    )) AS force_refresh
            FROM spuno_events se
            JOIN em_events_raw raw ON raw.betsapi_event_id = se.event_id
            WHERE se.send_to_sp <> 0
              AND se.sport_id IN (2,4)
              AND se.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 2 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND COALESCE(NULLIF(se.status,''), '0') NOT IN ('3','resulted','post game')
              AND (
                  raw.time_status = 1
                  OR COALESCE(raw.spuno_odds_updated_at_unix,0) < GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,1))
                  OR (
                      se.start_time_unix <= UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 3 HOUR)
                      AND COALESCE(raw.spuno_odds_updated_at_utc,'1970-01-01') < UTC_TIMESTAMP() - INTERVAL 10 SECOND
                  )
                  OR NOT EXISTS (
                      SELECT 1 FROM spuno_selections s
                      WHERE s.event_id=se.event_id AND s.market_name='Race Winner'
                  )
                  OR EXISTS (
                      SELECT 1 FROM spuno_selections s
                      WHERE s.event_id=se.event_id AND s.market_name='Race Winner'
                      GROUP BY s.event_id
                      HAVING SUM(s.scratch=0 AND s.active<>0 AND s.display<>0 AND s.price_back>1 AND s.price_back<1000) >= 2
                         AND SUM(CASE WHEN s.scratch=0 AND s.active<>0 AND s.display<>0 AND s.price_back>1 AND s.price_back<1000
                                      THEN 1/s.price_back ELSE 0 END) NOT BETWEEN 0.98 AND 1.02
                  )
              )
            ORDER BY CASE
                         WHEN COALESCE(raw.spuno_odds_updated_at_unix,0)=-5 THEN 0
                         WHEN COALESCE(raw.spuno_odds_updated_at_unix,0)=-2 THEN 1
                         WHEN COALESCE(raw.spuno_odds_updated_at_unix,0)=-4 THEN 2
                         WHEN COALESCE(raw.spuno_odds_updated_at_unix,0)=-3 THEN 3
                         ELSE 4
                     END,
                     raw.time_status DESC,
                     CASE
                         WHEN COALESCE(raw.spuno_odds_updated_at_unix,0) < GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,1)) THEN 0
                         WHEN NOT EXISTS (SELECT 1 FROM spuno_selections s WHERE s.event_id=se.event_id AND s.market_name='Race Winner') THEN 1
                         ELSE 2
                     END,
                     COALESCE(raw.spuno_odds_updated_at_utc,'1970-01-01'),
                     se.start_time_unix
            LIMIT 40;
            """;
        var output = new List<PricingEvent>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetBoolean(7)));
        return output;
    }

    public async Task MarkPricePollCompletedAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE em_events_raw
            SET spuno_odds_updated_at_unix=GREATEST(
                    COALESCE(odds_updated_at_unix,0),
                    COALESCE(updated_at_unix,0),
                    UNIX_TIMESTAMP()),
                spuno_odds_updated_at_utc=UTC_TIMESTAMP()
            WHERE betsapi_event_id=@eventId;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> QueueMeetingRepublishAsync(int sportId, string meeting, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE em_events_raw raw
            JOIN spuno_events e ON e.event_id=raw.betsapi_event_id
            SET raw.spuno_odds_updated_at_unix=0,
                raw.spuno_odds_updated_at_utc='1970-01-01 00:00:00'
            WHERE e.sport_id=@sportId AND e.meeting=@meeting
              AND e.send_to_sp<>0
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('3','resulted','post game');
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sportId", sportId);
        command.Parameters.AddWithValue("@meeting", RacingEventNames.CanonicalMeeting(meeting));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> QueueAllActiveEventsRepriceAsync(CancellationToken cancellationToken)
    {
        // One bounded update replaces the old meeting-by-meeting refresh loop.
        // A global refresh is an explicit operator request, so -5 places these
        // races ahead of routine provider deltas while the FIFO timestamp stops
        // one race from repeatedly jumping ahead of the rest.
        const string findSql = """
            SELECT e.event_id
            FROM spuno_events e
            WHERE e.send_to_sp<>0 AND e.active=1
              AND e.sport_id IN (2,4)
              AND e.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 48 HOUR)
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('2','3','resulted','post game')
            ORDER BY e.start_time_unix,e.event_id;
            """;
        const string updateSql = """
            UPDATE em_events_raw
            SET spuno_odds_updated_at_utc=@queued,
                spuno_odds_updated_at_unix=-5
            WHERE betsapi_event_id=@eventId;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var eventIds = new List<long>();
        await using (var find = new MySqlCommand(findSql, connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) eventIds.Add(reader.GetInt64(0));
        await using var lockTimeout = await SessionLockWaitTimeout.ApplyAsync(connection, 1, cancellationToken);
        var queued = 0;
        var queuedAt = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(4);
        foreach (var eventId in eventIds)
        {
            if (DateTime.UtcNow >= deadline) break;
            try
            {
                await using var update = new MySqlCommand(updateSql, connection) { CommandTimeout = 1 };
                update.Parameters.AddWithValue("@queued", queuedAt);
                update.Parameters.AddWithValue("@eventId", eventId);
                queued += await update.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (MySqlException)
            {
                // A currently publishing race will naturally be polled again;
                // never let that one row block the operator's global request.
            }
        }
        return queued;
    }

    public async Task QueueSpreadexRepriceAsync(string meeting, long startTimeUnix, CancellationToken cancellationToken)
    {
        // Resolve the canonical event first, then update em_events_raw by its indexed
        // provider id. The previous UPDATE ... JOIN caused MySQL to lock thousands of
        // unrelated event rows and could block the time-critical OFF transaction.
        const string findSql = """
            SELECT event_id
            FROM spuno_events
            WHERE sport_id=2
              AND LOWER(TRIM(meeting))=LOWER(TRIM(@meeting))
              AND ABS(start_time_unix-@start)<=180
              AND send_to_sp<>0 AND active=1
              AND COALESCE(NULLIF(status,''),'0') NOT IN ('2','3','resulted','post game')
            ORDER BY ABS(start_time_unix-@start),event_id
            LIMIT 1;
            """;
        const string updateSql = """
            UPDATE em_events_raw
            SET spuno_odds_updated_at_utc=CASE
                    -- Preserve the original queue time while this event is
                    -- already waiting. Continually moving an active item to
                    -- the head of the queue allowed the same early races to
                    -- starve every later race whose BetsAPI card needed a poll.
                    WHEN spuno_odds_updated_at_unix IN (-5,-2)
                    THEN spuno_odds_updated_at_utc
                    ELSE UTC_TIMESTAMP(6)
                END,
                spuno_odds_updated_at_unix=IF(spuno_odds_updated_at_unix=-5,-5,-2)
            WHERE betsapi_event_id=@eventId;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        long? eventId;
        await using (var find = new MySqlCommand(findSql, connection))
        {
            find.Parameters.AddWithValue("@meeting", RacingEventNames.CanonicalMeeting(meeting));
            find.Parameters.AddWithValue("@start", startTimeUnix);
            var value = await find.ExecuteScalarAsync(cancellationToken);
            eventId = value is null or DBNull ? null : Convert.ToInt64(value);
        }
        if (eventId is null) return;
        await using var update = new MySqlCommand(updateSql, connection);
        update.Parameters.AddWithValue("@eventId", eventId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> QueueEventRepriceAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE em_events_raw raw
            JOIN spuno_events e ON e.event_id=raw.betsapi_event_id
            SET raw.spuno_odds_updated_at_unix=-5,
                raw.spuno_odds_updated_at_utc='1970-01-01 00:00:00'
            WHERE e.event_id=@eventId AND e.send_to_sp<>0
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('2','3','resulted','post game');
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetEventsForPublishingAsync(IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.Where(x => x > 0).Distinct().Take(500).ToList();
        if (ids.Count == 0) return [];
        var names = string.Join(',', ids.Select((_, i) => $"@id{i}"));
        var sql = $"""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events
            WHERE event_id IN ({names}) AND send_to_sp<>0
            ORDER BY start_time_unix,event_id
            """;
        await using var connection = OpenConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue($"@id{i}", ids[i]);
        var events = new List<PricingEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) events.Add(new PricingEvent(
            reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6)));
        return events;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetEventsForTerminalRepairAsync(
        IReadOnlyList<long> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.Where(x => x > 0).Distinct().Take(500).ToList();
        if (ids.Count == 0) return [];
        var names = string.Join(',', ids.Select((_, i) => $"@repairId{i}"));
        var sql = $"""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,
                   COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events
            WHERE event_id IN ({names})
            ORDER BY start_time_unix,event_id;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue($"@repairId{i}", ids[i]);
        var events = new List<PricingEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6)));
        return events;
    }

    public async Task<IReadOnlyList<PricingEvent>> ClaimInitialEventPublicationsAsync(
        IReadOnlyList<PricingEvent> events, CancellationToken cancellationToken)
    {
        var claimed = new List<PricingEvent>();
        if (events.Count == 0) return claimed;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var racingEvent in events.Where(x => x.Status is not ("2" or "3")))
        {
            await using var command = new MySqlCommand("""
                INSERT IGNORE INTO web_event_lifecycle_publish(event_id,creation_claimed_at_utc)
                VALUES(@eventId,UTC_TIMESTAMP(6));
                """, connection);
            command.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
                claimed.Add(racingEvent);
        }
        return claimed;
    }

    public async Task ReleaseInitialEventPublicationClaimsAsync(
        IEnumerable<long> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0) return;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var eventId in ids)
        {
            await using var command = new MySqlCommand(
                "DELETE FROM web_event_lifecycle_publish WHERE event_id=@eventId;", connection);
            command.Parameters.AddWithValue("@eventId", eventId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<GeneratedEventSnapshot>> BuildCurrentSnapshotsForPublishingAsync(
        CancellationToken cancellationToken)
    {
        await EnsureLineMarketSchemaAsync(cancellationToken);
        const string eventSql = """
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,
                   COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events e
            WHERE e.send_to_sp<>0
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('2','3','resulted','post game')
              AND e.start_time_unix BETWEEN UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 10 MINUTE)
                                        AND UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 48 HOUR)
              AND EXISTS (
                  SELECT 1 FROM spuno_selections s
                  WHERE s.event_id=e.event_id AND s.market_name='Race Winner'
              )
            ORDER BY e.start_time_unix,e.event_id;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        var events = new List<PricingEvent>();
        await using (var command = new MySqlCommand(eventSql, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                events.Add(new PricingEvent(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6)));

        var snapshots = new List<GeneratedEventSnapshot>(events.Count);
        foreach (var racingEvent in events)
        {
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var runners = await LoadRaceWinnerAsync(
                connection, transaction, racingEvent.EventId, cancellationToken);
            if (runners.Count < 2)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }
            var configuration = await LoadConfigurationAsync(
                connection, transaction, racingEvent.EventId, cancellationToken);
            var markets = _calculator.Build(racingEvent, runners, configuration)
                .Select(market => market.MarketTypeId is 1010 or 1011
                    ? market with { Display = false }
                    : market)
                .ToList();
            await ApplyStoredSelectionStateAsync(
                connection, transaction, racingEvent.EventId, markets, cancellationToken);
            foreach (var market in markets)
                await UpsertMarketShellAsync(connection, transaction, racingEvent.EventId,
                    market.MarketId, market.Name, market.DisplayOrder, market.Active, market.Display,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            snapshots.Add(new GeneratedEventSnapshot(racingEvent, markets));
        }
        return snapshots;
    }

    public async Task<GeneratedEventSnapshot?> BuildConfiguredMarketForPublishingAsync(
        long eventId,
        int marketSuffix,
        CancellationToken cancellationToken)
    {
        await EnsureLineMarketSchemaAsync(cancellationToken);
        const string eventSql = """
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,
                   COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events
            WHERE event_id=@eventId AND send_to_sp<>0
              AND COALESCE(NULLIF(status,''),'0') NOT IN ('2','3','resulted','post game')
            LIMIT 1;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        PricingEvent? racingEvent;
        await using (var command = new MySqlCommand(eventSql, connection))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            racingEvent = await reader.ReadAsync(cancellationToken)
                ? new PricingEvent(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6))
                : null;
        }
        if (racingEvent is null) return null;

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);
        var runners = await LoadRaceWinnerAsync(connection, transaction, eventId, cancellationToken);
        if (runners.Count < 2)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var configuration = await LoadConfigurationAsync(connection, transaction, eventId, cancellationToken);
        var market = _calculator.Build(racingEvent, runners, configuration)
            .FirstOrDefault(candidate => candidate.MarketId % 10000 == marketSuffix);
        if (market is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await ApplyStoredSelectionStateAsync(
            connection, transaction, eventId, [market], cancellationToken);
        await UpsertMarketAsync(connection, transaction, eventId, market, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GeneratedEventSnapshot(racingEvent, [market]);
    }

    public async Task<IReadOnlyList<PricingEvent>> SuppressImplausibleHorseEventsAsync(CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT se.event_id,se.sport_id,se.meeting,se.ev_name,se.start_time_unix,COALESCE(NULLIF(se.status,''),'0')
            FROM spuno_events se
            JOIN em_events_raw raw ON raw.betsapi_event_id=se.event_id
            WHERE se.sport_id=2 AND se.send_to_sp<>0 AND COALESCE(NULLIF(se.status,''),'0') NOT IN ('3','resulted','post game')
              AND raw.time_status=1
              AND raw.starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 4 HOUR)
              AND (SELECT COUNT(*) FROM em_events_raw nearby
                   WHERE nearby.sport_id=2 AND nearby.league_name=raw.league_name
                     AND ABS(nearby.starts_at_unix-raw.starts_at_unix)<=120) >= 3;
            """;
        var events = new List<PricingEvent>();
        await using var connection = OpenConnection(); await connection.OpenAsync(cancellationToken);
        await using (var find = new MySqlCommand(findSql, connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) events.Add(new(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), "3", 0, true));
        foreach (var ev in events)
        {
            await using var suppress = new MySqlCommand("""
                UPDATE em_events_raw SET sendto_sp=0,active=0,display=0 WHERE betsapi_event_id=@eventId;
                UPDATE spuno_events SET status='3',send_to_sp=0,active=0,display=0,in_progress=0 WHERE event_id=@eventId;
                UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId;
                UPDATE spuno_selections SET active=0,display=0 WHERE event_id=@eventId;
                """, connection);
            suppress.Parameters.AddWithValue("@eventId", ev.EventId);
            await suppress.ExecuteNonQueryAsync(cancellationToken);
        }
        return events;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetDuePreLivePriceCandidatesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT se.event_id, se.sport_id, se.meeting, se.ev_name, se.start_time_unix,
                   COALESCE(NULLIF(se.status,''), CAST(raw.time_status AS CHAR), '0') AS event_status,
                   GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,0)) AS odds_updated,
                   0 AS force_refresh
            FROM spuno_events se
            JOIN em_events_raw raw ON raw.betsapi_event_id = se.event_id
            WHERE se.send_to_sp <> 0
              AND se.sport_id IN (2,4)
              AND se.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP())
              AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 12 HOUR)
              AND COALESCE(NULLIF(se.status,''), '0') NOT IN ('3','resulted','post game')
              AND COALESCE(raw.spuno_odds_updated_at_utc,'1970-01-01') < UTC_TIMESTAMP() - INTERVAL 10 SECOND
              ORDER BY EXISTS (
                  SELECT 1 FROM spuno_selections initial
                  WHERE initial.event_id=se.event_id AND initial.market_name='Race Winner'
              ) ASC,
              se.start_time_unix, COALESCE(raw.spuno_odds_updated_at_utc,'1970-01-01')
            LIMIT 10;
            """;
        var output = new List<PricingEvent>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetBoolean(7)));
        return output;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetSuspensionCandidatesAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT se.event_id, se.sport_id, se.meeting, se.ev_name, se.start_time_unix,
                   COALESCE(NULLIF(se.status,''), CAST(raw.time_status AS CHAR), '0') AS event_status,
                   GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,0)) AS odds_updated,
                   0 AS force_refresh
            FROM spuno_events se
            LEFT JOIN em_events_raw raw ON raw.betsapi_event_id = se.event_id
            WHERE se.send_to_sp <> 0
              AND se.sport_id IN (2,4)
              AND se.start_time_unix BETWEEN UNIX_TIMESTAMP()-7200 AND UNIX_TIMESTAMP()+900
              AND COALESCE(NULLIF(se.status,''), '0') NOT IN ('2','3','resulted','post game')
            -- Check races nearest to now first. Ordering by the oldest start
            -- allowed stale horse/greyhound rows to consume the old LIMIT 50
            -- and starve the race that was actually due to go off.
            ORDER BY ABS(se.start_time_unix-UNIX_TIMESTAMP()),se.start_time_unix
            LIMIT 250;
            """;
        var output = new List<PricingEvent>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetBoolean(7)));
        return output;
    }

    public async Task<IReadOnlyList<PricingEvent>> GetPriceCandidatesByIdsAsync(
        IReadOnlyList<long> eventIds,
        CancellationToken cancellationToken)
    {
        var ids = eventIds.Where(x => x > 0).Distinct().Take(100).ToList();
        if (ids.Count == 0) return [];
        var parameters = string.Join(',', ids.Select((_, index) => $"@eventId{index}"));
        var sql = $"""
            SELECT se.event_id, se.sport_id, se.meeting, se.ev_name, se.start_time_unix,
                   COALESCE(NULLIF(se.status,''), CAST(raw.time_status AS CHAR), '0') AS event_status,
                   GREATEST(COALESCE(raw.odds_updated_at_unix,0),COALESCE(raw.updated_at_unix,0)) AS odds_updated,
                   EXISTS (
                       SELECT 1 FROM spuno_selections fs
                       WHERE fs.event_id=se.event_id AND fs.market_name='Race Winner'
                       GROUP BY fs.event_id
                       HAVING SUM(fs.scratch=0 AND fs.active<>0 AND fs.display<>0 AND fs.price_back>1 AND fs.price_back<1000) >= 2
                          AND SUM(CASE WHEN fs.scratch=0 AND fs.active<>0 AND fs.display<>0 AND fs.price_back>1 AND fs.price_back<1000
                                       THEN 1/fs.price_back ELSE 0 END) NOT BETWEEN 0.98 AND 1.02
                   ) AS force_refresh
            FROM spuno_events se
            JOIN em_events_raw raw ON raw.betsapi_event_id = se.event_id
            WHERE se.send_to_sp <> 0
              AND se.sport_id IN (2,4)
              AND se.event_id IN ({parameters})
              AND se.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 2 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND COALESCE(NULLIF(se.status,''), '0') NOT IN ('3','resulted','post game')
            ORDER BY se.start_time_unix;
            """;
        var output = new List<PricingEvent>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        for (var index = 0; index < ids.Count; index++) command.Parameters.AddWithValue($"@eventId{index}", ids[index]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6), reader.GetBoolean(7)));
        return output;
    }

    public async Task StorePriceSourcesAsync(
        PricingEvent racingEvent,
        FeedPriceComparison comparison,
        CancellationToken cancellationToken)
    {
        var snapshots = new[] { comparison.Direct, comparison.BetsApi }.Where(x => x is not null).Cast<FeedPriceSnapshot>().ToList();
        if (snapshots.Count == 0) return;
        if (racingEvent.SportId == 4)
            await EnsureTrapHistorySchemaAsync(cancellationToken);

        const string snapshotSql = """
            INSERT INTO web_race_winner_source_snapshots
              (event_id,source_name,source_updated_at_utc,observed_at_utc,runner_count,priced_runner_count,is_selected)
            VALUES (@eventId,@source,@updated,@observed,@runnerCount,@pricedRunnerCount,@selected)
            ON DUPLICATE KEY UPDATE
              source_updated_at_utc=VALUES(source_updated_at_utc), observed_at_utc=VALUES(observed_at_utc),
              runner_count=VALUES(runner_count), priced_runner_count=VALUES(priced_runner_count),
              is_selected=VALUES(is_selected);
            """;
        const string priceSql = """
            INSERT INTO web_race_winner_source_prices
              (event_id,competitor_id,source_name,selection_name,identifier,price_text,decimal_price,has_price,
               source_updated_at_utc,observed_at_utc,raw_json)
            VALUES (@eventId,@competitorId,@source,@name,@identifier,@priceText,@price,@hasPrice,@updated,@observed,@raw)
            ON DUPLICATE KEY UPDATE selection_name=VALUES(selection_name),identifier=VALUES(identifier),
              price_text=VALUES(price_text),decimal_price=VALUES(decimal_price),has_price=VALUES(has_price),
              source_updated_at_utc=VALUES(source_updated_at_utc),observed_at_utc=VALUES(observed_at_utc),
              raw_json=VALUES(raw_json);
            """;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var previousTrapStates = racingEvent.SportId == 4
            ? await LoadLatestTrapStatesAsync(connection, transaction, racingEvent.EventId, cancellationToken)
            : new Dictionary<(string Source, int Trap), TrapObservationState>();
        await using (var clearSelected = new MySqlCommand(
            "UPDATE web_race_winner_source_snapshots SET is_selected=0 WHERE event_id=@eventId;", connection, transaction))
        {
            clearSelected.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await clearSelected.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var snapshot in snapshots)
        {
            var snapshotRunners = snapshot.Runners
                .Where(runner => runner.CompetitorId > 0)
                .GroupBy(runner => runner.CompetitorId)
                .Select(group => group.OrderByDescending(runner => runner.Price > 1).First())
                .ToList();
            await using (var saveSnapshot = new MySqlCommand(snapshotSql, connection, transaction))
            {
                saveSnapshot.Parameters.AddWithValue("@eventId", racingEvent.EventId);
                saveSnapshot.Parameters.AddWithValue("@source", snapshot.Source);
                saveSnapshot.Parameters.AddWithValue("@updated", snapshot.UpdatedAtUtc);
                saveSnapshot.Parameters.AddWithValue("@observed", snapshot.ObservedAtUtc);
                saveSnapshot.Parameters.AddWithValue("@runnerCount", snapshotRunners.Count);
                saveSnapshot.Parameters.AddWithValue("@pricedRunnerCount", snapshotRunners.Count(runner => runner.Price > 1));
                saveSnapshot.Parameters.AddWithValue("@selected", comparison.Selected?.Source == snapshot.Source ? 1 : 0);
                await saveSnapshot.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var runner in snapshotRunners)
            {
                await using var savePrice = new MySqlCommand(priceSql, connection, transaction);
                savePrice.Parameters.AddWithValue("@eventId", racingEvent.EventId);
                savePrice.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
                savePrice.Parameters.AddWithValue("@source", snapshot.Source);
                savePrice.Parameters.AddWithValue("@name", runner.Name);
                savePrice.Parameters.AddWithValue("@identifier", runner.Identifier);
                savePrice.Parameters.AddWithValue("@priceText", runner.PriceText);
                savePrice.Parameters.AddWithValue("@price", runner.Price > 1 ? runner.Price : DBNull.Value);
                savePrice.Parameters.AddWithValue("@hasPrice", runner.HasPrice ? 1 : 0);
                savePrice.Parameters.AddWithValue("@updated", runner.SourceUpdatedAtUtc ?? snapshot.UpdatedAtUtc);
                savePrice.Parameters.AddWithValue("@observed", snapshot.ObservedAtUtc);
                savePrice.Parameters.AddWithValue("@raw", runner.RawJson);
                await savePrice.ExecuteNonQueryAsync(cancellationToken);
                if (racingEvent.SportId == 4)
                    await StoreTrapObservationIfChangedAsync(connection, transaction, racingEvent, snapshot,
                        runner, previousTrapStates, cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GreyhoundTrapObservation>> GetTrapHistoryAsync(
        long eventId, CancellationToken cancellationToken)
    {
        await EnsureTrapHistorySchemaAsync(cancellationToken);
        const string sql = """
            SELECT id,event_id,trap_number,source_name,competitor_id,runner_name,price_text,
                   decimal_price,has_price,explicit_non_runner,is_vacant,change_type,
                   source_updated_at_utc,observed_at_utc,raw_json
            FROM web_greyhound_trap_observations
            WHERE event_id=@eventId
            ORDER BY observed_at_utc,id;
            """;
        var output = new List<GreyhoundTrapObservation>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new(
                reader.GetInt64("id"), reader.GetInt64("event_id"), reader.GetInt32("trap_number"),
                reader.GetString("source_name"), reader.GetInt64("competitor_id"), reader.GetString("runner_name"),
                reader.IsDBNull("price_text") ? null : reader.GetString("price_text"),
                reader.IsDBNull("decimal_price") ? null : reader.GetDouble("decimal_price"),
                reader.GetBoolean("has_price"), reader.GetBoolean("explicit_non_runner"),
                reader.GetBoolean("is_vacant"), reader.GetString("change_type"),
                reader.GetDateTime("source_updated_at_utc"), reader.GetDateTime("observed_at_utc"),
                reader.IsDBNull("raw_json") ? null : reader.GetString("raw_json")));
        return output;
    }

    private async Task EnsureTrapHistorySchemaAsync(CancellationToken cancellationToken)
    {
        if (_trapHistorySchemaReady) return;
        await _trapHistorySchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_trapHistorySchemaReady) return;
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS web_greyhound_trap_observations (
                    id BIGINT NOT NULL AUTO_INCREMENT,
                    event_id BIGINT NOT NULL,
                    trap_number INT NOT NULL,
                    source_name VARCHAR(16) NOT NULL,
                    competitor_id BIGINT NOT NULL,
                    runner_name VARCHAR(200) NOT NULL,
                    normalized_name VARCHAR(200) NOT NULL,
                    price_text VARCHAR(32) NULL,
                    decimal_price DOUBLE NULL,
                    has_price TINYINT(1) NOT NULL,
                    explicit_non_runner TINYINT(1) NOT NULL,
                    is_vacant TINYINT(1) NOT NULL,
                    change_type VARCHAR(32) NOT NULL,
                    source_updated_at_utc DATETIME(6) NOT NULL,
                    observed_at_utc DATETIME(6) NOT NULL,
                    raw_json LONGTEXT NULL,
                    PRIMARY KEY (id),
                    INDEX ix_trap_history_event (event_id,trap_number,observed_at_utc,id),
                    INDEX ix_trap_history_latest (event_id,source_name,trap_number,id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                """, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _trapHistorySchemaReady = true;
        }
        finally
        {
            _trapHistorySchemaLock.Release();
        }
    }

    private static async Task<Dictionary<(string Source, int Trap), TrapObservationState>> LoadLatestTrapStatesAsync(
        MySqlConnection connection, MySqlTransaction transaction, long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT h.source_name,h.trap_number,h.competitor_id,h.runner_name,h.normalized_name,
                   h.price_text,h.decimal_price,h.has_price,h.explicit_non_runner,h.is_vacant
            FROM web_greyhound_trap_observations h
            JOIN (
                SELECT source_name,trap_number,MAX(id) id
                FROM web_greyhound_trap_observations WHERE event_id=@eventId
                GROUP BY source_name,trap_number
            ) latest ON latest.id=h.id;
            """;
        var output = new Dictionary<(string Source, int Trap), TrapObservationState>();
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = new TrapObservationState(
                reader.GetInt64("competitor_id"), reader.GetString("runner_name"), reader.GetString("normalized_name"),
                reader.IsDBNull("price_text") ? null : reader.GetString("price_text"),
                reader.IsDBNull("decimal_price") ? null : reader.GetDouble("decimal_price"),
                reader.GetBoolean("has_price"), reader.GetBoolean("explicit_non_runner"), reader.GetBoolean("is_vacant"));
            output[(reader.GetString("source_name"), reader.GetInt32("trap_number"))] = state;
        }
        return output;
    }

    private static async Task StoreTrapObservationIfChangedAsync(
        MySqlConnection connection, MySqlTransaction transaction, PricingEvent racingEvent,
        FeedPriceSnapshot snapshot, FeedRunner runner,
        IDictionary<(string Source, int Trap), TrapObservationState> states,
        CancellationToken cancellationToken)
    {
        var normalizedName = BetfairMarketMatcher.NormalizeRunner(runner.Name);
        var isVacant = normalizedName.StartsWith("vacant trap", StringComparison.Ordinal) ||
                       normalizedName.Equals("vacant", StringComparison.Ordinal);
        var current = new TrapObservationState(runner.CompetitorId, runner.Name, normalizedName,
            runner.PriceText, runner.Price > 1 ? runner.Price : null, runner.HasPrice,
            runner.ExplicitNonRunner, isVacant);
        states.TryGetValue((snapshot.Source, runner.Identifier), out var previous);
        var changeType = TrapChangeType(previous, current);
        if (changeType is null) return;

        await using var command = new MySqlCommand("""
            INSERT INTO web_greyhound_trap_observations
              (event_id,trap_number,source_name,competitor_id,runner_name,normalized_name,
               price_text,decimal_price,has_price,explicit_non_runner,is_vacant,change_type,
               source_updated_at_utc,observed_at_utc,raw_json)
            VALUES
              (@eventId,@trap,@source,@competitorId,@name,@normalizedName,@priceText,@price,
               @hasPrice,@nonRunner,@vacant,@changeType,@updated,@observed,@raw);
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", racingEvent.EventId);
        command.Parameters.AddWithValue("@trap", runner.Identifier);
        command.Parameters.AddWithValue("@source", snapshot.Source);
        command.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
        command.Parameters.AddWithValue("@name", runner.Name);
        command.Parameters.AddWithValue("@normalizedName", normalizedName);
        command.Parameters.AddWithValue("@priceText", (object?)runner.PriceText ?? DBNull.Value);
        command.Parameters.AddWithValue("@price", current.DecimalPrice is double price ? price : DBNull.Value);
        command.Parameters.AddWithValue("@hasPrice", runner.HasPrice ? 1 : 0);
        command.Parameters.AddWithValue("@nonRunner", runner.ExplicitNonRunner ? 1 : 0);
        command.Parameters.AddWithValue("@vacant", isVacant ? 1 : 0);
        command.Parameters.AddWithValue("@changeType", changeType);
        command.Parameters.AddWithValue("@updated", runner.SourceUpdatedAtUtc ?? snapshot.UpdatedAtUtc);
        command.Parameters.AddWithValue("@observed", snapshot.ObservedAtUtc);
        command.Parameters.AddWithValue("@raw", runner.RawJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
        states[(snapshot.Source, runner.Identifier)] = current;
    }

    private static string? TrapChangeType(TrapObservationState? previous, TrapObservationState current)
    {
        if (previous is null) return "INITIAL";
        if (!previous.IsVacant && current.IsVacant) return "BECAME_VACANT";
        if (previous.IsVacant && !current.IsVacant) return "REPLACEMENT_ADDED";
        if (previous.CompetitorId != current.CompetitorId || previous.NormalizedName != current.NormalizedName)
            return "RUNNER_CHANGED";
        if (previous.ExplicitNonRunner != current.ExplicitNonRunner || previous.HasPrice != current.HasPrice)
            return current.ExplicitNonRunner || !current.HasPrice ? "SUSPENDED" : "REACTIVATED";
        if (previous.PriceText != current.PriceText || previous.DecimalPrice != current.DecimalPrice)
            return "PRICE_CHANGED";
        return null;
    }

    private sealed record TrapObservationState(long CompetitorId, string RunnerName, string NormalizedName,
        string? PriceText, double? DecimalPrice, bool HasPrice, bool ExplicitNonRunner, bool IsVacant);

    public async Task<GeneratedEventSnapshot?> ApplyPricesAsync(PricingEvent racingEvent, IReadOnlyList<FeedRunner> feedRunners,
        IReadOnlyList<double>? indexPoints, IReadOnlySet<long>? spreadexSuspendedCompetitorIds,
        IReadOnlySet<string>? spreadexSuspendedRunnerNames, CancellationToken cancellationToken,
        bool createSuspendedCard = false)
    {
        if (feedRunners.Count < 2) return null;
        await EnsureLineMarketSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var lockTimeout = await SessionLockWaitTimeout.ApplyAsync(connection, 2, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (racingEvent.SportId == 4)
        {
            // These are standard greyhound markets. Each event gets its own
            // permanently locked pick'em lines on the first successful price.
            await using var enableLineMarkets = new MySqlCommand("""
                INSERT IGNORE INTO web_greyhound_line_market_config
                  (event_id,multi_traps_enabled,triple_traps_enabled,updated_at_utc)
                VALUES(@eventId,1,1,UTC_TIMESTAMP(6));
                """, connection, transaction);
            enableLineMarkets.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await enableLineMarkets.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var eventState = new MySqlCommand("""
            SELECT COALESCE(NULLIF(status,''),'0')
            FROM spuno_events WHERE event_id=@eventId FOR UPDATE;
            """, connection, transaction))
        {
            eventState.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            var status = Convert.ToString(await eventState.ExecuteScalarAsync(cancellationToken));
            if (status is "2" or "3" or "resulted" or "post game")
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }
        await UpsertFeedRunnersAsync(connection, transaction, racingEvent, feedRunners, cancellationToken);
        await StoreModelRunnerSourcesAsync(connection, transaction, racingEvent, feedRunners, cancellationToken);
        // Bet365 often publishes the full card before it publishes a book. Use
        // an even temporary book solely to construct every configured market;
        // the persisted/published card below is then forced to 1.00 suspended.
        var calculationRunners = createSuspendedCard
            ? feedRunners.Select(runner => runner with
                { PriceText = "1.00", Price = 6d, HasPrice = true, PricingPrice = 6d }).ToList()
            : feedRunners;
        var normalizedFeedRunners = _calculator.NormalizeFeedWinnerBook(calculationRunners);
        await UpsertRaceWinnerAsync(connection, transaction, racingEvent, normalizedFeedRunners, cancellationToken);
        var runners = await LoadRaceWinnerAsync(connection, transaction, racingEvent.EventId, cancellationToken);
        var configuration = await LoadConfigurationAsync(connection, transaction, racingEvent.EventId, cancellationToken);
        var markets = _calculator.Build(racingEvent, runners, configuration, indexPoints)
            .Select(market => market.MarketTypeId is 1010 or 1011 ? market with { Display = false } : market)
            .ToList();
        if (racingEvent.SportId == 4 && (configuration.MultiTrapsEnabled || configuration.TripleTrapsEnabled))
        {
            var multiLine = markets.FirstOrDefault(x => x.MarketTypeId == RacingMarketCalculator.MultiTrapsMarketType)
                ?.Selections.FirstOrDefault()?.Line;
            var tripleLine = markets.FirstOrDefault(x => x.MarketTypeId == RacingMarketCalculator.TripleTrapsMarketType)
                ?.Selections.FirstOrDefault()?.Line;
            await using var lockLines = new MySqlCommand("""
                UPDATE web_greyhound_line_market_config
                SET multi_traps_line=COALESCE(multi_traps_line,@multiLine),
                    triple_traps_line=COALESCE(triple_traps_line,@tripleLine),
                    updated_at_utc=UTC_TIMESTAMP(6)
                WHERE event_id=@eventId;
                """, connection, transaction);
            lockLines.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            lockLines.Parameters.AddWithValue("@multiLine", multiLine is double ml ? ml : DBNull.Value);
            lockLines.Parameters.AddWithValue("@tripleLine", tripleLine is double tl ? tl : DBNull.Value);
            await lockLines.ExecuteNonQueryAsync(cancellationToken);
        }
        markets = ApplySpreadexSelectionSuspensions(markets, spreadexSuspendedCompetitorIds,
            spreadexSuspendedRunnerNames);
        if (createSuspendedCard)
        {
            foreach (var selection in markets.Where(market => market.MarketTypeId != GreyhoundDistanceMarkets.MarketType).SelectMany(market => market.Selections))
            {
                selection.PriceBack = 1d;
                selection.PriceBackLast = 1d;
                selection.PriceLay = 1d;
                selection.Active = 0;
                selection.Display = 1;
            }
            markets = markets.Select(market => market.MarketTypeId == GreyhoundDistanceMarkets.MarketType ? market : market with
                { Active = false, Display = true }).ToList();
        }
        foreach (var market in markets)
            await UpsertMarketAsync(connection, transaction, racingEvent.EventId, market, cancellationToken);
        await ApplyStoredSelectionStateAsync(connection, transaction, racingEvent.EventId, markets, cancellationToken);
        if (createSuspendedCard)
        {
            // UpsertMarket normally preserves the former live quote in
            // price_back_last. A card that has never had a genuine quote must
            // expose 1.00 consistently in both current and previous fields.
            await using var suspendCard = new MySqlCommand("""
                UPDATE spuno_selections
                SET price_back=1,price_back_last=1,price_lay=1,active=0,display=1
                WHERE event_id=@eventId AND MOD(market_id,10000)<>9600;
                UPDATE spuno_markets SET active=0,display=1 WHERE event_id=@eventId AND MOD(market_id,10000)<>9600;
                """, connection, transaction);
            suspendCard.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await suspendCard.ExecuteNonQueryAsync(cancellationToken);
        }
        markets = ApplySpreadexSelectionSuspensions(markets, spreadexSuspendedCompetitorIds,
            spreadexSuspendedRunnerNames);
        await using (var processed = new MySqlCommand("""
            UPDATE em_events_raw
            SET spuno_odds_updated_at_unix=GREATEST(COALESCE(odds_updated_at_unix,0),COALESCE(updated_at_unix,0),UNIX_TIMESTAMP()),
                spuno_odds_updated_at_utc=UTC_TIMESTAMP()
            WHERE betsapi_event_id=@eventId;
            """, connection, transaction))
        {
            processed.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await processed.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new GeneratedEventSnapshot(racingEvent, markets);
    }

    private static List<GeneratedMarket> ApplySpreadexSelectionSuspensions(
        IReadOnlyList<GeneratedMarket> markets,
        IReadOnlySet<long>? suspendedCompetitorIds,
        IReadOnlySet<string>? suspendedRunnerNames)
    {
        if ((suspendedCompetitorIds is null || suspendedCompetitorIds.Count == 0) &&
            (suspendedRunnerNames is null || suspendedRunnerNames.Count == 0))
            return markets.ToList();

        return markets.Select(market =>
        {
            var coupledMarket = market.Name.StartsWith("Match Betting:", StringComparison.OrdinalIgnoreCase) ||
                                market.Name.StartsWith("Forecast:", StringComparison.OrdinalIgnoreCase);
            var suspendedMember = market.Selections.Any(selection =>
                selection.CompetitorId is long competitorId &&
                suspendedCompetitorIds?.Contains(competitorId) == true);
            var suspendedExcludedRunner = market.Name.StartsWith("Betting Without ", StringComparison.OrdinalIgnoreCase) &&
                suspendedRunnerNames?.Any(name =>
                    BetfairMarketMatcher.NormalizeRunner(market.Name).Contains(name, StringComparison.Ordinal)) == true;
            var suspendWholeMarket = (coupledMarket && suspendedMember) || suspendedExcludedRunner;

            foreach (var selection in market.Selections)
            {
                if (suspendWholeMarket ||
                    (selection.CompetitorId is long competitorId &&
                     suspendedCompetitorIds?.Contains(competitorId) == true))
                    selection.Active = 0;
            }

            return market with
            {
                Active = market.Active && market.Selections.Any(selection => selection.Active != 0)
            };
        }).ToList();
    }

    private static async Task StoreModelRunnerSourcesAsync(MySqlConnection connection, MySqlTransaction transaction,
        PricingEvent racingEvent, IReadOnlyList<FeedRunner> runners, CancellationToken cancellationToken)
    {
        await using (var delete = new MySqlCommand(
                         "DELETE FROM web_race_winner_source_prices WHERE event_id=@eventId AND source_name LIKE 'MODEL_%';",
                         connection, transaction))
        {
            delete.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        const string sql = """
            INSERT INTO web_race_winner_source_prices
              (event_id,competitor_id,source_name,selection_name,identifier,price_text,decimal_price,has_price,
               source_updated_at_utc,observed_at_utc,raw_json)
            VALUES(@eventId,@competitorId,@source,@name,@identifier,@priceText,@price,@hasPrice,@updated,UTC_TIMESTAMP(6),@raw);
            """;
        foreach (var runner in runners.Where(r =>
                     r.CompetitorId > 0 && r.EffectivePrice > 1 &&
                     r.PricingSource is "SPREADEX" or "BETSAPI" or "BETFRED" or "BET365_DIRECT"))
        {
            var source = runner.PricingSource switch
            {
                "SPREADEX" => "MODEL_SDX",
                "BETAHOY" => "MODEL_AHOY",
                "BETSAPI" => "MODEL_API",
                "BETFRED" => "MODEL_BF",
                "BET365_DIRECT" => "MODEL_B365",
                _ => throw new InvalidOperationException("Unsupported racing model price source.")
            };
            await using var insert = new MySqlCommand(sql, connection, transaction);
            insert.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            insert.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
            insert.Parameters.AddWithValue("@source", source);
            insert.Parameters.AddWithValue("@name", runner.Name);
            insert.Parameters.AddWithValue("@identifier", runner.Identifier);
            insert.Parameters.AddWithValue("@priceText", runner.PriceText);
            insert.Parameters.AddWithValue("@price", runner.EffectivePrice);
            insert.Parameters.AddWithValue("@hasPrice", runner.HasPrice ? 1 : 0);
            insert.Parameters.AddWithValue("@updated", runner.SourceUpdatedAtUtc ?? DateTime.UtcNow);
            insert.Parameters.AddWithValue("@raw", runner.RawJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task StoreSpreadexPricesAsync(PricingEvent ev, IReadOnlyList<FeedRunner> runners,
        IReadOnlyList<SpreadexRunnerPrice> prices, CancellationToken cancellationToken)
    {
        if (prices.Count == 0) return;
        await using var connection = OpenConnection(); await connection.OpenAsync(cancellationToken);
        if (HasAmbiguousRunners(runners))
        {
            await DeleteSpreadexPricesAsync(connection, ev.EventId, cancellationToken);
            return;
        }
        const string sql = """
            INSERT INTO web_spreadex_runner_prices(event_id,competitor_id,runner_name,spread_buy,spread_sell,decimal_price,updated_at_utc,
              index_50_25_10_buy,index_50_25_10_sell,index_50_25_10_updated_at_utc,index_market_name,hundred_suspended,index_suspended)
            VALUES(@event,@competitor,@name,@buy,@sell,@decimal,@updated,@indexBuy,@indexSell,@indexUpdated,@indexMarket,@hundredSuspended,@indexSuspended)
            ON DUPLICATE KEY UPDATE runner_name=VALUES(runner_name),
              spread_buy=IF(VALUES(decimal_price)>1,VALUES(spread_buy),spread_buy),
              spread_sell=IF(VALUES(decimal_price)>1,VALUES(spread_sell),spread_sell),
              decimal_price=IF(VALUES(decimal_price)>1,VALUES(decimal_price),decimal_price),
              updated_at_utc=IF(VALUES(decimal_price)>1,VALUES(updated_at_utc),updated_at_utc),
              index_50_25_10_buy=VALUES(index_50_25_10_buy),
              index_50_25_10_sell=VALUES(index_50_25_10_sell),
              index_50_25_10_updated_at_utc=VALUES(index_50_25_10_updated_at_utc),index_market_name=VALUES(index_market_name),
              hundred_suspended=VALUES(hundred_suspended),index_suspended=VALUES(index_suspended);
            """;
        foreach (var price in prices)
        {
            var normalized = BetfairMarketMatcher.NormalizeRunner(price.Name);
            var runner = runners.FirstOrDefault(x => BetfairMarketMatcher.NormalizeRunner(x.Name) == normalized);
            if (runner is null) continue;
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@event", ev.EventId); command.Parameters.AddWithValue("@competitor", runner.CompetitorId);
            command.Parameters.AddWithValue("@name", price.Name); command.Parameters.AddWithValue("@buy", price.Buy);
            command.Parameters.AddWithValue("@sell", price.Sell); command.Parameters.AddWithValue("@decimal", price.DecimalPrice);
            command.Parameters.AddWithValue("@updated", price.UpdatedAtUtc);
            command.Parameters.AddWithValue("@indexBuy", price.Index502510Buy is null ? DBNull.Value : price.Index502510Buy);
            command.Parameters.AddWithValue("@indexSell", price.Index502510Sell is null ? DBNull.Value : price.Index502510Sell);
            command.Parameters.AddWithValue("@indexUpdated", price.Index502510UpdatedAtUtc is null ? DBNull.Value : price.Index502510UpdatedAtUtc);
            command.Parameters.AddWithValue("@indexMarket", price.IndexMarketName is null ? DBNull.Value : price.IndexMarketName);
            command.Parameters.AddWithValue("@hundredSuspended", price.HundredSuspended ? 1 : 0);
            command.Parameters.AddWithValue("@indexSuspended", price.IndexSuspended ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await StoreSpreadexHundredHistoryAsync(connection, ev.EventId, runner.CompetitorId, price, cancellationToken);
        }
        // The immediate Spreadex persistence loop owns incoherent-index cleanup.
        // Running the same UPDATE here contends with that loop and can prevent
        // an otherwise valid BetsAPI/Spreadex race from reaching calculation.
    }

    public async Task StoreSpreadexPricesImmediateAsync(string meeting, long startTimeUnix,
        IReadOnlyList<SpreadexRunnerPrice> prices, CancellationToken cancellationToken)
    {
        if (prices.Count == 0) return;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string runnerSql = """
            SELECT e.event_id,s.competitor_id,s.name
            FROM spuno_events e
            JOIN spuno_selections s ON s.event_id=e.event_id
            WHERE e.sport_id=2 AND LOWER(TRIM(e.meeting))=LOWER(TRIM(@meeting))
              AND ABS(e.start_time_unix-@start)<=180
              AND e.status NOT IN ('2','3') AND e.active=1
              AND s.market_name='Race Winner' AND s.competitor_id IS NOT NULL
            ORDER BY ABS(e.start_time_unix-@start),e.event_id;
            """;
        var matches = new List<(long EventId, long CompetitorId, string Name)>();
        await using (var command = new MySqlCommand(runnerSql, connection))
        {
            command.Parameters.AddWithValue("@meeting", meeting);
            command.Parameters.AddWithValue("@start", startTimeUnix);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                matches.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }
        if (matches.Count == 0) return;
        var eventId = matches[0].EventId;
        // Historical resends can leave identical Race Winner selection rows.
        // They are harmless and must not cause every provider quote for the
        // race to be deleted. Reject only genuine ID/name conflicts.
        var eventMatches = matches
            .Where(x => x.EventId == eventId)
            .GroupBy(x => (x.CompetitorId, Name: BetfairMarketMatcher.NormalizeRunner(x.Name)))
            .Select(x => x.First())
            .ToList();
        if (eventMatches.GroupBy(x => x.CompetitorId)
                .Any(x => x.Select(y => BetfairMarketMatcher.NormalizeRunner(y.Name))
                    .Distinct().Count() > 1) ||
            eventMatches.GroupBy(x => BetfairMarketMatcher.NormalizeRunner(x.Name))
                .Any(x => x.Key.Length > 0 && x.Select(y => y.CompetitorId).Distinct().Count() > 1))
        {
            await DeleteSpreadexPricesAsync(connection, eventId, cancellationToken);
            return;
        }
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < startTimeUnix)
        {
            var currentCompetitors = prices.Select(price =>
                    eventMatches.FirstOrDefault(x => BetfairMarketMatcher.NormalizeRunner(x.Name) == BetfairMarketMatcher.NormalizeRunner(price.Name)).CompetitorId)
                .Where(x => x > 0).Distinct().ToList();
            var keepParameters = string.Join(',', currentCompetitors.Select((_, index) => $"@keep{index}"));
            var cleanupSql = currentCompetitors.Count == 0
                ? "DELETE FROM web_spreadex_runner_prices WHERE event_id=@event;"
                : $"DELETE FROM web_spreadex_runner_prices WHERE event_id=@event AND competitor_id NOT IN ({keepParameters});";
            await using var cleanup = new MySqlCommand(cleanupSql, connection);
            cleanup.Parameters.AddWithValue("@event", eventId);
            for (var index = 0; index < currentCompetitors.Count; index++)
                cleanup.Parameters.AddWithValue($"@keep{index}", currentCompetitors[index]);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        const string upsertSql = """
            INSERT INTO web_spreadex_runner_prices(event_id,competitor_id,runner_name,spread_buy,spread_sell,decimal_price,updated_at_utc,
              index_50_25_10_buy,index_50_25_10_sell,index_50_25_10_updated_at_utc,index_market_name,hundred_suspended,index_suspended)
            VALUES(@event,@competitor,@name,@buy,@sell,@decimal,@updated,@indexBuy,@indexSell,@indexUpdated,@indexMarket,@hundredSuspended,@indexSuspended)
            ON DUPLICATE KEY UPDATE runner_name=VALUES(runner_name),
              spread_buy=IF(VALUES(decimal_price)>1,VALUES(spread_buy),spread_buy),
              spread_sell=IF(VALUES(decimal_price)>1,VALUES(spread_sell),spread_sell),
              decimal_price=IF(VALUES(decimal_price)>1,VALUES(decimal_price),decimal_price),
              updated_at_utc=IF(VALUES(decimal_price)>1,VALUES(updated_at_utc),updated_at_utc),
              index_50_25_10_buy=VALUES(index_50_25_10_buy),
              index_50_25_10_sell=VALUES(index_50_25_10_sell),
              index_50_25_10_updated_at_utc=VALUES(index_50_25_10_updated_at_utc),index_market_name=VALUES(index_market_name),
              hundred_suspended=VALUES(hundred_suspended),index_suspended=VALUES(index_suspended);
            """;
        foreach (var price in prices)
        {
            var key = BetfairMarketMatcher.NormalizeRunner(price.Name);
            var runner = eventMatches.FirstOrDefault(x => BetfairMarketMatcher.NormalizeRunner(x.Name) == key);
            if (runner.CompetitorId <= 0) continue;
            if (price.DecimalPrice <= 1 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < startTimeUnix)
            {
                await using var clear = new MySqlCommand(
                    "DELETE FROM web_spreadex_runner_prices WHERE event_id=@event AND competitor_id=@competitor;", connection);
                clear.Parameters.AddWithValue("@event", eventId);
                clear.Parameters.AddWithValue("@competitor", runner.CompetitorId);
                await clear.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }
            await using var command = new MySqlCommand(upsertSql, connection);
            command.Parameters.AddWithValue("@event", eventId); command.Parameters.AddWithValue("@competitor", runner.CompetitorId);
            command.Parameters.AddWithValue("@name", price.Name); command.Parameters.AddWithValue("@buy", price.Buy);
            command.Parameters.AddWithValue("@sell", price.Sell); command.Parameters.AddWithValue("@decimal", price.DecimalPrice);
            command.Parameters.AddWithValue("@updated", price.UpdatedAtUtc);
            command.Parameters.AddWithValue("@indexBuy", price.Index502510Buy is null ? DBNull.Value : price.Index502510Buy);
            command.Parameters.AddWithValue("@indexSell", price.Index502510Sell is null ? DBNull.Value : price.Index502510Sell);
            command.Parameters.AddWithValue("@indexUpdated", price.Index502510UpdatedAtUtc is null ? DBNull.Value : price.Index502510UpdatedAtUtc);
            command.Parameters.AddWithValue("@indexMarket", price.IndexMarketName is null ? DBNull.Value : price.IndexMarketName);
            command.Parameters.AddWithValue("@hundredSuspended", price.HundredSuspended ? 1 : 0);
            command.Parameters.AddWithValue("@indexSuspended", price.IndexSuspended ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await StoreSpreadexHundredHistoryAsync(connection, eventId, runner.CompetitorId, price, cancellationToken);
        }
        await ClearIncoherentSpreadexIndexesAsync(connection, eventId, cancellationToken);
    }

    private static async Task ClearIncoherentSpreadexIndexesAsync(MySqlConnection connection, long eventId,
        CancellationToken cancellationToken)
    {
        // The winner contribution to this finishing-order index is 50 points.
        // A quote below 50 / 100-win decimal is a reset/placeholder, not a price.
        const string sql = """
            UPDATE web_spreadex_runner_prices index_price
            JOIN web_race_winner_source_prices model_price
              ON model_price.event_id=index_price.event_id
             AND model_price.competitor_id=index_price.competitor_id
             AND model_price.source_name='MODEL_SDX'
             AND model_price.decimal_price>1
            SET index_price.index_50_25_10_buy=NULL,
                index_price.index_50_25_10_sell=NULL,
                index_price.index_50_25_10_updated_at_utc=NULL,
                index_price.index_market_name=NULL
            WHERE index_price.event_id=@event
              AND index_price.index_50_25_10_buy IS NOT NULL
              AND index_price.index_50_25_10_sell IS NOT NULL
              AND GREATEST(index_price.index_50_25_10_buy,index_price.index_50_25_10_sell)
                  < 50/model_price.decimal_price;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.CommandTimeout = 2;
        command.Parameters.AddWithValue("@event", eventId);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.Number is 1205 or 1213 or 1317 ||
                                        ex.Message.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase))
        {
            // This is defensive cleanup only. The next provider delta retries it;
            // never hold up live price persistence on a contended cleanup row.
        }
    }

    private static async Task StoreSpreadexHundredHistoryAsync(MySqlConnection connection, long eventId,
        long competitorId, SpreadexRunnerPrice price, CancellationToken cancellationToken)
    {
        if (price.Buy <= 0 || price.Sell <= 0 || price.UpdatedAtUtc == default) return;
        var bid = Math.Min(price.Buy, price.Sell);
        var offer = Math.Max(price.Buy, price.Sell);
        const string sql = """
            INSERT IGNORE INTO web_spreadex_100_index_history(
              event_id,competitor_id,runner_name,bid,offer,midpoint,price_status,quoted_at_utc,recorded_at_utc)
            VALUES(@event,@competitor,@name,@bid,@offer,@midpoint,@status,@quoted,UTC_TIMESTAMP(6));
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@event", eventId);
        command.Parameters.AddWithValue("@competitor", competitorId);
        command.Parameters.AddWithValue("@name", price.Name);
        command.Parameters.AddWithValue("@bid", bid);
        command.Parameters.AddWithValue("@offer", offer);
        command.Parameters.AddWithValue("@midpoint", (bid + offer) / 2d);
        command.Parameters.AddWithValue("@status", price.HundredSuspended ? 210003 : 210002);
        command.Parameters.AddWithValue("@quoted", price.UpdatedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SpreadexHundredIndexHistory>> GetSpreadexHundredHistoryAsync(
        long eventId, long? competitorId, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id,competitor_id,runner_name,bid,offer,midpoint,price_status,quoted_at_utc,recorded_at_utc
            FROM web_spreadex_100_index_history
            WHERE event_id=@event AND (@competitor IS NULL OR competitor_id=@competitor)
            ORDER BY quoted_at_utc DESC LIMIT @limit;
            """;
        var output = new List<SpreadexHundredIndexHistory>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@event", eventId);
        command.Parameters.AddWithValue("@competitor", competitorId is null ? DBNull.Value : competitorId);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) output.Add(new(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4),
            reader.GetDouble(5), reader.GetInt32(6), DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
        return output;
    }

    private static bool HasAmbiguousRunners(IReadOnlyList<FeedRunner> runners) =>
        runners.Where(x => x.CompetitorId > 0).GroupBy(x => x.CompetitorId).Any(x => x.Count() > 1) ||
        runners.GroupBy(x => BetfairMarketMatcher.NormalizeRunner(x.Name)).Any(x => x.Key.Length > 0 && x.Count() > 1);

    private static async Task DeleteSpreadexPricesAsync(MySqlConnection connection, long eventId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("DELETE FROM web_spreadex_runner_prices WHERE event_id=@eventId;", connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<GeneratedEventSnapshot?> SuspendRaceAsync(PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        await EnsureLineMarketSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var lockTimeout = await SessionLockWaitTimeout.ApplyAsync(connection, 2, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Claim the event lifecycle row before rebuilding markets. This makes
        // suspension win against any repricing transaction that starts later.
        await using (var suspendEvent = new MySqlCommand("""
            UPDATE spuno_events
            SET status='2', active=0, display=0, in_progress=0
            WHERE event_id=@eventId;
            """, connection, transaction))
        {
            suspendEvent.Parameters.AddWithValue("@eventId", racingEvent.EventId);
            await suspendEvent.ExecuteNonQueryAsync(cancellationToken);
        }
        var runners = await LoadRaceWinnerAsync(connection, transaction, racingEvent.EventId, cancellationToken);
        if (runners.Count == 0)
        {
            // Commit the event-level close before returning. `await using` otherwise rolls
            // back the very UPDATE this method claimed two statements ago - the opposite of
            // what the comment above it promises - silently, leaving markets and selections
            // live under a closed event.
            await transaction.CommitAsync(cancellationToken);
            _logger.LogWarning(
                "Suspended event {EventId} ({Meeting} {Name}) had no loaded Race Winner runners; the event was closed but no child payload could be built.",
                racingEvent.EventId, racingEvent.Meeting, racingEvent.Name);
            return null;
        }
        var configuration = await LoadConfigurationAsync(connection, transaction, racingEvent.EventId, cancellationToken);
        var built = _calculator.Build(racingEvent, runners, configuration);
        // Carry stored per-market Rule 4 onto the rebuilt selections BEFORE the market is
        // closed. This is the last payload Sportuno receives for the race, and without it
        // every derived market published rule4time null / rule4deduction 0 while the Race
        // Winner market in the same payload carried the real deduction. It must run before
        // the Active/Display zeroing below, because it also restores stored Active/Display
        // and would otherwise re-open the suspended market.
        await ApplyStoredSelectionStateAsync(connection, transaction, racingEvent.EventId, built, cancellationToken);
        var markets = built.Select(market =>
        {
            foreach (var selection in market.Selections)
            {
                selection.Active = 0;
                selection.Display = 0;
            }
            return market with { Active = false, Display = false };
        }).ToList();
        foreach (var market in markets)
            await UpsertMarketAsync(connection, transaction, racingEvent.EventId, market, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GeneratedEventSnapshot(racingEvent with { Status = "2" }, markets);
    }

    public async Task<IReadOnlyList<FeedRunner>> ApplyRecentStoredSpreadexAsync(
        PricingEvent ev, IReadOnlyList<FeedRunner> runners, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT competitor_id,decimal_price,updated_at_utc
            FROM web_spreadex_runner_prices
            WHERE event_id=@eventId AND decimal_price>1 AND hundred_suspended=0
              AND updated_at_utc>=UTC_TIMESTAMP(6)-INTERVAL 20 MINUTE;
            """;
        var prices = new Dictionary<long, (double Price, DateTime Updated)>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", ev.EventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            prices[reader.GetInt64(0)] = (reader.GetDouble(1), reader.GetDateTime(2));
        if (prices.Count == 0) return runners;
        return runners.Select(runner =>
        {
            if (runner.PricingSource == "SPREADEX" || !prices.TryGetValue(runner.CompetitorId, out var stored))
                return runner;
            return runner with
            {
                PricingPrice = stored.Price,
                PricingSource = "SPREADEX",
                SourceUpdatedAtUtc = DateTime.SpecifyKind(stored.Updated, DateTimeKind.Utc)
            };
        }).ToList();
    }

    public async Task<IReadOnlyList<FeedRunner>> ApplyCompleteBetfredShowBookAsync(
        PricingEvent ev, IReadOnlyList<FeedRunner> runners, CancellationToken cancellationToken)
    {
        if (ev.SportId != 4 || runners.Count == 0) return runners;
        const string sql = """
            SELECT competitor_id,decimal_price,source_updated_at_utc
            FROM web_race_winner_source_prices
            WHERE event_id=@eventId AND source_name='BETFRED' AND has_price=1
              AND decimal_price>1
              AND source_updated_at_utc>=UTC_TIMESTAMP(6)-INTERVAL 2 MINUTE;
            """;
        var prices = new Dictionary<long, (double Price, DateTime Updated)>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", ev.EventId);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                prices[reader.GetInt64(0)] = (reader.GetDouble(1), reader.GetDateTime(2));

        // A withdrawal may already be authoritative in our stored Race Winner
        // state while an older BetsAPI snapshot still carries a price. Exclude
        // that runner from Betfred completeness or one withdrawn trap prevents
        // a complete live show from ever taking over the remaining runners.
        var storedNonRunners = new HashSet<long>();
        await using (var storedState = new MySqlCommand("""
            SELECT DISTINCT competitor_id
            FROM spuno_selections
            WHERE event_id=@eventId AND market_name='Race Winner'
              AND competitor_id IS NOT NULL
              AND (active=0 OR scratch<>0 OR result_text IN ('-1','99'));
            """, connection))
        {
            storedState.Parameters.AddWithValue("@eventId", ev.EventId);
            await using var reader = await storedState.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                storedNonRunners.Add(reader.GetInt64(0));
        }

        bool IsPricedRunner(FeedRunner runner) =>
            !runner.ExplicitNonRunner &&
            !storedNonRunners.Contains(runner.CompetitorId) &&
            !runner.Name.Contains("vacant", StringComparison.OrdinalIgnoreCase);
        var active = runners.Where(IsPricedRunner).ToList();
        // A partial Betfred show must never create a mixed-source book. Continue
        // with the complete BetsAPI snapshot until every active trap is mapped.
        if (active.Count < 2 || active.Any(runner => !prices.ContainsKey(runner.CompetitorId)))
            return runners;

        return runners.Select(runner =>
        {
            if (!IsPricedRunner(runner) || !prices.TryGetValue(runner.CompetitorId, out var quote))
                return runner;
            return runner with
            {
                Price = quote.Price,
                PricingPrice = quote.Price,
                PricingSource = "BETFRED",
                HasPrice = true,
                SourceUpdatedAtUtc = DateTime.SpecifyKind(quote.Updated, DateTimeKind.Utc)
            };
        }).ToList();
    }

    public async Task<long?> GetBetfredEventIdAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT raw_json
            FROM web_race_winner_source_prices
            WHERE event_id=@eventId AND source_name='BETFRED'
              AND raw_json IS NOT NULL AND raw_json<>''
            ORDER BY source_updated_at_utc DESC
            LIMIT 1;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        var raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = Regex.Match(raw, "\\\"eventId\\\"\\s*:\\s*(?<id>\\d+)", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups["id"].Value, out var providerId) ? providerId : null;
    }

    public async Task<bool> StoreBetfredEventLinkAsync(
        long eventId, long betfredEventId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO web_race_winner_source_prices
              (event_id,competitor_id,source_name,selection_name,identifier,price_text,decimal_price,has_price,
               source_updated_at_utc,observed_at_utc,raw_json)
            SELECT s.event_id,s.competitor_id,'BETFRED',s.name,s.identifier,NULL,NULL,0,
                   UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),@raw
            FROM spuno_selections s
            WHERE s.event_id=@eventId AND s.market_name='Race Winner'
            ORDER BY s.selection_id
            LIMIT 1
            ON DUPLICATE KEY UPDATE raw_json=VALUES(raw_json),observed_at_utc=VALUES(observed_at_utc);
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@raw", $"{{\"eventId\":{betfredEventId}}}");
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> IsEventSuspendedAsync(long eventId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT COALESCE(NULLIF(status,''),'0') IN ('2','3','resulted','post game')
            FROM spuno_events WHERE event_id=@eventId LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<PricingEvent> SuspendEventShellAsync(PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE spuno_events SET status='2',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
            UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId;
            UPDATE spuno_selections SET active=0,display=0 WHERE event_id=@eventId;
            """, connection);
        command.Parameters.AddWithValue("@eventId", racingEvent.EventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return racingEvent with { Status = "2" };
    }

    public async Task<PricingEvent?> TrySuspendEventShellAsync(
        PricingEvent racingEvent, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var claim = new MySqlCommand("""
            UPDATE spuno_events
            SET status='2',active=0,display=0,in_progress=0
            WHERE event_id=@eventId
              AND COALESCE(NULLIF(status,''),'0') NOT IN ('2','3','resulted','post game');
            """, connection, transaction);
        claim.Parameters.AddWithValue("@eventId", racingEvent.EventId);
        if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return racingEvent with { Status = "2" };
    }

    private static async Task UpsertFeedRunnersAsync(MySqlConnection connection, MySqlTransaction transaction,
        PricingEvent ev, IReadOnlyList<FeedRunner> runners, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO em_b365_selections_raw (
              event_id,fixture_id,selection_id,selection_name,pn,price_text,has_price,is_non_runner,non_runner_reason,
              jockey,trainer,form_text,age,weight_text,si,sn_flag,su_flag,fl_flag,source_path,raw_token_json,scraped_utc,updated_utc
            ) VALUES (@eventId,@eventId,@competitorId,@name,@pn,@price,@hasPrice,@nr,@reason,
              NULL,NULL,NULL,NULL,NULL,@silk,NULL,NULL,NULL,'web-feed',@raw,UTC_TIMESTAMP(),UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE price_text=VALUES(price_text),has_price=VALUES(has_price),
              is_non_runner=VALUES(is_non_runner),non_runner_reason=VALUES(non_runner_reason),si=VALUES(si),
              raw_token_json=VALUES(raw_token_json),updated_utc=UTC_TIMESTAMP();
            """;
        foreach (var runner in runners)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@eventId", ev.EventId);
            command.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
            command.Parameters.AddWithValue("@name", runner.Name);
            command.Parameters.AddWithValue("@pn", runner.Identifier);
            command.Parameters.AddWithValue("@price", runner.PriceText);
            command.Parameters.AddWithValue("@hasPrice", runner.HasPrice ? 1 : 0);
            command.Parameters.AddWithValue("@nr", runner.ExplicitNonRunner ? 1 : 0);
            command.Parameters.AddWithValue("@reason", runner.ExplicitNonRunner ? "Feed price status NR" : DBNull.Value);
            command.Parameters.AddWithValue("@silk", string.IsNullOrWhiteSpace(runner.SilkId) ? DBNull.Value : runner.SilkId);
            command.Parameters.AddWithValue("@raw", runner.RawJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertRaceWinnerAsync(MySqlConnection connection, MySqlTransaction transaction,
        PricingEvent ev, IReadOnlyList<FeedRunner> runners, CancellationToken cancellationToken)
    {
        var marketId = ev.EventId * 10000L + RacingMarketCalculator.RaceWinnerSuffix;
        await UpsertMarketShellAsync(connection, transaction, ev.EventId, marketId, "Race Winner", 10, true, true, cancellationToken);
        // A fresh snapshot is authoritative. Retire rows no longer present before
        // reactivating the current priced runners; otherwise stale duplicates can
        // prevent Spreadex from driving a complete book.
        await using (var retirePreviousSnapshot = new MySqlCommand("""
            UPDATE spuno_selections
            SET active=0,display=0
            WHERE event_id=@eventId AND market_name='Race Winner' AND scratch=0;
            """, connection, transaction))
        {
            retirePreviousSnapshot.Parameters.AddWithValue("@eventId", ev.EventId);
            await retirePreviousSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        const string sql = """
            INSERT INTO spuno_selections (
              event_id,market_id,selection_id,competitor_id,market_name,name,identifier,
              price_back,price_back_last,price_lay,sel_index,line,active,display,scratch,
              rule4_time,rule4_deduction,asset_url,result_text
            ) VALUES (@eventId,@marketId,@selectionId,@competitorId,'Race Winner',@name,@identifier,
              @price,@price,@price,@identifier,0,@active,@display,@scratch,NULL,0,@asset,@result)
            ON DUPLICATE KEY UPDATE
              -- A selection ID is an immutable horse identity. Card/price refreshes
              -- may enrich its silk and prices, but must never rename/re-key it.
              price_back_last=spuno_selections.price_back,price_back=IF(spuno_selections.scratch=1,spuno_selections.price_back,VALUES(price_back)),
              price_lay=IF(spuno_selections.scratch=1,spuno_selections.price_lay,VALUES(price_lay)),
              active=IF(spuno_selections.scratch=1 OR VALUES(scratch)=1,0,VALUES(active)),
              -- Non-runners remain visible as suspended selections. A later
              -- card/price refresh must never hide an already scratched horse.
              display=IF(spuno_selections.scratch=1 OR VALUES(scratch)=1,1,VALUES(display)),
              scratch=GREATEST(spuno_selections.scratch,VALUES(scratch)),
              asset_url=CASE
                WHEN spuno_selections.asset_url LIKE 'https://images.jarlontrading.co.uk/%'
                  THEN spuno_selections.asset_url
                ELSE VALUES(asset_url)
              END,
              result_text=COALESCE(VALUES(result_text),spuno_selections.result_text);
            """;
        foreach (var runner in runners.Where(runner => !IsSyntheticRunner(runner, runners)))
        {
            var vacant = runner.Name.Trim().StartsWith("Vacant Trap", StringComparison.OrdinalIgnoreCase) ||
                         runner.Name.Trim().Equals("Vacant", StringComparison.OrdinalIgnoreCase);
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@eventId", ev.EventId);
            command.Parameters.AddWithValue("@marketId", marketId);
            command.Parameters.AddWithValue("@selectionId", marketId * 100L + runner.Identifier);
            command.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
            command.Parameters.AddWithValue("@name", runner.Name);
            command.Parameters.AddWithValue("@identifier", runner.Identifier.ToString("00"));
            command.Parameters.AddWithValue("@price", runner.HasPrice ? runner.EffectivePrice : 1000);
            command.Parameters.AddWithValue("@active", runner.HasPrice && !runner.ExplicitNonRunner ? 1 : 0);
            command.Parameters.AddWithValue("@display", vacant || runner.HasPrice && !runner.ExplicitNonRunner ? 1 : 0);
            command.Parameters.AddWithValue("@scratch", vacant ? 1 : 0);
            command.Parameters.AddWithValue("@result", vacant ? RacingResultCodes.Void : DBNull.Value);
            // Horse-racing silks are Betfred-only and are populated after the
            // image has been cached in R2. BetsAPI/Bet365 silk IDs are metadata only.
            command.Parameters.AddWithValue("@asset", ev.SportId == 2 ? DBNull.Value : AssetUrl(ev.SportId, runner));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var runner in runners.Where(runner => IsSyntheticRunner(runner, runners)))
        {
            await using var hideSynthetic = new MySqlCommand("""
                UPDATE spuno_selections SET active=0,display=0
                WHERE event_id=@eventId AND competitor_id=@competitorId;
                """, connection, transaction);
            hideSynthetic.Parameters.AddWithValue("@eventId", ev.EventId);
            hideSynthetic.Parameters.AddWithValue("@competitorId", runner.CompetitorId);
            await hideSynthetic.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var hideSyntheticFavourites = new MySqlCommand("""
            UPDATE spuno_selections
            SET active=0,display=0
            WHERE event_id=@eventId AND market_name='Race Winner'
              AND LOWER(name) LIKE '%favourite%';
            """, connection, transaction);
        hideSyntheticFavourites.Parameters.AddWithValue("@eventId", ev.EventId);
        await hideSyntheticFavourites.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsSyntheticRunner(FeedRunner runner, IReadOnlyList<FeedRunner> runners)
    {
        if (runner.Name.Contains("Favourite", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = runner.Name.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        var names = runners.Where(other => !ReferenceEquals(other, runner))
            .Select(other => BetfairMarketMatcher.NormalizeRunner(other.Name))
            .ToHashSet(StringComparer.Ordinal);
        return parts.All(part => names.Contains(BetfairMarketMatcher.NormalizeRunner(part)));
    }

    private static async Task<List<PricingRunner>> LoadRaceWinnerAsync(MySqlConnection connection, MySqlTransaction transaction,
        long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id,market_id,selection_id,competitor_id,name,identifier,price_back,price_back_last,
                   active,display,scratch,asset_url,result_text,market_name,rule4_time,rule4_deduction
            FROM spuno_selections WHERE event_id=@eventId AND market_name='Race Winner'
              AND (active<>0 OR display<>0 OR scratch<>0)
            ORDER BY CAST(NULLIF(identifier,'') AS UNSIGNED),name;
            """;
        var output = new List<PricingRunner>();
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new PricingRunner
            {
                EventId=reader.GetInt64(0),MarketId=reader.GetInt64(1),SelectionId=reader.GetInt64(2),
                CompetitorId=reader.IsDBNull(3)?null:reader.GetInt64(3),Name=reader.GetString(4),Identifier=reader.GetString(5),
                Price=reader.GetDouble(6),PreviousPrice=reader.GetDouble(7),Active=reader.GetBoolean(8),Display=reader.GetBoolean(9),
                Scratch=reader.GetBoolean(10),AssetUrl=reader.IsDBNull(11)?null:reader.GetString(11),Result=reader.IsDBNull(12)?null:reader.GetString(12),
                MarketName=reader.IsDBNull(13)?null:reader.GetString(13),
                Rule4Time=reader.IsDBNull(14)?null:reader.GetString(14),
                Rule4Deduction=reader.IsDBNull(15)?0:reader.GetDouble(15)
            });
        return output;
    }

    private static async Task<PricingConfiguration> LoadConfigurationAsync(MySqlConnection connection, MySqlTransaction transaction,
        long eventId, CancellationToken cancellationToken)
    {
        await using (var ensure = new MySqlCommand("""
            INSERT IGNORE INTO web_event_market_config(event_id,top3_enabled,betting_without_selection_id,
              insure2_enabled,insure3_enabled,insure4_enabled,updated_at_utc)
            VALUES(@eventId,1,NULL,1,1,1,UTC_TIMESTAMP(6));
            """, connection, transaction))
        { ensure.Parameters.AddWithValue("@eventId", eventId); await ensure.ExecuteNonQueryAsync(cancellationToken); }
        bool top3, insure2, insure3, insure4;
        await using (var command = new MySqlCommand("""
            SELECT top3_enabled,betting_without_selection_id,insure2_enabled,insure3_enabled,insure4_enabled
            FROM web_event_market_config WHERE event_id=@eventId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            top3=reader.GetBoolean(0);
            insure2=reader.GetBoolean(2);insure3=reader.GetBoolean(3);insure4=reader.GetBoolean(4);
        }
        var withoutMarkets = new List<PricingBettingWithout>();
        await using (var command = new MySqlCommand("""
            SELECT excluded_selection_id,market_suffix,display_order,enabled,voided
            FROM web_betting_without_markets
            WHERE event_id=@eventId AND (enabled=1 OR voided=1)
            ORDER BY display_order,id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken))
                withoutMarkets.Add(new(reader.GetInt64(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetBoolean(3),reader.GetBoolean(4)));
        }
        var pairs = new List<PricingMatchBet>();
        await using (var command = new MySqlCommand("""
            SELECT runner_a_selection_id,runner_b_selection_id,market_suffix,enabled,voided
            FROM web_match_bet_pairs WHERE event_id=@eventId ORDER BY display_order,id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) pairs.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt32(2),reader.GetBoolean(3),reader.GetBoolean(4)));
        }
        bool multiTraps=false, tripleTraps=false;
        double? multiTrapsLine=null, tripleTrapsLine=null;
        await using (var command = new MySqlCommand("""
            SELECT multi_traps_enabled,triple_traps_enabled,multi_traps_line,triple_traps_line
            FROM web_greyhound_line_market_config WHERE event_id=@eventId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                multiTraps=reader.GetBoolean(0);tripleTraps=reader.GetBoolean(1);
                multiTrapsLine=reader.IsDBNull(2)?null:reader.GetDouble(2);
                tripleTrapsLine=reader.IsDBNull(3)?null:reader.GetDouble(3);
            }
        }
        var forecasts = new List<PricingForecast>();
        await using (var command = new MySqlCommand("""
            SELECT runner_a_selection_id,runner_b_selection_id,market_suffix,enabled,voided
            FROM web_forecast_pairs WHERE event_id=@eventId ORDER BY display_order,id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken))
                forecasts.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt32(2),reader.GetBoolean(3),reader.GetBoolean(4)));
        }
        return new(top3,insure2,insure3,insure4,multiTraps,tripleTraps,multiTrapsLine,tripleTrapsLine,
            withoutMarkets,pairs,forecasts);
    }

    public async Task SetGreyhoundLineMarketsAsync(long eventId, bool multiTrapsEnabled,
        bool tripleTrapsEnabled, CancellationToken cancellationToken)
    {
        await EnsureLineMarketSchemaAsync(cancellationToken);
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            INSERT INTO web_greyhound_line_market_config
              (event_id,multi_traps_enabled,triple_traps_enabled,updated_at_utc)
            VALUES(@eventId,@multi,@triple,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE multi_traps_enabled=VALUES(multi_traps_enabled),
              triple_traps_enabled=VALUES(triple_traps_enabled),updated_at_utc=VALUES(updated_at_utc);
            """, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@multi", multiTrapsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("@triple", tripleTrapsEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureLineMarketSchemaAsync(CancellationToken cancellationToken)
    {
        if (_lineMarketSchemaReady) return;
        await _lineMarketSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_lineMarketSchemaReady) return;
            await using var connection = OpenConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                CREATE TABLE IF NOT EXISTS web_greyhound_line_market_config (
                    event_id BIGINT NOT NULL,
                    multi_traps_enabled TINYINT(1) NOT NULL DEFAULT 0,
                    triple_traps_enabled TINYINT(1) NOT NULL DEFAULT 0,
                    multi_traps_line DOUBLE NULL,
                    triple_traps_line DOUBLE NULL,
                    updated_at_utc DATETIME(6) NOT NULL,
                    PRIMARY KEY(event_id)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                """, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            foreach (var column in new[] { "multi_traps_line", "triple_traps_line" })
            {
                await using var exists = new MySqlCommand("""
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema=DATABASE() AND table_name='web_greyhound_line_market_config'
                      AND column_name=@column;
                    """, connection);
                exists.Parameters.AddWithValue("@column", column);
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    await using var alter = new MySqlCommand(
                        $"ALTER TABLE web_greyhound_line_market_config ADD COLUMN `{column}` DOUBLE NULL;", connection);
                    await alter.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            _lineMarketSchemaReady = true;
        }
        finally { _lineMarketSchemaLock.Release(); }
    }

    private static async Task ApplyStoredSelectionStateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        IReadOnlyList<GeneratedMarket> markets,
        CancellationToken cancellationToken)
    {
        var generated = markets.SelectMany(x => x.Selections)
            .GroupBy(x => (x.MarketId, x.SelectionId))
            .ToDictionary(group => group.Key, group => group.First());
        if (generated.Count == 0) return;
        await using var command = new MySqlCommand("""
            SELECT market_id,selection_id,active,display,scratch,rule4_time,rule4_deduction,result_text
            FROM spuno_selections
            WHERE event_id=@eventId
              AND (scratch<>0 OR rule4_time IS NOT NULL OR result_text IN ('-1','99'));
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!generated.TryGetValue((reader.GetInt64(0), reader.GetInt64(1)), out var selection)) continue;
            selection.Active = reader.GetBoolean(2) ? 1 : 0;
            selection.Display = reader.GetBoolean(3) ? 1 : 0;
            selection.Scratch = reader.GetBoolean(4) ? 1 : 0;
            var nonRunnerTime = reader.IsDBNull(5) ? null : reader.GetString(5);
            var nonRunnerPrice = reader.IsDBNull(6) ? 0d : reader.GetDouble(6);
            selection.Rule4Time = selection.Scratch != 0 ? nonRunnerTime : null;
            selection.Rule4Deduction = selection.Scratch != 0 ? nonRunnerPrice : 0d;
            selection.NonRunnerTime = selection.Scratch != 0 ? nonRunnerTime : null;
            selection.NonRunnerPrice = selection.Scratch != 0 && nonRunnerTime is not null ? nonRunnerPrice : null;
            if (!reader.IsDBNull(7)) selection.Result = reader.GetString(7);
        }
    }

    private static async Task UpsertMarketAsync(MySqlConnection connection, MySqlTransaction transaction, long eventId,
        GeneratedMarket market, CancellationToken cancellationToken)
    {
        await UpsertMarketShellAsync(connection,transaction,eventId,market.MarketId,market.Name,market.DisplayOrder,market.Active,market.Display,cancellationToken);
        if (market.Selections.Count == 0)
        {
            await using var disable = new MySqlCommand("""
                UPDATE spuno_selections SET active=0,display=0
                WHERE event_id=@eventId AND market_id=@marketId;
                """, connection, transaction);
            disable.Parameters.AddWithValue("@eventId", eventId);
            disable.Parameters.AddWithValue("@marketId", market.MarketId);
            await disable.ExecuteNonQueryAsync(cancellationToken);
            return;
        }
        // Selection IDs for runner markets are based on stable cloth numbers.
        // Retire positional IDs left by older builds (or runners absent from a
        // fresh card) so a late non-runner cannot leave duplicate live rows.
        var liveSelectionIds = market.Selections.Select(item => item.SelectionId).Distinct().ToList();
        var idParameters = string.Join(',', liveSelectionIds.Select((_, index) => $"@liveSelection{index}"));
        await using (var retire = new MySqlCommand($"""
            UPDATE spuno_selections SET active=0,display=0
            WHERE event_id=@eventId AND market_id=@marketId
              AND selection_id NOT IN ({idParameters});
            """, connection, transaction))
        {
            retire.Parameters.AddWithValue("@eventId", eventId);
            retire.Parameters.AddWithValue("@marketId", market.MarketId);
            for (var index = 0; index < liveSelectionIds.Count; index++)
                retire.Parameters.AddWithValue($"@liveSelection{index}", liveSelectionIds[index]);
            await retire.ExecuteNonQueryAsync(cancellationToken);
        }
        const string sql = """
            INSERT INTO spuno_selections(event_id,market_id,selection_id,competitor_id,market_name,name,identifier,
              price_back,price_back_last,price_lay,sel_index,line,active,display,scratch,rule4_time,rule4_deduction,asset_url,result_text)
            VALUES(@eventId,@marketId,@selectionId,@competitorId,@marketName,@name,@identifier,@price,@previous,@lay,@index,@line,
              @active,@display,@scratch,@rule4Time,@rule4Deduction,@asset,@result)
            ON DUPLICATE KEY UPDATE market_name=VALUES(market_name),
              -- Never let a changed runner order turn an existing selection into
              -- another horse. Identity changes require an explicit audited repair.
              price_back_last=spuno_selections.price_back,price_back=VALUES(price_back),price_lay=VALUES(price_lay),sel_index=VALUES(sel_index),
              line=VALUES(line),active=VALUES(active),display=VALUES(display),scratch=VALUES(scratch),
              rule4_time=COALESCE(spuno_selections.rule4_time,VALUES(rule4_time)),
              -- Rule 4 is per MARKET and is written exactly once, by the withdrawal
              -- path's direct UPDATE (corrected only by the explicit repair endpoints).
              -- A reprice must never change it. GREATEST() was a one-way ratchet: any
              -- generated value larger than the stored one overwrote this market's
              -- correct deduction, and an over-deduction could never be corrected
              -- afterwards. Keep an existing non-zero deduction untouched.
              rule4_deduction=IF(COALESCE(spuno_selections.rule4_deduction,0)>0,
                                 spuno_selections.rule4_deduction,VALUES(rule4_deduction)),
              asset_url=COALESCE(VALUES(asset_url),spuno_selections.asset_url),result_text=COALESCE(VALUES(result_text),spuno_selections.result_text);
            """;
        foreach (var selection in market.Selections)
        {
            await using var command = new MySqlCommand(sql,connection,transaction);
            command.Parameters.AddWithValue("@eventId",selection.EventId);command.Parameters.AddWithValue("@marketId",selection.MarketId);
            command.Parameters.AddWithValue("@selectionId",selection.SelectionId);command.Parameters.AddWithValue("@competitorId",selection.CompetitorId is null?DBNull.Value:selection.CompetitorId);
            command.Parameters.AddWithValue("@marketName",selection.MarketName);command.Parameters.AddWithValue("@name",selection.Name);
            command.Parameters.AddWithValue("@identifier",selection.Identifier);command.Parameters.AddWithValue("@price",selection.PriceBack);
            command.Parameters.AddWithValue("@previous",selection.PriceBackLast);command.Parameters.AddWithValue("@lay",selection.PriceLay);
            command.Parameters.AddWithValue("@index",selection.Index);command.Parameters.AddWithValue("@line",selection.Line);
            command.Parameters.AddWithValue("@active",selection.Active);command.Parameters.AddWithValue("@display",selection.Display);
            command.Parameters.AddWithValue("@scratch",selection.Scratch);command.Parameters.AddWithValue("@rule4Time",selection.Rule4Time is null?DBNull.Value:selection.Rule4Time);
            command.Parameters.AddWithValue("@rule4Deduction",selection.Rule4Deduction);command.Parameters.AddWithValue("@asset",selection.AssetUrl is null?DBNull.Value:selection.AssetUrl);
            command.Parameters.AddWithValue("@result",selection.Result is null?DBNull.Value:selection.Result);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertMarketShellAsync(MySqlConnection connection,MySqlTransaction transaction,long eventId,long marketId,
        string name,int order,bool active,bool display,CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            INSERT INTO spuno_markets(market_id,event_id,mkt_name,active,display,display_order,mkt_last_message,sel_last_message)
            VALUES(@marketId,@eventId,@name,@active,@display,@order,NULL,NULL)
            ON DUPLICATE KEY UPDATE event_id=VALUES(event_id),mkt_name=VALUES(mkt_name),active=VALUES(active),display=VALUES(display),display_order=VALUES(display_order);
            """,connection,transaction);
        command.Parameters.AddWithValue("@marketId",marketId);command.Parameters.AddWithValue("@eventId",eventId);command.Parameters.AddWithValue("@name",name);
        command.Parameters.AddWithValue("@active",active?1:0);command.Parameters.AddWithValue("@display",display?1:0);command.Parameters.AddWithValue("@order",order);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object AssetUrl(int sportId, FeedRunner runner)
    {
        if (sportId == 2) return DBNull.Value;
        return GreyhoundTrapAssetUrl(runner.Identifier) ?? (object)DBNull.Value;
    }

    internal static string? GreyhoundTrapAssetUrl(int trap)
    {
        var urls = new[] { "", "1-3067b3f3229dca7843723ce92053b6c62c7e04803e769db5948fb94d58b6c122.png", "2-3a15f572e9991ce470f9364b78f20abf15715ad50512ec5393c97b1cd028d901.png", "3-b4d21bc3be9165647fbb2f4a2fa401559ad8ef64817984554da124449ab0cda0.png", "4-7aaed777e5c3d264f7632eb410b39ced39a8ff842193100e6a93161de09df095.png", "5-2fb388181d75e9be41ff0d92f1122c3b099c495706efcf214574ed14610cc43b.png", "6-feec77ef81f73bc281714618e2871f76a39c643bce8785706accc41fa342b335.png" };
        return trap is >= 1 and <= 6
            ? "https://http2.gaming-assets.com/mobile/assets/icons/traps/" + urls[trap]
            : null;
    }
}

internal sealed record SpreadexHundredIndexHistory(long EventId, long CompetitorId, string RunnerName,
    double Bid, double Offer, double Midpoint, int PriceStatus, DateTime QuotedAtUtc, DateTime RecordedAtUtc);
