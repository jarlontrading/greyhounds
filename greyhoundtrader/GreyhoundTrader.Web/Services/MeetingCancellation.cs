using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySqlConnector;
using RabbitMQ.Client;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

// Mirrored in both trader repositories. Keep the settlement contract identical.
internal sealed record MeetingCancellationOptions(int SportId);
internal sealed record ConfirmedMeetingCancellation(Guid OperationId,int SportId,string Meeting,string Date,
    string Source,string Reason,string SourceUrl,string Confirmation,long[]? SourceRaceStarts=null);
internal sealed record CancellationRace(long EventId,int SportId,string Meeting,string Name,long Start,string Status,int Unsettled,int? ProviderStatus,bool NoRaceEvidence=false);
internal sealed record CancellationMarket(long MarketId,string Name,int Type,int Order,bool SinglesOnly,IReadOnlyList<JsonElement> Selections);
internal sealed record CancellationEvent(long EventId,int SportId,string Meeting,string Name,long Start,IReadOnlyList<CancellationMarket> Markets);
internal sealed record CancellationJob(long Id,string Target,string Hash,CancellationEvent Event,int Attempts);
internal sealed class CancellationExtrasLease(long[] ids,Func<CancellationToken,Task<IReadOnlyList<CancellationEvent>>> snapshot,Action release):IAsyncDisposable
{
    public long[] EventIds=>ids;
    public Task<IReadOnlyList<CancellationEvent>> Snapshot(CancellationToken ct)=>snapshot(ct);
    public ValueTask DisposeAsync(){release();return ValueTask.CompletedTask;}
}
internal interface IMeetingCancellationExtras
{
    Task<CancellationExtrasLease> Acquire(string meeting,DateOnly date,CancellationToken ct);
}
internal static class MeetingCancellationPolicy
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static readonly TimeZoneInfo Zone=TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows()?"GMT Standard Time":"Europe/London");
    internal static DateOnly Today=>DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,Zone).DateTime);
    internal static string Key(string value)=>string.Join(' ',value.Trim().ToUpperInvariant().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    internal static (long Start,long End) Bounds(DateOnly day)=>(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue),Zone)).ToUnixTimeSeconds(),new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue),Zone)).ToUnixTimeSeconds());
    internal static bool Unsettled(string? result)=>string.IsNullOrWhiteSpace(result);
    internal static bool Unsettled(JsonElement selection)=>!selection.TryGetProperty("result",out var r)||r.ValueKind==JsonValueKind.Null||r.ValueKind==JsonValueKind.String&&Unsettled(r.GetString());
    internal static JsonElement Close(JsonElement selection)
    {
        var copy=JsonNode.Parse(selection.GetRawText())!.AsObject();
        if(Unsettled(selection))copy["result"]="99";
        copy["active"]=0;copy["display"]=0;
        return JsonSerializer.SerializeToElement(copy,Json);
    }
    internal static DateOnly Validate(ConfirmedMeetingCancellation request,int sport)
    {
        if(request.OperationId==Guid.Empty||request.SportId!=sport||string.IsNullOrWhiteSpace(request.Meeting)||request.Meeting.Trim().Length>160||
           !DateOnly.TryParseExact(request.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||
           request.Confirmation!="VOID_UNSETTLED"||request.Source is not ("OPERATOR" or "SPORTINGLIFE" or "BETSAPI")||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>2000)
            throw new ArgumentException("An exact sport, meeting, date, confirmation and evidence reason are required.");
        if(request.Source!="OPERATOR"&&(request.SourceRaceStarts is null||request.SourceRaceStarts.Length==0))throw new ArgumentException("Automatic evidence must identify the matching race starts.");
        return day;
    }
    internal static int MarketType(long id)=>(id%10000) switch{9001=>1001,9002=>1002,9004=>1003,9006=>1005,9007=>1006,9008=>1009,9009=>1010,9010=>1011,9011=>1008,9400=>1013,9500=>1014,9600=>2001,9700=>2002,>=9100 and <=9199=>1001,>=9200 and <=9219=>1004,>=9300 and <=9319=>RacingMarketCalculator.ForecastMarketType,_=>1001};
}
internal static class MeetingCancellationFence
{
    static readonly ConcurrentDictionary<long,byte> Events=new();
    static readonly ConcurrentDictionary<string,byte> Meetings=new(StringComparer.Ordinal);
    static string Scope(int sport,string meeting,DateOnly day)=>$"{sport}/{day:yyyy-MM-dd}/{MeetingCancellationPolicy.Key(meeting)}";
    internal static bool IsCancelled(long id)=>Events.ContainsKey(id);
    internal static bool IsMeetingCancelled(int sport,string meeting,DateOnly day)=>Meetings.ContainsKey(Scope(sport,meeting,day));
    internal static bool Blocks(long id,int sport,string meeting,long start)=>IsCancelled(id)||IsMeetingCancelled(sport,meeting,DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(start),MeetingCancellationPolicy.Zone).DateTime));
    internal static void Claim(int sport,string meeting,DateOnly day,IEnumerable<long> ids){Meetings[Scope(sport,meeting,day)]=0;foreach(var id in ids)Events[id]=0;}
    internal static async Task<bool> Stored(MySqlConnection c,MySqlTransaction tx,long eventId,CancellationToken ct)
    {
        await using var q=new MySqlCommand("SELECT COUNT(*) FROM web_cancelled_events WHERE event_id=@id",c,tx);q.Parameters.AddWithValue("@id",eventId);
        return Convert.ToInt32(await q.ExecuteScalarAsync(ct))!=0;
    }
}
internal sealed class MeetingCancellationStore
{
    readonly string connectionString;public int SportId{get;}
    readonly ConcurrentDictionary<string,JsonElement> meetingFrames=new();
    public string AdminKey{get;}="";
    public MeetingCancellationStore(ExternalSettings s,MeetingCancellationOptions o):this(new MySqlConnectionStringBuilder{Server=s.MySqlServer,UserID=s.MySqlUser,Password=s.MySqlPassword,Database=s.RacingDatabase,SslMode=MySqlSslMode.Preferred,MaximumPoolSize=1,ConnectionTimeout=5,DefaultCommandTimeout=15}.ConnectionString,o.SportId)
    {
        var key=(o.SportId==2?"SPRACING":"GREYHOUNDTRADER")+"_CANCELLATION_ADMIN_KEY=";
        AdminKey=File.ReadLines(s.SourcePath).Select(l=>l.Trim()).FirstOrDefault(l=>l.StartsWith(key,StringComparison.Ordinal))?[key.Length..].Trim().Trim('"')??"";
    }
    internal MeetingCancellationStore(string connectionString,int sport){this.connectionString=connectionString;SportId=sport;}
    internal MySqlConnection Connection()=>new(connectionString);
    internal const string Schema="""
        CREATE TABLE IF NOT EXISTS web_confirmed_meeting_cancellations (
          sport_id INT NOT NULL,meeting_key VARCHAR(160) NOT NULL,meeting_date DATE NOT NULL,meeting_name VARCHAR(160) NOT NULL,
          operation_id CHAR(36) NOT NULL,evidence_json LONGTEXT NOT NULL,confirmed_utc DATETIME(6) NOT NULL,
          PRIMARY KEY(sport_id,meeting_key,meeting_date),UNIQUE KEY uq_cancel_operation(operation_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS web_cancelled_events (
          event_id BIGINT NOT NULL PRIMARY KEY,sport_id INT NOT NULL,meeting_key VARCHAR(160) NOT NULL,meeting_date DATE NOT NULL,
          payload_json LONGTEXT NOT NULL,created_utc DATETIME(6) NOT NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS web_cancellation_outbox (
          id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,event_id BIGINT NOT NULL,target VARCHAR(8) NOT NULL,
          payload_json LONGTEXT NOT NULL,payload_sha256 CHAR(64) NOT NULL,created_utc DATETIME(6) NOT NULL,
          acknowledged_utc DATETIME(6) NULL,attempts INT NOT NULL DEFAULT 0,last_error VARCHAR(100) NULL,
          next_attempt_utc DATETIME(6) NOT NULL,UNIQUE KEY uq_cancel_delivery(event_id,target),
          KEY ix_cancel_pending(acknowledged_utc,next_attempt_utc)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        CREATE TABLE IF NOT EXISTS web_meeting_market_frames (
          event_id BIGINT NOT NULL,market_id BIGINT NOT NULL,payload_json LONGTEXT NOT NULL,updated_utc DATETIME(6) NOT NULL,
          PRIMARY KEY(event_id,market_id)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        """;
    public async Task Initialise(CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using(var q=new MySqlCommand(Schema,c))await q.ExecuteNonQueryAsync(ct);
        await using(var q=new MySqlCommand("SELECT sport_id,meeting_name,meeting_date FROM web_confirmed_meeting_cancellations",c))
        {await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))MeetingCancellationFence.Claim(r.GetInt32(0),r.GetString(1),DateOnly.FromDateTime(r.GetDateTime(2)),[]);}
        await using(var q=new MySqlCommand("SELECT event_id,sport_id,meeting_key,meeting_date FROM web_cancelled_events",c))
        {await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))MeetingCancellationFence.Claim(r.GetInt32(1),r.GetString(2),DateOnly.FromDateTime(r.GetDateTime(3)),[r.GetInt64(0)]);}
    }
    internal static JsonElement MergeMeetingFrame(JsonElement incoming,JsonElement? previous)
    {
        var copy=JsonNode.Parse(incoming.GetRawText())!.AsObject();
        if(previous is {} old)
        {
            var present=copy["selections"]!.AsArray().Select(s=>s!["selectionId"]!.ToString()).ToHashSet();
            foreach(var prior in old.GetProperty("selections").EnumerateArray())
                if(!present.Contains(prior.GetProperty("selectionId").ToString()))copy["selections"]!.AsArray().Add(JsonNode.Parse(prior.GetRawText()));
            var known=old.GetProperty("selections").EnumerateArray().Where(s=>!MeetingCancellationPolicy.Unsettled(s)).ToDictionary(s=>s.GetProperty("selectionId").ToString());
            foreach(var selection in copy["selections"]!.AsArray())
                if(selection is JsonObject s&&known.TryGetValue(s["selectionId"]!.ToString(),out var settled)&&(!s.TryGetPropertyValue("result",out var result)||result is null||string.IsNullOrWhiteSpace(result.ToString())))
                {s["result"]=JsonNode.Parse(settled.GetProperty("result").GetRawText());s["active"]=0;s["display"]=0;}
        }
        return JsonSerializer.SerializeToElement(copy,MeetingCancellationPolicy.Json);
    }
    internal static string FrameSignature(JsonElement frame)=>string.Join('|',frame.GetProperty("selections").EnumerateArray().Select(s=>s.GetProperty("selectionId").ToString()+":"+(s.TryGetProperty("result",out var result)?result.ToString():"")));
    internal async Task RecordMeetingFrame(JsonElement incoming,CancellationToken ct)
    {
        var eventId=incoming.GetProperty("eventId").GetInt64();var marketId=incoming.GetProperty("marketId").GetInt64();var key=eventId+"/"+marketId;
        JsonElement? previous=meetingFrames.TryGetValue(key,out var cached)?cached:null;
        if(previous is not null&&FrameSignature(MergeMeetingFrame(incoming,previous))==FrameSignature(previous.Value))return;
        await using var c=Connection();await c.OpenAsync(ct);
        if(previous is null)
        {
            await using var read=new MySqlCommand("SELECT payload_json FROM web_meeting_market_frames WHERE event_id=@id AND market_id=@market",c);read.Parameters.AddWithValue("@id",eventId);read.Parameters.AddWithValue("@market",marketId);
            if(await read.ExecuteScalarAsync(ct) is string saved){using var doc=JsonDocument.Parse(saved);previous=doc.RootElement.Clone();}
        }
        var merged=MergeMeetingFrame(incoming,previous);
        await using var write=new MySqlCommand("INSERT INTO web_meeting_market_frames VALUES(@id,@market,@payload,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE payload_json=VALUES(payload_json),updated_utc=VALUES(updated_utc)",c);
        write.Parameters.AddWithValue("@id",eventId);write.Parameters.AddWithValue("@market",marketId);write.Parameters.AddWithValue("@payload",merged.GetRawText());await write.ExecuteNonQueryAsync(ct);meetingFrames[key]=merged;
    }
    internal async Task<IReadOnlyDictionary<long,JsonElement>> MeetingFrames(long eventId,CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand("SELECT market_id,payload_json FROM web_meeting_market_frames WHERE event_id=@id",c);q.Parameters.AddWithValue("@id",eventId);
        var frames=new Dictionary<long,JsonElement>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){using var doc=JsonDocument.Parse(r.GetString(1));frames[r.GetInt64(0)]=doc.RootElement.Clone();}return frames;
    }
    public async Task<IReadOnlyList<CancellationRace>> Races(string? meeting,DateOnly day,CancellationToken ct,MySqlConnection? current=null,MySqlTransaction? tx=null,bool locked=false,bool allScheduled=false)
    {
        var c=current??Connection();if(current is null)await c.OpenAsync(ct);
        try
        {
            var bounds=MeetingCancellationPolicy.Bounds(day);
            var sql="""
                SELECT e.event_id,e.sport_id,e.meeting,e.ev_name,e.start_time_unix,COALESCE(e.status,'0'),
                    (SELECT COUNT(*) FROM spuno_selections s WHERE s.event_id=e.event_id AND (s.result_text IS NULL OR s.result_text REGEXP '^[[:space:]]*$')),
                    CASE WHEN raw.sport_id=e.sport_id AND raw.starts_at_unix=e.start_time_unix AND raw.league_name=e.meeting THEN raw.time_status ELSE NULL END,
                    (EXISTS(SELECT 1 FROM web_runner_results rr WHERE rr.event_id=e.event_id AND rr.result_status='No Race' AND rr.source_url LIKE 'https://www.sportinglife.com/greyhounds/%')
                     AND NOT EXISTS(SELECT 1 FROM web_runner_results rr WHERE rr.event_id=e.event_id AND (rr.finishing_position IS NOT NULL OR COALESCE(rr.result_status,'')<>'No Race')))
                FROM spuno_events e LEFT JOIN em_events_raw raw ON raw.betsapi_event_id=e.event_id
                WHERE e.sport_id=@sport AND e.start_time_unix>=@start AND e.start_time_unix<@end
                  AND (@meeting IS NULL OR e.meeting=@meeting)
                  AND (@all=1 OR e.send_to_sp<>0 OR EXISTS(SELECT 1 FROM spuno_markets m WHERE m.event_id=e.event_id))
                ORDER BY e.event_id
                """+(locked?" FOR UPDATE":"");
            await using var q=new MySqlCommand(sql,c,tx);q.Parameters.AddWithValue("@sport",SportId);q.Parameters.AddWithValue("@all",allScheduled);q.Parameters.AddWithValue("@meeting",meeting);q.Parameters.AddWithValue("@start",bounds.Start);q.Parameters.AddWithValue("@end",bounds.End);
            var result=new List<CancellationRace>();await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))result.Add(new(r.GetInt64(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetString(5),r.GetInt32(6),r.IsDBNull(7)?null:r.GetInt32(7),r.GetBoolean(8)));
            return result;
        }
        finally{if(current is null)await c.DisposeAsync();}
    }
    internal async Task<IReadOnlyList<CancellationMarket>> Markets(MySqlConnection c,MySqlTransaction tx,long id,CancellationToken ct)
    {
        var markets=new Dictionary<long,CancellationMarket>();
        await using(var q=new MySqlCommand("SELECT market_id,mkt_name,display_order FROM spuno_markets WHERE event_id=@id ORDER BY market_id FOR UPDATE",c,tx))
        {q.Parameters.AddWithValue("@id",id);await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){var mid=r.GetInt64(0);markets[mid]=new(mid,r.IsDBNull(1)?"Market":r.GetString(1),MeetingCancellationPolicy.MarketType(mid),r.GetInt32(2),mid%10000==9700,[]);}}
        await using(var q=new MySqlCommand("SELECT * FROM spuno_selections WHERE event_id=@id ORDER BY market_id,selection_id FOR UPDATE",c,tx))
        {
            q.Parameters.AddWithValue("@id",id);await using var r=await q.ExecuteReaderAsync(ct);var selections=new Dictionary<long,List<JsonElement>>();
            while(await r.ReadAsync(ct))
            {
                object? Value(string name)=>r.IsDBNull(r.GetOrdinal(name))?null:r.GetValue(name);
                var mid=Convert.ToInt64(Value("market_id"));var name=Convert.ToString(Value("market_name"))??"Market";
                var s=new{eventId=id,marketId=mid,marketName=name,selectionId=Value("selection_id"),competitorId=Value("competitor_id"),name=Value("name"),identifier=Value("identifier"),
                    priceBack=Math.Max(1,Convert.ToDouble(Value("price_back")??1)),priceBackLast=Math.Max(1,Convert.ToDouble(Value("price_back_last")??1)),priceLay=Value("price_lay")??0,
                    index=Value("sel_index")??0,line=Value("line")??0,active=0,display=0,scratch=Convert.ToInt32(Value("scratch")??0),rule4Time=Value("rule4_time"),rule4Deduction=Value("rule4_deduction")??0,assetUrl=Value("asset_url"),result=Value("result_text")};
                if(!markets.ContainsKey(mid))markets[mid]=new(mid,name,MeetingCancellationPolicy.MarketType(mid),100,mid%10000==9700,[]);
                if(!selections.TryGetValue(mid,out var list))selections[mid]=list=[];list.Add(JsonSerializer.SerializeToElement(s,MeetingCancellationPolicy.Json));
            }
            foreach(var pair in selections)markets[pair.Key]=markets[pair.Key] with{Selections=pair.Value};
        }
        return markets.Values.ToArray();
    }
    public async Task<object> Apply(ConfirmedMeetingCancellation request,IReadOnlyList<CancellationRace> expected,IReadOnlyList<CancellationEvent> extras,string[] targets,CancellationToken ct)
    {
        var day=MeetingCancellationPolicy.Validate(request,SportId);if(targets.Length==0||targets.Any(t=>t is not ("PROD" or "DEMO")))throw new ArgumentException("Explicit publishing targets required");
        await using var c=Connection();await c.OpenAsync(ct);await using var tx=await c.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var key=MeetingCancellationPolicy.Key(request.Meeting);
        await using(var q=new MySqlCommand("INSERT INTO web_confirmed_meeting_cancellations VALUES(@sport,@key,@day,@meeting,@op,@evidence,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE operation_id=operation_id",c,tx))
        {q.Parameters.AddWithValue("@sport",SportId);q.Parameters.AddWithValue("@key",key);q.Parameters.AddWithValue("@day",day.ToDateTime(TimeOnly.MinValue));q.Parameters.AddWithValue("@meeting",request.Meeting);q.Parameters.AddWithValue("@op",request.OperationId.ToString());q.Parameters.AddWithValue("@evidence",JsonSerializer.Serialize(request,MeetingCancellationPolicy.Json));await q.ExecuteNonQueryAsync(ct);}
        await using(var q=new MySqlCommand("SELECT operation_id FROM web_confirmed_meeting_cancellations WHERE sport_id=@sport AND meeting_key=@key AND meeting_date=@day FOR UPDATE",c,tx))
        {q.Parameters.AddWithValue("@sport",SportId);q.Parameters.AddWithValue("@key",key);q.Parameters.AddWithValue("@day",day.ToDateTime(TimeOnly.MinValue));if(await q.ExecuteScalarAsync(ct) is null)throw new ArgumentException("Operation ID already belongs to another meeting");}
        var races=await Races(request.Meeting,day,ct,c,tx,true);
        if(races.Count==0||!races.Select(r=>r.EventId).SequenceEqual(expected.Select(r=>r.EventId)))throw new ArgumentException("Meeting card changed or identity did not match; preview again");
        if(!MeetingCancellationFence.IsMeetingCancelled(SportId,request.Meeting,day)&&request.Source!="OPERATOR"&&races.Where(r=>r.Unsettled>0||r.Status is not ("3" or "resulted" or "post game")).Any(r=>!request.SourceRaceStarts!.Contains(r.Start)))throw new ArgumentException("Cancellation evidence does not cover every unsettled race");
        if(!MeetingCancellationFence.IsMeetingCancelled(SportId,request.Meeting,day)&&request.Source=="BETSAPI"&&races.Where(r=>r.Unsettled>0||r.Status is not ("3" or "resulted" or "post game")).Any(r=>r.ProviderStatus is not (5 or 8)))throw new ArgumentException("Provider cancellation status changed; no void applied");
        var payloads=new List<CancellationEvent>();var changed=0;
        foreach(var race in races)
        {
            var markets=(await Markets(c,tx,race.EventId,ct)).ToList();
            foreach(var extra in extras.Where(e=>e.EventId==race.EventId))foreach(var m in extra.Markets)
            {
                var existing=markets.FindIndex(x=>x.MarketId==m.MarketId);
                if(existing<0)markets.Add(m);else markets[existing]=markets[existing] with{Selections=markets[existing].Selections.Concat(m.Selections).GroupBy(s=>s.GetProperty("selectionId").ToString()).Select(g=>g.First()).ToArray()};
            }
            var pending=markets.Sum(m=>m.Selections.Count(MeetingCancellationPolicy.Unsettled));
            // A retry with newly discovered selections must retain closure work
            // for non-SQL products, such as a published winning-distance market.
            var hadPrevious=false;
            await using(var previous=new MySqlCommand("SELECT payload_json FROM web_cancelled_events WHERE event_id=@id FOR UPDATE",c,tx))
            {
                previous.Parameters.AddWithValue("@id",race.EventId);
                if(await previous.ExecuteScalarAsync(ct) is string saved)
                {
                    hadPrevious=true;
                    var prior=JsonSerializer.Deserialize<CancellationEvent>(saved,MeetingCancellationPolicy.Json)!;
                    markets=prior.Markets.Concat(markets).GroupBy(m=>m.MarketId).Select(group=>group.First() with
                    {Selections=group.SelectMany(m=>m.Selections).GroupBy(sel=>sel.GetProperty("selectionId").ToString()).Select(g=>g.First()).ToArray()}).ToList();
                }
            }
            if(hadPrevious&&pending==0&&race.Status is "3" or "resulted" or "post game")continue;
            changed+=pending;
            payloads.Add(new(race.EventId,SportId,race.Meeting,race.Name,race.Start,markets.Select(m=>m with{Selections=m.Selections.Select(MeetingCancellationPolicy.Close).ToArray()}).ToArray()));
            await using var update=new MySqlCommand("""
                UPDATE spuno_selections SET result_text=CASE WHEN result_text IS NULL OR result_text REGEXP '^[[:space:]]*$' THEN '99' ELSE result_text END,active=0,display=0 WHERE event_id=@id;
                UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@id;
                UPDATE spuno_events SET status='3',active=0,display=0,in_progress=0 WHERE event_id=@id;
                UPDATE em_events_raw SET time_status=3,status=3,active=0,display=0 WHERE betsapi_event_id=@id;
                """,c,tx);update.Parameters.AddWithValue("@id",race.EventId);await update.ExecuteNonQueryAsync(ct);
        }
        foreach(var e in extras.Where(e=>!races.Any(r=>r.EventId==e.EventId)))
        {
            if(e.Markets.Count==0)continue;
            changed+=e.Markets.Sum(m=>m.Selections.Count(MeetingCancellationPolicy.Unsettled));
            payloads.Add(e with{Markets=e.Markets.Select(m=>m with{Selections=m.Selections.Select(MeetingCancellationPolicy.Close).ToArray()}).ToArray()});
        }
        foreach(var e in payloads)
        {
            var payload=JsonSerializer.Serialize(e,MeetingCancellationPolicy.Json);var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            await using(var q=new MySqlCommand("INSERT INTO web_cancelled_events VALUES(@id,@sport,@key,@day,@payload,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE payload_json=VALUES(payload_json)",c,tx))
            {q.Parameters.AddWithValue("@id",e.EventId);q.Parameters.AddWithValue("@sport",SportId);q.Parameters.AddWithValue("@key",key);q.Parameters.AddWithValue("@day",day.ToDateTime(TimeOnly.MinValue));q.Parameters.AddWithValue("@payload",payload);await q.ExecuteNonQueryAsync(ct);}
            foreach(var target in targets)
            {
                await using var q=new MySqlCommand("""
                    INSERT INTO web_cancellation_outbox(event_id,target,payload_json,payload_sha256,created_utc,next_attempt_utc)
                    VALUES(@id,@target,@payload,@hash,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                    ON DUPLICATE KEY UPDATE acknowledged_utc=IF(payload_sha256=VALUES(payload_sha256),acknowledged_utc,NULL),
                    next_attempt_utc=IF(payload_sha256=VALUES(payload_sha256),next_attempt_utc,UTC_TIMESTAMP(6)),
                    payload_json=VALUES(payload_json),payload_sha256=VALUES(payload_sha256)
                    """,c,tx);q.Parameters.AddWithValue("@id",e.EventId);q.Parameters.AddWithValue("@target",target);q.Parameters.AddWithValue("@payload",payload);q.Parameters.AddWithValue("@hash",hash);await q.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
        MeetingCancellationFence.Claim(SportId,request.Meeting,day,payloads.Select(e=>e.EventId));
        return new{request.OperationId,request.Meeting,request.Date,voidedSelections=changed,affectedEvents=payloads.Count,targets,delivery="durably queued; broker acknowledgement pending"};
    }
    public async Task<IReadOnlyList<CancellationJob>> Pending(CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand("SELECT id,target,payload_sha256,payload_json,attempts FROM web_cancellation_outbox WHERE acknowledged_utc IS NULL AND next_attempt_utc<=UTC_TIMESTAMP(6) ORDER BY id LIMIT 20",c);
        var jobs=new List<CancellationJob>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))jobs.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),JsonSerializer.Deserialize<CancellationEvent>(r.GetString(3),MeetingCancellationPolicy.Json)!,r.GetInt32(4)));return jobs;
    }
    public async Task Complete(CancellationJob job,string? error,CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand(error is null?
            "UPDATE web_cancellation_outbox SET acknowledged_utc=UTC_TIMESTAMP(6),last_error=NULL WHERE id=@id AND payload_sha256=@hash":
            "UPDATE web_cancellation_outbox SET attempts=attempts+1,last_error=@error,next_attempt_utc=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @delay SECOND) WHERE id=@id AND payload_sha256=@hash",c);
        q.Parameters.AddWithValue("@id",job.Id);q.Parameters.AddWithValue("@hash",job.Hash);q.Parameters.AddWithValue("@error",error);q.Parameters.AddWithValue("@delay",Math.Min(300,5*(1<<Math.Min(job.Attempts,6))));await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<IReadOnlyList<ConfirmedMeetingCancellation>> Confirmed(CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand("SELECT evidence_json FROM web_confirmed_meeting_cancellations WHERE sport_id=@sport",c);q.Parameters.AddWithValue("@sport",SportId);
        var list=new List<ConfirmedMeetingCancellation>();await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))list.Add(JsonSerializer.Deserialize<ConfirmedMeetingCancellation>(r.GetString(0),MeetingCancellationPolicy.Json)!);return list;
    }
    public async Task PruneDelivered(CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand("DELETE FROM web_cancellation_outbox WHERE acknowledged_utc<UTC_TIMESTAMP()-INTERVAL 30 DAY LIMIT 500",c);await q.ExecuteNonQueryAsync(ct);
    }
    public async Task<object> Status(CancellationToken ct)
    {
        await using var c=Connection();await c.OpenAsync(ct);await using var q=new MySqlCommand("SELECT COUNT(*),SUM(acknowledged_utc IS NULL),SUM(acknowledged_utc IS NOT NULL),MIN(IF(acknowledged_utc IS NULL,created_utc,NULL)) FROM web_cancellation_outbox",c);
        await using var r=await q.ExecuteReaderAsync(ct);await r.ReadAsync(ct);return new{sportId=SportId,deliveryJobs=r.GetInt64(0),pending=r.IsDBNull(1)?0:Convert.ToInt64(r.GetValue(1)),brokerAcknowledged=r.IsDBNull(2)?0:Convert.ToInt64(r.GetValue(2)),oldestPendingUtc=r.IsDBNull(3)?(DateTime?)null:r.GetDateTime(3),downstreamReceiptVerified=false};
    }
}
