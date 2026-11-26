using MySqlConnector;
using System.Text.Json;
using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

internal sealed class MeetingCancellationExtras(TrapChallengeService challenges,TrapChallengePublishingWorker worker,
    MeetingCancellationStore store,GreyhoundDistanceMarkets distances):IMeetingCancellationExtras
{
    public async Task<CancellationExtrasLease> Acquire(string meeting,DateOnly date,CancellationToken ct)
    {
        var races=await store.Races(meeting,date,ct);var anchor=races.Select(r=>r.Start).DefaultIfEmpty(0).Min();
        var mapping=await ExistingMeeting(meeting,date,ct);
        var release=mapping is null?null:await worker.AcquireCancellationLock(mapping.Value.Id,ct);
        return new(mapping is null?[]:[mapping.Value.Id],async token=>
        {
            var result=new List<CancellationEvent>();
            if(MeetingCancellationFence.IsMeetingCancelled(4,meeting,date))return result;
            if(mapping is {} existing)
            {
                var frames=await store.MeetingFrames(existing.Id,token);
                var snapshot=await challenges.SnapshotForCancellation(meeting,existing.Anchor,existing.Id,existing.Evening,token);
                if(snapshot is not null)result.Add(Convert(snapshot.Event,snapshot.Markets,true,frames));
                else
                {
                    var marketId=checked(existing.Id*10000+TrapChallengeService.MarketSuffix);
                    if(!frames.TryGetValue(marketId,out var frame))throw new InvalidOperationException("Published trap challenge requires its retained card or published outcome ledger before cancellation");
                    result.Add(new(existing.Id,4,meeting,TrapChallengeService.DisplayMeetingName(meeting,existing.Evening)+" Meeting",existing.Anchor,
                        [new(marketId,"Trap Challenge",TrapChallengeService.MarketTypeId,10,true,frame.GetProperty("selections").EnumerateArray().Select(s=>s.Clone()).ToArray())]));
                }
            }
            foreach(var race in races)
            {
                var ev=new PricingEvent(race.EventId,4,race.Meeting,race.Name,race.Start,race.Status,0);
                var distance=distances.Build(ev);
                var frames=await store.MeetingFrames(race.EventId,token);
                var known=new List<CancellationMarket>();
                foreach(var pair in frames.Where(p=>p.Key%10000==9600))
                {
                    var selections=pair.Value.GetProperty("selections").EnumerateArray().Select(s=>s.Clone()).ToArray();
                    var name=selections.FirstOrDefault().TryGetProperty("marketName",out var n)?n.GetString()??"Winning Distance":"Winning Distance";
                    known.Add(new(pair.Key,name,GreyhoundDistanceMarkets.MarketType,220,false,selections));
                }
                if(known.Count>0)result.Add(new(ev.EventId,4,ev.Meeting,ev.Name,ev.StartTimeUnix,known));
                else if(distance is not null)result.Add(Convert(ev,[distance],false,null));
            }
            return result;
        },()=>release?.Dispose());
    }
    async Task<(long Id,long Anchor,bool Evening)?> ExistingMeeting(string meeting,DateOnly day,CancellationToken ct)
    {
        await using var c=store.Connection();await c.OpenAsync(ct);
        await using var q=new MySqlCommand("SELECT event_id,first_start_time_unix,meeting_name FROM web_trap_challenge_events WHERE meeting_date=@day",c);q.Parameters.AddWithValue("@day",day.ToDateTime(TimeOnly.MinValue));
        var rows=new List<(long Id,long Start,string Name)>();await using var reader=await q.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2)));
        var matches=rows.Where(r=>MeetingCancellationPolicy.Key(r.Name)==MeetingCancellationPolicy.Key(meeting)).ToArray();
        if(matches.Length==0)return null;
        if(matches.Length!=1)throw new InvalidOperationException("Ambiguous meeting event mapping");
        var match=matches[0];return(match.Id,match.Start,rows.Any(r=>r.Start<match.Start&&RacingEventNames.MeetingKey(r.Name)==RacingEventNames.MeetingKey(meeting)));
    }
    static CancellationEvent Convert(PricingEvent e,IReadOnlyList<GeneratedMarket> markets,bool singlesOnly,IReadOnlyDictionary<long,JsonElement>? frames)=>new(e.EventId,e.SportId,e.Meeting,e.Name,e.StartTimeUnix,
        markets.Select(m=>new CancellationMarket(m.MarketId,m.Name,m.MarketTypeId,m.DisplayOrder,singlesOnly,frames is not null&&frames.TryGetValue(m.MarketId,out var frame)?frame.GetProperty("selections").EnumerateArray().Select(s=>s.Clone()).ToArray():m.Selections.Select(s=>JsonSerializer.SerializeToElement(s,MeetingCancellationPolicy.Json)).ToArray())).ToArray());
}
internal sealed partial class TrapChallengePublishingWorker
{
    internal async Task<IDisposable> AcquireCancellationLock(long id,CancellationToken ct)
    {
        var gate=_publishLocks.GetOrAdd(id,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct);return new Lease(gate);
    }
    sealed class Lease(SemaphoreSlim gate):IDisposable{public void Dispose()=>gate.Release();}
}

public sealed partial class TrapChallengeService
{
    internal async Task<GeneratedEventSnapshot?> SnapshotForCancellation(string meeting,long anchor,long eventId,bool evening,CancellationToken ct)
    {
        var races=SelectMeetingDay(await repository.GetMeetingTrapChallengeRacesAsync(meeting,ct,anchor),anchor);
        races=timeform.ApplyTrapChallengeFallback(meeting,races);
        return BuildPublishingSnapshot(meeting,races,eventId,evening);
    }
}
