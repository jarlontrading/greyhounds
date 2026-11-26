using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;
using RacingTrader.Web.Services;

if(args.Contains("--preview-live"))
{
    var sport=int.Parse(args[Array.IndexOf(args,"--preview-live")+1]);
    if(sport is not (2 or 4))throw new ArgumentException("Sport must be 2 or 4");
    var file=Environment.GetEnvironmentVariable("RG_CANCELLATION_SOURCE_FILE")??throw new ArgumentException("Private source file path required");
    var values=File.ReadLines(file).Select(l=>l.Trim()).Where(l=>l.Contains('=')&&!l.StartsWith('#')).Select(l=>l.Split('=',2)).ToDictionary(p=>p[0].Trim(),p=>p[1].Trim().Trim('"'));
    var prefix=sport==2?"SPRACING":"GREYHOUNDTRADER";
    var cs=new MySqlConnectionStringBuilder{Server=values[prefix+"_MYSQL_SERVER"],UserID=values[prefix+"_MYSQL_USER"],Password=values[prefix+"_MYSQL_PASSWORD"],Database=values.GetValueOrDefault(prefix=="SPRACING"?"SPRACING_RACING_DATABASE":"GREYHOUNDTRADER_DATABASE",sport==2?"randg":"greyhoundtrader"),MaximumPoolSize=1,ConnectionTimeout=5,DefaultCommandTimeout=15};
    if(cs.Server is not ("localhost" or "127.0.0.1"))throw new ArgumentException("Read-only preview source must be local");
    var store=new MeetingCancellationStore(cs.ConnectionString,sport);using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(20)};
    var found=new Dictionary<string,ConfirmedMeetingCancellation>();
    foreach(var date in new[]{MeetingCancellationPolicy.Today.AddDays(-1),MeetingCancellationPolicy.Today,MeetingCancellationPolicy.Today.AddDays(1)})
    {
        var candidates=await store.Races(null,date,default,allScheduled:true);var groups=candidates.GroupBy(r=>r.Meeting).ToArray();
        foreach(var group in groups){var evidence=CancellationEvidence.BetsApi(sport,group.Key,date,group.ToArray());if(evidence is not null)found[date+"/"+group.Key]=evidence;}
        foreach(var url in CancellationEvidence.Indexes(sport,date))
        {
            try{var html=await http.GetStringAsync(url);if(!CancellationEvidence.Usable(html)){Console.WriteLine(JsonSerializer.Serialize(new{source=url,usable=false}));continue;}
                foreach(var group in groups){var evidence=CancellationEvidence.SportingLife(html,url,sport,group.Key,date,group.ToArray());if(evidence is not null)found[date+"/"+group.Key]=evidence;}}
            catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{source=url,error=e.GetType().Name}));}
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new{readOnly=true,tradingMessagesPublished=false,confirmedMeetings=found.Values.Select(p=>new{p.SportId,p.Meeting,p.Date,p.Source,p.Reason})}));return;
}

var assertions=0;
void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
OffSignalIdentityChecks.Run(Check);
JsonElement Selection(string? result,string id="000000000000000000012345678901")=>JsonSerializer.SerializeToElement(new{selectionId=id,name="Selection",result,active=1,display=1,priceBack=6});
foreach(var result in new string?[]{null,""," ","1","0","99","-1"})
{
    var original=Selection(result);var closed=MeetingCancellationPolicy.Close(original);
    Check(closed.GetProperty("result").GetString()==(string.IsNullOrWhiteSpace(result)?"99":result),"Only unsettled results may become void");
    Check(closed.GetProperty("selectionId").GetString()==original.GetProperty("selectionId").GetString(),"Large string IDs preserved losslessly");
    Check(closed.GetProperty("active").GetInt32()==0&&closed.GetProperty("display").GetInt32()==0,"Cancellation closes and hides selections");
}
Check(MeetingCancellationPolicy.Bounds(new DateOnly(2026,3,29)) is var spring&&spring.End-spring.Start==23*3600,"UK spring day is not forced to 24 hours");
Check(MeetingCancellationPolicy.Bounds(new DateOnly(2026,10,25)) is var autumn&&autumn.End-autumn.Start==25*3600,"UK autumn day is not forced to 24 hours");
Check(CancellationEvidence.Indexes(4,MeetingCancellationPolicy.Today).Contains("https://www.sportinglife.com/greyhounds/racecards")&&!CancellationEvidence.Indexes(4,MeetingCancellationPolicy.Today.AddDays(1)).Any(u=>u.Contains("/racecards/")),"Greyhound monitor uses supported index routes rather than nonexistent dated racecards");
var day=new DateOnly(2026,11,22);
long Start(string time)=>new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.Parse(time)),MeetingCancellationPolicy.Zone)).ToUnixTimeSeconds();
object Meeting(string course,string status,string date="2026-11-22",string[]? stages=null)=>new{meeting_summary=new{meeting_reference=new{id=122691},date,status,course=new{name=course},abandoned=status=="ABANDONED"?"Frozen track":(string?)null},
    races=new[]{"12:43","13:13","13:43","14:13","14:43","15:13"}.Select((time,n)=>new{meeting_summary_reference=new{id=122691},date,time,race_stage=stages?[n]??status}).ToArray()};
string Page(object[] meetings,bool error=false)=>"<html><script id='__NEXT_DATA__' type='application/json'>"+JsonSerializer.Serialize(new{props=new{pageProps=new{meetings,hasError=error}}})+"</script><p>Advertising: abandoned elsewhere</p></html>";
var local=new[]{"12:43","13:13","13:43","14:13","14:43","15:13"}.Select((t,n)=>new CancellationRace(100+n,2,"Fakenham","Race",Start(t),"0",6,5)).ToArray();
var source="https://www.sportinglife.com/racing/results/2026-11-22";
Check(!CancellationEvidence.Usable("<html>Maintenance: abandoned elsewhere</html>")&&!CancellationEvidence.Usable(Page([],true)),"HTTP 200 challenge and error pages are not healthy cancellation evidence");
var proof=CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED")]),source,2,"Fakenham",day,local);
Check(proof?.Source=="SPORTINGLIFE"&&proof.SourceRaceStarts?.Length==6,"Fakenham structured six-race abandonment accepted");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED")]),source,2,"Fakenham",day,local.Select(r=>r with{Status="3",Unsettled=0}).ToArray()) is not null,"Confirmed abandoned card still closes unsettled meeting products after individual races have been voided");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED","2026-11-21")]),source,2,"Fakenham",day,local) is null,"Wrong date rejected");
Check(CancellationEvidence.SportingLife(Page([Meeting("Kelso","ABANDONED")]),source,2,"Fakenham",day,local) is null,"Wrong meeting rejected");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","SUSPENDED")]),source,2,"Fakenham",day,local) is null,"Suspension and page-wide abandoned text are not proof");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED")],true),source,2,"Fakenham",day,local) is null,"Incomplete/error snapshot rejected");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED")]),source,2,"Fakenham",day,local.Append(local[0] with{EventId=999,Start=Start("16:00")}).ToArray()) is null,"Missing expected race cannot imply cancellation");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED"),Meeting("Fakenham","ABANDONED")]),source,2,"Fakenham",day,local) is null,"Ambiguous duplicate source meeting rejected");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","ABANDONED",stages:["WEIGHEDIN","ABANDONED","ABANDONED","ABANDONED","ABANDONED","ABANDONED"])]),source,2,"Fakenham",day,local) is not null,"Partial meeting abandonment accepted without erasing completed results");
var dogs=local.Select(r=>r with{SportId=4,Meeting="Harlow EVE",ProviderStatus=null}).ToArray();
Check(CancellationEvidence.SportingLife(Page([Meeting("Harlow","ABANDONED")]),source,4,"Harlow EVE",day,dogs) is not null,"Greyhound session matched by exact date and all race times");
Check(CancellationEvidence.SportingLife(Page([Meeting("Harlow","RACECARD")]),"https://www.sportinglife.com/greyhounds/racecards",4,"Harlow EVE",day,dogs.Select(r=>r with{Status="3",Unsettled=0,NoRaceEvidence=true}).ToArray()) is not null,"Every race explicitly No Race on the complete greyhound card closes unsettled meeting products");
Check(CancellationEvidence.SportingLife(Page([Meeting("Harlow","RACECARD")]),"https://www.sportinglife.com/greyhounds/racecards",4,"Harlow EVE",day,dogs.Select((r,n)=>r with{NoRaceEvidence=n==0}).ToArray()) is null,"An individual no-race cannot cancel a whole meeting");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","DORMANT",stages:["ABANDONED","ABANDONED","ABANDONED","ABANDONED","ABANDONED","RACECARD"])]),"https://www.sportinglife.com/racing/racecards/2026-11-22",2,"Fakenham",day,local.Take(5).ToArray()) is null,"A partially booked card with an individual abandonment is not a whole-meeting cancellation");
Check(CancellationEvidence.SportingLife(Page([Meeting("Harlow","ABANDONED")]),source,4,"Harlow EVE",day,dogs.Select(r=>r with{Start=r.Start+6*3600}).ToArray()) is null,"Morning abandonment cannot cancel a different evening session");
foreach(var sport in new[]{2,4})
{
    Check(CancellationEvidence.BetsApi(sport,"Fakenham",day,local.Select(r=>r with{SportId=sport,ProviderStatus=8}).ToArray(),true) is not null,"All explicit provider abandonments confirm meeting");
    foreach(var status in new int?[]{null,0,1,2,3,4,7,10,99})Check(CancellationEvidence.BetsApi(sport,"Fakenham",day,local.Select((r,n)=>r with{SportId=sport,ProviderStatus=n==0?status:5}).ToArray(),true) is null,"Missing, suspended, postponed, removed or ended race is not meeting cancellation evidence");
}
Check(CancellationEvidence.BetsApi(2,"Fakenham",day,local) is null,"A set of locally known cancelled races without verified full-card coverage is not a confirmed meeting cancellation");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","RACECARD")]),"https://www.sportinglife.com/racing/racecards/2026-11-22",2,"Fakenham",day,local)?.Source=="BETSAPI","Exact explicit provider cancellations covering the complete source card are accepted");
Check(CancellationEvidence.SportingLife(Page([Meeting("Fakenham","RACECARD")]),"https://www.sportinglife.com/racing/racecards/2026-11-22",2,"Fakenham",day,local.Take(2).ToArray()) is null,"A partially discovered/booked cancelled subset cannot cancel the rest of a complete card");
var payload=new CancellationEvent(42,4,"Harlow","Harlow Meeting",Start("12:43"),[new(4200,"Trap Challenge",2002,10,true,[MeetingCancellationPolicy.Close(Selection(null)),MeetingCancellationPolicy.Close(Selection("1","2"))])]);
var oldFrame=JsonSerializer.SerializeToElement(new{eventId=42L,marketId=4200L,selections=new[]{Selection("1","1"),Selection(null,"2")}});
var incomingFrame=JsonSerializer.SerializeToElement(new{eventId=42L,marketId=4200L,selections=new[]{Selection(null,"1"),Selection(null,"3")}});
var mergedFrame=MeetingCancellationStore.MergeMeetingFrame(incomingFrame,oldFrame);
Check(mergedFrame.GetProperty("selections").GetArrayLength()==3&&mergedFrame.GetProperty("selections")[0].GetProperty("result").GetString()=="1","Meeting ledger retains decided outcomes and selections removed by a later incomplete card");
var messages=CancellationDelivery.Messages(payload);
Check(messages[0].Suffix=="events"&&messages[^1].Suffix=="events","Cancellation is bracketed by closed parent events");
foreach(var message in messages)
{
    using var doc=JsonDocument.Parse(message.Body);var e=doc.RootElement;
    if(message.Suffix is "events" or "markets")Check(e.GetProperty("active").GetInt32()==0&&e.GetProperty("display").GetInt32()==0&&e.GetProperty("singlesOnly").GetBoolean(),"Closed meeting wire flags and singles-only preserved");
    else Check(e.GetProperty("selections")[0].GetProperty("result").GetString()=="99"&&e.GetProperty("selections")[1].GetProperty("result").GetString()=="1","Void and already-settled selection results coexist correctly on the wire");
}

if(Environment.GetEnvironmentVariable("RG_CANCELLATION_TEST_MYSQL") is {Length:>0} admin)
    foreach(var sport in new[]{2,4})await DatabaseChecks.Run(admin,sport,Check);
else Console.WriteLine("SQL integration checks skipped: set RG_CANCELLATION_TEST_MYSQL to an isolated localhost administrator connection.");
Console.WriteLine($"PASS: {assertions} cancellation checks");

internal static class DatabaseChecks
{
    internal static async Task Run(string admin,int sport,Action<bool,string> check)
    {
        var cs=new MySqlConnectionStringBuilder(admin);
        if(cs.Server is not ("localhost" or "127.0.0.1"))throw new InvalidOperationException("Cancellation integration tests require localhost");
        var name="cxltest_"+Guid.NewGuid().ToString("N");cs.Database="mysql";
        await using var root=new MySqlConnection(cs.ConnectionString);await root.OpenAsync();
        await using(var create=new MySqlCommand($"CREATE DATABASE `{name}`",root))await create.ExecuteNonQueryAsync();
        try
        {
            cs.Database=name;await using var c=new MySqlConnection(cs.ConnectionString);await c.OpenAsync();
            var store=new MeetingCancellationStore(cs.ConnectionString,sport);await store.Initialise(default);
            async Task Exec(string sql){await using var q=new MySqlCommand(sql,c);await q.ExecuteNonQueryAsync();}
            async Task<string?> Scalar(string sql){await using var q=new MySqlCommand(sql,c);return Convert.ToString(await q.ExecuteScalarAsync());}
            await Exec("""
                CREATE TABLE web_runner_results(event_id BIGINT,selection_id BIGINT,competitor_id BIGINT NULL,identifier VARCHAR(30),runner_name VARCHAR(160),finishing_position INT NULL,result_status VARCHAR(40),source_url VARCHAR(500),resulted_at_utc DATETIME(6),PRIMARY KEY(event_id,selection_id)) ENGINE=InnoDB;
                CREATE TABLE spuno_events(event_id BIGINT PRIMARY KEY,sport_id INT,meeting VARCHAR(160),ev_name VARCHAR(160),start_time_unix BIGINT,status VARCHAR(30),send_to_sp INT DEFAULT 1,active INT DEFAULT 1,display INT DEFAULT 1,in_progress INT DEFAULT 0) ENGINE=InnoDB;
                CREATE TABLE em_events_raw(betsapi_event_id BIGINT PRIMARY KEY,sport_id INT,league_name VARCHAR(160),starts_at_unix BIGINT,time_status INT,status INT,active INT,display INT) ENGINE=InnoDB;
                CREATE TABLE spuno_markets(market_id BIGINT PRIMARY KEY,event_id BIGINT,mkt_name VARCHAR(160),display_order INT DEFAULT 10,active INT DEFAULT 1,display INT DEFAULT 1) ENGINE=InnoDB;
                CREATE TABLE spuno_selections(event_id BIGINT,market_id BIGINT,selection_id BIGINT,competitor_id BIGINT NULL,market_name VARCHAR(160),name VARCHAR(160),identifier VARCHAR(20),price_back DOUBLE DEFAULT 6,price_back_last DOUBLE DEFAULT 6,price_lay DOUBLE DEFAULT 0,sel_index INT DEFAULT 0,line DOUBLE DEFAULT 0,active INT DEFAULT 1,display INT DEFAULT 1,scratch INT DEFAULT 0,rule4_time VARCHAR(40) NULL,rule4_deduction DOUBLE DEFAULT 0,asset_url VARCHAR(500) NULL,result_text VARCHAR(50) NULL,PRIMARY KEY(event_id,market_id,selection_id)) ENGINE=InnoDB;
                """);
            var day=new DateOnly(2099,11,26);var start=MeetingCancellationPolicy.Bounds(day).Start+12*3600;var track="Test "+Guid.NewGuid().ToString("N")[..12];
            await Exec($"INSERT INTO spuno_events(event_id,sport_id,meeting,ev_name,start_time_unix,status) VALUES(1001,{sport},'{track}','First',{start},'3'),(1002,{sport},'{track}','Second',{start+600},'0'),(1003,{sport},'Other','Other',{start},'0'),(1004,{sport},'{track}','Tomorrow',{start+86400},'0'); INSERT INTO em_events_raw SELECT event_id,sport_id,meeting,start_time_unix,5,5,1,1 FROM spuno_events; INSERT INTO spuno_markets(market_id,event_id,mkt_name) SELECT event_id*10000+9001,event_id,'Race Winner' FROM spuno_events;");
            foreach(var (id,result) in new[]{(1,"'1'"),(2,"'0'"),(3,"NULL"),(4,"'99'")})await Exec($"INSERT INTO spuno_selections(event_id,market_id,selection_id,name,market_name,identifier,result_text) VALUES(1001,10019001,{id},'Runner {id}','Race Winner','{id}',{result})");
            await Exec("INSERT INTO spuno_selections(event_id,market_id,selection_id,name,market_name,identifier,result_text) VALUES(1002,10029001,5,'Pending','Race Winner','5',NULL),(1003,10039001,6,'Other','Race Winner','6',NULL),(1004,10049001,7,'Tomorrow','Race Winner','7',NULL)");
            var request=new ConfirmedMeetingCancellation(Guid.NewGuid(),sport,track,"2099-11-26","OPERATOR","Explicitly confirmed test cancellation","fixture","VOID_UNSETTLED");
            var extra=new CancellationEvent(9999,sport,track,track+" Meeting",start,[new(99999000,"Meeting",2002,10,true,[JsonSerializer.SerializeToElement(new{selectionId="000000001234567890123456789",result=(string?)null,active=1,display=1}),JsonSerializer.SerializeToElement(new{selectionId="000000001234567890123456788",result="1",active=0,display=0})])]);
            var races=await store.Races(track,day,default);check(races.Count==2,"Exact date/meeting preview excludes tomorrow and other tracks");
            var distance=new CancellationEvent(1001,sport,track,"First",start,[new(10019600,"Winning Distance",2001,220,false,[JsonSerializer.SerializeToElement(new{selectionId=1001960001L,result=(string?)null,active=1,display=1})])]);
            await store.Apply(request,races,[extra,distance],["PROD","DEMO"],default);
            check(await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=1")=="1"&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=2")=="0","Win/loss results preserved in SQL");
            check(await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=3")=="99"&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=4")=="99"&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=5")=="99","Only unset results become void");
            check(await Scalar("SELECT COUNT(*) FROM spuno_selections WHERE event_id IN(1003,1004) AND result_text IS NULL")=="2","Other meeting/day untouched");
            check(await Scalar("SELECT COUNT(*) FROM spuno_events WHERE event_id IN(1001,1002) AND status='3' AND active=0 AND display=0")=="2","Affected races terminal and hidden");
            var jobs=await store.Pending(default);check(jobs.Count==6,"Atomic outbox includes both environments and the meeting-level event");
            check(jobs.Count(j=>j.Event.EventId==9999)==2,"Derived meeting included without fake SQL event IDs");
            var first=jobs.First();await store.Complete(first,"IOException",default);
            check(await Scalar("SELECT COUNT(*) FROM web_cancellation_outbox WHERE acknowledged_utc IS NULL")=="6","Failed send leaves every delivery durable");
            await Exec("UPDATE web_cancellation_outbox SET next_attempt_utc=UTC_TIMESTAMP(6)");
            var restarted=new MeetingCancellationStore(cs.ConnectionString,sport);await restarted.Initialise(default);
            check((await restarted.Pending(default)).Count==6&&MeetingCancellationFence.IsCancelled(9999),"Pending delivery and terminal fences survive restart");
            await store.Complete(first,null,default);
            check(await Scalar("SELECT COUNT(*) FROM web_cancellation_outbox WHERE acknowledged_utc IS NOT NULL")=="1","Broker acknowledgement is per environment/event");
            await store.Apply(request,await store.Races(track,day,default),[],["PROD","DEMO"],default);
            check(await Scalar("SELECT COUNT(*) FROM web_cancellation_outbox")=="6"&&await Scalar("SELECT COUNT(*) FROM web_cancellation_outbox WHERE acknowledged_utc IS NOT NULL")=="1","Repeat confirmation does not duplicate or reopen deliveries");
            await Exec("INSERT INTO spuno_selections(event_id,market_id,selection_id,name,market_name,identifier,result_text) VALUES(1001,10019001,8,'Late','Race Winner','8',NULL)");
            await store.Apply(request,await store.Races(track,day,default),[],["PROD","DEMO"],default);
            await store.Complete(first,null,default);
            check((await store.Pending(default)).Single(j=>j.Id==first.Id).Event.Markets.Any(m=>m.MarketId==10019600),"Late-selection retry retains the non-SQL winning-distance closure");
            check(await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=8")=="99"&&await Scalar($"SELECT COUNT(*) FROM web_cancellation_outbox WHERE id={first.Id} AND acknowledged_utc IS NULL")=="1","Late selections reconciled and an old acknowledgement cannot clear newer work");
            try{await store.Apply(request with{Date="2099-11-27"},await store.Races(track,day.AddDays(1),default),[],["PROD"],default);throw new Exception("Operation ID reused across dates");}catch(ArgumentException){}
            check(await Scalar("SELECT COUNT(*) FROM web_confirmed_meeting_cancellations")=="1"&&await Scalar("SELECT result_text IS NULL FROM spuno_selections WHERE selection_id=7")=="1","Identity failure rolls back without creating a cancellation or changing tomorrow");
            await using(var tx=await c.BeginTransactionAsync()){check(await MeetingCancellationFence.Stored(c,tx,1001,default),"Resulting guard reads the committed terminal marker");await tx.RollbackAsync();}
            var frame=JsonSerializer.SerializeToElement(new{eventId=9999L,marketId=99999000L,selections=new[]{JsonSerializer.SerializeToElement(new{selectionId="paid",result="1",active=0,display=0}),JsonSerializer.SerializeToElement(new{selectionId="pending",result=(string?)null,active=1,display=1})}});
            await store.RecordMeetingFrame(frame,default);
            var fresh=new MeetingCancellationStore(cs.ConnectionString,sport);
            await fresh.RecordMeetingFrame(JsonSerializer.SerializeToElement(new{eventId=9999L,marketId=99999000L,selections=new[]{JsonSerializer.SerializeToElement(new{selectionId="paid",result=(string?)null,active=1,display=1})}}),default);
            var storedFrames=await fresh.MeetingFrames(9999,default);
            check(storedFrames[99999000L].GetProperty("selections")[0].GetProperty("result").GetString()=="1"&&storedFrames[99999000L].GetProperty("selections").GetArrayLength()==2,"Published outcome ledger survives restart and incomplete metadata without losing paid or pending selections");
            var repository=new RacingResultRepository(cs.ConnectionString,Microsoft.Extensions.Logging.Abstractions.NullLogger<RacingResultRepository>.Instance);
            var final=await repository.SettleAsync(new ResultPage(1001,sport,"fixture",[new ResultRunner("Runner 3",1,"Finished","3")],1,true),default);
            check(final is null&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=3")=="99","A delayed final result cannot replace cancellation");
            check(await repository.RevertFalseResultToSuspendedAsync(1001,default) is null,"Operator repair cannot reopen a confirmed cancellation");
            var unknown=await repository.CloseUnresultedAsync(1003,default);
            check(unknown is not null&&await Scalar("SELECT result_text IS NULL FROM spuno_selections WHERE selection_id=6")=="1","No-result timeout closes betting without inventing a void");
            var delivered=(await store.Pending(default)).Last();await store.Complete(delivered,null,default);
            await Exec($"UPDATE web_cancellation_outbox SET acknowledged_utc=UTC_TIMESTAMP()-INTERVAL 31 DAY WHERE id={delivered.Id}");await store.PruneDelivered(default);
            check(await Scalar($"SELECT COUNT(*) FROM web_cancellation_outbox WHERE id={delivered.Id}")=="0"&&await Scalar("SELECT COUNT(*) FROM web_cancelled_events")!="0"&&await Scalar("SELECT COUNT(*) FROM web_cancellation_outbox WHERE acknowledged_utc IS NULL")!="0","Retention removes only old acknowledged delivery receipts, preserving terminal records and pending retries");
            if(sport==4&&typeof(ResultPage).GetProperty("IsVoid") is not null)
            {
                await Exec($"INSERT INTO spuno_events(event_id,sport_id,meeting,ev_name,start_time_unix,status) VALUES(3001,4,'No Race','No Race',{start},'2');INSERT INTO em_events_raw VALUES(3001,4,'No Race',{start},5,5,1,1);INSERT INTO spuno_markets(market_id,event_id,mkt_name) VALUES(30019001,3001,'Race Winner');INSERT INTO spuno_selections(event_id,market_id,selection_id,name,market_name,identifier,result_text) VALUES(3001,30019001,31,'Existing winner','Race Winner','1','1'),(3001,30019001,32,'Pending','Race Winner','2',NULL);INSERT INTO web_runner_results(event_id,selection_id,identifier,runner_name,finishing_position,result_status) VALUES(3001,31,'1','Existing winner',1,'Finished')");
                var noRace=(ResultPage)Activator.CreateInstance(typeof(ResultPage),new object?[]{3001L,4,"fixture",new List<ResultRunner>{new("Existing winner",null,"No Race","1"),new("Pending",null,"No Race","2")},2,true,true})!;
                var closed=await repository.SettleAsync(noRace,default);
                check(closed is not null&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=31")=="1"&&await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=32")=="99","Explicit individual no-race handling also preserves already settled selections");
                check(await Scalar("SELECT finishing_position FROM web_runner_results WHERE event_id=3001 AND selection_id=31")=="1","No-race handling preserves the original paid runner result evidence");
                check(await repository.SettleAsync(noRace,default) is null,"Repeated mixed settled/void no-race does not continually republish");
            }
            var concurrentTrack=track+" Concurrent";
            await Exec($"INSERT INTO spuno_events(event_id,sport_id,meeting,ev_name,start_time_unix,status) VALUES(2001,{sport},'{concurrentTrack}','Concurrent',{start},'0'); INSERT INTO em_events_raw VALUES(2001,{sport},'{concurrentTrack}',{start},5,5,1,1); INSERT INTO spuno_markets(market_id,event_id,mkt_name) VALUES(20019001,2001,'Race Winner'); INSERT INTO spuno_selections(event_id,market_id,selection_id,name,market_name,identifier,result_text) VALUES(2001,20019001,20,'Winner','Race Winner','1',NULL)");
            var beforeResult=await store.Races(concurrentTrack,day,default);
            await using(var resultTx=await c.BeginTransactionAsync())
            {
                await using(var claim=new MySqlCommand("SELECT event_id FROM spuno_events WHERE event_id=2001 FOR UPDATE",c,resultTx))await claim.ExecuteScalarAsync();
                var cancellation=store.Apply(request with{OperationId=Guid.NewGuid(),Meeting=concurrentTrack},beforeResult,[],["PROD","DEMO"],default);
                await Task.Delay(100);check(!cancellation.IsCompleted,"Cancellation waits for an in-flight result transaction");
                await using(var finish=new MySqlCommand("UPDATE spuno_selections SET result_text='1' WHERE selection_id=20;UPDATE spuno_events SET status='3' WHERE event_id=2001",c,resultTx))await finish.ExecuteNonQueryAsync();
                await resultTx.CommitAsync();await cancellation;
            }
            check(await Scalar("SELECT result_text FROM spuno_selections WHERE selection_id=20")=="1"&&(await store.Pending(default)).Where(j=>j.Event.EventId==2001).All(j=>j.Event.Markets[0].Selections[0].GetProperty("result").GetString()=="1"),"An in-flight valid result is preserved and reliably republished rather than lost behind the cancellation fence");
            check((await store.Pending(default)).All(j=>j.Event.Markets.All(m=>m.Selections.All(s=>!MeetingCancellationPolicy.Unsettled(s)))),"Every replay payload has settled/void results");
        }
        finally
        {
            if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^cxltest_[a-f0-9]{32}$"))throw new InvalidOperationException("Unsafe test cleanup target");
            await using var drop=new MySqlCommand($"DROP DATABASE `{name}`",root);await drop.ExecuteNonQueryAsync();
        }
    }
}
