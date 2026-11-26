using System.Data;
using System.Globalization;
using MySqlConnector;
using RacingTrader.Web.Configuration;
using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

public sealed class RacingRepository
{
    private readonly string _connectionString;
    private readonly ILogger<RacingRepository> _logger;
    private readonly RacingMarketCalculator _marketCalculator = new();

    public RacingRepository(ExternalSettings settings, ILogger<RacingRepository> logger)
    {
        _logger = logger;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = settings.MySqlServer,
            UserID = settings.MySqlUser,
            Password = settings.MySqlPassword,
            Database = settings.RacingDatabase,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 8,
            DefaultCommandTimeout = 15,
            AllowUserVariables = true,
            // The browser loads meetings, the chronological rail, the selected
            // event and monitor panels concurrently. A pool of two made those
            // harmless reads queue behind one another and intermittently hit the
            // connection timeout, returning HTTP 500 even though MariaDB was
            // healthy. Keep enough local read connections for one page load.
            MaximumPoolSize = 12,
            ConnectionIdleTimeout = 30
        }.ConnectionString;
    }

    private MySqlConnection OpenConnection() => new(_connectionString);

    /// <summary>
    /// Appended to a greyhound's name when a reserve takes its trap, so the substitution is
    /// visible downstream. Must stay in BetfairMarketMatcher.CountrySuffixes so every
    /// provider name comparison normalises it away.
    /// </summary>
    internal const string ReserveNameSuffix = " (RES)";

    public async Task EnsureRuntimeFeatureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var ensureForecasts = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS web_forecast_pairs (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                runner_a_selection_id BIGINT NOT NULL,
                runner_b_selection_id BIGINT NOT NULL,
                market_suffix INT NOT NULL,
                display_order INT NOT NULL,
                enabled TINYINT(1) NOT NULL DEFAULT 1,
                voided TINYINT(1) NOT NULL DEFAULT 0,
                void_reason VARCHAR(240) NULL,
                voided_at_utc DATETIME(6) NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_forecast_pair_suffix (event_id, market_suffix),
                INDEX ix_forecast_pair_event (event_id, enabled, display_order)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """, connection);
        await ensureForecasts.ExecuteNonQueryAsync(cancellationToken);

        await using var ensureEventLifecycle = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS web_event_lifecycle_publish (
                event_id BIGINT NOT NULL,
                creation_claimed_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (event_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            INSERT IGNORE INTO web_event_lifecycle_publish(event_id,creation_claimed_at_utc)
            SELECT event_id,UTC_TIMESTAMP(6)
            FROM spuno_events;
            """, connection);
        await ensureEventLifecycle.ExecuteNonQueryAsync(cancellationToken);

        await using var ensureTrapChallengeEvents = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS web_trap_challenge_events (
                meeting_key VARCHAR(160) NOT NULL,
                meeting_date DATE NOT NULL,
                meeting_name VARCHAR(160) NOT NULL,
                event_id BIGINT NOT NULL,
                first_start_time_unix BIGINT NOT NULL,
                created_at_utc DATETIME(6) NOT NULL,
                last_seen_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (meeting_key, meeting_date),
                UNIQUE KEY ux_trap_challenge_event_id (event_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """, connection);
        await ensureTrapChallengeEvents.ExecuteNonQueryAsync(cancellationToken);

        await using var normalizeDynamicOrders = new MySqlCommand("""
            UPDATE web_betting_without_markets
            SET display_order=50+market_suffix-9200
            WHERE market_suffix BETWEEN 9200 AND 9219
              AND display_order<>50+market_suffix-9200;
            UPDATE web_match_bet_pairs
            SET display_order=70+market_suffix-9100
            WHERE market_suffix BETWEEN 9100 AND 9119
              AND display_order<>70+market_suffix-9100;
            UPDATE web_forecast_pairs
            SET display_order=90+market_suffix-9300
            WHERE market_suffix BETWEEN 9300 AND 9319
              AND display_order<>90+market_suffix-9300;
            """, connection);
        await normalizeDynamicOrders.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<long> ResolveTrapChallengeEventIdAsync(
        string meeting, DateTime meetingDate, long candidateEventId, long firstStartTimeUnix,
        CancellationToken cancellationToken)
    {
        var meetingKey = string.Join(' ', meeting.Trim().ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS web_trap_challenge_events (
                meeting_key VARCHAR(160) NOT NULL,
                meeting_date DATE NOT NULL,
                meeting_name VARCHAR(160) NOT NULL,
                event_id BIGINT NOT NULL,
                first_start_time_unix BIGINT NOT NULL,
                created_at_utc DATETIME(6) NOT NULL,
                last_seen_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (meeting_key, meeting_date),
                UNIQUE KEY ux_trap_challenge_event_id (event_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            INSERT INTO web_trap_challenge_events
                (meeting_key,meeting_date,meeting_name,event_id,first_start_time_unix,created_at_utc,last_seen_at_utc)
            VALUES
                (@meetingKey,@meetingDate,@meeting,@candidate,@firstStart,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                meeting_name=VALUES(meeting_name),
                first_start_time_unix=LEAST(first_start_time_unix,VALUES(first_start_time_unix)),
                last_seen_at_utc=UTC_TIMESTAMP(6);
            SELECT event_id
            FROM web_trap_challenge_events
            WHERE meeting_key=@meetingKey AND meeting_date=@meetingDate;
            """, connection);
        command.Parameters.AddWithValue("@meetingKey", meetingKey);
        command.Parameters.AddWithValue("@meetingDate", meetingDate.Date);
        command.Parameters.AddWithValue("@meeting", meeting.Trim());
        command.Parameters.AddWithValue("@candidate", candidateEventId);
        command.Parameters.AddWithValue("@firstStart", firstStartTimeUnix);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS web_meeting_subscriptions (
                sport_id INT NOT NULL,
                meeting VARCHAR(160) NOT NULL,
                subscribed TINYINT(1) NOT NULL DEFAULT 0,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (sport_id, meeting)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_competition_subscriptions (
                sport_id INT NOT NULL,
                competition_key VARCHAR(160) NOT NULL,
                display_name VARCHAR(160) NOT NULL,
                subscribed TINYINT(1) NOT NULL DEFAULT 0,
                subscription_source VARCHAR(16) NOT NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (sport_id, competition_key)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_activity_log (
                id BIGINT NOT NULL AUTO_INCREMENT,
                activity_type VARCHAR(32) NOT NULL,
                sport_id INT NULL,
                event_id BIGINT NULL,
                meeting VARCHAR(160) NULL,
                event_name VARCHAR(200) NULL,
                message VARCHAR(500) NOT NULL,
                created_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                INDEX ix_activity_created (created_at_utc),
                INDEX ix_activity_event (event_id, id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_runner_actions (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                selection_id BIGINT NOT NULL,
                competitor_id BIGINT NULL,
                sport_id INT NOT NULL,
                action_type VARCHAR(24) NOT NULL,
                old_name VARCHAR(200) NULL,
                new_name VARCHAR(200) NULL,
                reason VARCHAR(240) NULL,
                price_at_action DOUBLE NULL,
                rule4_deduction DOUBLE NULL,
                action_time_utc DATETIME(6) NOT NULL,
                action_source VARCHAR(24) NOT NULL,
                PRIMARY KEY (id),
                INDEX ix_runner_action (event_id, selection_id, action_type),
                INDEX ix_runner_time (event_id, action_time_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_runner_state (
                event_id BIGINT NOT NULL,
                selection_id BIGINT NOT NULL,
                is_withdrawn TINYINT(1) NOT NULL,
                last_seen_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (event_id, selection_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_non_runner_candidates (
                event_id BIGINT NOT NULL,
                selection_id BIGINT NOT NULL,
                reason VARCHAR(160) NOT NULL,
                first_seen_utc DATETIME(6) NOT NULL,
                last_seen_utc DATETIME(6) NOT NULL,
                observations INT NOT NULL DEFAULT 1,
                PRIMARY KEY (event_id, selection_id),
                INDEX ix_candidate_seen (last_seen_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_price_history (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                market_id BIGINT NOT NULL,
                selection_id BIGINT NOT NULL,
                price DOUBLE NOT NULL,
                recorded_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                INDEX ix_price_history_runner (event_id, selection_id, recorded_at_utc),
                INDEX ix_price_history_cleanup (recorded_at_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_race_winner_source_snapshots (
                event_id BIGINT NOT NULL,
                source_name VARCHAR(16) NOT NULL,
                source_updated_at_utc DATETIME(6) NOT NULL,
                observed_at_utc DATETIME(6) NOT NULL,
                runner_count INT NOT NULL,
                priced_runner_count INT NOT NULL,
                is_selected TINYINT(1) NOT NULL DEFAULT 0,
                PRIMARY KEY (event_id, source_name),
                INDEX ix_source_snapshot_selected (event_id, is_selected)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_spreadex_runner_prices (
                event_id BIGINT NOT NULL,
                competitor_id BIGINT NOT NULL,
                runner_name VARCHAR(200) NOT NULL,
                spread_buy DOUBLE NOT NULL,
                spread_sell DOUBLE NOT NULL,
                decimal_price DOUBLE NOT NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                index_50_25_10_buy DOUBLE NULL,
                index_50_25_10_sell DOUBLE NULL,
                index_50_25_10_updated_at_utc DATETIME(6) NULL,
                index_market_name VARCHAR(40) NULL,
                hundred_suspended TINYINT(1) NOT NULL DEFAULT 0,
                index_suspended TINYINT(1) NOT NULL DEFAULT 0,
                PRIMARY KEY (event_id, competitor_id),
                INDEX ix_spreadex_updated (updated_at_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_spreadex_100_index_history (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                competitor_id BIGINT NOT NULL,
                runner_name VARCHAR(200) NOT NULL,
                bid DOUBLE NOT NULL,
                offer DOUBLE NOT NULL,
                midpoint DOUBLE NOT NULL,
                price_status INT NOT NULL DEFAULT 0,
                quoted_at_utc DATETIME(6) NOT NULL,
                recorded_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_spreadex_100_quote (event_id,competitor_id,quoted_at_utc),
                INDEX ix_spreadex_100_runner (event_id,competitor_id,quoted_at_utc),
                INDEX ix_spreadex_100_recorded (recorded_at_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_race_winner_source_prices (
                event_id BIGINT NOT NULL,
                competitor_id BIGINT NOT NULL,
                source_name VARCHAR(16) NOT NULL,
                selection_name VARCHAR(200) NOT NULL,
                identifier INT NOT NULL,
                price_text VARCHAR(32) NULL,
                decimal_price DOUBLE NULL,
                has_price TINYINT(1) NOT NULL,
                source_updated_at_utc DATETIME(6) NOT NULL,
                observed_at_utc DATETIME(6) NOT NULL,
                raw_json LONGTEXT NULL,
                PRIMARY KEY (event_id, competitor_id, source_name),
                INDEX ix_source_price_runner (event_id, identifier, source_name)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_event_market_config (
                event_id BIGINT NOT NULL,
                top3_enabled TINYINT(1) NOT NULL DEFAULT 1,
                betting_without_selection_id BIGINT NULL,
                insure2_enabled TINYINT(1) NOT NULL DEFAULT 1,
                insure3_enabled TINYINT(1) NOT NULL DEFAULT 1,
                insure4_enabled TINYINT(1) NOT NULL DEFAULT 1,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (event_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_betting_without_markets (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                excluded_selection_id BIGINT NOT NULL,
                market_suffix INT NOT NULL,
                display_order INT NOT NULL,
                enabled TINYINT(1) NOT NULL DEFAULT 1,
                voided TINYINT(1) NOT NULL DEFAULT 0,
                void_reason VARCHAR(240) NULL,
                voided_at_utc DATETIME(6) NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_without_runner (event_id, excluded_selection_id),
                UNIQUE KEY ux_without_suffix (event_id, market_suffix),
                INDEX ix_without_event (event_id, enabled, display_order)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_match_bet_pairs (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                runner_a_selection_id BIGINT NOT NULL,
                runner_b_selection_id BIGINT NOT NULL,
                market_suffix INT NOT NULL,
                display_order INT NOT NULL,
                enabled TINYINT(1) NOT NULL DEFAULT 1,
                voided TINYINT(1) NOT NULL DEFAULT 0,
                void_reason VARCHAR(240) NULL,
                voided_at_utc DATETIME(6) NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_match_pair_suffix (event_id, market_suffix),
                INDEX ix_match_pair_event (event_id, enabled, display_order)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_forecast_pairs (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                runner_a_selection_id BIGINT NOT NULL,
                runner_b_selection_id BIGINT NOT NULL,
                market_suffix INT NOT NULL,
                display_order INT NOT NULL,
                enabled TINYINT(1) NOT NULL DEFAULT 1,
                voided TINYINT(1) NOT NULL DEFAULT 0,
                void_reason VARCHAR(240) NULL,
                voided_at_utc DATETIME(6) NULL,
                updated_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_forecast_pair_suffix (event_id, market_suffix),
                INDEX ix_forecast_pair_event (event_id, enabled, display_order)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_market_adjustments (
                id BIGINT NOT NULL AUTO_INCREMENT,
                event_id BIGINT NOT NULL,
                market_id BIGINT NOT NULL,
                market_name VARCHAR(200) NOT NULL,
                withdrawn_selection_id BIGINT NOT NULL,
                runner_name VARCHAR(200) NOT NULL,
                adjustment_type VARCHAR(24) NOT NULL,
                price_at_withdrawal DOUBLE NOT NULL,
                deduction DOUBLE NOT NULL,
                applied_at_utc DATETIME(6) NOT NULL,
                action_source VARCHAR(24) NOT NULL,
                PRIMARY KEY (id),
                UNIQUE KEY ux_market_adjustment (event_id, market_id, withdrawn_selection_id, adjustment_type),
                INDEX ix_market_adjustment_event (event_id, market_id, applied_at_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS web_runner_results (
                event_id BIGINT NOT NULL,
                selection_id BIGINT NOT NULL,
                competitor_id BIGINT NULL,
                identifier VARCHAR(40) NULL,
                runner_name VARCHAR(200) NOT NULL,
                finishing_position INT NULL,
                result_status VARCHAR(32) NOT NULL,
                source_url VARCHAR(1000) NULL,
                resulted_at_utc DATETIME(6) NOT NULL,
                PRIMARY KEY (event_id, selection_id),
                INDEX ix_runner_results_position (event_id, finishing_position)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            """;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        foreach (var migration in new[]
        {
            (Name: "index_50_25_10_buy", Type: "DOUBLE NULL"),
            (Name: "index_50_25_10_sell", Type: "DOUBLE NULL"),
            (Name: "index_50_25_10_updated_at_utc", Type: "DATETIME(6) NULL"),
            (Name: "index_market_name", Type: "VARCHAR(40) NULL"),
            (Name: "hundred_suspended", Type: "TINYINT(1) NOT NULL DEFAULT 0"),
            (Name: "index_suspended", Type: "TINYINT(1) NOT NULL DEFAULT 0")
        })
        {
            await using var exists = new MySqlCommand("""
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema=DATABASE() AND table_name='web_spreadex_runner_prices' AND column_name=@column;
                """, connection);
            exists.Parameters.AddWithValue("@column", migration.Name);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0) continue;
            await using var alter = new MySqlCommand(
                $"ALTER TABLE web_spreadex_runner_prices ADD COLUMN `{migration.Name}` {migration.Type};", connection);
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        // A previous build briefly stored the 50-25-10 index as a 100 Win index.
        // Remove those event snapshots so the UI/derivation falls back to Bet365
        // until a genuine Spreadex 100 Win panel has been captured.
        await using (var cleanMisclassifiedSpreadex = new MySqlCommand("""
            UPDATE web_spreadex_runner_prices p
            JOIN (
              SELECT event_id
              FROM web_spreadex_runner_prices
              WHERE index_50_25_10_buy IS NOT NULL
                AND ABS(spread_buy - index_50_25_10_buy) < 0.0001
              GROUP BY event_id
              HAVING COUNT(*) >= 2
            ) bad ON bad.event_id = p.event_id
            SET p.spread_buy = 0, p.spread_sell = 0, p.decimal_price = 0;
            """, connection))
            await cleanMisclassifiedSpreadex.ExecuteNonQueryAsync(cancellationToken);

        await using (var cleanIncoherentSpreadexIndexes = new MySqlCommand("""
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
            WHERE index_price.index_50_25_10_buy IS NOT NULL
              AND index_price.index_50_25_10_sell IS NOT NULL
              AND GREATEST(index_price.index_50_25_10_buy,index_price.index_50_25_10_sell)
                  < 50/model_price.decimal_price;
            """, connection))
            await cleanIncoherentSpreadexIndexes.ExecuteNonQueryAsync(cancellationToken);

        await using (var migrateBettingWithout = new MySqlCommand("""
            INSERT IGNORE INTO web_betting_without_markets
              (event_id, excluded_selection_id, market_suffix, display_order, enabled, voided, updated_at_utc)
            SELECT event_id, betting_without_selection_id, 9200, 50, 1, 0, updated_at_utc
            FROM web_event_market_config
            WHERE betting_without_selection_id IS NOT NULL;
            DELETE FROM spuno_selections WHERE MOD(market_id, 10000) = 9005;
            DELETE FROM spuno_markets WHERE MOD(market_id, 10000) = 9005;
            UPDATE web_betting_without_markets
            SET display_order=50+market_suffix-9200
            WHERE market_suffix BETWEEN 9200 AND 9219;
            UPDATE web_match_bet_pairs
            SET display_order=70+market_suffix-9100
            WHERE market_suffix BETWEEN 9100 AND 9119;
            UPDATE spuno_markets
            SET display_order=50+MOD(market_id,10000)-9200
            WHERE MOD(market_id,10000) BETWEEN 9200 AND 9219;
            UPDATE spuno_markets
            SET display_order=70+MOD(market_id,10000)-9100
            WHERE MOD(market_id,10000) BETWEEN 9100 AND 9119;
            UPDATE web_forecast_pairs
            SET display_order=90+market_suffix-9300
            WHERE market_suffix BETWEEN 9300 AND 9319;
            UPDATE spuno_markets
            SET display_order=90+MOD(market_id,10000)-9300
            WHERE MOD(market_id,10000) BETWEEN 9300 AND 9319;
            UPDATE em_events_raw raw_event
            JOIN spuno_events racing_event ON racing_event.event_id=raw_event.betsapi_event_id
            SET raw_event.spuno_odds_updated_at_unix=0,
                raw_event.spuno_odds_updated_at_utc=NULL
            WHERE racing_event.send_to_sp<>0
              AND racing_event.sport_id IN (2,4)
              AND racing_event.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 24 HOUR)
              AND NOT EXISTS (
                  SELECT 1 FROM spuno_markets generated_market
                  WHERE generated_market.event_id=racing_event.event_id
                    AND MOD(generated_market.market_id,10000) IN (9009,9010)
              );
            """, connection))
        {
            migrateBettingWithout.CommandTimeout = 120;
            await migrateBettingWithout.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var migrateSubscriptions = new MySqlCommand("""
            INSERT INTO web_competition_subscriptions
              (sport_id, competition_key, display_name, subscribed, subscription_source, updated_at_utc)
            SELECT sport_id, competition_key, MAX(meeting), MAX(subscribed), 'LEGACY', MAX(updated_at_utc)
            FROM (
              SELECT sport_id, meeting, subscribed, updated_at_utc,
                     LOWER(TRIM(CASE
                       WHEN UPPER(TRIM(meeting)) LIKE '% EVE' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 4)
                       WHEN UPPER(TRIM(meeting)) LIKE '% AM' OR UPPER(TRIM(meeting)) LIKE '% PM' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 3)
                       ELSE TRIM(meeting) END)) AS competition_key
              FROM web_meeting_subscriptions
            ) legacy_subscriptions
            GROUP BY sport_id, competition_key
            ON DUPLICATE KEY UPDATE
              subscribed = GREATEST(web_competition_subscriptions.subscribed, VALUES(subscribed)),
              updated_at_utc = GREATEST(web_competition_subscriptions.updated_at_utc, VALUES(updated_at_utc));
            """, connection))
            await migrateSubscriptions.ExecuteNonQueryAsync(cancellationToken);

        await using (var seedTradedCompetitions = new MySqlCommand("""
            INSERT IGNORE INTO web_competition_subscriptions
              (sport_id, competition_key, display_name, subscribed, subscription_source, updated_at_utc)
            SELECT sport_id, competition_key, MAX(meeting), 1, 'AUTO_HISTORY', UTC_TIMESTAMP(6)
            FROM (
              SELECT sport_id, meeting,
                     LOWER(TRIM(CASE
                       WHEN UPPER(TRIM(meeting)) LIKE '% EVE' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 4)
                       WHEN UPPER(TRIM(meeting)) LIKE '% AM' OR UPPER(TRIM(meeting)) LIKE '% PM' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 3)
                       ELSE TRIM(meeting) END)) AS competition_key
              FROM spuno_events
              WHERE sport_id IN (2,4) AND send_to_sp <> 0 AND COALESCE(meeting, '') <> ''
            ) traded_competitions
            GROUP BY sport_id, competition_key;
            """, connection))
            await seedTradedCompetitions.ExecuteNonQueryAsync(cancellationToken);

        const string columnExistsSql = """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND table_name = 'spuno_markets'
              AND column_name = 'display_order';
            """;
        await using (var columnCheck = new MySqlCommand(columnExistsSql, connection))
        {
            var exists = Convert.ToInt32(await columnCheck.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
                await using var addColumn = new MySqlCommand(
                    "ALTER TABLE spuno_markets ADD COLUMN display_order INT NOT NULL DEFAULT 100 AFTER display;",
                    connection);
                await addColumn.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        const string backfillDisplayOrderSql = """
            UPDATE spuno_markets
            SET display_order = CASE MOD(market_id, 10000)
                WHEN 9001 THEN 10
                WHEN 9002 THEN 20
                WHEN 9004 THEN 30
                WHEN 9003 THEN 40
                WHEN 9005 THEN 50
                WHEN 9006 THEN 60
                WHEN 9007 THEN 70
                WHEN 9008 THEN 80
                WHEN 9100 THEN 100
                ELSE display_order
            END
            WHERE display_order = 100;
            """;
        await using var backfillDisplayOrder = new MySqlCommand(backfillDisplayOrderSql, connection);
        await backfillDisplayOrder.ExecuteNonQueryAsync(cancellationToken);

        await using (var backfillMatchDisplayOrder = new MySqlCommand("""
            UPDATE spuno_markets
            SET display_order = 100 + (MOD(market_id, 10000) - 9100)
            WHERE MOD(market_id, 10000) BETWEEN 9100 AND 9199
              AND display_order = 100;
            """, connection))
            await backfillMatchDisplayOrder.ExecuteNonQueryAsync(cancellationToken);

        await using var cleanup = new MySqlCommand("""
            DELETE FROM web_price_history WHERE recorded_at_utc < UTC_TIMESTAMP() - INTERVAL 7 DAY;
            DELETE FROM web_non_runner_candidates WHERE last_seen_utc < UTC_TIMESTAMP() - INTERVAL 1 DAY;
            DELETE FROM web_activity_log WHERE created_at_utc < UTC_TIMESTAMP() - INTERVAL 7 DAY;
            UPDATE spuno_selections
            SET result_text = '99'
            WHERE scratch <> 0 AND rule4_time IS NOT NULL
              AND (result_text IS NULL OR result_text = '-1');
            UPDATE spuno_selections selections
            INNER JOIN web_match_bet_pairs pair
              ON pair.event_id = selections.event_id
             AND pair.market_suffix = MOD(selections.market_id, 10000)
            SET selections.result_text = '99'
            WHERE pair.voided <> 0;
            UPDATE spuno_selections selections
            INNER JOIN web_betting_without_markets market
              ON market.event_id = selections.event_id
             AND market.market_suffix = MOD(selections.market_id, 10000)
            SET selections.result_text = '99'
            WHERE market.voided <> 0;
            DELETE selections
            FROM spuno_selections selections
            INNER JOIN web_match_bet_pairs pair
              ON pair.event_id = selections.event_id
             AND pair.market_suffix = MOD(selections.market_id, 10000)
            WHERE pair.enabled = 0;
            UPDATE spuno_markets market
            INNER JOIN web_match_bet_pairs pair
              ON pair.event_id = market.event_id
             AND pair.market_suffix = MOD(market.market_id, 10000)
            SET market.active = 0, market.display = 0
            WHERE pair.enabled = 0;
            UPDATE em_events_raw raw_event
            INNER JOIN (SELECT DISTINCT event_id FROM web_match_bet_pairs WHERE enabled = 0) removed
              ON removed.event_id = raw_event.betsapi_event_id
            SET raw_event.spuno_odds_updated_at_unix = 0,
                raw_event.spuno_odds_updated_at_utc = NULL;
            """, connection);
        try
        {
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException ex) when (ex.ErrorCode is MySqlErrorCode.CommandTimeoutExpired or MySqlErrorCode.QueryInterrupted)
        {
            // Retention and stale-state repair are maintenance only. A large
            // history table must never prevent the live feed host from starting.
            _logger.LogWarning(ex, "Startup database maintenance timed out; live services will start and maintenance can retry later.");
        }
    }

    public async Task<IReadOnlyList<MeetingSummary>> GetMeetingsAsync(int sportId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                e.sport_id,
                COALESCE(NULLIF(e.league_name, ''), 'Unassigned') AS meeting,
                COUNT(*) AS event_count,
                SUM(CASE WHEN COALESCE(se.send_to_sp, e.sendto_sp, 0) <> 0 THEN 1 ELSE 0 END) AS subscribed_count,
                COALESCE(cs.subscribed, 0) AS meeting_subscribed,
                MIN(e.starts_at_unix) AS first_start,
                MAX(e.starts_at_unix) AS last_start
            FROM em_events_raw e
            LEFT JOIN spuno_events se ON se.event_id = e.betsapi_event_id
            LEFT JOIN web_competition_subscriptions cs
              ON cs.sport_id = e.sport_id
             AND cs.competition_key = LOWER(TRIM(CASE
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
                   ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
            WHERE e.sport_id = @sportId
              AND e.starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
              AND e.starts_at_unix <= UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
            GROUP BY e.sport_id, COALESCE(NULLIF(e.league_name, ''), 'Unassigned'), cs.subscribed
            ORDER BY first_start, meeting;
            """;

        var output = new List<MeetingSummary>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sportId", sportId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var count = reader.GetInt32("event_count");
            var subscribedCount = reader.GetInt32("subscribed_count");
            output.Add(new MeetingSummary(
                reader.GetInt32("sport_id"),
                reader.GetString("meeting"),
                count,
                subscribedCount,
                reader.GetBoolean("meeting_subscribed") || (count > 0 && subscribedCount == count),
                reader.GetInt64("first_start"),
                reader.GetInt64("last_start")));
        }
        return output;
    }

    public async Task<IReadOnlyList<IndexMarketToHide>> SetAllIndexMarketsHiddenAsync(CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT m.event_id,m.market_id,m.mkt_name,
                   CASE MOD(m.market_id,10000) WHEN 9009 THEN 1010 ELSE 1011 END AS market_type_id,
                   m.display_order
            FROM spuno_markets m
            WHERE MOD(m.market_id,10000) IN (9009,9010);
            """;
        var markets = new List<IndexMarketToHide>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var select = new MySqlCommand(selectSql, connection))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) markets.Add(new(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        await using var update = new MySqlCommand(
            "UPDATE spuno_markets SET display=0 WHERE MOD(market_id,10000) IN (9009,9010);", connection);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return markets;
    }

    public async Task<IReadOnlyList<EventSummary>> GetMeetingEventsAsync(
        int sportId,
        string meeting,
        bool subscribedOnly,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                e.betsapi_event_id AS event_id,
                e.sport_id,
                COALESCE(NULLIF(e.league_name, ''), 'Unassigned') AS meeting,
                COALESCE(NULLIF(se.ev_name, ''), NULLIF(e.home_name, ''), NULLIF(e.away_name, ''),
                         CONCAT(COALESCE(NULLIF(e.league_name, ''), 'Race'), ' ',
                                DATE_FORMAT(FROM_UNIXTIME(e.starts_at_unix), '%H:%i'))) AS event_name,
                e.starts_at_unix,
                COALESCE(NULLIF(se.status, ''), CAST(e.time_status AS CHAR), '0') AS status,
                COALESCE(se.send_to_sp, e.sendto_sp, 0) AS subscribed,
                COALESCE(se.active, e.active, 0) AS active,
                COALESCE(se.display, e.display, 0) AS display,
                COALESCE(se.in_progress, 0) AS in_progress,
                (SELECT COUNT(*) FROM web_race_winner_source_prices api_price
                  WHERE api_price.event_id=e.betsapi_event_id
                    AND api_price.source_name='BETSAPI'
                    AND api_price.has_price=1 AND api_price.decimal_price>1
                    AND NOT EXISTS (
                        SELECT 1 FROM spuno_selections scratched
                        WHERE scratched.event_id=e.betsapi_event_id
                          AND scratched.competitor_id=api_price.competitor_id
                          AND scratched.market_name='Race Winner'
                          AND scratched.scratch=1)) AS betsapi_priced_runner_count
            FROM em_events_raw e
            LEFT JOIN spuno_events se ON se.event_id = e.betsapi_event_id
            WHERE e.sport_id = @sportId
              AND COALESCE(NULLIF(e.league_name, ''), 'Unassigned') = @meeting
              AND e.starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
              AND (@subscribedOnly = 0 OR COALESCE(se.send_to_sp, e.sendto_sp, 0) <> 0)
            ORDER BY e.starts_at_unix, event_name;
            """;

        var output = new List<EventSummary>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sportId", sportId);
        command.Parameters.AddWithValue("@meeting", meeting);
        command.Parameters.AddWithValue("@subscribedOnly", subscribedOnly ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventMeeting = reader.GetString("meeting");
            var eventStart = reader.GetInt64("starts_at_unix");
            output.Add(new EventSummary(
                reader.GetInt64("event_id"),
                reader.GetInt32("sport_id"),
                eventMeeting,
                RacingEventNames.Canonical(eventMeeting, eventStart),
                eventStart,
                reader.GetString("status"),
                reader.GetBoolean("subscribed"),
                reader.GetBoolean("active"),
                reader.GetBoolean("display"),
                reader.GetBoolean("in_progress"), reader.GetInt32("betsapi_priced_runner_count")));
        }
        return output;
    }

    public async Task SetMeetingSubscriptionAsync(
        int sportId,
        string meeting,
        bool subscribed,
        CancellationToken cancellationToken)
    {
        const string saveSubscription = """
            INSERT INTO web_meeting_subscriptions (sport_id, meeting, subscribed, updated_at_utc)
            VALUES (@sportId, @meeting, @subscribed, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
              subscribed = VALUES(subscribed),
              updated_at_utc = VALUES(updated_at_utc);
            """;
        const string saveCompetitionSubscription = """
            INSERT INTO web_competition_subscriptions
              (sport_id, competition_key, display_name, subscribed, subscription_source, updated_at_utc)
            VALUES (@sportId, @competitionKey, @meeting, @subscribed, 'MANUAL', UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
              display_name = VALUES(display_name), subscribed = VALUES(subscribed),
              subscription_source = 'MANUAL', updated_at_utc = VALUES(updated_at_utc);
            """;
        const string updateRaw = """
            UPDATE em_events_raw raw
            LEFT JOIN spuno_events staged ON staged.event_id=raw.betsapi_event_id
            SET raw.sendto_sp = @subscribed,
                raw.active = CASE
                    WHEN COALESCE(NULLIF(staged.status,''),CAST(raw.time_status AS CHAR),'0')
                         IN ('2','3','resulted','post game') THEN 0
                    ELSE @subscribed END,
                raw.display = CASE
                    WHEN COALESCE(NULLIF(staged.status,''),CAST(raw.time_status AS CHAR),'0')
                         IN ('2','3','resulted','post game') THEN 0
                    ELSE @subscribed END
            WHERE raw.sport_id = @sportId
              AND LOWER(TRIM(CASE
                    WHEN UPPER(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned'))) - 4)
                    WHEN UPPER(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned'))) - 3)
                    ELSE TRIM(COALESCE(NULLIF(raw.league_name, ''), 'Unassigned')) END)) = @competitionKey
              AND raw.starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR);
            """;
        const string subscribeEvents = """
            INSERT INTO spuno_events (
                event_id, sport_id, meeting, ev_name, start_time, start_time_unix,
                status, send_to_sp, active, display, in_progress
            )
            SELECT
                betsapi_event_id, sport_id,
                COALESCE(NULLIF(league_name, ''), 'Unassigned'),
                COALESCE(NULLIF(home_name, ''), NULLIF(away_name, ''),
                         CONCAT(COALESCE(NULLIF(league_name, ''), 'Race'), ' ',
                                DATE_FORMAT(FROM_UNIXTIME(starts_at_unix), '%H:%i'))),
                FROM_UNIXTIME(starts_at_unix), starts_at_unix,
                CAST(time_status AS CHAR), 1, 1, 1, 0
            FROM em_events_raw
            WHERE sport_id = @sportId
              AND LOWER(TRIM(CASE
                    WHEN UPPER(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned'))) - 4)
                    WHEN UPPER(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned'))) - 3)
                    ELSE TRIM(COALESCE(NULLIF(league_name, ''), 'Unassigned')) END)) = @competitionKey
              AND starts_at_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
            ON DUPLICATE KEY UPDATE
              meeting = VALUES(meeting), ev_name = VALUES(ev_name),
              start_time = VALUES(start_time), start_time_unix = VALUES(start_time_unix),
              status = CASE
                  WHEN COALESCE(NULLIF(spuno_events.status,''),'0') IN ('2','3','resulted','post game')
                  THEN spuno_events.status
                  WHEN VALUES(start_time_unix) >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                  THEN VALUES(status) ELSE spuno_events.status END,
              send_to_sp = 1,
              active = CASE
                  WHEN COALESCE(NULLIF(spuno_events.status,''),'0') IN ('2','3','resulted','post game')
                  THEN 0 ELSE VALUES(active) END,
              display = CASE
                  WHEN COALESCE(NULLIF(spuno_events.status,''),'0') IN ('2','3','resulted','post game')
                  THEN 0 ELSE VALUES(display) END;
            """;
        const string unsubscribeEvents = """
            UPDATE spuno_events
            SET send_to_sp = 0, active = 0, display = 0
            WHERE sport_id = @sportId
              AND LOWER(TRIM(CASE
                    WHEN UPPER(TRIM(meeting)) LIKE '% EVE' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 4)
                    WHEN UPPER(TRIM(meeting)) LIKE '% AM' OR UPPER(TRIM(meeting)) LIKE '% PM' THEN LEFT(TRIM(meeting), CHAR_LENGTH(TRIM(meeting)) - 3)
                    ELSE TRIM(meeting) END)) = @competitionKey
              AND start_time_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR);
            """;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sql in new[] { saveSubscription, saveCompetitionSubscription, updateRaw, subscribed ? subscribeEvents : unsubscribeEvents })
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@sportId", sportId);
            command.Parameters.AddWithValue("@meeting", meeting);
            command.Parameters.AddWithValue("@competitionKey", CompetitionKey(meeting));
            command.Parameters.AddWithValue("@subscribed", subscribed ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(int SportId, string Meeting)>> ReconcileMeetingSubscriptionsAsync(CancellationToken cancellationToken)
    {
        const string closeSubscriptionsSql = """
            UPDATE em_events_raw raw_event
            JOIN spuno_events closed_event ON closed_event.event_id=raw_event.betsapi_event_id
            SET raw_event.sendto_sp=0,raw_event.active=0,raw_event.display=0,
                closed_event.send_to_sp=0,closed_event.active=0,closed_event.display=0
            WHERE COALESCE(NULLIF(closed_event.status,''),'0') IN ('3','resulted','post game');
            """;
        const string clearUnsubscribedSql = """
            UPDATE em_events_raw e
            LEFT JOIN web_competition_subscriptions cs
              ON cs.sport_id=e.sport_id
             AND cs.competition_key=LOWER(TRIM(CASE
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
                   ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
            LEFT JOIN spuno_events se ON se.event_id=e.betsapi_event_id
            SET e.sendto_sp=0,e.active=0,e.display=0,
                se.send_to_sp=0,se.active=0,se.display=0
            WHERE e.sport_id IN (2,4)
              AND e.starts_at_unix BETWEEN UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                                       AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND COALESCE(cs.subscribed,0)=0;
            """;
        const string competitionKey = """
            LOWER(TRIM(CASE
              WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
              WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
              ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
            """;
        const string ukTradingWindow = """
            TIME(CONVERT_TZ(FROM_UNIXTIME(e.starts_at_unix), '+00:00', 'Europe/London')) >= '11:00:00'
            AND TIME(CONVERT_TZ(FROM_UNIXTIME(e.starts_at_unix), '+00:00', 'Europe/London')) <= '22:00:00'
            """;
        var findAutoSql = $$"""
            SELECT DISTINCT e.sport_id, COALESCE(NULLIF(e.league_name, ''), 'Unassigned') AS meeting
            FROM em_events_raw e
            JOIN web_competition_subscriptions cs
              ON cs.sport_id = e.sport_id AND cs.competition_key = {{competitionKey}}
            WHERE e.sport_id IN (2,4)
              AND e.starts_at_unix BETWEEN UNIX_TIMESTAMP(UTC_TIMESTAMP()) AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND {{ukTradingWindow}}
              AND cs.subscribed = 1
              AND cs.subscription_source = 'AUTO_HISTORY'
              AND COALESCE(e.sendto_sp, 0) = 0;
            """;
        var updateRawSql = $$"""
            UPDATE em_events_raw e
            JOIN web_competition_subscriptions cs
              ON cs.sport_id = e.sport_id
             AND cs.competition_key = LOWER(TRIM(CASE
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
                   ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
             AND cs.subscribed = 1
            SET e.sendto_sp = 1, e.active = 1, e.display = 1
            WHERE e.sport_id IN (2, 4)
              AND e.starts_at_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND {{ukTradingWindow}}
              AND COALESCE(e.sendto_sp, 0) = 0
              AND NOT EXISTS (
                  SELECT 1 FROM spuno_events closed_event
                  WHERE closed_event.event_id=e.betsapi_event_id
                    AND COALESCE(NULLIF(closed_event.status,''),'0') IN ('2','3','resulted','post game'));
            """;
        var updateExistingSql = $$"""
            UPDATE spuno_events se
            JOIN em_events_raw e ON e.betsapi_event_id = se.event_id
            JOIN web_competition_subscriptions cs
              ON cs.sport_id = e.sport_id
             AND cs.competition_key = LOWER(TRIM(CASE
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
                   ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
             AND cs.subscribed = 1
            SET se.send_to_sp = 1
            WHERE e.sport_id IN (2, 4)
              AND e.starts_at_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND {{ukTradingWindow}}
              AND COALESCE(se.send_to_sp, 0) = 0
              AND COALESCE(NULLIF(se.status,''),'0') NOT IN ('3','resulted','post game');
            """;
        var insertNewSql = $$"""
            INSERT IGNORE INTO spuno_events (
                event_id, sport_id, meeting, ev_name, start_time, start_time_unix,
                status, send_to_sp, active, display, in_progress
            )
            SELECT
                e.betsapi_event_id, e.sport_id,
                COALESCE(NULLIF(e.league_name, ''), 'Unassigned'),
                COALESCE(NULLIF(e.home_name, ''), NULLIF(e.away_name, ''),
                         CONCAT(COALESCE(NULLIF(e.league_name, ''), 'Race'), ' ',
                                DATE_FORMAT(FROM_UNIXTIME(e.starts_at_unix), '%H:%i'))),
                FROM_UNIXTIME(e.starts_at_unix), e.starts_at_unix,
                CAST(e.time_status AS CHAR), 1, 1, 1, 0
            FROM em_events_raw e
            JOIN web_competition_subscriptions cs
              ON cs.sport_id = e.sport_id
             AND cs.competition_key = LOWER(TRIM(CASE
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% EVE' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 4)
                   WHEN UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% AM' OR UPPER(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) LIKE '% PM' THEN LEFT(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')), CHAR_LENGTH(TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned'))) - 3)
                   ELSE TRIM(COALESCE(NULLIF(e.league_name, ''), 'Unassigned')) END))
             AND cs.subscribed = 1
            WHERE e.sport_id IN (2, 4)
              AND e.starts_at_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND {{ukTradingWindow}};
            """;

        var removeOutsideAutoWindowSql = $$"""
            UPDATE em_events_raw e
            JOIN web_competition_subscriptions cs
              ON cs.sport_id=e.sport_id AND cs.competition_key={{competitionKey}}
             AND cs.subscribed=1 AND cs.subscription_source='AUTO_HISTORY'
            LEFT JOIN spuno_events se ON se.event_id=e.betsapi_event_id
            SET e.sendto_sp=0,e.active=0,e.display=0,
                se.send_to_sp=0,se.active=0,se.display=0
            WHERE e.sport_id IN (2,4)
              AND e.starts_at_unix BETWEEN UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 48 HOUR)
                                       AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND COALESCE(NULLIF(se.status,''),'0') IN ('2','3','resulted','post game')
              AND (e.starts_at_unix < UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                   OR NOT ({{ukTradingWindow}}));
            """;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var autoSubscribed = new List<(int SportId, string Meeting)>();
        await using (var findAuto = new MySqlCommand(findAutoSql, connection, transaction))
        {
            await using var reader = await findAuto.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                autoSubscribed.Add((reader.GetInt32(0), reader.GetString(1)));
        }
        foreach (var sql in new[] { closeSubscriptionsSql, clearUnsubscribedSql, removeOutsideAutoWindowSql, updateRawSql, updateExistingSql, insertNewSql })
        {
            await using var reconcile = new MySqlCommand(sql, connection, transaction);
            await reconcile.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return autoSubscribed;
    }

    private static string CompetitionKey(string meeting)
    {
        var key = meeting.Trim();
        foreach (var suffix in new[] { " EVE", " AM", " PM" })
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return key[..^suffix.Length].Trim().ToLowerInvariant();
        return key.ToLowerInvariant();
    }

    public async Task<IReadOnlyList<ProviderPriceComparisonRow>> GetProviderPriceComparisonAsync(
        bool includeTomorrow,
        CancellationToken cancellationToken)
    {
        var ukZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var ukNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ukZone);
        var ukFrom = DateTime.SpecifyKind(ukNow.Date, DateTimeKind.Unspecified);
        var ukUntil = DateTime.SpecifyKind(ukNow.Date.AddDays(includeTomorrow ? 2 : 1), DateTimeKind.Unspecified);
        var fromUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(ukFrom, ukZone)).ToUnixTimeSeconds();
        var untilUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(ukUntil, ukZone)).ToUnixTimeSeconds();

        const string sql = """
            SELECT
                e.event_id,e.meeting,e.ev_name,e.start_time_unix,
                COALESCE(NULLIF(e.status,''),'0') AS event_status,
                s.selection_id,s.competitor_id,s.identifier,s.name,s.scratch,
                direct_price.decimal_price AS direct_price,
                direct_price.source_updated_at_utc AS direct_updated,
                direct_price.observed_at_utc AS direct_observed,
                api_price.decimal_price AS api_price,
                api_price.source_updated_at_utc AS api_updated,
                api_price.observed_at_utc AS api_observed,
                betfred_price.decimal_price AS betfred_price,
                betfred_price.source_updated_at_utc AS betfred_updated,
                betfred_price.observed_at_utc AS betfred_observed,
                spreadex.decimal_price AS spreadex_price,
                spreadex.spread_buy AS spreadex_buy,
                spreadex.spread_sell AS spreadex_sell,
                spreadex.updated_at_utc AS spreadex_updated
            FROM spuno_events e
            JOIN spuno_selections s
              ON s.event_id=e.event_id AND MOD(s.market_id,10000)=9001
            LEFT JOIN web_race_winner_source_prices direct_price
              ON direct_price.event_id=s.event_id
             AND direct_price.competitor_id=s.competitor_id
             AND direct_price.source_name IN ('DIRECT','BET365_DIRECT')
            LEFT JOIN web_race_winner_source_prices api_price
              ON api_price.event_id=s.event_id
             AND api_price.competitor_id=s.competitor_id
             AND api_price.source_name='BETSAPI'
            LEFT JOIN web_race_winner_source_prices betfred_price
              ON betfred_price.event_id=s.event_id
             AND betfred_price.competitor_id=s.competitor_id
             AND betfred_price.source_name='BETFRED'
            LEFT JOIN web_spreadex_runner_prices spreadex
              ON spreadex.event_id=s.event_id
             AND spreadex.competitor_id=s.competitor_id
            WHERE e.sport_id=4
              AND e.start_time_unix>=@fromUnix AND e.start_time_unix<@untilUnix
              AND LOWER(s.name) NOT LIKE '%favourite%'
              AND (e.send_to_sp<>0 OR direct_price.event_id IS NOT NULL
                   OR api_price.event_id IS NOT NULL
                   OR betfred_price.event_id IS NOT NULL
                   OR spreadex.event_id IS NOT NULL)
            ORDER BY e.start_time_unix,CAST(NULLIF(s.identifier,'') AS UNSIGNED),s.name;
            """;

        var output = new List<ProviderPriceComparisonRow>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@fromUnix", fromUnix);
        command.Parameters.AddWithValue("@untilUnix", untilUnix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            double? Number(string name) => reader.IsDBNull(name) ? null : reader.GetDouble(name);
            DateTime? Timestamp(string name) => reader.IsDBNull(name)
                ? null
                : DateTime.SpecifyKind(reader.GetDateTime(name), DateTimeKind.Utc);
            var directPrice = Number("direct_price");
            var apiPrice = Number("api_price");
            var directUpdated = Timestamp("direct_updated");
            var apiUpdated = Timestamp("api_updated");
            var difference = directPrice.HasValue && apiPrice.HasValue
                ? directPrice.Value - apiPrice.Value
                : (double?)null;
            var probabilityDifference = directPrice is > 1 && apiPrice is > 1
                ? (100d / directPrice.Value) - (100d / apiPrice.Value)
                : (double?)null;
            var updateLag = directUpdated.HasValue && apiUpdated.HasValue
                ? (directUpdated.Value - apiUpdated.Value).TotalSeconds
                : (double?)null;
            var latestUpdate = new[] { directUpdated, apiUpdated }
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty()
                .Max();

            output.Add(new ProviderPriceComparisonRow(
                reader.GetInt64("event_id"),
                reader.GetString("meeting"),
                reader.GetString("ev_name"),
                reader.GetInt64("start_time_unix"),
                reader.GetString("event_status"),
                reader.GetInt64("selection_id"),
                reader.IsDBNull("competitor_id") ? null : reader.GetInt64("competitor_id"),
                reader.IsDBNull("identifier") ? "" : reader.GetString("identifier"),
                reader.GetString("name"),
                !reader.IsDBNull("scratch") && reader.GetBoolean("scratch"),
                directPrice,
                directUpdated,
                Timestamp("direct_observed"),
                apiPrice,
                apiUpdated,
                Timestamp("api_observed"),
                Number("betfred_price"),
                Timestamp("betfred_updated"),
                Timestamp("betfred_observed"),
                Number("spreadex_price"),
                Number("spreadex_buy"),
                Number("spreadex_sell"),
                Timestamp("spreadex_updated"),
                difference,
                difference.HasValue ? Math.Abs(difference.Value) : null,
                probabilityDifference,
                updateLag,
                latestUpdate == default ? null : latestUpdate));
        }
        return output;
    }

    internal async Task<IReadOnlyList<TrapChallengeRaceInput>> GetMeetingTrapChallengeRacesAsync(
        string meeting,
        CancellationToken cancellationToken, long? anchorStartTimeUnix=null)
    {
        const string sql = """
            SELECT e.betsapi_event_id,e.starts_at_unix,
                   COALESCE(NULLIF(se.status,''),CAST(e.time_status AS CHAR),'0') AS event_status,
                   COALESCE(se.active,e.active,0) AS event_active,
                   COALESCE(se.in_progress,0) AS in_progress,
                   s.identifier,s.name,s.price_back,s.price_back_last,s.scratch,s.result_text,
                   rr.finishing_position,rr.result_status
            FROM em_events_raw e
            LEFT JOIN spuno_events se ON se.event_id=e.betsapi_event_id
            LEFT JOIN spuno_selections s
              ON s.event_id=e.betsapi_event_id
             AND (s.market_name='Race Winner' OR MOD(s.market_id,10000)=9001)
            LEFT JOIN web_runner_results rr
              ON rr.event_id=s.event_id AND rr.selection_id=s.selection_id
            WHERE e.sport_id=4
              AND COALESCE(NULLIF(e.league_name,''),'Unassigned')=@meeting
              AND e.starts_at_unix>=@from
              AND e.starts_at_unix<@to
            ORDER BY e.starts_at_unix,CAST(NULLIF(s.identifier,'') AS UNSIGNED),s.selection_id;
            """;
        var builders = new Dictionary<long, TrapChallengeRaceBuilder>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@meeting", meeting);
        var date=anchorStartTimeUnix is {} anchor?DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(anchor),MeetingCancellationPolicy.Zone).DateTime):(DateOnly?)null;
        var bounds=date is {} day?MeetingCancellationPolicy.Bounds(day):(DateTimeOffset.UtcNow.AddHours(-18).ToUnixTimeSeconds(),DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("@from",bounds.Item1);command.Parameters.AddWithValue("@to",bounds.Item2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var eventId = reader.GetInt64(0);
            if (!builders.TryGetValue(eventId, out var race))
            {
                race = new TrapChallengeRaceBuilder(
                    eventId, reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4));
                builders[eventId] = race;
            }
            if (reader.IsDBNull(5) || !TryTrap(reader.GetString(5), out var trap)) continue;
            race.Runners.Add(new TrapChallengeRunnerInput(
                trap,
                reader.IsDBNull(6) ? $"Trap {trap}" : reader.GetString(6),
                reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                !reader.IsDBNull(9) && reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return builders.Values.OrderBy(x => x.StartTimeUnix)
            .Select(x => new TrapChallengeRaceInput(
                x.EventId, x.StartTimeUnix, x.Status, x.Active, x.InProgress, x.Runners))
            .ToList();
    }

    private static bool TryTrap(string identifier, out int trap)
    {
        var digits = new string(identifier.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out trap) && trap is >= 1 and <= 6;
    }

    private sealed class TrapChallengeRaceBuilder(
        long eventId, long startTimeUnix, string status, bool active, bool inProgress)
    {
        public long EventId { get; } = eventId;
        public long StartTimeUnix { get; } = startTimeUnix;
        public string Status { get; } = status;
        public bool Active { get; } = active;
        public bool InProgress { get; } = inProgress;
        public List<TrapChallengeRunnerInput> Runners { get; } = [];
    }

    public async Task<IReadOnlyList<EventSummary>> GetChronologicalEventsAsync(
        int sportId,
        bool subscribedOnly,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                e.betsapi_event_id AS event_id,
                e.sport_id,
                COALESCE(NULLIF(e.league_name, ''), 'Unassigned') AS meeting,
                e.starts_at_unix,
                COALESCE(NULLIF(se.status, ''), CAST(e.time_status AS CHAR), '0') AS status,
                COALESCE(se.send_to_sp, e.sendto_sp, 0) AS subscribed,
                COALESCE(se.active, e.active, 0) AS active,
                COALESCE(se.display, e.display, 0) AS display,
                COALESCE(se.in_progress, 0) AS in_progress,
                (SELECT COUNT(*) FROM web_race_winner_source_prices api_price
                  WHERE api_price.event_id=e.betsapi_event_id
                    AND api_price.source_name='BETSAPI'
                    AND api_price.has_price=1 AND api_price.decimal_price>1
                    AND NOT EXISTS (
                        SELECT 1 FROM spuno_selections scratched
                        WHERE scratched.event_id=e.betsapi_event_id
                          AND scratched.competitor_id=api_price.competitor_id
                          AND scratched.market_name='Race Winner'
                          AND scratched.scratch=1)) AS betsapi_priced_runner_count
            FROM em_events_raw e
            LEFT JOIN spuno_events se ON se.event_id=e.betsapi_event_id
            WHERE e.sport_id=@sportId
              AND e.starts_at_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 4 HOUR)
              AND e.starts_at_unix<=UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 48 HOUR)
              AND (@subscribedOnly=0 OR COALESCE(se.send_to_sp,e.sendto_sp,0)<>0)
            ORDER BY e.starts_at_unix,e.betsapi_event_id;
            """;
        var output = new List<EventSummary>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@sportId", sportId);
        command.Parameters.AddWithValue("@subscribedOnly", subscribedOnly ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var meeting = reader.GetString("meeting");
            var start = reader.GetInt64("starts_at_unix");
            output.Add(new EventSummary(
                reader.GetInt64("event_id"), reader.GetInt32("sport_id"), meeting,
                RacingEventNames.Canonical(meeting, start), start, reader.GetString("status"),
                reader.GetBoolean("subscribed"), reader.GetBoolean("active"), reader.GetBoolean("display"),
                reader.GetBoolean("in_progress"),
                reader.GetInt32("betsapi_priced_runner_count")));
        }
        return output;
    }

    public async Task<IReadOnlyList<IndexArbRow>> GetIndexArbsAsync(CancellationToken cancellationToken)
    {
        var ukZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var ukNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ukZone);
        var ukFrom = DateTime.SpecifyKind(ukNow.Date, DateTimeKind.Unspecified);
        var ukUntil = DateTime.SpecifyKind(ukNow.Date.AddDays(1), DateTimeKind.Unspecified);
        var fromUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(ukFrom, ukZone)).ToUnixTimeSeconds();
        var untilUnix = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(ukUntil, ukZone)).ToUnixTimeSeconds();

        const string sql = """
            SELECT e.event_id,e.ev_name,e.start_time_unix,
                   s.market_name,s.competitor_id,s.identifier,s.name,s.asset_url,s.price_back,
                   spreadex.index_50_25_10_sell AS spreadex_bid,
                   spreadex.index_50_25_10_buy AS spreadex_offer,
                   spreadex.index_50_25_10_updated_at_utc AS spreadex_updated
            FROM spuno_events e
            JOIN spuno_selections s
              ON s.event_id=e.event_id
             AND s.market_name IN ('50-25-10 Index','50-30-20-10 Index')
            JOIN web_spreadex_runner_prices spreadex
              ON spreadex.event_id=s.event_id
             AND spreadex.competitor_id=s.competitor_id
             AND spreadex.index_market_name=s.market_name
            LEFT JOIN web_race_winner_source_prices model_source
              ON model_source.event_id=s.event_id
             AND model_source.competitor_id=s.competitor_id
             AND model_source.source_name='MODEL_SDX'
            WHERE e.sport_id=2 AND e.send_to_sp<>0
              AND e.start_time_unix>=@fromUnix AND e.start_time_unix<@untilUnix
              AND e.active=1
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('2','3','resulted','post game')
              AND s.active=1 AND s.display=1 AND s.scratch=0
              AND s.price_back>0
              AND spreadex.index_50_25_10_sell IS NOT NULL
              AND spreadex.index_50_25_10_buy IS NOT NULL
              AND (model_source.decimal_price IS NULL
                   OR GREATEST(spreadex.index_50_25_10_sell,spreadex.index_50_25_10_buy)
                      >= 50/model_source.decimal_price)
            ORDER BY e.start_time_unix,CAST(NULLIF(s.identifier,'') AS UNSIGNED),s.name;
            """;

        var output = new List<IndexArbRow>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@fromUnix", fromUnix);
        command.Parameters.AddWithValue("@untilUnix", untilUnix);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var (rtBid, rtOffer) = CentredIndexQuote(reader.GetDouble("price_back"));
            var spreadexFirst = reader.GetDouble("spreadex_bid");
            var spreadexSecond = reader.GetDouble("spreadex_offer");
            var spreadexBid = Math.Min(spreadexFirst, spreadexSecond);
            var spreadexOffer = Math.Max(spreadexFirst, spreadexSecond);
            var sellRtBuySpreadex = rtBid - spreadexOffer;
            var sellSpreadexBuyRt = spreadexBid - rtOffer;
            // Signed as Spreadex over RacingTrader: positive when the
            // executable Spreadex bid exceeds the RT offer, negative when
            // the executable RT bid exceeds the Spreadex offer.
            var arbPoints = sellSpreadexBuyRt > .0001
                ? sellSpreadexBuyRt
                : sellRtBuySpreadex > .0001
                    ? -sellRtBuySpreadex
                    : 0;
            if (Math.Abs(arbPoints) <= .0001) continue;
            output.Add(new IndexArbRow(
                reader.GetInt64("event_id"), reader.GetString("ev_name"), reader.GetInt64("start_time_unix"),
                reader.GetString("market_name"),
                reader.IsDBNull("competitor_id") ? null : reader.GetInt64("competitor_id"),
                reader.IsDBNull("identifier") ? "" : reader.GetString("identifier"), reader.GetString("name"),
                reader.IsDBNull("asset_url") ? null : reader.GetString("asset_url"),
                rtBid, rtOffer, spreadexBid, spreadexOffer,
                Math.Round(arbPoints, 2, MidpointRounding.AwayFromZero),
                reader.IsDBNull("spreadex_updated") ? null :
                    DateTime.SpecifyKind(reader.GetDateTime("spreadex_updated"), DateTimeKind.Utc)));
        }
        return output
            .OrderByDescending(row => Math.Abs(row.ArbPoints))
            .ThenBy(row => row.StartTimeUnix)
            .ThenBy(row => row.RunnerName)
            .ToList();
    }

    private static (double Bid, double Offer) CentredIndexQuote(double value)
    {
        var candidates = new List<(double Bid, double Offer)>
        {
            (.1, 1), (.5, 2), (1, 3)
        };
        for (var bid = 2; bid <= 7; bid++) candidates.Add((bid, bid + 2));
        for (var bid = 8; bid <= 100; bid++) candidates.Add((bid, bid + 3));
        return candidates
            .Select((quote, index) => new
            {
                Quote = quote,
                Index = index,
                Distance = Math.Abs(value - (quote.Bid + quote.Offer) / 2)
            })
            .OrderBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Index)
            .First().Quote;
    }

    public async Task<EventBoard?> GetEventBoardAsync(long eventId, CancellationToken cancellationToken)
    {
        var eventInfo = await GetEventAsync(eventId, cancellationToken);
        if (eventInfo is null) return null;

        const string sql = """
            SELECT
                s.selection_id, s.competitor_id, s.identifier, s.name,
                s.asset_url, s.price_back, s.price_back_last,
                s.active, s.display AS selection_display, s.scratch, s.result_text,
                s.rule4_time, s.rule4_deduction,
                s.market_id, COALESCE(NULLIF(m.mkt_name, ''), NULLIF(s.market_name, ''), 'Market') AS market_name,
                COALESCE(m.display_order, 100) AS display_order,
                COALESCE(m.active, s.active, 0) AS market_active,
                COALESCE(m.display, s.display, 0) AS market_display,
                rr.finishing_position, rr.result_status,
                api_price.decimal_price AS betsapi_price,
                api_price.source_updated_at_utc AS betsapi_updated_at_utc,
                betfred_price.decimal_price AS betfred_price,
                betfred_price.source_updated_at_utc AS betfred_updated_at_utc,
                direct_price.decimal_price AS bet365_direct_price,
                direct_price.source_updated_at_utc AS bet365_direct_updated_at_utc,
                selected_source.source_name AS selected_price_source,
                model_price.source_name AS model_price_source,
                model_price.decimal_price AS model_source_price,
                spreadex.decimal_price AS spreadex_price,
                spreadex.spread_buy AS spreadex_buy,
                CASE WHEN (model_price.decimal_price IS NULL OR
                                GREATEST(spreadex.index_50_25_10_sell,spreadex.index_50_25_10_buy)>=50/model_price.decimal_price)
                     THEN spreadex.index_50_25_10_buy END AS spreadex_index_50_25_10_buy,
                CASE WHEN (model_price.decimal_price IS NULL OR
                                GREATEST(spreadex.index_50_25_10_sell,spreadex.index_50_25_10_buy)>=50/model_price.decimal_price)
                     THEN spreadex.index_50_25_10_sell END AS spreadex_index_50_25_10_sell,
                CASE WHEN (model_price.decimal_price IS NULL OR
                                GREATEST(spreadex.index_50_25_10_sell,spreadex.index_50_25_10_buy)>=50/model_price.decimal_price)
                     THEN spreadex.index_50_25_10_updated_at_utc END AS spreadex_index_50_25_10_updated_at_utc,
                CASE WHEN (model_price.decimal_price IS NULL OR
                                GREATEST(spreadex.index_50_25_10_sell,spreadex.index_50_25_10_buy)>=50/model_price.decimal_price)
                     THEN spreadex.index_market_name END AS spreadex_index_market_name,
                spreadex.updated_at_utc AS spreadex_updated_at_utc
            FROM spuno_selections s
            LEFT JOIN spuno_markets m ON m.market_id = s.market_id
            LEFT JOIN web_runner_results rr
              ON rr.event_id = s.event_id AND rr.selection_id = s.selection_id
            LEFT JOIN web_race_winner_source_prices api_price
              ON api_price.event_id = s.event_id AND api_price.competitor_id = s.competitor_id
             AND api_price.source_name = 'BETSAPI'
            LEFT JOIN web_race_winner_source_prices betfred_price
              ON betfred_price.event_id = s.event_id AND betfred_price.competitor_id = s.competitor_id
             AND betfred_price.source_name = 'BETFRED'
            LEFT JOIN web_race_winner_source_prices direct_price
              ON direct_price.event_id = s.event_id AND direct_price.competitor_id = s.competitor_id
             AND direct_price.source_name = 'BET365_DIRECT'
            LEFT JOIN web_race_winner_source_snapshots selected_source
              ON selected_source.event_id = s.event_id AND selected_source.is_selected = 1
            LEFT JOIN web_race_winner_source_prices model_price
              ON model_price.event_id = s.event_id AND model_price.competitor_id = s.competitor_id
             AND model_price.source_name LIKE 'MODEL_%'
            LEFT JOIN web_spreadex_runner_prices spreadex
              ON spreadex.event_id = s.event_id AND spreadex.competitor_id = s.competitor_id
            WHERE s.event_id = @eventId
            ORDER BY
                CASE WHEN COALESCE(s.market_name, m.mkt_name) = 'Race Winner' THEN 0 ELSE 1 END,
                CAST(NULLIF(s.identifier, '') AS UNSIGNED), s.name, market_name;
            """;

        var selections = new List<SelectionSnapshot>();

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var selectionId = reader.GetInt64("selection_id");
            var competitorId = reader.IsDBNull("competitor_id") ? (long?)null : reader.GetInt64("competitor_id");
            var identifier = reader.IsDBNull("identifier") ? "" : reader.GetString("identifier");
            var name = reader.IsDBNull("name") ? $"Runner {selectionId}" : reader.GetString("name");
            var marketId = reader.IsDBNull("market_id") ? 0L : reader.GetInt64("market_id");
            var marketName = reader.GetString("market_name");
            var price = reader.IsDBNull("price_back") ? 0d : reader.GetDouble("price_back");
            var previous = reader.IsDBNull("price_back_last") ? 0d : reader.GetDouble("price_back_last");
            selections.Add(new SelectionSnapshot(
                selectionId, competitorId, identifier, name,
                reader.IsDBNull("asset_url") ? null : reader.GetString("asset_url"),
                reader.IsDBNull("betsapi_price") ? null : reader.GetDouble("betsapi_price"),
                reader.IsDBNull("betsapi_updated_at_utc") ? null : reader.GetDateTime("betsapi_updated_at_utc"),
                reader.IsDBNull("betfred_price") ? null : reader.GetDouble("betfred_price"),
                reader.IsDBNull("betfred_updated_at_utc") ? null : reader.GetDateTime("betfred_updated_at_utc"),
                reader.IsDBNull("bet365_direct_price") ? null : reader.GetDouble("bet365_direct_price"),
                reader.IsDBNull("bet365_direct_updated_at_utc") ? null : reader.GetDateTime("bet365_direct_updated_at_utc"),
                reader.IsDBNull("selected_price_source") ? null : reader.GetString("selected_price_source"),
                reader.IsDBNull("model_price_source") ? null : reader.GetString("model_price_source"),
                reader.IsDBNull("model_source_price") ? null : reader.GetDouble("model_source_price"),
                reader.IsDBNull("spreadex_price") ? null : reader.GetDouble("spreadex_price"),
                reader.IsDBNull("spreadex_buy") ? null : reader.GetDouble("spreadex_buy"),
                reader.IsDBNull("spreadex_index_50_25_10_buy") ? null : reader.GetDouble("spreadex_index_50_25_10_buy"),
                reader.IsDBNull("spreadex_index_50_25_10_sell") ? null : reader.GetDouble("spreadex_index_50_25_10_sell"),
                reader.IsDBNull("spreadex_index_50_25_10_updated_at_utc") ? null : reader.GetDateTime("spreadex_index_50_25_10_updated_at_utc"),
                reader.IsDBNull("spreadex_index_market_name") ? null : reader.GetString("spreadex_index_market_name"),
                reader.IsDBNull("spreadex_updated_at_utc") ? null : reader.GetDateTime("spreadex_updated_at_utc"),
                price, previous,
                !reader.IsDBNull("active") && reader.GetBoolean("active"),
                !reader.IsDBNull("selection_display") && reader.GetBoolean("selection_display"),
                !reader.IsDBNull("scratch") && reader.GetBoolean("scratch"),
                reader.IsDBNull("result_text") ? null : reader.GetString("result_text"),
                reader.IsDBNull("finishing_position") ? null : reader.GetInt32("finishing_position"),
                reader.IsDBNull("result_status") ? null : reader.GetString("result_status"),
                reader.IsDBNull("rule4_time") ? null : reader.GetString("rule4_time"),
                reader.IsDBNull("rule4_deduction") ? 0d : reader.GetDouble("rule4_deduction"),
                marketId, marketName,
                reader.GetInt32("display_order"),
                reader.GetBoolean("market_active"),
                reader.GetBoolean("market_display")));
        }

        if (eventInfo.SportId == 4)
            selections.RemoveAll(selection => selection.MarketId % 10000 is
                RacingMarketCalculator.Index502510Suffix or RacingMarketCalculator.Index50302010Suffix);

        var primarySelections = selections
            .Where(x => x.MarketName.Equals("Race Winner", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (primarySelections.Count == 0 && selections.Count > 0)
        {
            var primaryMarket = selections.GroupBy(x => x.MarketName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count()).First().Key;
            primarySelections = selections.Where(x => x.MarketName.Equals(primaryMarket, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var priceHistory = await GetPriceHistoryAsync(eventId, cancellationToken);
        var betfairPrices = await GetBetfairDisplayPricesAsync(eventId, cancellationToken);
        var runners = primarySelections.Select(x => new RunnerAccumulator(x.SelectionId, x.CompetitorId, x.Identifier, x.Name)
        {
            AssetUrl = x.AssetUrl,
            BetsApiPrice = x.BetsApiPrice,
            BetsApiUpdatedAtUtc = x.BetsApiUpdatedAtUtc,
            BetfredPrice = x.BetfredPrice,
            BetfredUpdatedAtUtc = x.BetfredUpdatedAtUtc,
            Bet365DirectPrice = x.Bet365DirectPrice,
            Bet365DirectUpdatedAtUtc = x.Bet365DirectUpdatedAtUtc,
            SelectedPriceSource = x.SelectedPriceSource,
            ModelPriceSource = x.ModelPriceSource,
            ModelSourcePrice = x.ModelSourcePrice,
            SpreadexPrice = x.SpreadexPrice,
            SpreadexBuy = x.SpreadexBuy,
            SpreadexIndex502510Buy = x.SpreadexIndex502510Buy,
            SpreadexIndex502510Sell = x.SpreadexIndex502510Sell,
            SpreadexIndex502510UpdatedAtUtc = x.SpreadexIndex502510UpdatedAtUtc,
            SpreadexIndexMarketName = x.SpreadexIndexMarketName,
            SpreadexUpdatedAtUtc = x.SpreadexUpdatedAtUtc,
            Betfair = betfairPrices.GetValueOrDefault(x.SelectionId),
            Active = x.Active,
            Display = x.Display,
            Withdrawn = x.Withdrawn,
            Result = x.Result,
            FinishingPosition = x.FinishingPosition,
            ResultStatus = x.ResultStatus,
            Rule4TimeUtc = x.Rule4TimeUtc,
            Rule4Deduction = x.Rule4Deduction,
            PriceHistory = priceHistory.GetValueOrDefault(x.SelectionId, [])
        }).ToList();
        var byCompetitor = runners.Where(x => x.CompetitorId is > 0)
            .GroupBy(x => x.CompetitorId!.Value).ToDictionary(x => x.Key, x => x.First());
        var byName = runners.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var markets = new Dictionary<string, MarketColumn>(StringComparer.OrdinalIgnoreCase);
        var matchedSelections = new HashSet<(long SelectionId, long MarketId)>();

        foreach (var selection in selections)
        {
            RunnerAccumulator? runner = null;
            if (selection.CompetitorId is > 0) byCompetitor.TryGetValue(selection.CompetitorId.Value, out runner);
            if (runner is null) byName.TryGetValue(selection.Name.Trim(), out runner);
            if (runner is null) continue;

            matchedSelections.Add((selection.SelectionId, selection.MarketId));
            runner.Prices[selection.MarketName] = new PriceCell(
                selection.MarketId, selection.MarketName, selection.Price, selection.PreviousPrice,
                selection.Active, selection.Display, selection.Result,
                selection.Rule4TimeUtc, selection.Rule4Deduction);
            markets.TryAdd(selection.MarketName, new MarketColumn(
                selection.MarketId, selection.MarketName, selection.DisplayOrder,
                selection.MarketActive, selection.MarketDisplay));
        }

        var marketList = markets.Values
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToList();
        var activeRunners = runners.Where(x => x.Active && !x.Withdrawn).ToList();
        var betfredIndexRunners = runners.Select(runner => new PricingRunner
        {
            EventId = eventInfo.EventId,
            MarketId = runner.Prices.GetValueOrDefault("Race Winner")?.MarketId ?? 0,
            SelectionId = runner.SelectionId,
            CompetitorId = runner.CompetitorId,
            MarketName = "Race Winner",
            Name = runner.Name,
            Identifier = runner.Identifier,
            Price = runner.BetfredPrice ?? 0,
            PreviousPrice = runner.BetfredPrice ?? 0,
            Active = runner.Active,
            Display = runner.Display,
            Scratch = runner.Withdrawn,
            AssetUrl = runner.AssetUrl,
            Result = runner.Result,
            Rule4Time = runner.Rule4TimeUtc,
            Rule4Deduction = runner.Rule4Deduction
        }).ToList();
        var betfredIndexes = _marketCalculator.BuildProviderIndexes(
            new PricingEvent(eventInfo.EventId, eventInfo.SportId, eventInfo.Meeting, eventInfo.Name,
                eventInfo.StartTimeUnix, eventInfo.Status, 0),
            betfredIndexRunners);
        foreach (var index in betfredIndexes)
        {
            RunnerAccumulator? runner = null;
            if (index.CompetitorId is > 0) byCompetitor.TryGetValue(index.CompetitorId.Value, out runner);
            if (runner is null) byName.TryGetValue(index.Name.Trim(), out runner);
            if (runner is null) continue;
            runner.BetfredIndex502510 = index.Index502510;
            runner.BetfredIndex50302010 = index.Index50302010;
        }
        // The model source is persisted per runner so the ladder and audit show
        // whether BetsAPI, Bet365 direct, Betfred or Spreadex drove each price.
        foreach (var runner in runners)
        {
            if (runner.ModelSourcePrice is > 1 && runner.Prices.TryGetValue("Race Winner", out var winnerPrice))
            {
                runner.LadderMoves = RacingMarketCalculator.CountLadderMoves(runner.ModelSourcePrice.Value, winnerPrice.Price);
                runner.LadderSource = runner.ModelPriceSource switch
                {
                    "MODEL_SDX" => "SPREADEX",
                    "MODEL_AHOY" => "BETAHOY",
                    "MODEL_API" => "BETSAPI",
                    "MODEL_BF" => "BETFRED",
                    "MODEL_B365" => "BET365_DIRECT",
                    _ => null
                };
            }
        }
        var runnerList = runners
            .OrderBy(x => ParseIdentifier(x.Identifier))
            .ThenBy(x => x.Name)
            .Select(x => x.ToModel())
            .ToList();
        var derivedMarkets = selections
            .Where(selection => !matchedSelections.Contains((selection.SelectionId, selection.MarketId)))
            .GroupBy(selection => new
            {
                selection.MarketId,
                selection.MarketName,
                selection.DisplayOrder,
                selection.MarketActive,
                selection.MarketDisplay
            })
            .Select(group => new DerivedMarket(
                group.Key.MarketId,
                group.Key.MarketName,
                group.Key.DisplayOrder,
                group.Key.MarketActive,
                group.Key.MarketDisplay,
                group.OrderBy(x => ParseIdentifier(x.Identifier)).ThenBy(x => x.Name)
                    .Select(x => new DerivedSelection(
                        x.SelectionId, x.Identifier, x.Name, x.Price, x.PreviousPrice,
                        x.Active, x.Display, x.Withdrawn, x.Result))
                    .ToList()))
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.Name)
            .ToList();
        var totalRule4 = Math.Min(.90, runnerList.Where(x => x.Withdrawn).Sum(x => x.Rule4Deduction));

        var configuration = await GetEventMarketConfigurationAsync(eventId, cancellationToken);
        var adjustments = await GetMarketAdjustmentsAsync(eventId, cancellationToken);
        var raceCardSpreads = _marketCalculator.BuildRaceCardSpreads(runnerList);
        return new EventBoard(eventInfo, marketList, runnerList, derivedMarkets, totalRule4, configuration, adjustments, raceCardSpreads);
    }

    public async Task<MeetingRaceCardSpreadBoard> GetMeetingRaceCardSpreadsAsync(string meeting, CancellationToken cancellationToken)
    {
        var events = await GetMeetingEventsAsync(2, meeting, true, cancellationToken);
        var races = new List<MeetingRaceCardSpreadRace>();
        foreach (var racingEvent in events)
        {
            var board = await GetEventBoardAsync(racingEvent.EventId, cancellationToken);
            if (board is not null)
                races.Add(new(racingEvent.EventId, racingEvent.Name, racingEvent.StartTimeUnix, racingEvent.Status, board.RaceCardSpreads));
        }
        var definitions = races.SelectMany(r => r.Markets).GroupBy(m => m.Key).Select(group =>
        {
            var complete = group.Count() == races.Count && group.All(m => m.Bid.HasValue && m.Offer.HasValue);
            var first = group.First();
            return complete
                ? new RaceCardSpreadPrice(first.Key, first.Name, group.Sum(m => m.Bid!.Value), group.Sum(m => m.Offer!.Value), group.Sum(m => m.Midpoint!.Value), first.HelpText)
                : new RaceCardSpreadPrice(first.Key, first.Name, null, null, null, first.HelpText);
        }).ToList();
        return new(meeting, races, definitions);
    }

    private static bool IsBalancedLadderComparison(
        IReadOnlyList<RunnerAccumulator> runners,
        Func<RunnerAccumulator, double?> sourcePrice)
    {
        if (runners.Count < 2) return false;
        var moves = new List<int>(runners.Count);
        foreach (var runner in runners)
        {
            var source = sourcePrice(runner);
            if (source is not > 1 ||
                !runner.Prices.TryGetValue("Race Winner", out var winner) ||
                winner.Price <= 1)
                return false;
            moves.Add(RacingMarketCalculator.CountLadderMoves(source.Value, winner.Price));
        }

        return moves.Min() >= 0 && moves.Max() - moves.Min() <= 1;
    }

    private async Task<IReadOnlyDictionary<long, BetfairRunnerPrice>> GetBetfairDisplayPricesAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT runner.internal_selection_id,price.betfair_market_id,price.runner_status,
                   price.best_back_price,price.best_lay_price,price.last_traded_price,
                   price.market_status,price.in_play,price.is_delayed,price.bet_delay,price.captured_at_utc
            FROM web_betfair_runner_links runner
            JOIN web_betfair_event_links event_link
              ON event_link.event_id=runner.event_id AND event_link.match_status='MATCHED'
            JOIN web_betfair_prices price
              ON price.event_id=runner.event_id AND price.betfair_selection_id=runner.betfair_selection_id
            WHERE runner.event_id=@eventId;
            """;
        var output = new Dictionary<long, BetfairRunnerPrice>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var captured = DateTime.SpecifyKind(reader.GetDateTime(10), DateTimeKind.Utc);
            output[reader.GetInt64(0)] = new BetfairRunnerPrice(
                reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetString(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt32(9), captured);
        }
        return output;
    }

    public async Task<EventMarketConfiguration> GetEventMarketConfigurationAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string ensureSql = """
            INSERT IGNORE INTO web_event_market_config (
                event_id, top3_enabled, betting_without_selection_id,
                insure2_enabled, insure3_enabled, insure4_enabled, updated_at_utc
            ) VALUES (@eventId, 1, NULL, 1, 1, 1, UTC_TIMESTAMP(6));
            """;
        const string configSql = """
            SELECT c.event_id, c.top3_enabled,
                   c.insure2_enabled, c.insure3_enabled, c.insure4_enabled
            FROM web_event_market_config c
            WHERE c.event_id = @eventId LIMIT 1;
            """;
        const string withoutSql = """
            SELECT w.id, w.event_id, w.excluded_selection_id,
                   COALESCE(s.name, CONCAT('Runner ', w.excluded_selection_id)) AS excluded_runner_name,
                   w.market_suffix, w.display_order, w.enabled, w.voided, w.void_reason
            FROM web_betting_without_markets w
            LEFT JOIN spuno_selections s
              ON s.event_id = w.event_id
             AND s.selection_id = w.excluded_selection_id
             AND MOD(s.market_id, 10000) = 9001
            WHERE w.event_id = @eventId AND (w.enabled = 1 OR w.voided = 1)
            ORDER BY w.display_order, w.id;
            """;
        const string pairsSql = """
            SELECT p.id, p.event_id, p.runner_a_selection_id,
                   COALESCE(a.name, CONCAT('Runner ', p.runner_a_selection_id)) AS runner_a_name,
                   p.runner_b_selection_id,
                   COALESCE(b.name, CONCAT('Runner ', p.runner_b_selection_id)) AS runner_b_name,
                   p.market_suffix, p.display_order, p.enabled, p.voided, p.void_reason
            FROM web_match_bet_pairs p
            LEFT JOIN spuno_selections a
              ON a.event_id = p.event_id AND a.selection_id = p.runner_a_selection_id
            LEFT JOIN spuno_selections b
              ON b.event_id = p.event_id AND b.selection_id = p.runner_b_selection_id
            WHERE p.event_id = @eventId AND (p.enabled = 1 OR p.voided = 1)
            ORDER BY p.display_order, p.id;
            """;
        const string forecastsSql = """
            SELECT p.id, p.event_id, p.runner_a_selection_id,
                   COALESCE(a.name, CONCAT('Runner ', p.runner_a_selection_id)) AS runner_a_name,
                   p.runner_b_selection_id,
                   COALESCE(b.name, CONCAT('Runner ', p.runner_b_selection_id)) AS runner_b_name,
                   p.market_suffix, p.display_order, p.enabled, p.voided, p.void_reason
            FROM web_forecast_pairs p
            LEFT JOIN spuno_selections a
              ON a.event_id = p.event_id AND a.selection_id = p.runner_a_selection_id
            LEFT JOIN spuno_selections b
              ON b.event_id = p.event_id AND b.selection_id = p.runner_b_selection_id
            WHERE p.event_id = @eventId AND (p.enabled = 1 OR p.voided = 1)
            ORDER BY p.display_order, p.id;
            """;

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var ensure = new MySqlCommand(ensureSql, connection))
        {
            ensure.Parameters.AddWithValue("@eventId", eventId);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        bool top3;
        bool insure2;
        bool insure3;
        bool insure4;
        await using (var config = new MySqlCommand(configSql, connection))
        {
            config.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await config.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Event market configuration could not be loaded.");
            top3 = reader.GetBoolean("top3_enabled");
            insure2 = reader.GetBoolean("insure2_enabled");
            insure3 = reader.GetBoolean("insure3_enabled");
            insure4 = reader.GetBoolean("insure4_enabled");
        }

        var bettingWithoutMarkets = new List<BettingWithoutMarket>();
        await using (var command = new MySqlCommand(withoutSql, connection))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bettingWithoutMarkets.Add(new BettingWithoutMarket(
                    reader.GetInt64("id"), reader.GetInt64("event_id"),
                    reader.GetInt64("excluded_selection_id"), reader.GetString("excluded_runner_name"),
                    reader.GetInt32("market_suffix"), reader.GetInt32("display_order"),
                    reader.GetBoolean("enabled"), reader.GetBoolean("voided"),
                    reader.IsDBNull("void_reason") ? null : reader.GetString("void_reason")));
            }
        }

        var pairs = new List<MatchBetPair>();
        await using (var command = new MySqlCommand(pairsSql, connection))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pairs.Add(new MatchBetPair(
                    reader.GetInt64("id"), reader.GetInt64("event_id"),
                    reader.GetInt64("runner_a_selection_id"), reader.GetString("runner_a_name"),
                    reader.GetInt64("runner_b_selection_id"), reader.GetString("runner_b_name"),
                    reader.GetInt32("market_suffix"), reader.GetInt32("display_order"),
                    reader.GetBoolean("enabled"), reader.GetBoolean("voided"),
                    reader.IsDBNull("void_reason") ? null : reader.GetString("void_reason")));
            }
        }

        var forecasts = new List<ForecastPair>();
        await using (var command = new MySqlCommand(forecastsSql, connection))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                forecasts.Add(new ForecastPair(
                    reader.GetInt64("id"), reader.GetInt64("event_id"),
                    reader.GetInt64("runner_a_selection_id"), reader.GetString("runner_a_name"),
                    reader.GetInt64("runner_b_selection_id"), reader.GetString("runner_b_name"),
                    reader.GetInt32("market_suffix"), reader.GetInt32("display_order"),
                    reader.GetBoolean("enabled"), reader.GetBoolean("voided"),
                    reader.IsDBNull("void_reason") ? null : reader.GetString("void_reason")));
            }
        }

        return new EventMarketConfiguration(eventId, top3, insure2, insure3, insure4,
            bettingWithoutMarkets, pairs, forecasts);
    }

    public async Task<EventMarketConfiguration> SaveEventMarketConfigurationAsync(
        long eventId,
        UpdateEventMarketsRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            INSERT INTO web_event_market_config (
                event_id, top3_enabled,
                insure2_enabled, insure3_enabled, insure4_enabled, updated_at_utc
            ) VALUES (
                @eventId, @top3, @insure2, @insure3, @insure4, UTC_TIMESTAMP(6)
            ) ON DUPLICATE KEY UPDATE
                top3_enabled = VALUES(top3_enabled),
                insure2_enabled = VALUES(insure2_enabled),
                insure3_enabled = VALUES(insure3_enabled),
                insure4_enabled = VALUES(insure4_enabled),
                updated_at_utc = VALUES(updated_at_utc);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@top3", request.Top3Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@insure2", request.Insure2Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@insure3", request.Insure3Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@insure4", request.Insure4Enabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MarkEventForRepriceAsync(connection, null, eventId, cancellationToken);
        return await GetEventMarketConfigurationAsync(eventId, cancellationToken);
    }

    public async Task<BettingWithoutMarket> AddBettingWithoutMarketAsync(
        long eventId,
        CreateBettingWithoutRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await ValidateRaceWinnerSelectionAsync(connection, eventId, request.ExcludedSelectionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        const string suffixSql = """
            SELECT candidate.suffix
            FROM (
                SELECT 9200 + ones.n + tens.n * 10 AS suffix
                FROM (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                      UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) ones
                CROSS JOIN (SELECT 0 n UNION ALL SELECT 1) tens
            ) candidate
            LEFT JOIN web_betting_without_markets existing
              ON existing.event_id = @eventId AND existing.market_suffix = candidate.suffix
            WHERE existing.id IS NULL
            ORDER BY candidate.suffix
            LIMIT 1;
            """;
        int suffix;
        await using (var suffixCommand = new MySqlCommand(suffixSql, connection, transaction))
        {
            suffixCommand.Parameters.AddWithValue("@eventId", eventId);
            var value = await suffixCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null) throw new InvalidOperationException("This event already has the maximum 20 Betting Without markets.");
            suffix = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        long id;
        await using (var insert = new MySqlCommand("""
            INSERT INTO web_betting_without_markets
              (event_id, excluded_selection_id, market_suffix, display_order, enabled, voided, updated_at_utc)
            VALUES (@eventId, @selectionId, @suffix, @displayOrder, 1, 0, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE enabled=1, voided=0, void_reason=NULL, voided_at_utc=NULL,
              updated_at_utc=UTC_TIMESTAMP(6), id=LAST_INSERT_ID(id);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@eventId", eventId);
            insert.Parameters.AddWithValue("@selectionId", request.ExcludedSelectionId);
            insert.Parameters.AddWithValue("@suffix", suffix);
            insert.Parameters.AddWithValue("@displayOrder",
                RacingMarketCalculator.BettingWithoutDisplayOrderStart + suffix - 9200);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            id = (long)insert.LastInsertedId;
        }
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetEventMarketConfigurationAsync(eventId, cancellationToken))
            .BettingWithoutMarkets.Single(x => x.Id == id);
    }

    public async Task DeleteBettingWithoutMarketAsync(
        long eventId,
        long id,
        CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var find = new MySqlCommand("""
            SELECT market_suffix FROM web_betting_without_markets
            WHERE event_id=@eventId AND id=@id FOR UPDATE;
            """, connection, transaction);
        find.Parameters.AddWithValue("@eventId", eventId);
        find.Parameters.AddWithValue("@id", id);
        var suffixValue = await find.ExecuteScalarAsync(cancellationToken);
        if (suffixValue is null) throw new KeyNotFoundException("Betting Without market was not found.");
        var marketId = eventId * 10000L + Convert.ToInt32(suffixValue, CultureInfo.InvariantCulture);

        await using var command = new MySqlCommand("""
            UPDATE web_betting_without_markets
            SET enabled=0, voided=0, void_reason=NULL, voided_at_utc=NULL, updated_at_utc=UTC_TIMESTAMP(6)
            WHERE event_id=@eventId AND id=@id;
            DELETE FROM spuno_selections WHERE event_id=@eventId AND market_id=@marketId;
            UPDATE spuno_markets SET active=0, display=0 WHERE event_id=@eventId AND market_id=@marketId;
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@marketId", marketId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MatchBetPair> AddMatchBetPairAsync(
        long eventId,
        CreateMatchBetRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RunnerASelectionId == request.RunnerBSelectionId)
            throw new ArgumentException("Choose two different runners.");

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await ValidateRaceWinnerSelectionAsync(connection, eventId, request.RunnerASelectionId, cancellationToken);
        await ValidateRaceWinnerSelectionAsync(connection, eventId, request.RunnerBSelectionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        const string suffixSql = """
            SELECT candidate.suffix
            FROM (
                SELECT 9100 + ones.n + tens.n * 10 AS suffix
                FROM (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                      UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) ones
                CROSS JOIN (SELECT 0 n UNION ALL SELECT 1) tens
            ) candidate
            LEFT JOIN web_match_bet_pairs existing
              ON existing.event_id = @eventId AND existing.market_suffix = candidate.suffix
            WHERE existing.id IS NULL
            ORDER BY candidate.suffix
            LIMIT 1;
            """;
        int suffix;
        await using (var suffixCommand = new MySqlCommand(suffixSql, connection, transaction))
        {
            suffixCommand.Parameters.AddWithValue("@eventId", eventId);
            var value = await suffixCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null) throw new InvalidOperationException("This event already has the maximum 20 Match Bets.");
            suffix = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        const string insertSql = """
            INSERT INTO web_match_bet_pairs (
                event_id, runner_a_selection_id, runner_b_selection_id,
                market_suffix, display_order, enabled, voided, updated_at_utc
            ) VALUES (@eventId, @runnerA, @runnerB, @suffix, @displayOrder, 1, 0, UTC_TIMESTAMP(6));
            """;
        long pairId;
        await using (var insert = new MySqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("@eventId", eventId);
            insert.Parameters.AddWithValue("@runnerA", request.RunnerASelectionId);
            insert.Parameters.AddWithValue("@runnerB", request.RunnerBSelectionId);
            insert.Parameters.AddWithValue("@suffix", suffix);
            insert.Parameters.AddWithValue("@displayOrder",
                RacingMarketCalculator.MatchBetDisplayOrderStart + suffix - 9100);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            pairId = (long)insert.LastInsertedId;
        }
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetEventMarketConfigurationAsync(eventId, cancellationToken)).MatchBets.Single(x => x.Id == pairId);
    }

    public async Task DeleteMatchBetPairAsync(long eventId, long pairId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var find = new MySqlCommand("""
            SELECT market_suffix
            FROM web_match_bet_pairs
            WHERE event_id = @eventId AND id = @pairId
            FOR UPDATE;
            """, connection, transaction);
        find.Parameters.AddWithValue("@eventId", eventId);
        find.Parameters.AddWithValue("@pairId", pairId);
        var suffixValue = await find.ExecuteScalarAsync(cancellationToken);
        if (suffixValue is null) throw new KeyNotFoundException("Match Bet pair was not found.");
        var marketId = eventId * 10000L + Convert.ToInt32(suffixValue, CultureInfo.InvariantCulture);

        await using var command = new MySqlCommand("""
            UPDATE web_match_bet_pairs
            SET enabled = 0, voided = 0, void_reason = NULL, voided_at_utc = NULL,
                updated_at_utc = UTC_TIMESTAMP(6)
            WHERE event_id = @eventId AND id = @pairId;
            DELETE FROM spuno_selections WHERE event_id = @eventId AND market_id = @marketId;
            UPDATE spuno_markets SET active = 0, display = 0
            WHERE event_id = @eventId AND market_id = @marketId;
            DELETE FROM web_market_adjustments WHERE event_id = @eventId AND market_id = @marketId;
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@pairId", pairId);
        command.Parameters.AddWithValue("@marketId", marketId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ForecastPair> AddForecastPairAsync(
        long eventId,
        CreateForecastRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RunnerASelectionId == request.RunnerBSelectionId)
            throw new ArgumentException("Choose two different runners.");

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await ValidateRaceWinnerSelectionAsync(connection, eventId, request.RunnerASelectionId, cancellationToken);
        await ValidateRaceWinnerSelectionAsync(connection, eventId, request.RunnerBSelectionId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await using (var duplicate = new MySqlCommand("""
            SELECT COUNT(*) FROM web_forecast_pairs
            WHERE event_id=@eventId AND enabled=1
              AND ((runner_a_selection_id=@runnerA AND runner_b_selection_id=@runnerB)
                OR (runner_a_selection_id=@runnerB AND runner_b_selection_id=@runnerA));
            """, connection, transaction))
        {
            duplicate.Parameters.AddWithValue("@eventId", eventId);
            duplicate.Parameters.AddWithValue("@runnerA", request.RunnerASelectionId);
            duplicate.Parameters.AddWithValue("@runnerB", request.RunnerBSelectionId);
            if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException("That Forecast pair already exists.");
        }

        const string suffixSql = """
            SELECT candidate.suffix
            FROM (
                SELECT 9300 + ones.n + tens.n * 10 AS suffix
                FROM (SELECT 0 n UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4
                      UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) ones
                CROSS JOIN (SELECT 0 n UNION ALL SELECT 1) tens
            ) candidate
            LEFT JOIN web_forecast_pairs existing
              ON existing.event_id=@eventId AND existing.market_suffix=candidate.suffix
             AND (existing.enabled=1 OR existing.voided=1)
            WHERE existing.id IS NULL
            ORDER BY candidate.suffix
            LIMIT 1;
            """;
        int suffix;
        await using (var suffixCommand = new MySqlCommand(suffixSql, connection, transaction))
        {
            suffixCommand.Parameters.AddWithValue("@eventId", eventId);
            var value = await suffixCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null) throw new InvalidOperationException("This event already has the maximum 20 Forecast markets.");
            suffix = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        long pairId;
        await using (var insert = new MySqlCommand("""
            INSERT INTO web_forecast_pairs (
                event_id,runner_a_selection_id,runner_b_selection_id,
                market_suffix,display_order,enabled,voided,updated_at_utc
            ) VALUES (@eventId,@runnerA,@runnerB,@suffix,@displayOrder,1,0,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                id=LAST_INSERT_ID(id),
                runner_a_selection_id=VALUES(runner_a_selection_id),
                runner_b_selection_id=VALUES(runner_b_selection_id),
                display_order=VALUES(display_order),
                enabled=1,voided=0,void_reason=NULL,voided_at_utc=NULL,
                updated_at_utc=VALUES(updated_at_utc);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@eventId", eventId);
            insert.Parameters.AddWithValue("@runnerA", request.RunnerASelectionId);
            insert.Parameters.AddWithValue("@runnerB", request.RunnerBSelectionId);
            insert.Parameters.AddWithValue("@suffix", suffix);
            insert.Parameters.AddWithValue("@displayOrder",
                RacingMarketCalculator.ReservedNewMarketDisplayOrderStart + suffix -
                RacingMarketCalculator.ForecastFirstSuffix);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            pairId = (long)insert.LastInsertedId;
        }
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetEventMarketConfigurationAsync(eventId, cancellationToken))
            .Forecasts.Single(x => x.Id == pairId);
    }

    public async Task DeleteForecastPairAsync(long eventId, long pairId, CancellationToken cancellationToken)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var find = new MySqlCommand("""
            SELECT market_suffix FROM web_forecast_pairs
            WHERE event_id=@eventId AND id=@pairId FOR UPDATE;
            """, connection, transaction);
        find.Parameters.AddWithValue("@eventId", eventId);
        find.Parameters.AddWithValue("@pairId", pairId);
        var suffixValue = await find.ExecuteScalarAsync(cancellationToken);
        if (suffixValue is null) throw new KeyNotFoundException("Forecast market was not found.");
        var marketId = eventId * 10000L + Convert.ToInt32(suffixValue, CultureInfo.InvariantCulture);

        await using var command = new MySqlCommand("""
            UPDATE web_forecast_pairs
            SET enabled=0,voided=0,void_reason=NULL,voided_at_utc=NULL,updated_at_utc=UTC_TIMESTAMP(6)
            WHERE event_id=@eventId AND id=@pairId;
            DELETE FROM spuno_selections WHERE event_id=@eventId AND market_id=@marketId;
            UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId AND market_id=@marketId;
            DELETE FROM web_market_adjustments WHERE event_id=@eventId AND market_id=@marketId;
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@pairId", pairId);
        command.Parameters.AddWithValue("@marketId", marketId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<MarketRule4Adjustment>> GetMarketAdjustmentsAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT market_id, market_name, withdrawn_selection_id, runner_name,
                   price_at_withdrawal, deduction, applied_at_utc, adjustment_type
            FROM web_market_adjustments
            WHERE event_id = @eventId
            ORDER BY applied_at_utc, market_id;
            """;
        var output = new List<MarketRule4Adjustment>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new MarketRule4Adjustment(
                reader.GetInt64("market_id"), reader.GetString("market_name"),
                reader.GetInt64("withdrawn_selection_id"), reader.GetString("runner_name"),
                reader.GetDouble("price_at_withdrawal"), reader.GetDouble("deduction"),
                DateTime.SpecifyKind(reader.GetDateTime("applied_at_utc"), DateTimeKind.Utc),
                reader.GetString("adjustment_type")));
        return output;
    }

    private static async Task ValidateRaceWinnerSelectionAsync(
        MySqlConnection connection,
        long eventId,
        long selectionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*) FROM spuno_selections
            WHERE event_id = @eventId AND selection_id = @selectionId
              AND market_name = 'Race Winner';
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@selectionId", selectionId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            throw new KeyNotFoundException("The selected Race Winner runner was not found.");
    }

    internal async Task<IReadOnlyList<RunnerActionReceipt>> ApplyExplicitFeedNonRunnersAsync(
        long eventId,
        IReadOnlyList<FeedNonRunner> nonRunners,
        DateTime detectedAtUtc,
        CancellationToken cancellationToken)
    {
        if (nonRunners.Count == 0) return [];
        const string sql = """
            SELECT s.selection_id, s.name, s.identifier, e.sport_id,
                   COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(raw.raw_token_json, '$.DI')) AS UNSIGNED), 0) AS source_runner_id,
                   s.scratch,
                   COALESCE((
                       SELECT action.action_source
                       FROM web_runner_actions action
                       WHERE action.event_id=s.event_id AND action.selection_id=s.selection_id
                         AND action.action_type='WITHDRAW'
                       ORDER BY action.id DESC LIMIT 1
                   ), '') AS last_withdrawal_source
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id=s.event_id AND e.sport_id IN (2,4)
            LEFT JOIN em_b365_selections_raw raw
              ON raw.event_id=s.event_id AND raw.selection_id=s.competitor_id
            WHERE s.event_id=@eventId AND s.market_name='Race Winner';
            """;
        var candidates = new List<FeedWithdrawalCandidate>();
        await using (var connection = OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                candidates.Add(new(
                    reader.GetInt64("selection_id"),
                    reader.GetString("name"),
                    reader.IsDBNull("identifier") ? "" : reader.GetString("identifier"),
                    reader.GetInt32("sport_id"),
                    reader.GetInt64("source_runner_id"),
                    reader.GetBoolean("scratch"),
                    reader.GetString("last_withdrawal_source")));
        }

        var matchedMarkers = nonRunners.Count(marker =>
            candidates.Any(candidate => candidate.SportId == 4
                ? marker.Identifier is > 0 && ParseIdentifier(candidate.Identifier) == marker.Identifier
                : (marker.SourceRunnerId is > 0 && candidate.SourceRunnerId == marker.SourceRunnerId) ||
                  BetfairMarketMatcher.NormalizeRunner(candidate.Name) == BetfairMarketMatcher.NormalizeRunner(marker.Name)));
        if (candidates.Count >= 3 && matchedMarkers >= candidates.Count - 1)
        {
            _logger.LogWarning(
                "Ignored implausible bulk non-runner snapshot for event {EventId}: {Matched}/{RunnerCount} runners marked withdrawn.",
                eventId, matchedMarkers, candidates.Count);
            return [];
        }

        var receipts = new List<RunnerActionReceipt>();
        foreach (var marker in nonRunners)
        {
            var normalized = BetfairMarketMatcher.NormalizeRunner(marker.Name);
            var candidate = candidates.FirstOrDefault(x => x.SportId == 4 && marker.Identifier is > 0 &&
                ParseIdentifier(x.Identifier) == marker.Identifier);
            candidate ??= marker.SourceRunnerId is > 0
                ? candidates.FirstOrDefault(x => x.SportId != 4 && x.SourceRunnerId == marker.SourceRunnerId)
                : null;
            candidate ??= candidates.FirstOrDefault(x => x.SportId != 4 &&
                BetfairMarketMatcher.NormalizeRunner(x.Name) == normalized);
            // Cloth/draw numbers are not identity. A late non-runner can leave
            // a gap or a provider can renumber its display list; falling back
            // to number alone previously applied Majestic Jude's NR marker to
            // Mino. Require the provider runner ID or normalized horse name.
            if (candidate is null) continue;

            // Explicit feed data may repair an earlier automatic suspension-based withdrawal,
            // but it must not replace a trader-entered withdrawal time.
            if (candidate.Scratch &&
                (candidate.LastWithdrawalSource.Equals("WEB", StringComparison.OrdinalIgnoreCase) ||
                 candidate.LastWithdrawalSource.Equals("BETSAPI", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Remove(candidate);
                continue;
            }

            // This path has already refused to overwrite a WEB or BETSAPI withdrawal above,
            // so any scratched runner reaching here carries an automatic withdrawal that
            // explicit feed data is allowed to repair.
            receipts.Add(await WithdrawHorseAsync(eventId, candidate.SelectionId, marker.Reason,
                "BETSAPI", cancellationToken, detectedAtUtc, allowRepair: candidate.Scratch));
            candidates.Remove(candidate);
        }
        return receipts;
    }

    public async Task<int> RepairStaleBulkWithdrawalsAsync(string meeting, CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT s.event_id, s.selection_id
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id=s.event_id AND e.sport_id IN (2, 4)
            JOIN (
                SELECT event_id, rule4_time, COUNT(*) stale_count
                FROM spuno_selections
                WHERE market_name='Race Winner' AND scratch=1
                  AND COALESCE(rule4_deduction,0)=0 AND rule4_time IS NOT NULL
                GROUP BY event_id, rule4_time
            ) stale ON stale.event_id=s.event_id AND stale.rule4_time=s.rule4_time
            JOIN (
                SELECT event_id, COUNT(*) runner_count
                FROM spuno_selections WHERE market_name='Race Winner' GROUP BY event_id
            ) totals ON totals.event_id=s.event_id
            WHERE s.market_name='Race Winner' AND s.scratch=1
              AND COALESCE(s.rule4_deduction,0)=0
              AND stale.stale_count >= totals.runner_count - 1
              AND stale.stale_count >= 2
              AND e.meeting=@meeting AND e.status IN ('0','1')
              AND e.start_time_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR);
            """;
        var stale = new List<(long EventId, long SelectionId)>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var find = new MySqlCommand(findSql, connection))
        {
            find.Parameters.AddWithValue("@meeting", meeting);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) stale.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        if (stale.Count == 0) return 0;
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        foreach (var runner in stale)
        {
            await using var resetSelections = new MySqlCommand("""
                UPDATE spuno_selections
                SET scratch=0, rule4_time=NULL, rule4_deduction=0,
                    result_text=IF(result_text='99',NULL,result_text)
                WHERE event_id=@eventId AND selection_id=@selectionId;
                """, connection, transaction);
            resetSelections.Parameters.AddWithValue("@eventId", runner.EventId);
            resetSelections.Parameters.AddWithValue("@selectionId", runner.SelectionId);
            await resetSelections.ExecuteNonQueryAsync(cancellationToken);
            await using var resetState = new MySqlCommand("""
                UPDATE web_runner_state SET is_withdrawn=0,last_seen_utc=UTC_TIMESTAMP(6)
                WHERE event_id=@eventId AND selection_id=@selectionId;
                DELETE FROM web_non_runner_candidates WHERE event_id=@eventId AND selection_id=@selectionId;
                """, connection, transaction);
            resetState.Parameters.AddWithValue("@eventId", runner.EventId);
            resetState.Parameters.AddWithValue("@selectionId", runner.SelectionId);
            await resetState.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        _logger.LogWarning("Repaired {Count} stale bulk-withdrawal runner records for {Meeting}.", stale.Count, meeting);
        return stale.Count;
    }

    public async Task<int> RepairZeroRule4DeductionsAsync(CancellationToken cancellationToken)
    {
        const string findSql = """
            SELECT s.event_id,s.market_id,s.selection_id,s.identifier,s.name,s.rule4_time,
                   (SELECT h.price FROM web_price_history h
                    WHERE h.event_id=s.event_id AND h.market_id=s.market_id AND h.selection_id=s.selection_id
                      AND h.price>1 AND h.price<1000
                      AND h.recorded_at_utc<=STR_TO_DATE(REPLACE(REPLACE(s.rule4_time,'T',' '),'Z',''),'%Y-%m-%d %H:%i:%s.%f')
                    ORDER BY h.recorded_at_utc DESC LIMIT 1) AS withdrawal_price
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id=s.event_id AND e.sport_id IN (2, 4)
            WHERE MOD(s.market_id,10000) NOT IN (9009,9010)
              AND s.scratch=1 AND s.rule4_time IS NOT NULL
              AND COALESCE(s.rule4_deduction,0)=0 AND e.status IN ('0','1','2')
              AND e.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 12 HOUR);
            """;
        var repairs = new List<(long EventId, long MarketId, long SelectionId, string Identifier, string Name,
            DateTime ActionTime, double Price)>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var find = new MySqlCommand(findSql, connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!DateTime.TryParse(reader.GetString("rule4_time"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var actionTime)) continue;
                var price = reader.IsDBNull("withdrawal_price") ? 0d : reader.GetDouble("withdrawal_price");
                repairs.Add((reader.GetInt64("event_id"), reader.GetInt64("market_id"), reader.GetInt64("selection_id"),
                    reader.IsDBNull("identifier") ? "" : reader.GetString("identifier"), reader.GetString("name"),
                    actionTime, price));
            }
        }
        var repairedCount = 0;
        foreach (var repair in repairs)
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var runner = await LoadRunnerForUpdateAsync(connection, transaction, repair.EventId, repair.SelectionId, cancellationToken);
            var price = repair.Price;
            if (!ValidPrice(price) || Rule4Calculator.FromDecimalOdds(price) <= 0)
            {
                var reconstructed = await ReconstructMarketPriceAtWithdrawalAsync(connection, transaction, runner,
                    repair.MarketId, repair.ActionTime, cancellationToken);
                if (ValidPrice(reconstructed)) price = reconstructed;
            }
            var deduction = Rule4Calculator.FromDecimalOdds(price);
            if (deduction <= 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }
            await using var update = new MySqlCommand("""
                UPDATE spuno_selections
                SET rule4_deduction=@deduction
                WHERE event_id=@eventId AND market_id=@marketId AND scratch=1
                  AND (selection_id=@selectionId OR (identifier=@identifier AND name=@name));
                UPDATE web_market_adjustments
                SET price_at_withdrawal=@price,deduction=@deduction
                WHERE event_id=@eventId AND market_id=@marketId
                  AND withdrawn_selection_id=@selectionId AND adjustment_type='RULE4';
                """, connection, transaction);
            update.Parameters.AddWithValue("@eventId", repair.EventId);
            update.Parameters.AddWithValue("@marketId", repair.MarketId);
            update.Parameters.AddWithValue("@selectionId", repair.SelectionId);
            update.Parameters.AddWithValue("@identifier", repair.Identifier);
            update.Parameters.AddWithValue("@name", repair.Name);
            update.Parameters.AddWithValue("@price", price);
            update.Parameters.AddWithValue("@deduction", deduction);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await MarkEventForRepriceAsync(connection, transaction, repair.EventId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            repairedCount++;
        }
        if (repairedCount > 0)
            _logger.LogWarning("Repaired {Count} zero Rule 4 deductions from market history or reconstructed pre-withdrawal books.", repairedCount);
        return repairedCount;
    }

    public async Task<IReadOnlyList<Rule4MarketPrice>> RepairZeroRule4DeductionsForEventAsync(
        long eventId, CancellationToken cancellationToken)
    {
        var candidates = new List<(long MarketId, long SelectionId, string MarketName, DateTime ActionTime, double HistoryPrice)>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var find = new MySqlCommand("""
            SELECT s.market_id,s.selection_id,s.market_name,s.rule4_time,
                   (SELECT h.price FROM web_price_history h
                    WHERE h.event_id=s.event_id AND h.market_id=s.market_id AND h.selection_id=s.selection_id
                      AND h.price>1 AND h.price<1000
                      AND h.recorded_at_utc<=STR_TO_DATE(REPLACE(REPLACE(s.rule4_time,'T',' '),'Z',''),'%Y-%m-%d %H:%i:%s.%f')
                    ORDER BY h.recorded_at_utc DESC LIMIT 1) AS withdrawal_price
            FROM spuno_selections s
            WHERE s.event_id=@eventId AND MOD(s.market_id,10000) NOT IN (9009,9010)
              AND NOT (MOD(s.market_id,10000) BETWEEN 9100 AND 9199)
              AND NOT (MOD(s.market_id,10000) BETWEEN 9300 AND 9319)
              AND s.scratch=1 AND s.rule4_time IS NOT NULL;
            """, connection))
        {
            find.Parameters.AddWithValue("@eventId", eventId);
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!DateTime.TryParse(reader.GetString("rule4_time"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var actionTime)) continue;
                candidates.Add((reader.GetInt64("market_id"), reader.GetInt64("selection_id"),
                    reader.GetString("market_name"), actionTime,
                    reader.IsDBNull("withdrawal_price") ? 0d : reader.GetDouble("withdrawal_price")));
            }
        }

        var output = new List<Rule4MarketPrice>();
        foreach (var candidate in candidates)
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var runner = await LoadRunnerForUpdateAsync(connection, transaction, eventId, candidate.SelectionId, cancellationToken);
            var price = candidate.HistoryPrice;
            var suffix = (int)(candidate.MarketId % 10000);
            if (suffix != RacingMarketCalculator.RaceWinnerSuffix)
            {
                var reconstructed = await ReconstructMarketPriceAtWithdrawalAsync(connection, transaction, runner,
                    candidate.MarketId, candidate.ActionTime, cancellationToken);
                if (ValidPrice(reconstructed)) price = reconstructed;
            }
            var deduction = Rule4Calculator.FromDecimalOdds(price);
            output.Add(new Rule4MarketPrice(candidate.MarketName, price, deduction));
            await using var update = new MySqlCommand("""
                UPDATE spuno_selections SET rule4_deduction=@deduction
                WHERE event_id=@eventId AND market_id=@marketId AND selection_id=@selectionId AND scratch=1;
                UPDATE web_market_adjustments SET price_at_withdrawal=@price,deduction=@deduction
                WHERE event_id=@eventId AND market_id=@marketId AND withdrawn_selection_id=@selectionId
                  AND adjustment_type='RULE4';
                """, connection, transaction);
            update.Parameters.AddWithValue("@eventId", eventId);
            update.Parameters.AddWithValue("@marketId", candidate.MarketId);
            update.Parameters.AddWithValue("@selectionId", candidate.SelectionId);
            update.Parameters.AddWithValue("@price", price);
            update.Parameters.AddWithValue("@deduction", deduction);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return output;
    }

    /// <summary>
    /// Applies Rule 4 to a withdrawn runner, per market.
    ///
    /// Greyhounds: this is the NO-RESERVE path. When a reserve takes the vacated trap the
    /// dog is not withdrawn at all - use <see cref="ReplaceGreyhoundAsync"/>, which renames
    /// the trap and applies no deduction. A dog withdrawn with no reserve leaves the race
    /// short and does attract Rule 4, exactly as a horse does.
    /// </summary>
    /// <param name="allowRepair">
    /// When true an already-withdrawn runner is re-processed, so explicit feed data can
    /// correct an earlier automatic withdrawal. When false (the default, and what the trader
    /// UI and the automatic worker use) an already-withdrawn runner is a no-op: re-running it
    /// would move rule4_time forward, recompute the deduction from a price the first
    /// withdrawal's reprice has already shortened, and overwrite the original
    /// web_market_adjustments row.
    /// </param>
    public async Task<RunnerActionReceipt> WithdrawHorseAsync(
        long eventId,
        long selectionId,
        string? reason,
        string source,
        CancellationToken cancellationToken,
        DateTime? detectedAtUtc = null,
        bool allowRepair = false)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var runner = await LoadRunnerForUpdateAsync(connection, transaction, eventId, selectionId, cancellationToken);
        // Sport 4 (greyhounds) is in scope: the candidate query that feeds this method has
        // always selected sport_id IN (2,4), so refusing 4 here made every dog non-runner
        // throw part-way through its race's pricing pass, abandoning the rest of that pass
        // on every cycle.
        if (runner.SportId is not (2 or 4))
            throw new InvalidOperationException("Rule 4 withdrawal is only available for horse and greyhound racing.");

        // Checked under the FOR UPDATE row lock, so two overlapping passes cannot both apply
        // the same withdrawal.
        if (runner.Scratch && !allowRepair)
        {
            var existing = await LoadAppliedWithdrawalAsync(connection, transaction, eventId, runner, cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogInformation(
                    "Rule 4 withdrawal for event {EventId} selection {SelectionId} ({Runner}) was already applied; ignoring duplicate from {Source}.",
                    eventId, selectionId, runner.Name, source);
                return existing;
            }
        }

        // Rule 4 applies only BEFORE coming under starter's orders. Status 2 (suspended) and
        // 3 (resulted) are past that point.
        if (runner.EventStatus is not ("0" or "1"))
            throw new InvalidOperationException(
                $"Rule 4 withdrawal is not available once the race is off or resulted (event status {runner.EventStatus}).");

        // Only ZERO remaining runners is refused - always a feed error. One remaining runner
        // is a walkover: unusual but legitimate, and blocking it would leave a real
        // non-runner priced and throw on every feed cycle. This matters more for a six-dog
        // card than a fourteen-horse one.
        var liveRunners = await CountLiveRaceWinnerRunnersAsync(
            connection, transaction, eventId, runner, cancellationToken);
        if (liveRunners < 1)
            throw new InvalidOperationException(
                $"Refusing to withdraw {runner.Name}: no live runner would remain in event {eventId}.");
        if (liveRunners == 1)
            _logger.LogWarning(
                "Withdrawing {Runner} leaves a single live runner in event {EventId} (walkover?); Rule 4 still applied.",
                runner.Name, eventId);

        var now = DateTime.UtcNow;
        var actionTime = detectedAtUtc.HasValue
            ? detectedAtUtc.Value.Kind == DateTimeKind.Utc
                ? detectedAtUtc.Value
                : detectedAtUtc.Value.ToUniversalTime()
            : now;
        if (actionTime > now) actionTime = now;
        var runnerMarkets = await LoadRunnerMarketPricesForUpdateAsync(connection, transaction, runner, cancellationToken);
        var price = ValidPrice(runner.Price) ? runner.Price : runner.PreviousPrice;
        if (!ValidPrice(price))
            price = await LoadLastRaceWinnerPriceAsync(connection, transaction, runner, actionTime, cancellationToken);
        var deduction = Math.Min(Rule4Calculator.MaximumTotalDeduction, Rule4Calculator.FromDecimalOdds(price));
        // What actually landed on the Race Winner market after capping. The action log and
        // the receipt must report the applied figure, not the raw scale lookup.
        var raceWinnerDeduction = deduction;
        var matchSql = RunnerMatchSql(runner);
        var rule4MarketPrices = new List<Rule4MarketPrice>();

        foreach (var market in runnerMarkets)
        {
            var suffix = (int)(market.MarketId % 10000);
            if (suffix is >= 9100 and <= 9199)
            {
                await VoidMarketAsync(connection, transaction, eventId, market.MarketId, runner, market,
                    "MATCH_VOID", "Match Bet runner withdrawn", actionTime, source, cancellationToken);
                await using var voidPair = new MySqlCommand("""
                    UPDATE web_match_bet_pairs
                    SET voided = 1, void_reason = @reason, voided_at_utc = @time, updated_at_utc = @time
                    WHERE event_id = @eventId AND market_suffix = @suffix;
                    """, connection, transaction);
                voidPair.Parameters.AddWithValue("@reason", $"{runner.Name} withdrawn");
                voidPair.Parameters.AddWithValue("@time", actionTime);
                voidPair.Parameters.AddWithValue("@eventId", eventId);
                voidPair.Parameters.AddWithValue("@suffix", suffix);
                await voidPair.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            if (suffix is >= RacingMarketCalculator.ForecastFirstSuffix and <= RacingMarketCalculator.ForecastLastSuffix)
            {
                await VoidMarketAsync(connection, transaction, eventId, market.MarketId, runner, market,
                    "FORECAST_VOID", "Forecast runner withdrawn", actionTime, source, cancellationToken);
                await using var voidForecast = new MySqlCommand("""
                    UPDATE web_forecast_pairs
                    SET voided=1,void_reason=@reason,voided_at_utc=@time,updated_at_utc=@time
                    WHERE event_id=@eventId AND market_suffix=@suffix;
                    """, connection, transaction);
                voidForecast.Parameters.AddWithValue("@reason", $"{runner.Name} withdrawn");
                voidForecast.Parameters.AddWithValue("@time", actionTime);
                voidForecast.Parameters.AddWithValue("@eventId", eventId);
                voidForecast.Parameters.AddWithValue("@suffix", suffix);
                await voidForecast.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            if (suffix is RacingMarketCalculator.Index502510Suffix or RacingMarketCalculator.Index50302010Suffix)
            {
                var indexUpdateSql = $"""
                    UPDATE spuno_selections
                    SET scratch=1, active=0, display=0, rule4_time=@rule4Time,
                        rule4_deduction=0, result_text='99'
                    WHERE event_id=@eventId AND market_id=@marketId AND {matchSql};
                    """;
                await using var indexUpdate = new MySqlCommand(indexUpdateSql, connection, transaction);
                AddRunnerParameters(indexUpdate, runner);
                indexUpdate.Parameters.AddWithValue("@marketId", market.MarketId);
                indexUpdate.Parameters.AddWithValue("@rule4Time", actionTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                await indexUpdate.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            var marketPrice = ValidPrice(market.Price) ? market.Price : market.PreviousPrice;
            if (!ValidPrice(marketPrice))
                marketPrice = await LoadLastMarketPriceAsync(connection, transaction, eventId, market.MarketId,
                    runner, actionTime, cancellationToken);
            var marketBook = 0d;
            if (suffix != RacingMarketCalculator.RaceWinnerSuffix)
            {
                var (reconstructedPrice, reconstructedBook) = await ReconstructMarketAtWithdrawalAsync(
                    connection, transaction, runner, market.MarketId, actionTime, cancellationToken);
                if (ValidPrice(reconstructedPrice))
                {
                    marketPrice = reconstructedPrice;
                    marketBook = reconstructedBook;
                }
            }

            // Rule 4 is market-specific: use this runner's price in that market. Which scale
            // applies depends on the market's BOOK, not its name, so a new market suffix
            // cannot silently get the wrong one:
            //   book ~= 1.0  (Race Winner, Betting Without) -> Tattersalls win scale
            //   book  > 1.0  (Top 2/3/4, Insure, Greyhound Groups, trap products) -> share
            // Applying the win scale to a price drawn from a 200/300/400% book treats a share
            // of several places as a share of one and over-deducts several fold. Greyhound
            // Groups is a 300% book, so this matters more here than for horses.
            var marketDeduction = marketBook > 1.05
                ? Rule4Calculator.FromMarketBookShare(marketPrice, marketBook)
                : Math.Min(Rule4Calculator.MaximumTotalDeduction, Rule4Calculator.FromDecimalOdds(marketPrice));

            // Total deductions for a market must not exceed 90p when two or more runners are
            // withdrawn. The cap has to hold on the persisted and published per-selection
            // values, not only in the board's display code.
            var alreadyDeducted = await LoadAppliedMarketDeductionAsync(
                connection, transaction, eventId, market.MarketId, runner, cancellationToken);
            var headroom = Math.Max(0d, Rule4Calculator.MaximumTotalDeduction - alreadyDeducted);
            if (marketDeduction > headroom)
            {
                _logger.LogInformation(
                    "Rule 4 for event {EventId} market {MarketId} capped from {Raw:0.00} to {Capped:0.00}; {Applied:0.00} already deducted.",
                    eventId, market.MarketId, marketDeduction, headroom, alreadyDeducted);
                marketDeduction = headroom;
            }

            if (suffix == RacingMarketCalculator.RaceWinnerSuffix) raceWinnerDeduction = marketDeduction;
            rule4MarketPrices.Add(new Rule4MarketPrice(market.MarketName, marketPrice, marketDeduction));
            var updateSql = $"""
                UPDATE spuno_selections
                SET scratch = 1, active = 0, display = 1,
                    rule4_time = @rule4Time, rule4_deduction = @deduction,
                    result_text = '99'
                WHERE event_id = @eventId AND market_id = @marketId AND {matchSql};
                """;
            await using (var update = new MySqlCommand(updateSql, connection, transaction))
            {
                AddRunnerParameters(update, runner);
                update.Parameters.AddWithValue("@marketId", market.MarketId);
                update.Parameters.AddWithValue("@rule4Time", actionTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                update.Parameters.AddWithValue("@deduction", marketDeduction);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertMarketAdjustmentAsync(connection, transaction, eventId, market.MarketId, market.MarketName,
                runner, "RULE4", marketPrice, marketDeduction, actionTime, source, cancellationToken);
        }

        var excludedWithoutMarkets = new List<(long MarketId, int Suffix)>();
        await using (var excludedCheck = new MySqlCommand("""
            SELECT @eventId * 10000 + market_suffix AS market_id, market_suffix
            FROM web_betting_without_markets
            WHERE event_id=@eventId AND excluded_selection_id=@selectionId AND enabled=1;
            """, connection, transaction))
        {
            excludedCheck.Parameters.AddWithValue("@eventId", eventId);
            excludedCheck.Parameters.AddWithValue("@selectionId", selectionId);
            await using var reader = await excludedCheck.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                excludedWithoutMarkets.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        foreach (var excludedMarket in excludedWithoutMarkets)
        {
            var bettingWithout = new RunnerMarketPrice(excludedMarket.MarketId, $"Betting Without {runner.Name}", 0, 0);
            await VoidMarketAsync(connection, transaction, eventId, excludedMarket.MarketId, runner, bettingWithout,
                "MARKET_VOID", "Excluded Betting Without runner withdrawn", actionTime, source, cancellationToken);
            await using var voidWithout = new MySqlCommand("""
                UPDATE web_betting_without_markets
                SET voided=1, void_reason=@reason, voided_at_utc=@time, updated_at_utc=@time
                WHERE event_id=@eventId AND market_suffix=@suffix;
                """, connection, transaction);
            voidWithout.Parameters.AddWithValue("@reason", $"{runner.Name} withdrawn");
            voidWithout.Parameters.AddWithValue("@time", actionTime);
            voidWithout.Parameters.AddWithValue("@eventId", eventId);
            voidWithout.Parameters.AddWithValue("@suffix", excludedMarket.Suffix);
            await voidWithout.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateRawRunnerAsync(connection, transaction, runner, true, null,
            $"Withdrawn via {source}: {reason ?? "Non-runner"}", cancellationToken);
        await InsertActionAsync(connection, transaction, runner, "WITHDRAW", null, reason, price, raceWinnerDeduction, actionTime, source, cancellationToken);
        await SetRunnerStateAsync(connection, transaction, eventId, selectionId, true, cancellationToken);
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RunnerActionReceipt(eventId, selectionId, "WITHDRAW", runner.Name, null, price, raceWinnerDeduction, actionTime,
            rule4MarketPrices.OrderBy(item => item.MarketName == "Race Winner" ? 0 : 1).ThenBy(item => item.MarketName).ToList());
    }

    /// <summary>
    /// Live Race Winner runners that would remain if this runner were withdrawn. Counted
    /// inside the caller's transaction so it reflects withdrawals committed by a concurrent
    /// pass, not a stale pre-read.
    /// </summary>
    private static async Task<int> CountLiveRaceWinnerRunnersAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        RunnerState runner,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*)
            FROM spuno_selections
            WHERE event_id = @eventId AND market_name = 'Race Winner'
              AND COALESCE(scratch, 0) = 0
              AND NOT COALESCE({RunnerMatchSql(runner)}, FALSE);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        AddRunnerParameters(command, runner);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Total RULE4 deduction already applied to OTHER withdrawn runners in this market.
    /// Used to hold the market's running total at or below the 90p cap.
    /// </summary>
    private static async Task<double> LoadAppliedMarketDeductionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        long marketId,
        RunnerState runner,
        CancellationToken cancellationToken)
    {
        // COALESCE around the match predicate so rows with a NULL competitor_id are treated
        // as "not this runner" and stay in the sum, rather than evaluating to NULL and
        // silently dropping out of it.
        var sql = $"""
            SELECT COALESCE(SUM(rule4_deduction), 0)
            FROM spuno_selections
            WHERE event_id = @eventId AND market_id = @marketId
              AND scratch = 1 AND rule4_deduction > 0
              AND NOT COALESCE({RunnerMatchSql(runner)}, FALSE);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        AddRunnerParameters(command, runner);
        command.Parameters.AddWithValue("@marketId", marketId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rebuilds the receipt for a withdrawal that has already been applied, so a duplicate
    /// request is answered from stored state instead of re-deducting. Returns null when the
    /// runner is scratched but carries no Rule 4 record, which is a feed scratch rather than
    /// a priced withdrawal and should proceed normally.
    /// </summary>
    private static async Task<RunnerActionReceipt?> LoadAppliedWithdrawalAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        RunnerState runner,
        CancellationToken cancellationToken)
    {
        var prices = new List<Rule4MarketPrice>();
        var appliedAt = DateTime.MinValue;
        var raceWinnerDeduction = 0d;
        var raceWinnerPrice = 0d;

        await using (var command = new MySqlCommand("""
            SELECT market_name, market_id, price_at_withdrawal, deduction, applied_at_utc
            FROM web_market_adjustments
            WHERE event_id = @eventId AND withdrawn_selection_id = @selectionId
              AND adjustment_type = 'RULE4';
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var marketName = reader.GetString("market_name");
                var marketId = reader.GetInt64("market_id");
                var price = reader.GetDouble("price_at_withdrawal");
                var value = reader.GetDouble("deduction");
                prices.Add(new Rule4MarketPrice(marketName, price, value));
                var stamp = reader.GetDateTime("applied_at_utc");
                if (stamp > appliedAt) appliedAt = stamp;
                if (marketId % 10000 == RacingMarketCalculator.RaceWinnerSuffix)
                {
                    raceWinnerDeduction = value;
                    raceWinnerPrice = price;
                }
            }
        }
        if (prices.Count == 0) return null;

        return new RunnerActionReceipt(eventId, runner.SelectionId, "WITHDRAW", runner.Name, null,
            raceWinnerPrice, raceWinnerDeduction,
            DateTime.SpecifyKind(appliedAt, DateTimeKind.Utc),
            prices.OrderBy(item => item.MarketName == "Race Winner" ? 0 : 1)
                .ThenBy(item => item.MarketName).ToList());
    }

    private static async Task<List<RunnerMarketPrice>> LoadRunnerMarketPricesForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState runner,
        CancellationToken cancellationToken)
    {
        var matchSql = RunnerMatchSql(runner);
        var sql = $"""
            SELECT s.market_id,
                   COALESCE(NULLIF(m.mkt_name, ''), NULLIF(s.market_name, ''), 'Market') AS market_name,
                   COALESCE(s.price_back, 0) AS price_back,
                   COALESCE(s.price_back_last, 0) AS price_back_last
            FROM spuno_selections s
            LEFT JOIN spuno_markets m ON m.market_id = s.market_id
            WHERE s.event_id = @eventId AND {matchSql}
            FOR UPDATE;
            """;
        var output = new List<RunnerMarketPrice>();
        await using var command = new MySqlCommand(sql, connection, transaction);
        AddRunnerParameters(command, runner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new RunnerMarketPrice(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3)));
        return output;
    }

    private static async Task<double> LoadLastRaceWinnerPriceAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState runner,
        DateTime actionTime,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT h.price
            FROM web_price_history h
            JOIN spuno_selections s
              ON s.event_id=h.event_id AND s.market_id=h.market_id AND s.selection_id=h.selection_id
            WHERE h.event_id=@eventId AND s.market_name='Race Winner'
              AND h.recorded_at_utc<=@actionTime
              AND h.price>1 AND h.price<1000
              AND (s.selection_id=@selectionId
                   OR (@competitorId IS NOT NULL AND s.competitor_id=@competitorId)
                   OR (s.identifier=@identifier AND s.name=@name))
            ORDER BY h.recorded_at_utc DESC
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", runner.EventId);
        command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
        command.Parameters.AddWithValue("@competitorId", runner.CompetitorId is null ? DBNull.Value : runner.CompetitorId);
        command.Parameters.AddWithValue("@identifier", runner.Identifier);
        command.Parameters.AddWithValue("@name", runner.Name);
        command.Parameters.AddWithValue("@actionTime", actionTime);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static async Task<double> LoadLastMarketPriceAsync(
        MySqlConnection connection, MySqlTransaction transaction, long eventId, long marketId,
        RunnerState runner, DateTime actionTime, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT h.price
            FROM web_price_history h
            JOIN spuno_selections s
              ON s.event_id=h.event_id AND s.market_id=h.market_id AND s.selection_id=h.selection_id
            WHERE h.event_id=@eventId AND h.market_id=@marketId AND h.recorded_at_utc<=@actionTime
              AND h.price>1 AND h.price<1000
              AND (s.selection_id=@selectionId
                   OR (@competitorId IS NOT NULL AND s.competitor_id=@competitorId)
                   OR (s.identifier=@identifier AND s.name=@name))
            ORDER BY h.recorded_at_utc DESC LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@marketId", marketId);
        command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
        command.Parameters.AddWithValue("@competitorId", runner.CompetitorId is null ? DBNull.Value : runner.CompetitorId);
        command.Parameters.AddWithValue("@identifier", runner.Identifier);
        command.Parameters.AddWithValue("@name", runner.Name);
        command.Parameters.AddWithValue("@actionTime", actionTime);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private async Task<double> ReconstructMarketPriceAtWithdrawalAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState withdrawnRunner,
        long targetMarketId,
        DateTime actionTime,
        CancellationToken cancellationToken) =>
        (await ReconstructMarketAtWithdrawalAsync(connection, transaction, withdrawnRunner, targetMarketId,
            actionTime, cancellationToken)).Price;

    /// <summary>
    /// Rebuilds the target market as it stood immediately before this withdrawal and returns
    /// both the withdrawn runner's price in it and the market's total book. The book is what
    /// makes a place-market deduction computable: see
    /// <see cref="Rule4Calculator.FromMarketBookShare"/>.
    /// </summary>
    private async Task<(double Price, double Book)> ReconstructMarketAtWithdrawalAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState withdrawnRunner,
        long targetMarketId,
        DateTime actionTime,
        CancellationToken cancellationToken)
    {
        PricingEvent? racingEvent = null;
        await using (var eventCommand = new MySqlCommand("""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,COALESCE(NULLIF(status,''),'0')
            FROM spuno_events WHERE event_id=@eventId LIMIT 1;
            """, connection, transaction))
        {
            eventCommand.Parameters.AddWithValue("@eventId", withdrawnRunner.EventId);
            await using var reader = await eventCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                racingEvent = new PricingEvent(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetString(5), 0);
        }
        if (racingEvent is null) return (0d, 0d);

        var historicalRunners = new List<PricingRunner>();
        await using (var runnerCommand = new MySqlCommand("""
            SELECT s.event_id,s.market_id,s.selection_id,s.competitor_id,s.name,s.identifier,
                   COALESCE((
                       SELECT h.price FROM web_price_history h
                       WHERE h.event_id=s.event_id AND h.market_id=s.market_id AND h.selection_id=s.selection_id
                         AND h.recorded_at_utc<=@actionTime AND h.price>1 AND h.price<1000
                       ORDER BY h.recorded_at_utc DESC LIMIT 1
                   ),0) AS historical_price,
                   s.rule4_time,s.asset_url
            FROM spuno_selections s
            WHERE s.event_id=@eventId AND s.market_name='Race Winner'
            ORDER BY CAST(NULLIF(s.identifier,'') AS UNSIGNED),s.name;
            """, connection, transaction))
        {
            runnerCommand.Parameters.AddWithValue("@eventId", withdrawnRunner.EventId);
            runnerCommand.Parameters.AddWithValue("@actionTime", actionTime);
            await using var reader = await runnerCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var selectionId = reader.GetInt64("selection_id");
                var price = reader.GetDouble("historical_price");
                if (!ValidPrice(price)) continue;
                var rule4Time = reader.IsDBNull("rule4_time") ? null : reader.GetString("rule4_time");
                var competitorId = reader.IsDBNull("competitor_id") ? (long?)null : reader.GetInt64("competitor_id");
                var identifier = reader.IsDBNull("identifier") ? "" : reader.GetString("identifier");
                var name = reader.GetString("name");
                var isTargetRunner = (withdrawnRunner.CompetitorId is not null && competitorId == withdrawnRunner.CompetitorId) ||
                    (identifier == withdrawnRunner.Identifier &&
                     BetfairMarketMatcher.NormalizeRunner(name) == BetfairMarketMatcher.NormalizeRunner(withdrawnRunner.Name));
                var withdrawnBeforeAction = !isTargetRunner &&
                    DateTime.TryParse(rule4Time, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var priorWithdrawal) &&
                    priorWithdrawal <= actionTime;
                historicalRunners.Add(new PricingRunner
                {
                    EventId = reader.GetInt64("event_id"),
                    MarketId = reader.GetInt64("market_id"),
                    SelectionId = selectionId,
                    CompetitorId = competitorId,
                    MarketName = "Race Winner",
                    Name = name,
                    Identifier = identifier,
                    Price = price,
                    PreviousPrice = price,
                    Active = !withdrawnBeforeAction,
                    Display = !withdrawnBeforeAction,
                    Scratch = withdrawnBeforeAction,
                    AssetUrl = reader.IsDBNull("asset_url") ? null : reader.GetString("asset_url")
                });
            }
        }
        if (historicalRunners.Count < 2) return (0d, 0d);

        var withoutMarkets = new List<PricingBettingWithout>();
        await using (var configCommand = new MySqlCommand("""
            SELECT excluded_selection_id,market_suffix,display_order,enabled,voided
            FROM web_betting_without_markets
            WHERE event_id=@eventId AND (enabled=1 OR voided=1);
            """, connection, transaction))
        {
            configCommand.Parameters.AddWithValue("@eventId", withdrawnRunner.EventId);
            await using var reader = await configCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                withoutMarkets.Add(new PricingBettingWithout(reader.GetInt64(0), reader.GetInt32(1),
                    reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        }
        var configuration = new PricingConfiguration(true, true, true, true, false, false, null, null,
            withoutMarkets, [], []);
        var market = _marketCalculator.Build(racingEvent, historicalRunners, configuration)
            .FirstOrDefault(candidate => candidate.MarketId == targetMarketId);
        var selection = market?.Selections.FirstOrDefault(candidate =>
            (withdrawnRunner.CompetitorId is not null && candidate.CompetitorId == withdrawnRunner.CompetitorId) ||
            (candidate.Identifier == withdrawnRunner.Identifier &&
             BetfairMarketMatcher.NormalizeRunner(candidate.Name) == BetfairMarketMatcher.NormalizeRunner(withdrawnRunner.Name)));
        _logger.LogWarning(
            "Rule 4 reconstruction event {EventId} market {MarketId} target {Runner}/{CompetitorId}/{Identifier}; inputs [{Inputs}]; outputs [{Outputs}]; selected {Selected}",
            withdrawnRunner.EventId, targetMarketId, withdrawnRunner.Name, withdrawnRunner.CompetitorId,
            withdrawnRunner.Identifier,
            string.Join("; ", historicalRunners.Select(item =>
                $"{item.Name}/{item.CompetitorId}/{item.Identifier}={item.Price:0.####},scratch={item.Scratch}")),
            string.Join("; ", market?.Selections.Select(item =>
                $"{item.Name}/{item.CompetitorId}/{item.Identifier}={item.PriceBack:0.####}") ?? []),
            selection?.PriceBack);
        if (selection is null || !ValidPrice(selection.PriceBack)) return (0d, 0d);

        // The book this runner was part of. Scratched runners carry no stake and are
        // excluded; the withdrawn runner itself is still live in this reconstruction and is
        // correctly included.
        var book = market!.Selections
            .Where(item => item.Scratch == 0 && ValidPrice(item.PriceBack))
            .Sum(item => 1d / item.PriceBack);
        return (selection.PriceBack, book);
    }

    private static async Task InsertMarketAdjustmentAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        long marketId,
        string marketName,
        RunnerState runner,
        string adjustmentType,
        double price,
        double deduction,
        DateTime actionTime,
        string source,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO web_market_adjustments (
                event_id, market_id, market_name, withdrawn_selection_id, runner_name,
                adjustment_type, price_at_withdrawal, deduction, applied_at_utc, action_source
            ) VALUES (
                @eventId, @marketId, @marketName, @selectionId, @runnerName,
                @type, @price, @deduction, @time, @source
            ) ON DUPLICATE KEY UPDATE
                market_name = VALUES(market_name), runner_name = VALUES(runner_name),
                price_at_withdrawal = VALUES(price_at_withdrawal), deduction = VALUES(deduction),
                applied_at_utc = VALUES(applied_at_utc), action_source = VALUES(action_source);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@marketId", marketId);
        command.Parameters.AddWithValue("@marketName", marketName);
        command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
        command.Parameters.AddWithValue("@runnerName", runner.Name);
        command.Parameters.AddWithValue("@type", adjustmentType);
        command.Parameters.AddWithValue("@price", price);
        command.Parameters.AddWithValue("@deduction", deduction);
        command.Parameters.AddWithValue("@time", actionTime);
        command.Parameters.AddWithValue("@source", source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VoidMarketAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        long marketId,
        RunnerState runner,
        RunnerMarketPrice market,
        string adjustmentType,
        string reason,
        DateTime actionTime,
        string source,
        CancellationToken cancellationToken)
    {
        await using (var selections = new MySqlCommand("""
            UPDATE spuno_selections
            SET result_text = '99', active = 0, display = 1
            WHERE event_id = @eventId AND market_id = @marketId;
            """, connection, transaction))
        {
            selections.Parameters.AddWithValue("@eventId", eventId);
            selections.Parameters.AddWithValue("@marketId", marketId);
            await selections.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var marketUpdate = new MySqlCommand("""
            UPDATE spuno_markets SET active = 0, display = 1
            WHERE event_id = @eventId AND market_id = @marketId;
            """, connection, transaction))
        {
            marketUpdate.Parameters.AddWithValue("@eventId", eventId);
            marketUpdate.Parameters.AddWithValue("@marketId", marketId);
            await marketUpdate.ExecuteNonQueryAsync(cancellationToken);
        }
        var withdrawnSelectionSql = $"""
            UPDATE spuno_selections
            SET scratch=1, active=0, display=1,
                rule4_time=@rule4Time, rule4_deduction=0, result_text='99'
            WHERE event_id=@eventId AND market_id=@marketId AND {RunnerMatchSql(runner)};
            """;
        await using (var withdrawnSelection = new MySqlCommand(withdrawnSelectionSql, connection, transaction))
        {
            AddRunnerParameters(withdrawnSelection, runner);
            withdrawnSelection.Parameters.AddWithValue("@marketId", marketId);
            withdrawnSelection.Parameters.AddWithValue("@rule4Time", actionTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            await withdrawnSelection.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertMarketAdjustmentAsync(connection, transaction, eventId, marketId, market.MarketName,
            runner, adjustmentType, ValidPrice(market.Price) ? market.Price : market.PreviousPrice,
            0, actionTime, source, cancellationToken);
        _ = reason;
    }

    public async Task<RunnerActionReceipt> ReplaceGreyhoundAsync(
        long eventId,
        long selectionId,
        string replacementName,
        double? replacementPrice,
        CancellationToken cancellationToken)
    {
        replacementName = replacementName.Trim();
        if (replacementName.Length is < 2 or > 200) throw new ArgumentException("Enter a replacement dog name.");
        // A reserve taking the vacated trap is NOT a withdrawal: the race still has a full
        // field, so no Rule 4 applies (the rule4 columns are cleared below). Mark the name so
        // the trader and Sportuno can see a reserve ran. "res" is in
        // BetfairMarketMatcher.CountrySuffixes, so NormalizeRunner strips the suffix and it
        // cannot break fingerprint matching, Betfair pairing or non-runner matching against
        // providers, which all report the bare name.
        if (!replacementName.EndsWith(ReserveNameSuffix, StringComparison.OrdinalIgnoreCase))
            replacementName += ReserveNameSuffix;
        if (replacementPrice is <= 1d) throw new ArgumentException("Replacement price must be greater than 1.00.");

        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var runner = await LoadRunnerForUpdateAsync(connection, transaction, eventId, selectionId, cancellationToken);
        if (runner.SportId != 4) throw new InvalidOperationException("Dog replacement is only available for greyhound racing.");

        var matchSql = RunnerMatchSql(runner);
        var priceSql = replacementPrice.HasValue
            ? ", price_back = @price, price_lay = @price, price_back_last = @price"
            : "";
        var updateSql = $"""
            UPDATE spuno_selections
            SET name = @newName, scratch = 0, active = 1, display = 1,
                rule4_time = NULL, rule4_deduction = 0 {priceSql}
            WHERE event_id = @eventId AND {matchSql};
            """;
        await using (var update = new MySqlCommand(updateSql, connection, transaction))
        {
            AddRunnerParameters(update, runner);
            update.Parameters.AddWithValue("@newName", replacementName);
            if (replacementPrice.HasValue) update.Parameters.AddWithValue("@price", replacementPrice.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateRawRunnerAsync(connection, transaction, runner, false, replacementName, null, cancellationToken);
        var actionTime = DateTime.UtcNow;
        await InsertActionAsync(connection, transaction, runner, "REPLACE", replacementName, null,
            replacementPrice ?? runner.Price, 0d, actionTime, "WEB", cancellationToken);
        await MarkEventForRepriceAsync(connection, transaction, eventId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RunnerActionReceipt(eventId, selectionId, "REPLACE", runner.Name, replacementName,
            replacementPrice ?? runner.Price, 0d, actionTime);
    }

    public async Task<int> CapturePriceHistoryAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO web_price_history (
                event_id, market_id, selection_id, price, recorded_at_utc
            )
            SELECT s.event_id, s.market_id, s.selection_id, s.price_back, UTC_TIMESTAMP(6)
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id = s.event_id
            WHERE e.send_to_sp <> 0
              AND e.status IN ('0', '1')
              AND e.start_time_unix BETWEEN
                  UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
                  AND UNIX_TIMESTAMP(UTC_TIMESTAMP() + INTERVAL 48 HOUR)
              AND MOD(s.market_id,10000) NOT IN (9009,9010)
              AND s.price_back > 1 AND s.price_back < 1000
              AND COALESCE((
                  SELECT h.price
                  FROM web_price_history h
                  WHERE h.event_id = s.event_id
                    AND h.market_id = s.market_id
                    AND h.selection_id = s.selection_id
                  ORDER BY h.recorded_at_utc DESC
                  LIMIT 1
              ), -1) <> s.price_back;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> ApplyPendingHorseWithdrawalsAsync(CancellationToken cancellationToken)
    {
        const string baselineSql = """
            INSERT IGNORE INTO web_runner_state (event_id, selection_id, is_withdrawn, last_seen_utc)
            SELECT s.event_id, s.selection_id, s.scratch, UTC_TIMESTAMP(6)
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id = s.event_id AND e.sport_id IN (2, 4)
            WHERE s.market_name = 'Race Winner'
              AND e.start_time_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR);
            """;
        const string observeCandidatesSql = """
            INSERT INTO web_non_runner_candidates (
                event_id, selection_id, reason, first_seen_utc, last_seen_utc, observations
            )
            SELECT
                s.event_id,
                s.selection_id,
                CASE WHEN COALESCE(rawsel.is_non_runner, 0) = 1
                     THEN 'Explicit feed non-runner'
                     ELSE 'Isolated suspension while other runners remain active'
                END,
                UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), 1
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id = s.event_id AND e.sport_id IN (2, 4)
            JOIN (
                SELECT event_id,
                       SUM(CASE WHEN active <> 0 AND scratch = 0 THEN 1 ELSE 0 END) AS active_count,
                       SUM(CASE WHEN active = 0 AND scratch = 0 THEN 1 ELSE 0 END) AS suspended_count
                FROM spuno_selections
                WHERE market_name = 'Race Winner'
                GROUP BY event_id
            ) counts ON counts.event_id = s.event_id
            LEFT JOIN em_b365_selections_raw rawsel
              ON rawsel.event_id = s.event_id
             AND rawsel.selection_id = COALESCE(s.competitor_id, s.selection_id)
            LEFT JOIN web_runner_state state
              ON state.event_id = s.event_id AND state.selection_id = s.selection_id
            WHERE s.market_name = 'Race Winner'
              AND e.status IN ('0', '1')
              AND e.active <> 0
              AND e.display <> 0
              AND e.start_time_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 1 HOUR)
              AND COALESCE(state.is_withdrawn, 0) = 0
              AND (
                  COALESCE(rawsel.is_non_runner, 0) = 1
                  OR (
                      -- The isolated-suspension heuristic is HORSES ONLY. It was tuned for
                      -- fourteen-runner fields; in a six-dog race one or two traps suspended
                      -- during ordinary price churn is common, so applying it to greyhounds
                      -- would deduct Rule 4 from dogs that are still running. A dog is only
                      -- withdrawn automatically on a confirmed feed non-runner flag.
                      e.sport_id = 2
                      AND s.active = 0 AND s.scratch = 0
                      AND counts.active_count >= 2
                      AND counts.suspended_count BETWEEN 1 AND 2
                  )
              )
            ON DUPLICATE KEY UPDATE
              first_seen_utc = IF(
                  web_non_runner_candidates.reason <> VALUES(reason)
                    OR web_non_runner_candidates.last_seen_utc < UTC_TIMESTAMP(6) - INTERVAL 30 SECOND,
                  UTC_TIMESTAMP(6), web_non_runner_candidates.first_seen_utc),
              observations = IF(
                  web_non_runner_candidates.reason <> VALUES(reason)
                    OR web_non_runner_candidates.last_seen_utc < UTC_TIMESTAMP(6) - INTERVAL 30 SECOND,
                  1, web_non_runner_candidates.observations + 1),
              reason = VALUES(reason),
              last_seen_utc = UTC_TIMESTAMP(6);
            """;
        const string transitionsSql = """
            SELECT s.event_id, s.selection_id, 'Explicit feed non-runner' AS reason
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id = s.event_id AND e.sport_id IN (2, 4)
            JOIN web_runner_state state
              ON state.event_id = s.event_id AND state.selection_id = s.selection_id
            WHERE state.is_withdrawn = 0
              AND s.scratch = 1
              AND s.market_name = 'Race Winner'
              AND e.status IN ('0', '1')
              AND e.start_time_unix >= UNIX_TIMESTAMP(UTC_TIMESTAMP() - INTERVAL 4 HOUR)
            LIMIT 50;
            """;
        const string confirmedCandidatesSql = """
            SELECT c.event_id, c.selection_id, c.reason
            FROM web_non_runner_candidates c
            JOIN web_runner_state state
              ON state.event_id = c.event_id AND state.selection_id = c.selection_id
            WHERE state.is_withdrawn = 0
              AND c.last_seen_utc >= UTC_TIMESTAMP(6) - INTERVAL 15 SECOND
              AND (
                  c.reason = 'Explicit feed non-runner'
                  OR (c.observations >= 3 AND TIMESTAMPDIFF(SECOND, c.first_seen_utc, c.last_seen_utc) >= 20)
              )
            LIMIT 50;
            """;

        var pending = new Dictionary<(long EventId, long SelectionId), string>();
        await using (var connection = OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using (var baseline = new MySqlCommand(baselineSql, connection))
                await baseline.ExecuteNonQueryAsync(cancellationToken);
            await using (var observe = new MySqlCommand(observeCandidatesSql, connection))
                await observe.ExecuteNonQueryAsync(cancellationToken);

            foreach (var sql in new[] { transitionsSql, confirmedCandidatesSql })
            {
                await using var command = new MySqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    pending[(reader.GetInt64(0), reader.GetInt64(1))] = reader.GetString(2);
            }
        }

        var applied = 0;
        foreach (var item in pending)
        {
            try
            {
                await WithdrawHorseAsync(item.Key.EventId, item.Key.SelectionId, item.Value, "AUTO", cancellationToken);
                applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not apply automatic Rule 4 for event {EventId}, selection {SelectionId}",
                    item.Key.EventId, item.Key.SelectionId);
            }
        }
        return applied;
    }

    private static async Task SetRunnerStateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        long selectionId,
        bool withdrawn,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO web_runner_state (event_id, selection_id, is_withdrawn, last_seen_utc)
            VALUES (@eventId, @selectionId, @withdrawn, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
              is_withdrawn = VALUES(is_withdrawn), last_seen_utc = VALUES(last_seen_utc);
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@selectionId", selectionId);
        command.Parameters.AddWithValue("@withdrawn", withdrawn ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkEventForRepriceAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        long eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            UPDATE em_events_raw
            SET spuno_odds_updated_at_unix = 0, spuno_odds_updated_at_utc = NULL
            WHERE betsapi_event_id = @eventId;
            """, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<long, IReadOnlyList<PriceHistoryPoint>>> GetPriceHistoryAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT selection_id, price, recorded_at_utc
            FROM web_price_history
            WHERE event_id = @eventId
              AND recorded_at_utc >= UTC_TIMESTAMP() - INTERVAL 6 HOUR
            ORDER BY selection_id, recorded_at_utc;
            """;
        var points = new Dictionary<long, List<PriceHistoryPoint>>();
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var selectionId = reader.GetInt64(0);
            if (!points.TryGetValue(selectionId, out var runnerPoints))
            {
                runnerPoints = [];
                points[selectionId] = runnerPoints;
            }
            var recorded = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
            runnerPoints.Add(new PriceHistoryPoint(recorded, reader.GetDouble(1)));
        }

        return points.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<PriceHistoryPoint>)item.Value.TakeLast(60).ToList());
    }

    private async Task<EventSummary?> GetEventAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT se.event_id, se.sport_id, COALESCE(NULLIF(se.meeting, ''), 'Unassigned') AS meeting,
                   COALESCE(NULLIF(se.ev_name, ''), CONCAT('Event ', se.event_id)) AS event_name,
                   COALESCE(se.start_time_unix, 0) AS starts_at_unix,
                   COALESCE(NULLIF(se.status, ''), '0') AS status,
                   COALESCE(se.send_to_sp, 0) AS subscribed,
                   COALESCE(se.active, 0) AS active,
                   COALESCE(se.display, 0) AS display,
                   COALESCE(se.in_progress, 0) AS in_progress,
                   (SELECT COUNT(*) FROM web_race_winner_source_prices api_price
                     WHERE api_price.event_id=se.event_id
                       AND api_price.source_name='BETSAPI'
                       AND api_price.has_price=1 AND api_price.decimal_price>1
                       AND NOT EXISTS (
                           SELECT 1 FROM spuno_selections scratched
                           WHERE scratched.event_id=se.event_id
                             AND scratched.competitor_id=api_price.competitor_id
                             AND scratched.market_name='Race Winner'
                             AND scratched.scratch=1)) AS betsapi_priced_runner_count
            FROM spuno_events se WHERE se.event_id = @eventId LIMIT 1;
            """;
        await using var connection = OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@eventId", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var meeting = reader.GetString(2);
        var start = reader.GetInt64(4);
        return new EventSummary(reader.GetInt64(0), reader.GetInt32(1), meeting, RacingEventNames.Canonical(meeting, start),
            start, reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7),
            reader.GetBoolean(8), reader.GetBoolean(9), reader.GetInt32(10));
    }

    private static async Task<RunnerState> LoadRunnerForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        long eventId,
        long selectionId,
        CancellationToken cancellationToken)
    {
        // s.scratch and e.status are read UNDER the row lock so a caller can tell whether
        // this withdrawal has already been applied, and whether the race has gone off,
        // without a second unlocked read.
        const string sql = """
            SELECT s.selection_id, s.competitor_id, s.identifier, s.name,
                   s.price_back, s.price_back_last, e.sport_id,
                   COALESCE(s.scratch, 0) AS scratch,
                   COALESCE(NULLIF(e.status, ''), '0') AS event_status
            FROM spuno_selections s
            JOIN spuno_events e ON e.event_id = s.event_id
            WHERE s.event_id = @eventId AND s.selection_id = @selectionId
            LIMIT 1 FOR UPDATE;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", eventId);
        command.Parameters.AddWithValue("@selectionId", selectionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new KeyNotFoundException("Runner was not found.");
        return new RunnerState(
            eventId,
            reader.GetInt64("selection_id"),
            reader.IsDBNull("competitor_id") ? null : reader.GetInt64("competitor_id"),
            reader.IsDBNull("identifier") ? "" : reader.GetString("identifier"),
            reader.GetString("name"),
            reader.IsDBNull("price_back") ? 0d : reader.GetDouble("price_back"),
            reader.IsDBNull("price_back_last") ? 0d : reader.GetDouble("price_back_last"),
            reader.GetInt32("sport_id"),
            reader.GetBoolean("scratch"),
            reader.GetString("event_status"));
    }

    private static async Task UpdateRawRunnerAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState runner,
        bool withdrawn,
        string? replacementName,
        string? withdrawalReason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE em_b365_selections_raw
            SET is_non_runner = @withdrawn,
                non_runner_reason = @reason,
                selection_name = COALESCE(@replacementName, selection_name)
            WHERE event_id = @eventId AND selection_id = @selectionId;
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@withdrawn", withdrawn ? 1 : 0);
        command.Parameters.AddWithValue("@reason", withdrawn ? withdrawalReason ?? "Withdrawn" : DBNull.Value);
        command.Parameters.AddWithValue("@replacementName", replacementName is null ? DBNull.Value : replacementName);
        command.Parameters.AddWithValue("@eventId", runner.EventId);
        command.Parameters.AddWithValue("@selectionId", runner.CompetitorId ?? runner.SelectionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertActionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        RunnerState runner,
        string action,
        string? newName,
        string? reason,
        double price,
        double deduction,
        DateTime actionTime,
        string source,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO web_runner_actions (
                event_id, selection_id, competitor_id, sport_id, action_type,
                old_name, new_name, reason, price_at_action, rule4_deduction,
                action_time_utc, action_source
            ) VALUES (
                @eventId, @selectionId, @competitorId, @sportId, @action,
                @oldName, @newName, @reason, @price, @deduction,
                @actionTime, @source
            );
            """;
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@eventId", runner.EventId);
        command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
        command.Parameters.AddWithValue("@competitorId", runner.CompetitorId is null ? DBNull.Value : runner.CompetitorId);
        command.Parameters.AddWithValue("@sportId", runner.SportId);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@oldName", runner.Name);
        command.Parameters.AddWithValue("@newName", newName is null ? DBNull.Value : newName);
        command.Parameters.AddWithValue("@reason", reason is null ? DBNull.Value : reason);
        command.Parameters.AddWithValue("@price", price);
        command.Parameters.AddWithValue("@deduction", deduction);
        command.Parameters.AddWithValue("@actionTime", actionTime);
        command.Parameters.AddWithValue("@source", source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string RunnerMatchSql(RunnerState runner) => runner.CompetitorId is > 0
        ? "competitor_id = @competitorId"
        : !string.IsNullOrWhiteSpace(runner.Identifier)
            ? "identifier = @identifier"
            : "name = @oldName";

    private static void AddRunnerParameters(MySqlCommand command, RunnerState runner)
    {
        command.Parameters.AddWithValue("@eventId", runner.EventId);
        command.Parameters.AddWithValue("@selectionId", runner.SelectionId);
        command.Parameters.AddWithValue("@competitorId", runner.CompetitorId is null ? DBNull.Value : runner.CompetitorId);
        command.Parameters.AddWithValue("@identifier", runner.Identifier);
        command.Parameters.AddWithValue("@oldName", runner.Name);
    }

    private static bool ValidPrice(double price) => double.IsFinite(price) && price > 1d && price < 1000d;

    private static int ParseIdentifier(string identifier) =>
        int.TryParse(identifier, out var value) ? value : int.MaxValue;

    private sealed record RunnerState(
        long EventId,
        long SelectionId,
        long? CompetitorId,
        string Identifier,
        string Name,
        double Price,
        double PreviousPrice,
        int SportId,
        bool Scratch = false,
        string EventStatus = "0");

    private sealed record RunnerMarketPrice(
        long MarketId,
        string MarketName,
        double Price,
        double PreviousPrice);

    private sealed record FeedWithdrawalCandidate(
        long SelectionId,
        string Name,
        string Identifier,
        int SportId,
        long SourceRunnerId,
        bool Scratch,
        string LastWithdrawalSource);

    private sealed record SelectionSnapshot(
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
        string? SelectedPriceSource,
        string? ModelPriceSource,
        double? ModelSourcePrice,
        double? SpreadexPrice,
        double? SpreadexBuy,
        double? SpreadexIndex502510Buy,
        double? SpreadexIndex502510Sell,
        DateTime? SpreadexIndex502510UpdatedAtUtc,
        string? SpreadexIndexMarketName,
        DateTime? SpreadexUpdatedAtUtc,
        double Price,
        double PreviousPrice,
        bool Active,
        bool Display,
        bool Withdrawn,
        string? Result,
        int? FinishingPosition,
        string? ResultStatus,
        string? Rule4TimeUtc,
        double Rule4Deduction,
        long MarketId,
        string MarketName,
        int DisplayOrder,
        bool MarketActive,
        bool MarketDisplay);

    private sealed class RunnerAccumulator(long selectionId, long? competitorId, string identifier, string name)
    {
        public long SelectionId { get; set; } = selectionId;
        public long? CompetitorId { get; } = competitorId;
        public string Identifier { get; } = identifier;
        public string Name { get; set; } = name;
        public string? AssetUrl { get; set; }
        public double? BetsApiPrice { get; set; }
        public DateTime? BetsApiUpdatedAtUtc { get; set; }
        public double? BetfredPrice { get; set; }
        public DateTime? BetfredUpdatedAtUtc { get; set; }
        public double? Bet365DirectPrice { get; set; }
        public DateTime? Bet365DirectUpdatedAtUtc { get; set; }
        public double? BetfredIndex502510 { get; set; }
        public double? BetfredIndex50302010 { get; set; }
        public string? SelectedPriceSource { get; set; }
        public string? ModelPriceSource { get; set; }
        public double? ModelSourcePrice { get; set; }
        public string? LadderSource { get; set; }
        public double? SpreadexPrice { get; set; }
        public double? SpreadexBuy { get; set; }
        public double? SpreadexIndex502510Buy { get; set; }
        public double? SpreadexIndex502510Sell { get; set; }
        public DateTime? SpreadexIndex502510UpdatedAtUtc { get; set; }
        public string? SpreadexIndexMarketName { get; set; }
        public DateTime? SpreadexUpdatedAtUtc { get; set; }
        public int? LadderMoves { get; set; }
        public BetfairRunnerPrice? Betfair { get; set; }
        public bool Active { get; set; }
        public bool Display { get; set; }
        public bool Withdrawn { get; set; }
        public string? Result { get; set; }
        public int? FinishingPosition { get; set; }
        public string? ResultStatus { get; set; }
        public string? Rule4TimeUtc { get; set; }
        public double Rule4Deduction { get; set; }
        public Dictionary<string, PriceCell> Prices { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<PriceHistoryPoint> PriceHistory { get; set; } = [];

        public RunnerRow ToModel() => new(SelectionId, CompetitorId, Identifier, Name, AssetUrl,
            Withdrawn ? null : BetsApiPrice, BetsApiUpdatedAtUtc, Withdrawn ? null : BetfredPrice, BetfredUpdatedAtUtc,
            Withdrawn ? null : Bet365DirectPrice, Bet365DirectUpdatedAtUtc,
            BetfredIndex502510, BetfredIndex50302010, SpreadexPrice, SpreadexBuy,
            SpreadexIndex502510Buy, SpreadexIndex502510Sell, SpreadexIndex502510UpdatedAtUtc, SpreadexIndexMarketName, SpreadexUpdatedAtUtc,
            SelectedPriceSource, LadderSource, Withdrawn ? null : ModelSourcePrice, Withdrawn ? null : LadderMoves, Betfair, Active, Display,
            Withdrawn, Result, FinishingPosition, ResultStatus, Rule4TimeUtc, Rule4Deduction, Prices, PriceHistory,
            null, null);
    }
}
