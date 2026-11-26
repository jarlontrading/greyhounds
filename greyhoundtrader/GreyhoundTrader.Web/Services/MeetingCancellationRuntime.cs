using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using MySqlConnector;
using RabbitMQ.Client;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class MeetingCancellationService(MeetingCancellationStore store,IMeetingCancellationExtras extras,RacingPublisher publisher,IConfiguration configuration)
{
    readonly SemaphoreSlim gate=new(1,1);
    internal async Task<object> Confirm(ConfirmedMeetingCancellation request,CancellationToken ct)
    {
        var day=MeetingCancellationPolicy.Validate(request,store.SportId);
        await gate.WaitAsync(ct);
        try
        {
            var races=await store.Races(request.Meeting,day,ct);
            if(races.Count==0)throw new ArgumentException("No published races match that exact meeting and date");
            await using var extra=await extras.Acquire(request.Meeting,day,ct);
            await using var locks=await publisher.AcquireCancellationLocks(races.Select(r=>r.EventId).Concat(extra.EventIds),ct);
            var snapshots=await extra.Snapshot(ct);
            return await store.Apply(request,races,snapshots,RacingPublisher.ParseExchangePrefixes(configuration),ct);
        }
        finally{gate.Release();}
    }
}
internal static class CancellationEvidence
{
    static string Text(JsonElement e,string name)=>e.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    static bool Abandoned(string value)=>value.Trim().ToUpperInvariant() is "ABANDONED" or "CANCELLED" or "CANCELED";
    internal static IEnumerable<string> Indexes(int sport,DateOnly day)
    {
        var label=sport==2?"racing":"greyhounds";
        if(sport==4)
        {
            if(day==MeetingCancellationPolicy.Today)yield return "https://www.sportinglife.com/greyhounds/racecards";
            yield return $"https://www.sportinglife.com/greyhounds/results/{day:yyyy-MM-dd}";
        }
        else
        {
            if(day>=MeetingCancellationPolicy.Today)yield return $"https://www.sportinglife.com/racing/racecards/{day:yyyy-MM-dd}";
            if(day<=MeetingCancellationPolicy.Today)yield return $"https://www.sportinglife.com/racing/results/{day:yyyy-MM-dd}";
        }
    }
    internal static bool Usable(string html)
    {
        try
        {
            var text=new HtmlParser().ParseDocument(html).QuerySelector("script#__NEXT_DATA__")?.TextContent;
            if(string.IsNullOrWhiteSpace(text))return false;
            using var doc=JsonDocument.Parse(text);var page=doc.RootElement.GetProperty("props").GetProperty("pageProps");
            return (!page.TryGetProperty("hasError",out var error)||error.ValueKind!=JsonValueKind.True)&&page.TryGetProperty("meetings",out var meetings)&&meetings.ValueKind==JsonValueKind.Array;
        }
        catch(JsonException){return false;}catch(KeyNotFoundException){return false;}catch(InvalidOperationException){return false;}
    }
    internal static ConfirmedMeetingCancellation? SportingLife(string html,string url,int sport,string meeting,DateOnly day,IReadOnlyList<CancellationRace> local)
    {
        if(local.Count==0||local.Any(r=>r.SportId!=sport||MeetingCancellationPolicy.Key(r.Meeting)!=MeetingCancellationPolicy.Key(meeting)))return null;
        var document=new HtmlParser().ParseDocument(html);var script=document.QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if(string.IsNullOrWhiteSpace(script))return null;
        using var json=JsonDocument.Parse(script);var page=json.RootElement.GetProperty("props").GetProperty("pageProps");
        if(page.TryGetProperty("hasError",out var error)&&error.ValueKind==JsonValueKind.True)return null;
        if(page.TryGetProperty("date",out var date)&&date.ValueKind==JsonValueKind.String&&date.GetString()!=day.ToString("yyyy-MM-dd"))return null;
        if(!page.TryGetProperty("meetings",out var meetings)||meetings.ValueKind!=JsonValueKind.Array)return null;
        var needed=local.Where(r=>r.Unsettled>0||r.Status is not ("3" or "resulted" or "post game")).Select(r=>r.Start).Distinct().ToArray();
        if(needed.Length==0)needed=local.Select(r=>r.Start).Distinct().ToArray();
        if(needed.Length==0)return null;
        var matches=new List<ConfirmedMeetingCancellation>();
        foreach(var item in meetings.EnumerateArray())
        {
            if(!item.TryGetProperty("meeting_summary",out var summary)||Text(summary,"date")!=day.ToString("yyyy-MM-dd")||
               !summary.TryGetProperty("course",out var course)||RacingEventNames.MeetingKey(Text(course,"name"))!=RacingEventNames.MeetingKey(meeting)||
               !item.TryGetProperty("races",out var races)||races.ValueKind!=JsonValueKind.Array||!summary.TryGetProperty("meeting_reference",out var reference)||!reference.TryGetProperty("id",out var sourceId))continue;
            var observed=new Dictionary<long,string>();var ambiguous=false;
            foreach(var race in races.EnumerateArray())
            {
                if(Text(race,"date")!=day.ToString("yyyy-MM-dd")||!race.TryGetProperty("meeting_summary_reference",out var parent)||!parent.TryGetProperty("id",out var parentId)||parentId.ToString()!=sourceId.ToString()||
                   !TimeOnly.TryParseExact(Text(race,"time"),["HH:mm","H:mm"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))continue;
                var start=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(time),MeetingCancellationPolicy.Zone)).ToUnixTimeSeconds();
                if(!observed.TryAdd(start,Text(race,"race_stage")))ambiguous=true;
            }
            if(ambiguous||needed.Any(t=>!observed.ContainsKey(t)))continue;
            var explicitMeeting=Abandoned(Text(summary,"status"));
            var wholeCardStage=url.Contains("/racecards",StringComparison.Ordinal)&&observed.Count>0&&observed.Values.All(Abandoned);
            var wholeCardNoRace=sport==4&&url.Contains("/racecards",StringComparison.Ordinal)&&observed.Count>0&&
                observed.Keys.All(start=>local.Any(r=>r.Start==start&&r.NoRaceEvidence));
            var wholeCardProvider=url.Contains("/racecards",StringComparison.Ordinal)&&observed.Count>0&&
                observed.Keys.All(start=>local.Any(r=>r.Start==start&&r.ProviderStatus is 5 or 8));
            if(!explicitMeeting&&!wholeCardStage&&!wholeCardNoRace&&!wholeCardProvider)continue;
            var reason=Text(summary,"abandoned");if(reason.Length>1000)reason=reason[..1000];
            matches.Add(new(Guid.NewGuid(),sport,meeting,day.ToString("yyyy-MM-dd"),wholeCardProvider&&!explicitMeeting?"BETSAPI":"SPORTINGLIFE",
                $"Structured meeting {sourceId}: {(explicitMeeting?Text(summary,"status"):wholeCardNoRace?"every race on the full card explicitly No Race":wholeCardProvider?"every race on the complete source card has an exact provider cancelled/abandoned status":"every race on the full card explicitly abandoned")}. {reason}",url,"VOID_UNSETTLED",observed.Keys.ToArray()));
        }
        return matches.Count==1?matches[0]:null;
    }
    internal static ConfirmedMeetingCancellation? BetsApi(int sport,string meeting,DateOnly day,IReadOnlyList<CancellationRace> local,bool completeCardVerified=false)
    {
        if(!completeCardVerified||local.Any(r=>r.SportId!=sport||MeetingCancellationPolicy.Key(r.Meeting)!=MeetingCancellationPolicy.Key(meeting)))return null;
        var needed=local.Where(r=>r.Unsettled>0||r.Status is not ("3" or "resulted" or "post game")).ToArray();
        if(needed.Length==0||needed.Any(r=>r.ProviderStatus is not (5 or 8)))return null;
        return new(Guid.NewGuid(),sport,meeting,day.ToString("yyyy-MM-dd"),"BETSAPI","Every unsettled published race has an exact sport/time/meeting-matched provider cancelled (5) or abandoned (8) status.",
            "https://api.b365api.com/v1/bet365/result","VOID_UNSETTLED",needed.Select(r=>r.Start).Distinct().ToArray());
    }
}
internal sealed class CancellationDelivery(ExternalSettings settings):IAsyncDisposable
{
    IConnection? connection;IChannel? channel;
    internal static IReadOnlyList<(string Suffix,byte[] Body)> Messages(CancellationEvent e)
    {
        var messages=new List<(string,byte[])>();
        void Add(string suffix,object value)=>messages.Add((suffix,JsonSerializer.SerializeToUtf8Bytes(value,MeetingCancellationPolicy.Json)));
        var parent=new Dictionary<string,object?>{["messageType"]="event",["messageTimeStampUTC"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),["eventId"]=e.EventId,["sportId"]=e.SportId,["eventName"]=e.Name,["competition"]=e.Meeting,
            ["unixStartDate"]=e.Start,["startDate"]=DateTimeOffset.FromUnixTimeSeconds(e.Start).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss"),["active"]=0,["display"]=0,["inplay"]=0};
        if(e.Markets.Any(m=>m.SinglesOnly))parent["singlesOnly"]=true;
        Add("events",parent);
        foreach(var market in e.Markets)
        {
            var m=new Dictionary<string,object?>{["messageType"]="market",["messageTimeStampUTC"]=parent["messageTimeStampUTC"],["eventID"]=e.EventId,["marketId"]=market.MarketId,["marketName"]=market.Name,["marketTypeId"]=market.Type,["displayOrder"]=market.Order,["active"]=0,["display"]=0};
            if(market.SinglesOnly)m["singlesOnly"]=true;
            Add("markets",m);
            if(market.Selections.Count>0)Add("selections",new{messageType="selection",messageTimeStampUTC=parent["messageTimeStampUTC"],eventId=e.EventId,sportId=e.SportId,marketId=market.MarketId,selections=market.Selections});
        }
        Add("events",parent);return messages;
    }
    internal async Task Send(CancellationJob job,CancellationToken ct)
    {
        if(job.Target is not ("PROD" or "DEMO"))throw new InvalidDataException("Unexpected cancellation target");
        if(connection?.IsOpen!=true||channel?.IsOpen!=true)
        {
            await DisposeAsync();var uri=settings.RabbitMqUrlSp;if(uri.StartsWith("amqp://",StringComparison.OrdinalIgnoreCase))uri="amqps://"+uri[7..];
            connection=await new ConnectionFactory{Uri=new Uri(uri),ClientProvidedName="Confirmed-meeting-cancellation-delivery"}.CreateConnectionAsync(ct);
            channel=await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled:true,publisherConfirmationTrackingEnabled:true),cancellationToken:ct);
        }
        try
        {
            foreach(var (suffix,body) in Messages(job.Event))
            {using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));await channel.BasicPublishAsync(job.Target+suffix,"",true,body,deadline.Token);}
        }
        catch{await DisposeAsync();throw;}
    }
    public async ValueTask DisposeAsync()
    {
        if(channel is not null){try{await channel.DisposeAsync();}catch{}channel=null;}
        if(connection is not null){try{await connection.DisposeAsync();}catch{}connection=null;}
    }
}
internal sealed class MeetingCancellationWorker(MeetingCancellationStore store,MeetingCancellationService coordinator,CancellationDelivery delivery,IHttpClientFactory clients,ILogger<MeetingCancellationWorker> logger):BackgroundService
{
    public string? LastError{get;private set;}
    public string? DeliveryError{get;private set;}
    public DateTime? LastCycleUtc{get;private set;}
    public DateTime? LastEvidencePollUtc{get;private set;}
    public System.Collections.Concurrent.ConcurrentDictionary<string,string> EvidenceErrors{get;}=new();
    public DateTime? LastDeliveryCycleUtc{get;private set;}
    protected override Task ExecuteAsync(CancellationToken stoppingToken)=>Task.WhenAll(Deliver(stoppingToken),Observe(stoppingToken));
    async Task Deliver(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken);var nextPrune=DateTime.MinValue;
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(DateTime.UtcNow>=nextPrune){await store.PruneDelivered(stoppingToken);nextPrune=DateTime.UtcNow.AddDays(1);}
                foreach(var job in await store.Pending(stoppingToken))
                {
                    try{await delivery.Send(job,stoppingToken);await store.Complete(job,null,stoppingToken);}
                    catch(Exception ex) when(!stoppingToken.IsCancellationRequested)
                    {await store.Complete(job,ex.GetType().Name,stoppingToken);DeliveryError=ex.GetType().Name;logger.LogError("Cancellation delivery pending for {EventId}/{Target}: {ErrorType}; retained for retry",job.Event.EventId,job.Target,DeliveryError);break;}
                }
                var status=JsonSerializer.SerializeToElement(await store.Status(stoppingToken));
                if(status.GetProperty("pending").GetInt64()==0)DeliveryError=null;
                LastDeliveryCycleUtc=DateTime.UtcNow;
            }
            catch(Exception ex) when(!stoppingToken.IsCancellationRequested){DeliveryError=ex.GetType().Name;logger.LogError("Cancellation delivery cycle unavailable: {ErrorType}",DeliveryError);}
            await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken);
        }
    }
    async Task Observe(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30),stoppingToken);
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LastEvidencePollUtc=DateTime.UtcNow;
                    var known=await store.Confirmed(stoppingToken);
                    foreach(var existing in known.Where(r=>DateOnly.ParseExact(r.Date,"yyyy-MM-dd")>=MeetingCancellationPolicy.Today.AddDays(-7)))
                        await coordinator.Confirm(existing,stoppingToken); // Reconcile late-created selections; never drop the original outbox.
                    foreach(var day in new[]{MeetingCancellationPolicy.Today.AddDays(-1),MeetingCancellationPolicy.Today,MeetingCancellationPolicy.Today.AddDays(1)})
                    {
                        var races=await store.Races(null,day,stoppingToken,allScheduled:true);
                        var groups=races.GroupBy(r=>r.Meeting).Where(g=>!MeetingCancellationFence.IsMeetingCancelled(store.SportId,g.Key,day)).ToArray();
                        if(groups.Length==0)continue;
                        foreach(var url in CancellationEvidence.Indexes(store.SportId,day))
                        {
                            try
                            {
                                var html=await clients.CreateClient("MeetingCancellationEvidence").GetStringAsync(url,stoppingToken);
                                if(!CancellationEvidence.Usable(html))throw new InvalidDataException("Cancellation index is incomplete or unavailable");
                                EvidenceErrors.TryRemove(url,out _);
                                foreach(var group in groups)
                                {
                                    if(MeetingCancellationFence.IsMeetingCancelled(store.SportId,group.Key,day))continue;
                                    var proof=CancellationEvidence.SportingLife(html,url,store.SportId,group.Key,day,group.ToArray());
                                    if(proof is not null)await coordinator.Confirm(proof,stoppingToken);
                                }
                            }
                            catch(Exception ex) when(!stoppingToken.IsCancellationRequested){EvidenceErrors[url]=ex.GetType().Name;logger.LogWarning("Cancellation evidence unavailable for {Sport}/{Date}/{Index}: {ErrorType}",store.SportId,day,url,ex.GetType().Name);}
                        }
                    }

                LastError=null;LastCycleUtc=DateTime.UtcNow;
            }
            catch(Exception ex) when(!stoppingToken.IsCancellationRequested){LastError=ex.GetType().Name;logger.LogError("Meeting cancellation evidence cycle failed: {ErrorType}",LastError);}
            await Task.Delay(TimeSpan.FromMinutes(2),stoppingToken);
        }
    }
}

internal static class MeetingCancellationEndpoints
{
    internal static void MapMeetingCancellations(this WebApplication app)
    {
        static bool Authorised(HttpContext context,MeetingCancellationStore store)=>store.AdminKey.Length>=32&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(store.AdminKey)),SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers["X-Cancellation-Admin-Key"].ToString())));
        app.MapGet("/api/cancellations/status",async(MeetingCancellationStore store,MeetingCancellationWorker worker,CancellationToken ct)=>Results.Ok(new{queue=await store.Status(ct),monitorError=worker.LastError,deliveryError=worker.DeliveryError,evidenceErrors=worker.EvidenceErrors.ToArray(),worker.LastCycleUtc,worker.LastEvidencePollUtc,worker.LastDeliveryCycleUtc,administrativeConfirmationsConfigured=store.AdminKey.Length>=32,policy="Void only unsettled selections after explicit exact-meeting cancellation confirmation",automaticSources=new[]{"structured Sporting Life abandonment","explicit provider statuses covering a complete source card"}}));
        app.MapPost("/api/admin/cancellations/preview",async(HttpContext context,ConfirmedMeetingCancellation request,MeetingCancellationStore store,CancellationToken ct)=>
        {
            if(!Authorised(context,store))return Results.Unauthorized();
            try{var day=MeetingCancellationPolicy.Validate(request,store.SportId);return Results.Ok(new{request.Meeting,request.Date,races=await store.Races(request.Meeting,day,ct),warning="No data changed. Confirmation also covers unsettled meeting-level products."});}
            catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        });
        app.MapPost("/api/admin/cancellations/confirm",async(HttpContext context,ConfirmedMeetingCancellation request,MeetingCancellationStore store,MeetingCancellationService service,CancellationToken ct)=>
        {
            if(!Authorised(context,store))return Results.Unauthorized();
            if(request.Source!="OPERATOR")return Results.BadRequest(new{error="Provider confirmation is accepted only through the internal validated evidence adapters"});
            try{return Results.Ok(await service.Confirm(request,ct));}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}
        });
    }
}
