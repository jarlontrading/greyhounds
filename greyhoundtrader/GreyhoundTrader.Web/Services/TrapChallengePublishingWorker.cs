using System.Globalization;
using System.Collections.Concurrent;

namespace RacingTrader.Web.Services;

internal sealed class TrapChallengePublishingWorker(
    RacingRepository repository,
    TrapChallengeService trapChallenges,
    RacingPublisher publisher,
    ILogger<TrapChallengePublishingWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<long, string> _publishedSignatures = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _publishLocks = new();

    internal async Task PublishMeetingAsync(
        string meeting, long startTimeUnix, CancellationToken cancellationToken)
    {
        var snapshot = await trapChallenges.BuildPublishingSnapshotAsync(
            meeting, startTimeUnix, cancellationToken);
        if (snapshot is null) return;

        var publishLock = _publishLocks.GetOrAdd(snapshot.Event.EventId, _ => new SemaphoreSlim(1, 1));
        await publishLock.WaitAsync(cancellationToken);
        try
        {
            var signature = Signature(snapshot);
            if (_publishedSignatures.GetValueOrDefault(snapshot.Event.EventId) == signature) return;

            logger.LogInformation(
                "Sending {EventName} ({EventId}), market {MarketId}, {SelectionCount} selections to Sportuno.",
                snapshot.Event.Name, snapshot.Event.EventId, snapshot.Markets[0].MarketId,
                snapshot.Markets[0].Selections.Count);
            if (snapshot.Event.Status == "3" && !_publishedSignatures.ContainsKey(snapshot.Event.EventId))
                await publisher.PublishAsync(AsOpenSuspended(snapshot), cancellationToken);
            await publisher.PublishAsync(snapshot, cancellationToken);
            _publishedSignatures[snapshot.Event.EventId] = signature;
            logger.LogInformation(
                "Published {EventName} ({EventId}) to Sportuno: {State}, {SelectionCount} selections.",
                snapshot.Event.Name, snapshot.Event.EventId,
                snapshot.Event.Status == "3" ? "resulted" : snapshot.Markets[0].Active ? "active" : "suspended",
                snapshot.Markets[0].Selections.Count);
        }
        finally
        {
            publishLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the one-shot Timeform tissue cache time to fill before the first
        // post-restart snapshot, avoiding a transient suspended republish.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var meetings = (await repository.GetMeetingsAsync(4, stoppingToken))
                    .Where(meeting => meeting.Subscribed && meeting.EventCount >= 2)
                    .OrderBy(meeting => meeting.FirstStartUnix)
                    .ToList();
                foreach (var meeting in meetings)
                {
                    try
                    {
                        await PublishMeetingAsync(meeting.Name, meeting.FirstStartUnix, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not publish the {Meeting} Trap Challenge.", meeting.Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh Trap Challenge Sportuno messages.");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private static GeneratedEventSnapshot AsOpenSuspended(GeneratedEventSnapshot resulted)
    {
        var markets = resulted.Markets.Select(market => market with
        {
            Active = false,
            Display = true,
            Selections = market.Selections.Select(selection => new GeneratedSelection
            {
                EventId = selection.EventId,
                MarketName = selection.MarketName,
                SelectionId = selection.SelectionId,
                CompetitorId = selection.CompetitorId,
                Name = selection.Name,
                Identifier = selection.Identifier,
                PriceBack = selection.PriceBack,
                PriceBackLast = selection.PriceBackLast,
                PriceLay = selection.PriceLay,
                Index = selection.Index,
                Line = selection.Line,
                Active = 0,
                Display = 1,
                Scratch = 0,
                Rule4Time = null,
                Rule4Deduction = 0,
                AssetUrl = selection.AssetUrl,
                Result = null,
                MarketId = selection.MarketId
            }).ToList()
        }).ToList();
        return new GeneratedEventSnapshot(resulted.Event with { Status = "0" }, markets);
    }

    private static string Signature(GeneratedEventSnapshot snapshot)
    {
        var market = snapshot.Markets[0];
        return string.Join('|', new[]
        {
            snapshot.Event.Status,
            market.Active ? "1" : "0",
            market.Display ? "1" : "0",
            string.Join(';', market.Selections.Select(selection => string.Join(',',
                selection.SelectionId.ToString(CultureInfo.InvariantCulture),
                selection.Name,
                selection.PriceBack.ToString("0.########", CultureInfo.InvariantCulture),
                selection.Active.ToString(CultureInfo.InvariantCulture),
                selection.Rule4Deduction.ToString("0.####", CultureInfo.InvariantCulture),
                selection.Result ?? "")))
        });
    }
}
