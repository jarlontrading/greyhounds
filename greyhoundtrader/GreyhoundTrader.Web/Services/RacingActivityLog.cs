using MySqlConnector;
using RacingTrader.Web.Configuration;
using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

public sealed class RacingActivityLog
{
    private readonly string _connectionString;

    public RacingActivityLog(ExternalSettings settings)
    {
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = settings.MySqlServer,
            UserID = settings.MySqlUser,
            Password = settings.MySqlPassword,
            Database = settings.RacingDatabase,
            SslMode = MySqlSslMode.Preferred,
            ConnectionTimeout = 8,
            DefaultCommandTimeout = 15,
            MaximumPoolSize = 1,
            ConnectionIdleTimeout = 30
        }.ConnectionString;
    }

    public async Task WriteAsync(string activityType, string message, int? sportId = null,
        long? eventId = null, string? meeting = null, string? eventName = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO web_activity_log
              (activity_type, sport_id, event_id, meeting, event_name, message, created_at_utc)
            VALUES
              (@type, @sportId, @eventId, @meeting, @eventName, @message, UTC_TIMESTAMP(6));
            """;
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@type", activityType);
        command.Parameters.AddWithValue("@sportId", sportId is null ? DBNull.Value : sportId.Value);
        command.Parameters.AddWithValue("@eventId", eventId is null ? DBNull.Value : eventId.Value);
        command.Parameters.AddWithValue("@meeting", string.IsNullOrWhiteSpace(meeting) ? DBNull.Value : meeting);
        command.Parameters.AddWithValue("@eventName", string.IsNullOrWhiteSpace(eventName) ? DBNull.Value : eventName);
        command.Parameters.AddWithValue("@message", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityUpdate>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, activity_type, sport_id, event_id, meeting, event_name, message, created_at_utc
            FROM web_activity_log
            ORDER BY id DESC
            LIMIT @limit;
            """;
        var output = new List<ActivityUpdate>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 250));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.Add(new ActivityUpdate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        }
        return output;
    }

    public async Task<IReadOnlyList<ActivityUpdate>> GetRecentRule4Async(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,activity_type,sport_id,event_id,meeting,event_name,message,created_at_utc
            FROM web_activity_log WHERE activity_type='rule4'
            ORDER BY id DESC LIMIT @limit;
            """;
        var output = new List<ActivityUpdate>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 250));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
                DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)));
        return output;
    }

    public async Task<SuspensionMonitor> GetSuspensionMonitorAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        SuspensionMonitorRace? last = null;
        await using (var command = new MySqlCommand("""
            SELECT event_id,meeting,event_name,created_at_utc
            FROM web_activity_log
            WHERE activity_type='suspension' AND sport_id=4 AND event_id IS NOT NULL
            ORDER BY id DESC LIMIT 1;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
                last = new(reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "Race" : reader.GetString(2), null,
                    DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));

        var next = new List<SuspensionMonitorRace>();
        await using (var command = new MySqlCommand("""
            SELECT event_id,meeting,ev_name,start_time_unix
            FROM spuno_events
            WHERE sport_id=4 AND send_to_sp<>0 AND active=1
              AND COALESCE(NULLIF(status,''),'0') NOT IN ('2','3','resulted','post game')
              AND start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 HOUR)
            ORDER BY start_time_unix,event_id LIMIT 5;
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                next.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), null));
        return new(last, next);
    }

    public async Task<IReadOnlyList<FeedLastUpdate>> GetFeedLastUpdatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_name,event_id,race_name,updated_at_utc
            FROM (
              SELECT snapshots.source_name,snapshots.event_id,
                     COALESCE(NULLIF(events.ev_name,''),CONCAT('Event ',snapshots.event_id)) race_name,
                     snapshots.source_updated_at_utc updated_at_utc
              FROM web_race_winner_source_snapshots snapshots
              LEFT JOIN spuno_events events ON events.event_id=snapshots.event_id
              WHERE snapshots.source_name IN ('DIRECT','BET365_DIRECT','BETSAPI','BETFRED')
              UNION ALL
              SELECT 'SPREADEX',prices.event_id,
                     COALESCE(NULLIF(events.ev_name,''),CONCAT('Event ',prices.event_id)),
                     MAX(prices.updated_at_utc)
              FROM web_spreadex_runner_prices prices
              LEFT JOIN spuno_events events ON events.event_id=prices.event_id
              GROUP BY prices.event_id,events.ev_name
            ) updates
            ORDER BY updated_at_utc DESC;
            """;
        var latest = new Dictionary<string, FeedLastUpdate>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = reader.GetString(0);
            if (latest.ContainsKey(source)) continue;
            latest[source] = new(source, reader.GetInt64(1), reader.GetString(2),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        }
        return new[] { "SPREADEX", "BETFRED", "BET365_DIRECT", "BETSAPI" }
            .Select(source => latest.GetValueOrDefault(source) ?? new FeedLastUpdate(source, null, null, null))
            .ToList();
    }

    public async Task<IReadOnlyList<MissingBetsApiEvent>> GetMissingBetsApiEventsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT e.event_id,e.meeting,e.ev_name,e.start_time_unix,
                   EXISTS(
                     SELECT 1 FROM web_race_winner_source_snapshots direct
                     WHERE direct.event_id=e.event_id AND direct.source_name IN ('DIRECT','BET365_DIRECT')
                   ) has_direct,
                   EXISTS(
                     SELECT 1 FROM web_spreadex_runner_prices spreadex
                     WHERE spreadex.event_id=e.event_id
                   ) has_spreadex
            FROM spuno_events e
            WHERE e.sport_id=4
              AND COALESCE(e.send_to_sp,0)<>0
              AND e.start_time_unix BETWEEN @windowStart AND @windowEnd
              AND COALESCE(NULLIF(e.status,''),'0') NOT IN ('2','3','resulted','post game')
              AND NOT EXISTS(
                SELECT 1 FROM web_race_winner_source_snapshots betsapi
                WHERE betsapi.event_id=e.event_id AND betsapi.source_name='BETSAPI'
              )
            ORDER BY e.start_time_unix,e.event_id;
            """;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var localToday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).Date;
        var windowStart = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localToday, DateTimeKind.Unspecified), timeZone))
            .ToUnixTimeSeconds();
        var windowEnd = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localToday.AddDays(2), DateTimeKind.Unspecified), timeZone))
            .ToUnixTimeSeconds() - 1;

        var output = new List<MissingBetsApiEvent>();
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@windowStart", windowStart);
        command.Parameters.AddWithValue("@windowEnd", windowEnd);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            output.Add(new(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "Race" : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        return output;
    }
}

public sealed record SuspensionMonitorRace(long EventId, string Meeting, string EventName,
    long? StartTimeUnix, DateTime? SuspendedAtUtc);
public sealed record SuspensionMonitor(SuspensionMonitorRace? LastSuspended,
    IReadOnlyList<SuspensionMonitorRace> NextRaces);
public sealed record MissingBetsApiEvent(long EventId, string Meeting, string EventName,
    long StartTimeUnix, bool HasBet365Browser, bool HasSpreadex);
