using System.Text.Json;
using System.Threading.Channels;

namespace RacingTrader.Web.Services;

internal sealed record SuspensionSentinelStatus(
    DateTime? LastLoopUtc, DateTime? LastScheduleRefreshUtc,
    DateTime? LastSignalUtc, DateTime? LastTerminalPublishUtc,
    int ScheduledRaces, int PendingFollowUps, int DurableOutboxDepth,
    string? LastError);

/// <summary>
/// Detects race-off from an in-memory schedule and provider caches. The only
/// awaited operation on the critical path is the terminal event publication;
/// database closure, child payloads, Trap Challenge and audit run on a retrying
/// follow-up queue.
/// </summary>
internal sealed class RacingSuspensionWorker(
    RacingFeedRepository feedRepository,
    SpreadexRacingService spreadex,
    BetfredSuspensionClient betfredSuspensions,
    BetAhoyFeed betAhoy,
    Bet365CaptureBridge bet365Capture,
    BetsApiInPlayMonitor betsApiInPlay,
    RacingPublisher publisher,
    TrapChallengePublishingWorker trapChallengePublisher,
    RacingActivityLog activity,
    RacingDiagnosticCapture diagnostics,
    RaceAuditTrail audit,
    RaceTerminalGate terminalGate,
    ILogger<RacingSuspensionWorker> logger) : BackgroundService
{
    private sealed record SuspensionWork(PricingEvent Event, string Reason, DateTime ScheduledOffUtc, int Attempt = 0);
    private readonly Channel<SuspensionWork> _followUps = Channel.CreateUnbounded<SuspensionWork>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private volatile PricingEvent[] _schedule = [];
    private static readonly string OutboxDirectory = @"D:\Temp\greyhound-suspension-outbox";
    private long _lastLoopTicks;
    private long _lastScheduleRefreshTicks;
    private long _lastSignalTicks;
    private long _lastTerminalPublishTicks;
    private int _pendingFollowUps;
    private volatile string? _lastError;

    public SuspensionSentinelStatus Status => new(
        TicksToUtc(Interlocked.Read(ref _lastLoopTicks)),
        TicksToUtc(Interlocked.Read(ref _lastScheduleRefreshTicks)),
        TicksToUtc(Interlocked.Read(ref _lastSignalTicks)),
        TicksToUtc(Interlocked.Read(ref _lastTerminalPublishTicks)),
        _schedule.Length, Volatile.Read(ref _pendingFollowUps), OutboxDepth(), _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return control to the generic host before touching the database or
        // outbox. Kestrel must bind even when MySQL or the machine is saturated.
        await Task.Yield();
        _ = spreadex; _ = bet365Capture;
        Directory.CreateDirectory(OutboxDirectory);
        var scheduleTask = RefreshScheduleAsync(stoppingToken);
        var followUpTask = ProcessFollowUpsAsync(stoppingToken);
        await ReplayPendingAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                Interlocked.Exchange(ref _lastLoopTicks, now.Ticks);
                foreach (var racingEvent in _schedule)
                {
                    var scheduledOffUtc = DateTimeOffset.FromUnixTimeSeconds(racingEvent.StartTimeUnix).UtcDateTime;
                    if (now < scheduledOffUtc) continue;

                    string? reason = null;
                    if (betfredSuspensions.TryGetRaceOffSignal(racingEvent, out var betfredServiceReason))
                        reason = betfredServiceReason;
                    else if (betAhoy.TryGetOff(racingEvent, out var betAhoyReason))
                        reason = betAhoyReason;
                    else
                    {
                        var betsApiSignal = betsApiInPlay.TryGetRaceOffSignal(racingEvent);
                        if (betsApiSignal.IsOff) reason = betsApiSignal.Reason;
                    }
                    if (reason is null && now >= scheduledOffUtc.AddSeconds(90))
                        reason = "Scheduled-off safeguard: neither Betfred nor BetsAPI supplied a usable OFF signal within 90 seconds.";
                    if (reason is null || !terminalGate.TryClaim(racingEvent.EventId)) continue;

                    var work = new SuspensionWork(racingEvent, reason, scheduledOffUtc);
                    Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
                    try
                    {
                        await PersistPendingAsync(work, stoppingToken);
                        var shell = racingEvent with { Status = "2" };
                        diagnostics.Write("suspension", "signal-accepted", new
                        {
                            racingEvent.EventId, racingEvent.Meeting, racingEvent.Name,
                            racingEvent.StartTimeUnix, reason
                        });
                        await publisher.PublishEventsAsync([shell], stoppingToken);
                        terminalGate.MarkExternallyPublished(racingEvent.EventId);
                        Interlocked.Exchange(ref _lastTerminalPublishTicks, DateTime.UtcNow.Ticks);
                        _lastError = null;
                        Interlocked.Increment(ref _pendingFollowUps);
                        await _followUps.Writer.WriteAsync(work, stoppingToken);
                        _ = ReinforceTerminalEventAsync(shell, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        terminalGate.Release(racingEvent.EventId);
                        _lastError = ex.Message;
                        logger.LogError(ex,
                            "Critical suspension publish failed for event {EventId}; durable outbox retained and retrying.",
                            racingEvent.EventId);
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _followUps.Writer.TryComplete();
            try { await Task.WhenAll(scheduleTask, followUpTask); } catch (OperationCanceledException) { }
        }
    }

    private async Task RefreshScheduleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _schedule = (await feedRepository.GetSuspensionCandidatesAsync(cancellationToken))
                    .Where(item => item.SportId == 4)
                    .OrderBy(item => Math.Abs(item.StartTimeUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                    .ToArray();
                Interlocked.Exchange(ref _lastScheduleRefreshTicks, DateTime.UtcNow.Ticks);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Keep using the last successful in-memory schedule. Database
                // trouble must not stop scheduled fallback or provider signals.
                logger.LogWarning(ex, "Could not refresh suspension schedule; retaining {Count} cached races.", _schedule.Length);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private async Task ProcessFollowUpsAsync(CancellationToken cancellationToken)
    {
        await foreach (var work in _followUps.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var racingEvent = work.Event;
                await audit.WriteAsync("suspension-signal-received", racingEvent.EventId,
                    racingEvent.SportId, racingEvent.Meeting, racingEvent.Name,
                    racingEvent.StartTimeUnix,
                    work.Reason.StartsWith("Betfred", StringComparison.OrdinalIgnoreCase) ? "BETFRED-BROWSER" :
                    work.Reason.StartsWith("BetAhoy", StringComparison.OrdinalIgnoreCase) ? "BETAHOY" :
                    work.Reason.StartsWith("BetsAPI", StringComparison.OrdinalIgnoreCase) ? "BETSAPI" : "RACINGTRADER-SAFEGUARD",
                    "cached suspension sentinel", work.Reason,
                    new { work.ScheduledOffUtc }, cancellationToken);
                await audit.WriteAsync("suspension-event-sent", racingEvent.EventId,
                    racingEvent.SportId, racingEvent.Meeting, racingEvent.Name,
                    racingEvent.StartTimeUnix, "RACINGTRADER", "event-first suspension",
                    "Sportuno terminal event completed before queued database and child closure.",
                    new { work.Reason }, cancellationToken);

                await feedRepository.TrySuspendEventShellAsync(racingEvent, cancellationToken);
                await activity.WriteAsync("suspension", $"Markets suspended because {work.Reason}",
                    racingEvent.SportId, racingEvent.EventId, racingEvent.Meeting,
                    racingEvent.Name, cancellationToken);

                await trapChallengePublisher.PublishMeetingAsync(
                    racingEvent.Meeting, racingEvent.StartTimeUnix, cancellationToken);
                var suspended = await feedRepository.SuspendRaceAsync(racingEvent, cancellationToken);
                if (suspended is not null)
                    await publisher.PublishAsync(suspended, cancellationToken);
                await audit.WriteAsync("suspension-complete", racingEvent.EventId,
                    racingEvent.SportId, racingEvent.Meeting, racingEvent.Name,
                    racingEvent.StartTimeUnix, "RACINGTRADER", "queued closed market and selection payload",
                    "Suspension follow-up completed.", new { work.Reason, payloadBuilt = suspended is not null }, cancellationToken);
                // The outbox represents the complete suspension operation, not
                // only the terminal event shell. Keep it durable until the
                // event, database shell, markets, selections and challenge have
                // all completed so a restart can replay unfinished closure work.
                DeletePending(racingEvent.EventId);
                Interlocked.Decrement(ref _pendingFollowUps);
                diagnostics.Write("suspension", "full-payload-published", new
                {
                    racingEvent.EventId, racingEvent.Meeting, racingEvent.Name, work.Reason
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Suspension follow-up failed for event {EventId}; retrying.", work.Event.EventId);
                var delay = TimeSpan.FromSeconds(Math.Min(30, 2 << Math.Min(work.Attempt, 3)));
                try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { return; }
                await _followUps.Writer.WriteAsync(work with { Attempt = work.Attempt + 1 }, cancellationToken);
            }
        }
    }

    private async Task ReplayPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(OutboxDirectory, "*.json"))
        {
            try
            {
                var work = JsonSerializer.Deserialize<SuspensionWork>(await File.ReadAllTextAsync(path, cancellationToken));
                if (work is null || !terminalGate.TryClaim(work.Event.EventId)) continue;
                var shell = work.Event with { Status = "2" };
                await publisher.PublishEventsAsync([shell], cancellationToken);
                terminalGate.MarkExternallyPublished(work.Event.EventId);
                Interlocked.Exchange(ref _lastTerminalPublishTicks, DateTime.UtcNow.Ticks);
                Interlocked.Increment(ref _pendingFollowUps);
                await _followUps.Writer.WriteAsync(work, cancellationToken);
                _ = ReinforceTerminalEventAsync(shell, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not replay pending suspension {Path}.", path); }
        }
    }

    private static async Task PersistPendingAsync(SuspensionWork work, CancellationToken cancellationToken)
    {
        var finalPath = Path.Combine(OutboxDirectory, $"{work.Event.EventId}.json");
        var temporaryPath = finalPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(work), cancellationToken);
        File.Move(temporaryPath, finalPath, true);
    }

    private static void DeletePending(long eventId)
    {
        var path = Path.Combine(OutboxDirectory, $"{eventId}.json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static int OutboxDepth()
    {
        try { return Directory.Exists(OutboxDirectory) ? Directory.EnumerateFiles(OutboxDirectory, "*.json").Count() : 0; }
        catch { return -1; }
    }

    private static DateTime? TicksToUtc(long ticks) =>
        ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    private async Task ReinforceTerminalEventAsync(PricingEvent shell, CancellationToken cancellationToken)
    {
        for (var sendNumber = 2; sendNumber <= 5; sendNumber++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                await publisher.PublishEventsAsync([shell], cancellationToken);
                diagnostics.Write("suspension", "terminal-event-reinforced", new
                {
                    shell.EventId, shell.Meeting, shell.Name, sendNumber, totalSends = 5,
                    active = 0, display = 0
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not reinforce terminal event {EventId} ({SendNumber}/5).",
                    shell.EventId, sendNumber);
            }
        }
    }
}
