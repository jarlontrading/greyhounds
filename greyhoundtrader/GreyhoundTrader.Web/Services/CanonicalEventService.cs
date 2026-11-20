using System.Text.Json;
using MySqlConnector;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed record ProviderRace(
    string Provider, string ProviderEventId, string Meeting, long StartTimeUnix,
    IReadOnlyList<string> Runners, IReadOnlyDictionary<string, string>? ProviderIds = null);
internal sealed record ProviderLinkIssue(
    long EventId, string Meeting, string EventName, long StartTimeUnix, string State,
    bool HasBet365, bool HasSpreadex, string? SpreadexMatchMethod, double? MatchScore,
    long? CounterpartEventId);

internal sealed class CanonicalEventService(ExternalSettings settings, ILogger<CanonicalEventService> logger)
{
    private const long FirstInternalEventId = 2_000_000_000L;
    private readonly string _connectionString = new MySqlConnectionStringBuilder
    {
        Server = settings.MySqlServer, UserID = settings.MySqlUser, Password = settings.MySqlPassword,
        Database = settings.RacingDatabase, SslMode = MySqlSslMode.Preferred,
        ConnectionTimeout = 8, DefaultCommandTimeout = 30, AllowUserVariables = true,
        MaximumPoolSize = 1, ConnectionIdleTimeout = 30
    }.ConnectionString;

    public async Task InitialiseAsync(CancellationToken ct)
    {
        const string sql = """
        CREATE TABLE IF NOT EXISTS racing_canonical_events (
          event_id BIGINT NOT NULL PRIMARY KEY, sport_id INT NOT NULL, meeting VARCHAR(160) NOT NULL,
          start_time_unix BIGINT NOT NULL, created_from VARCHAR(20) NOT NULL,
          created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
          UNIQUE KEY uq_canonical_race (sport_id,meeting,start_time_unix)
        );
        CREATE TABLE IF NOT EXISTS racing_event_provider_links (
          provider VARCHAR(20) NOT NULL, provider_event_id VARCHAR(100) NOT NULL,
          event_id BIGINT NOT NULL, provider_ids_json JSON NULL, matched_by VARCHAR(40) NOT NULL,
          match_score DECIMAL(6,5) NULL, created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
          PRIMARY KEY(provider,provider_event_id), KEY ix_provider_event(event_id)
        );
        CREATE TABLE IF NOT EXISTS racing_runner_provider_links (
          event_id BIGINT NOT NULL, provider VARCHAR(20) NOT NULL,
          provider_runner_id VARCHAR(100) NOT NULL, normalized_name VARCHAR(180) NOT NULL,
          created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
          PRIMARY KEY(event_id,provider,provider_runner_id), KEY ix_runner_name(event_id,normalized_name)
        );
        CREATE TABLE IF NOT EXISTS racing_match_audit (
          audit_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY, provider VARCHAR(20) NOT NULL,
          provider_event_id VARCHAR(100) NOT NULL, event_id BIGINT NULL, decision VARCHAR(20) NOT NULL,
          reason VARCHAR(255) NOT NULL, match_score DECIMAL(6,5) NULL,
          created_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
        );
        CREATE TABLE IF NOT EXISTS racing_id_sequence (
          sequence_name VARCHAR(40) NOT NULL PRIMARY KEY, next_id BIGINT NOT NULL
        );
        INSERT IGNORE INTO racing_id_sequence(sequence_name,next_id) VALUES('event',2000000000);
        """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FeedEvent>> ReconcileBet365Async(IEnumerable<FeedEvent> events, CancellationToken ct)
    {
        var reconciled = new List<FeedEvent>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        foreach (var ev in events)
        {
            await using var find = new MySqlCommand("""
            SELECT event_id FROM racing_canonical_events
            WHERE sport_id=@sport
              AND LOWER(REPLACE(REPLACE(meeting,' ',''),'-',''))=
                  LOWER(REPLACE(REPLACE(@meeting,' ',''),'-',''))
              AND ABS(start_time_unix-@start)<=90
            ORDER BY ABS(start_time_unix-@start), event_id LIMIT 1;
            """, connection);
            find.Parameters.AddWithValue("@sport", ev.SportId);
            find.Parameters.AddWithValue("@meeting", ev.Meeting);
            find.Parameters.AddWithValue("@start", ev.StartTimeUnix);
            var existing = await find.ExecuteScalarAsync(ct);
            var canonicalId = existing is null ? ev.EventId : Convert.ToInt64(existing);
            await using var command = new MySqlCommand("""
            INSERT IGNORE INTO racing_canonical_events(event_id,sport_id,meeting,start_time_unix,created_from)
            VALUES(@id,@sport,@meeting,@start,'B365');
            INSERT INTO racing_event_provider_links(provider,provider_event_id,event_id,matched_by,match_score)
            VALUES('B365',@providerId,@id,'VENUE_TIME',1)
            ON DUPLICATE KEY UPDATE event_id=VALUES(event_id),matched_by=VALUES(matched_by),match_score=1;
            """, connection);
            command.Parameters.AddWithValue("@id", canonicalId);
            command.Parameters.AddWithValue("@providerId", ev.EventId.ToString());
            command.Parameters.AddWithValue("@sport", ev.SportId);
            command.Parameters.AddWithValue("@meeting", ev.Meeting);
            command.Parameters.AddWithValue("@start", ev.StartTimeUnix);
            await command.ExecuteNonQueryAsync(ct);
            reconciled.Add(ev with { EventId = canonicalId });
        }
        return reconciled.GroupBy(x => x.EventId).Select(x => x.OrderByDescending(e => e.UpdatedAtUnix).First()).ToList();
    }

    public async Task<IReadOnlyList<FeedEvent>> ReconcileSpreadexAsync(
        IReadOnlyList<ProviderRace> races, CancellationToken ct)
    {
        var created = new List<FeedEvent>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        foreach (var race in races.Where(x => x.StartTimeUnix > 0 && x.Runners.Count >= 2))
        {
            var existingLink = await ExistingLinkAsync(connection, race, ct);
            if (existingLink is { HasBet365: true }) continue;
            var candidates = await CandidatesAsync(connection, race, ct);
            // Keep a genuine Spreadex-only race on its existing internal ID.
            // Reconciliation is retried when a Bet365 candidate later appears.
            if (existingLink is not null && candidates.Count == 0) continue;
            var scored = candidates.Select(x => (x.EventId, Score(race.Runners, x.Runners)))
                .OrderByDescending(x => x.Item2).ToList();
            long eventId;
            string decision;
            double score;
            if (scored.Count > 0 && scored[0].Item2 >= .60 &&
                (scored.Count == 1 || scored[0].Item2 - scored[1].Item2 >= .20))
            {
                (eventId, score) = scored[0];
                decision = "RUNNER_MATCH";
            }
            else if (candidates.Count == 1 &&
                     Math.Abs(candidates[0].StartTimeUnix - race.StartTimeUnix) <= 60 &&
                     (candidates[0].Runners.Count == 0 || scored[0].Item2 >= .60))
            {
                eventId = candidates[0].EventId;
                score = scored.FirstOrDefault().Item2;
                decision = "VENUE_TIME";
            }
            else
            {
                eventId = await AllocateAsync(connection, ct);
                score = 0;
                decision = "CREATED";
                created.Add(new FeedEvent(eventId, 2, 0, race.Meeting,
                    RacingEventNames.Canonical(race.Meeting, race.StartTimeUnix), race.StartTimeUnix,
                    0, 0, 0, JsonSerializer.Serialize(race)));
            }
            await SaveAsync(connection, race, eventId, decision, score, ct);
        }
        return created;
    }

    private sealed record ExistingProviderLink(long EventId, bool HasBet365);

    private static async Task<ExistingProviderLink?> ExistingLinkAsync(
        MySqlConnection c, ProviderRace race, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            SELECT link.event_id,
                   EXISTS(
                     SELECT 1 FROM racing_event_provider_links sibling
                     WHERE sibling.event_id=link.event_id AND sibling.provider='B365'
                   )
            FROM racing_event_provider_links link
            WHERE link.provider=@p AND link.provider_event_id=@id
            LIMIT 1;
            """, c);
        cmd.Parameters.AddWithValue("@p", race.Provider); cmd.Parameters.AddWithValue("@id", race.ProviderEventId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ExistingProviderLink(reader.GetInt64(0), reader.GetBoolean(1))
            : null;
    }

    private static async Task<List<(long EventId,long StartTimeUnix,List<string> Runners)>> CandidatesAsync(
        MySqlConnection c, ProviderRace race, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
        SELECT ce.event_id,ce.start_time_unix,
          GROUP_CONCAT(DISTINCT COALESCE(NULLIF(s.name,''),r.normalized_name) SEPARATOR '\n')
        FROM racing_canonical_events ce
        JOIN racing_event_provider_links b365
          ON b365.event_id=ce.event_id AND b365.provider='B365'
        LEFT JOIN spuno_selections s ON s.event_id=ce.event_id AND s.market_name='Race Winner'
        LEFT JOIN racing_runner_provider_links r ON r.event_id=ce.event_id
        WHERE ce.sport_id=2 AND LOWER(REPLACE(REPLACE(ce.meeting,' ',''),'-',''))=
          LOWER(REPLACE(REPLACE(@meeting,' ',''),'-',''))
          AND ABS(ce.start_time_unix-@start)<=180
        GROUP BY ce.event_id,ce.start_time_unix
        """, c);
        cmd.Parameters.AddWithValue("@meeting", race.Meeting); cmd.Parameters.AddWithValue("@start", race.StartTimeUnix);
        var result = new List<(long,long,List<string>)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add((reader.GetInt64(0),reader.GetInt64(1),
                reader.IsDBNull(2)?[]:reader.GetString(2).Split('\n').ToList()));
        return result;
    }

    private static double Score(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var left=a.Select(BetfairMarketMatcher.NormalizeRunner).Where(x=>x.Length>0).ToHashSet();
        var right=b.Select(BetfairMarketMatcher.NormalizeRunner).Where(x=>x.Length>0).ToHashSet();
        return left.Count==0||right.Count==0 ? 0 : 2d*left.Intersect(right).Count()/(left.Count+right.Count);
    }

    private static async Task<long> AllocateAsync(MySqlConnection c, CancellationToken ct)
    {
        await using var tx=await c.BeginTransactionAsync(ct);
        await using var read=new MySqlCommand("SELECT next_id FROM racing_id_sequence WHERE sequence_name='event' FOR UPDATE",c,tx);
        var id=Convert.ToInt64(await read.ExecuteScalarAsync(ct));
        if(id<FirstInternalEventId) id=FirstInternalEventId;
        await using var update=new MySqlCommand("UPDATE racing_id_sequence SET next_id=@next WHERE sequence_name='event'",c,tx);
        update.Parameters.AddWithValue("@next",id+1); await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct); return id;
    }

    private static async Task SaveAsync(MySqlConnection c, ProviderRace race, long eventId, string decision, double score, CancellationToken ct)
    {
        await using var cmd=new MySqlCommand("""
        INSERT IGNORE INTO racing_canonical_events(event_id,sport_id,meeting,start_time_unix,created_from)
        VALUES(@event,2,@meeting,@start,'SPREADEX');
        INSERT INTO racing_event_provider_links(provider,provider_event_id,event_id,provider_ids_json,matched_by,match_score)
        VALUES(@provider,@providerId,@event,@ids,@decision,@score)
        ON DUPLICATE KEY UPDATE
          event_id=VALUES(event_id),provider_ids_json=VALUES(provider_ids_json),
          matched_by=VALUES(matched_by),match_score=VALUES(match_score);
        INSERT INTO racing_match_audit(provider,provider_event_id,event_id,decision,reason,match_score)
        VALUES(@provider,@providerId,@event,@decision,'meeting/time/runner reconciliation',@score);
        """,c);
        cmd.Parameters.AddWithValue("@event",eventId);cmd.Parameters.AddWithValue("@meeting",race.Meeting);
        cmd.Parameters.AddWithValue("@start",race.StartTimeUnix);cmd.Parameters.AddWithValue("@provider",race.Provider);
        cmd.Parameters.AddWithValue("@providerId",race.ProviderEventId);
        cmd.Parameters.AddWithValue("@ids",race.ProviderIds is null?DBNull.Value:JsonSerializer.Serialize(race.ProviderIds));
        cmd.Parameters.AddWithValue("@decision",decision);cmd.Parameters.AddWithValue("@score",score);
        await cmd.ExecuteNonQueryAsync(ct);
        foreach(var runner in race.Runners)
        {
            await using var r=new MySqlCommand("""
            INSERT IGNORE INTO racing_runner_provider_links(event_id,provider,provider_runner_id,normalized_name)
            VALUES(@event,@provider,@runner,@normalized)
            """,c);
            r.Parameters.AddWithValue("@event",eventId);r.Parameters.AddWithValue("@provider",race.Provider);
            r.Parameters.AddWithValue("@runner",runner);r.Parameters.AddWithValue("@normalized",BetfairMarketMatcher.NormalizeRunner(runner));
            await r.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyList<ProviderLinkIssue>> GetProviderLinkIssuesAsync(CancellationToken ct)
    {
        const string sql = """
            SELECT ce.event_id,ce.meeting,ce.start_time_unix,
                   COALESCE(NULLIF(e.ev_name,''),CONCAT(ce.meeting,' ',
                     DATE_FORMAT(FROM_UNIXTIME(ce.start_time_unix),'%H:%i'))) event_name,
                   MAX(CASE WHEN link.provider='B365' THEN 1 ELSE 0 END) has_b365,
                   MAX(CASE WHEN link.provider='SPREADEX' THEN 1 ELSE 0 END) has_spreadex,
                   MAX(CASE WHEN link.provider='SPREADEX' THEN link.matched_by END) spreadex_method,
                   MAX(CASE WHEN link.provider='SPREADEX' THEN link.match_score END) spreadex_score
            FROM racing_canonical_events ce
            LEFT JOIN spuno_events e ON e.event_id=ce.event_id
            LEFT JOIN racing_event_provider_links link ON link.event_id=ce.event_id
            WHERE ce.sport_id=2
              AND COALESCE(e.send_to_sp,0)<>0
              AND ce.start_time_unix BETWEEN UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 6 HOUR)
                                         AND UNIX_TIMESTAMP(UTC_TIMESTAMP()+INTERVAL 8 HOUR)
            GROUP BY ce.event_id,ce.meeting,ce.start_time_unix,e.ev_name
            ORDER BY ce.start_time_unix,ce.event_id;
            """;
        var rows = new List<(long EventId,string Meeting,string EventName,long Start,bool HasBet365,
            bool HasSpreadex,string? Method,double? Score)>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((
                reader.GetInt64(0), reader.GetString(1), reader.GetString(3), reader.GetInt64(2),
                reader.GetInt32(4) != 0, reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7)));

        var issues = new List<ProviderLinkIssue>();
        foreach (var row in rows.Where(x =>
                     (x.HasBet365 || x.HasSpreadex) &&
                     (!x.HasBet365 || !x.HasSpreadex)))
        {
            var counterpart = rows
                .Where(x => x.EventId != row.EventId &&
                            RacingEventNames.MeetingKey(x.Meeting) == RacingEventNames.MeetingKey(row.Meeting) &&
                            Math.Abs(x.Start - row.Start) <= 180 &&
                            (x.HasBet365 && row.HasSpreadex || x.HasSpreadex && row.HasBet365))
                .OrderBy(x => Math.Abs(x.Start - row.Start))
                .FirstOrDefault();
            var hasCounterpart = counterpart.EventId > 0;
            // Represent a split once, anchored on the Bet365/Sportuno event.
            if (hasCounterpart && !row.HasBet365) continue;
            var state = hasCounterpart ? "SPLIT" : row.HasBet365 ? "NO SPREADEX" : "NO BET365";
            issues.Add(new ProviderLinkIssue(
                row.EventId, row.Meeting, row.EventName, row.Start, state,
                row.HasBet365, row.HasSpreadex, row.Method, row.Score,
                hasCounterpart ? counterpart.EventId : null));
        }
        return issues
            .OrderByDescending(x => x.State == "SPLIT")
            .ThenBy(x => x.StartTimeUnix)
            .Take(100)
            .ToList();
    }
}
