namespace RacingTrader.Web.Services;

internal sealed class RacingFeedDiagnostics
{
    private readonly object _gate = new();
    private readonly Queue<FeedWorkerTrace> _recent = new();
    private DateTime _startedAtUtc = DateTime.UtcNow;
    private DateTime? _lastCycleStartedAtUtc;
    private DateTime? _lastCycleCompletedAtUtc;
    private string _stage = "starting";
    private long? _currentEventId;
    private string? _currentRace;
    private int _changedCandidates;
    private int _suspensionCandidates;
    private int _dueCandidates;
    private int _workBatch;
    private string? _lastError;
    private DateTime? _lastErrorAtUtc;

    public void CycleStarted()
    {
        lock (_gate)
        {
            _lastCycleStartedAtUtc = DateTime.UtcNow;
            _stage = "loading candidates";
            _currentEventId = null;
            _currentRace = null;
        }
    }

    public void CandidatesLoaded(int changed, int suspension, int due, int workBatch)
    {
        lock (_gate)
        {
            _changedCandidates = changed;
            _suspensionCandidates = suspension;
            _dueCandidates = due;
            _workBatch = workBatch;
            _stage = "processing races";
        }
    }

    public void Stage(string stage, PricingEvent? racingEvent = null)
    {
        lock (_gate)
        {
            _stage = stage;
            if (racingEvent is null) return;
            _currentEventId = racingEvent.EventId;
            _currentRace = racingEvent.Name;
        }
    }

    public void EventStarted(PricingEvent racingEvent)
    {
        lock (_gate)
        {
            _stage = "starting race";
            _currentEventId = racingEvent.EventId;
            _currentRace = racingEvent.Name;
            Add(new(DateTime.UtcNow, racingEvent.EventId, racingEvent.Name, "start", null));
        }
    }

    public void EventCompleted(PricingEvent racingEvent, string outcome)
    {
        lock (_gate)
        {
            Add(new(DateTime.UtcNow, racingEvent.EventId, racingEvent.Name, outcome, null));
        }
    }

    public void Error(Exception exception, PricingEvent? racingEvent = null)
    {
        lock (_gate)
        {
            _lastError = exception.ToString();
            _lastErrorAtUtc = DateTime.UtcNow;
            _stage = "error";
            Add(new(DateTime.UtcNow, racingEvent?.EventId, racingEvent?.Name, "error", _lastError));
        }
    }

    public void CycleCompleted()
    {
        lock (_gate)
        {
            _lastCycleCompletedAtUtc = DateTime.UtcNow;
            _stage = "waiting";
            _currentEventId = null;
            _currentRace = null;
        }
    }

    public FeedWorkerDiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
            return new(_startedAtUtc, _lastCycleStartedAtUtc, _lastCycleCompletedAtUtc,
                _stage, _currentEventId, _currentRace, _changedCandidates,
                _suspensionCandidates, _dueCandidates, _workBatch,
                _lastError, _lastErrorAtUtc, _recent.Reverse().ToList());
    }

    private void Add(FeedWorkerTrace trace)
    {
        _recent.Enqueue(trace);
        while (_recent.Count > 100) _recent.Dequeue();
    }
}

internal sealed record FeedWorkerDiagnosticsSnapshot(
    DateTime StartedAtUtc,
    DateTime? LastCycleStartedAtUtc,
    DateTime? LastCycleCompletedAtUtc,
    string Stage,
    long? CurrentEventId,
    string? CurrentRace,
    int ChangedCandidates,
    int SuspensionCandidates,
    int DueCandidates,
    int WorkBatch,
    string? LastError,
    DateTime? LastErrorAtUtc,
    IReadOnlyList<FeedWorkerTrace> Recent);

internal sealed record FeedWorkerTrace(
    DateTime AtUtc,
    long? EventId,
    string? Race,
    string Outcome,
    string? Error);
