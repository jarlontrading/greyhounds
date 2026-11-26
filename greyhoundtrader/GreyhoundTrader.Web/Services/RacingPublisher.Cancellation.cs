namespace RacingTrader.Web.Services;

internal sealed partial class RacingPublisher
{
    internal async Task<IAsyncDisposable> AcquireCancellationLocks(IEnumerable<long> ids,CancellationToken ct)
    {
        var held=new List<SemaphoreSlim>();
        try
        {
            foreach(var id in ids.Distinct().Order())
            {
                var gate=_eventGates.GetOrAdd(id,_=>new SemaphoreSlim(1,1));
                await gate.WaitAsync(ct);held.Add(gate);
            }
            return new CancellationLocks(held);
        }
        catch{foreach(var gate in held.AsEnumerable().Reverse())gate.Release();throw;}
    }
    sealed class CancellationLocks(List<SemaphoreSlim> gates):IAsyncDisposable
    {
        public ValueTask DisposeAsync(){foreach(var gate in gates.AsEnumerable().Reverse())gate.Release();return ValueTask.CompletedTask;}
    }
}
