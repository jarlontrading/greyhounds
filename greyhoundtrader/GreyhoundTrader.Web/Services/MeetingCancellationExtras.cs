using System.Text.Json;
using RacingTrader.Web.Models;

namespace RacingTrader.Web.Services;

internal sealed class MeetingCancellationExtras(TrapChallengeService challenges,TrapChallengePublishingWorker worker,
    MeetingCancellationStore store,GreyhoundDistanceMarkets distances):IMeetingCancellationExtras
{
    public async Task<CancellationExtrasLease> Acquire(string meeting,DateOnly date,CancellationToken ct)
    {
        var races=await store.Races(meeting,date,ct);var anchor=races.Select(r=>r.Start).DefaultIfEmpty(0).Min();
        var existing=anchor>0?await challenges.BuildPublishingSnapshotAsync(meeting,anchor,ct):null;
        var release=existing is null?null:await worker.AcquireCancellationLock(existing.Event.EventId,ct);
        return new(existing is null?[]:[existing.Event.EventId],async token=>
        {
            var result=new List<CancellationEvent>();
            if(MeetingCancellationFence.IsMeetingCancelled(4,meeting,date))return result;
            var snapshot=anchor>0?await challenges.BuildPublishingSnapshotAsync(meeting,anchor,token):null;
            if(snapshot is not null)
            {
                var frames=await store.MeetingFrames(snapshot.Event.EventId,token);
                result.Add(Convert(snapshot.Event,snapshot.Markets,true,frames));
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
