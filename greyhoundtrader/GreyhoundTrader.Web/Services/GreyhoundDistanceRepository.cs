using MySqlConnector;
using System.Text.RegularExpressions;

namespace RacingTrader.Web.Services;

internal sealed partial class RacingFeedRepository
{
    public async Task<IReadOnlyList<PricingEvent>> GetDistanceEventsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events e
            WHERE sport_id=4 AND (send_to_sp<>0 OR EXISTS (
                SELECT 1 FROM spuno_markets existing_distance
                WHERE existing_distance.event_id=e.event_id AND MOD(existing_distance.market_id,10000)=9600))
              -- Keep distance polling bounded around live racing. Historical recovery is
              -- an explicit admin job; placing it in this worker previously delayed the
              -- higher-priority suspension loop and made the whole app unresponsive.
              AND start_time_unix BETWEEN UNIX_TIMESTAMP()-7200 AND UNIX_TIMESTAMP()+5400
            ORDER BY CASE
                       WHEN COALESCE(NULLIF(status,''),'0') IN ('3','resulted','post game')
                         AND EXISTS (
                           SELECT 1 FROM spuno_selections s WHERE s.event_id=e.event_id
                             AND MOD(s.market_id,10000)=9600 AND (s.result_text IS NULL OR s.result_text='')) THEN 0
                       WHEN start_time_unix BETWEEN UNIX_TIMESTAMP()-7200 AND UNIX_TIMESTAMP()+600 THEN 1
                       ELSE 2
                     END,
                     start_time_unix DESC;
            """, connection);
        var events = new List<PricingEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) events.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetString(5), 0));
        return events;
    }

    public async Task<bool> DistanceRunnerNamesMatchAsync(long eventId, IReadOnlyList<string> sourceNames, CancellationToken ct)
    {
        static string Key(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT name FROM spuno_selections
            WHERE event_id=@id AND MOD(market_id,10000)=9001 AND scratch=0 AND name NOT LIKE 'Vacant Trap%';
            """, connection);
        command.Parameters.AddWithValue("@id", eventId);
        var names = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) names.Add(Key(reader.GetString(0)));
        return names.Count >= 2 && names.Count == sourceNames.Count && names.SetEquals(sourceNames.Select(Key));
    }

    public async Task<GeneratedEventSnapshot?> SaveDistanceMarketAsync(long eventId, GreyhoundDistanceMarkets distances, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        PricingEvent? ev = null;
        await using (var command = new MySqlCommand("""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,COALESCE(NULLIF(status,''),'0'),0
            FROM spuno_events e WHERE event_id=@id AND sport_id=4
              AND (send_to_sp<>0 OR EXISTS (
                SELECT 1 FROM spuno_markets existing_distance
                WHERE existing_distance.event_id=e.event_id AND MOD(existing_distance.market_id,10000)=9600))
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@id", eventId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) ev = new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), 0);
        }
        if (ev is null) return null;
        // A late margin must never reopen an event or set the other markets' results.
        if (ev.Status is "resulted" or "post game") ev = ev with { Status = "3" };
        if (distances.Get(eventId)?.Margin is not null && ev.Status is "0" or "not started")
            return null; // wait for the existing result worker to establish the terminal event
        var market = distances.Build(ev);
        if (market is null) return null;
        await UpsertMarketAsync(connection, transaction, eventId, market, ct);
        await transaction.CommitAsync(ct);
        return new(ev, [market]);
    }
}
