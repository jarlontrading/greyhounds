using System.Collections.Concurrent;

namespace RacingTrader.Web.Services;

/// <summary>
/// Process-local terminal claim. A confirmed off signal can stop outbound
/// prices and publish the terminal event without waiting behind MySQL work.
/// </summary>
internal sealed class RaceTerminalGate
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(10);
    private sealed record TerminalState(bool ExternallyPublished, DateTime ClaimedAtUtc);
    private readonly ConcurrentDictionary<long, TerminalState> _terminal = new();

    public bool TryClaim(long eventId)
    {
        var replacement = new TerminalState(false, DateTime.UtcNow);
        while (true)
        {
            if (_terminal.TryAdd(eventId, replacement)) return true;
            if (!_terminal.TryGetValue(eventId, out var current)) continue;
            if (current.ExternallyPublished) return false;
            if (DateTime.UtcNow - current.ClaimedAtUtc < ClaimLease) return false;
            if (_terminal.TryUpdate(eventId, replacement, current)) return true;
        }
    }
    public bool IsTerminal(long eventId) => _terminal.ContainsKey(eventId);
    public bool IsExternallyPublished(long eventId) =>
        _terminal.TryGetValue(eventId, out var state) && state.ExternallyPublished;
    public void MarkExternallyPublished(long eventId) =>
        _terminal.AddOrUpdate(eventId,
            _ => new TerminalState(true, DateTime.UtcNow),
            static (_, current) => current with { ExternallyPublished = true });
    public void Release(long eventId) => _terminal.TryRemove(eventId, out _);
}
