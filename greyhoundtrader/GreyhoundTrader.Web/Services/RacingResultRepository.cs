using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using MySqlConnector;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class RacingResultRepository
{
    private sealed record StoredRunnerResult(long SelectionId,string Name,int? Position,string Status);
    private sealed record SelectionResultState(string? Result,bool Active,bool Display,bool Scratch);

    private readonly string _connectionString;
    private readonly ILogger<RacingResultRepository> _logger;
    public RacingResultRepository(ExternalSettings settings, ILogger<RacingResultRepository> logger)
    {
        _logger = logger;
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server=settings.MySqlServer,UserID=settings.MySqlUser,Password=settings.MySqlPassword,Database=settings.RacingDatabase,
            SslMode=MySqlSslMode.Preferred,ConnectionTimeout=8,DefaultCommandTimeout=20,AllowUserVariables=true,
            MaximumPoolSize=1,ConnectionIdleTimeout=30
        }.ConnectionString;
    }
    private MySqlConnection OpenConnection()=>new(_connectionString);

    public async Task<IReadOnlyList<ResultCandidate>> GetCandidatesAsync(CancellationToken cancellationToken, bool allToday = false)
    {
        const string sql="""
            SELECT e.event_id,e.sport_id,e.meeting,e.ev_name,e.start_time_unix FROM spuno_events e
            WHERE e.sport_id=4
              AND (@allToday=0 OR e.start_time_unix>=@todayStart)
              AND (
                    e.send_to_sp<>0
                    OR EXISTS (SELECT 1 FROM spuno_markets m WHERE m.event_id=e.event_id)
                  )
              -- Routine recovery only needs yesterday and today. Scanning a
              -- full week caused each restart to drain old unresolved races in
              -- 120-event batches and publish a stream of closed/no-result
              -- messages to Sportuno.
              AND e.start_time_unix BETWEEN UNIX_TIMESTAMP()-129600
                                        AND UNIX_TIMESTAMP()-30
              AND (
                    (@allToday=1 AND e.start_time_unix>=@todayStart)
                    OR COALESCE(NULLIF(e.status,''),'0') NOT IN('3','resulted','post game')
                    OR (EXISTS (SELECT 1 FROM spuno_markets m WHERE m.event_id=e.event_id AND MOD(m.market_id,10000) IN (9008,9011))
                        AND NOT EXISTS (SELECT 1 FROM web_runner_results rr WHERE rr.event_id=e.event_id AND rr.finishing_position=4))
                    OR (
                         e.start_time_unix>=UNIX_TIMESTAMP()-129600
                         AND NOT EXISTS (
                             SELECT 1 FROM web_runner_results rr
                             WHERE rr.event_id=e.event_id AND rr.finishing_position IS NOT NULL
                         )
                       )
                  )
            ORDER BY
              CASE WHEN COALESCE(NULLIF(e.status,''),'0') IN('3','resulted','post game') THEN 1 ELSE 0 END,
              e.start_time_unix DESC
            LIMIT 120;
            """;
        var output=new List<ResultCandidate>();
        await using var connection=OpenConnection();await connection.OpenAsync(cancellationToken);
        await using var command=new MySqlCommand(sql,connection);
        command.Parameters.AddWithValue("@allToday",allToday);
        command.Parameters.AddWithValue("@todayStart",new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds());
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) output.Add(new(reader.GetInt64(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4)));
        return output;
    }

    public async Task<GeneratedEventSnapshot?> SettleAsync(ResultPage resultPage,CancellationToken cancellationToken)
    {
        await using var connection=OpenConnection();await connection.OpenAsync(cancellationToken);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
        var ev=await LoadEventAsync(connection,transaction,resultPage.EventId,cancellationToken);
        if(ev is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        // A delayed fast/provisional page must never reopen a race after a
        // confirmed final has closed it.
        if(StatusEquals(ev.Status,"3")&&!resultPage.IsFinal)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        var wasSuspended=StatusEquals(ev.Status,"2");
        var selections=await LoadSelectionsAsync(connection,transaction,resultPage.EventId,cancellationToken);
        if(resultPage.IsFinal&&resultPage.IsVoid)
        {
            var alreadyVoided=StatusEquals(ev.Status,"3")&&
                selections.Count>0&&selections.All(selection=>selection.Result==RacingResultCodes.Void&&!selection.Active);
            if(alreadyVoided)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            foreach(var selection in selections)
            {
                selection.Active=false;
                selection.Display=false;
                selection.Result=RacingResultCodes.Void;
            }
            await using(var voidRace=new MySqlCommand("""
                UPDATE spuno_selections
                SET result_text=@voidResult,active=0,display=0
                WHERE event_id=@eventId;
                UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId;
                UPDATE spuno_events SET status='3',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
                UPDATE em_events_raw SET time_status=3,status=3,active=0,display=0 WHERE betsapi_event_id=@eventId;
                """,connection,transaction))
            {
                voidRace.Parameters.AddWithValue("@voidResult",RacingResultCodes.Void);
                voidRace.Parameters.AddWithValue("@eventId",resultPage.EventId);
                await voidRace.ExecuteNonQueryAsync(cancellationToken);
            }
            await ReplaceRunnerResultsAsync(connection,transaction,resultPage,selections,cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var voidMarkets=selections.GroupBy(selection=>selection.MarketId).Select(group=>
            {
                var first=group.First();
                var suffix=Suffix(first.MarketId);
                var marketName=first.MarketName??MarketName(first.MarketId,"Market");
                var generated=group.Select(selection=>new GeneratedSelection
                {
                    EventId=selection.EventId,MarketId=selection.MarketId,SelectionId=selection.SelectionId,
                    CompetitorId=selection.CompetitorId,MarketName=marketName,Name=selection.Name,
                    Identifier=selection.Identifier,PriceBack=selection.Price,PriceBackLast=selection.PreviousPrice,
                    PriceLay=0,Active=0,Display=0,Scratch=selection.Scratch?1:0,
                    Rule4Time=selection.Rule4Time,Rule4Deduction=selection.Rule4Deduction,
                    AssetUrl=selection.AssetUrl,Result=RacingResultCodes.Void
                }).ToList();
                return new GeneratedMarket(first.MarketId,marketName,MarketType(suffix),DisplayOrder(suffix),false,false,generated);
            }).OrderBy(market=>market.DisplayOrder).ToList();
            _logger.LogWarning("Voided no-race event {EventId} from {SourceUrl}.",resultPage.EventId,resultPage.SourceUrl);
            return new GeneratedEventSnapshot(ev with{Status="3"},voidMarkets);
        }
        var raceWinnerSelections=selections.Where(x=>Suffix(x.MarketId)==9001).ToList();
        // Exception recovery: an event can reach UAT before its market hierarchy.
        // A complete final still closes that orphan event, while normal events
        // continue through the stricter market/selection validation below.
        if(raceWinnerSelections.Count==0)
        {
            if(!resultPage.IsFinal||resultPage.Runners.Count(x=>x.Position==1)!=1||
               resultPage.ExpectedRunnerCount is not >0||
               resultPage.Runners.Count(x=>x.Position is>0)!=resultPage.ExpectedRunnerCount)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            if(StatusEquals(ev.Status,"3"))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
            await using(var closeEvent=new MySqlCommand("""
                UPDATE spuno_events SET status='3',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
                UPDATE em_events_raw SET time_status='3',status='3',active=0,display=0 WHERE betsapi_event_id=@eventId;
                """,connection,transaction))
            {
                closeEvent.Parameters.AddWithValue("@eventId",resultPage.EventId);
                await closeEvent.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            _logger.LogWarning("Closed event-only final result for event {EventId}; no Race Winner market existed. Source={SourceUrl}",resultPage.EventId,resultPage.SourceUrl);
            return new GeneratedEventSnapshot(ev with{Status="3"},[]);
        }
        var participantRaceWinnerSelections=raceWinnerSelections.Where(x=>!IsVacantTrap(x.Name)).ToList();
        var positions=BuildPositions(resultPage.Runners);
        var positionedRunners=resultPage.Runners.Where(x=>x.Position is>0).ToList();
        var resultWinners=positionedRunners.Where(x=>x.Position==1).ToList();
        var matchedRunnerCount=positionedRunners.Count(r=>participantRaceWinnerSelections.Any(x=>Matches(x,r)));
        var matchedParticipantCount=resultPage.Runners.Count(
            result=>participantRaceWinnerSelections.Any(selection=>Matches(selection,result)));
        var matchedWinners=participantRaceWinnerSelections.Where(x=>Position(x,positions)==1).ToList();
        // The trap decides the match, so a card change that moves a dog between traps now
        // settles on the trap instead of silently matching two selections. Record it: for
        // greyhounds a trap/name disagreement is the signal that the card changed.
        foreach(var selection in participantRaceWinnerSelections)
            foreach(var runner in resultPage.Runners)
            {
                if(!TrapsAgree(selection,runner))continue;
                if(Normalize(selection.Name)==Normalize(runner.Name))continue;
                _logger.LogWarning(
                    "Trap/name disagreement for event {EventId}: trap {Trap} is {StoredName} on our card but {ResultName} in the result; settling on the trap. Source={SourceUrl}",
                    resultPage.EventId,selection.Identifier,selection.Name,runner.Name,resultPage.SourceUrl);
            }
        // Dead heats: two or more runners returned on the same finishing position. Count
        // distinct runners per position so a duplicated source row is not read as a tie.
        var deadHeats=positionedRunners
            .GroupBy(x=>x.Position!.Value)
            .Select(group=>(Position:group.Key,Count:group
                .Select(x=>string.IsNullOrWhiteSpace(x.Identifier)
                    ?"name:"+Normalize(x.Name)
                    :"trap:"+NormalizeIdentifier(x.Identifier!))
                .Distinct(StringComparer.Ordinal).Count()))
            .Where(x=>x.Count>1)
            .OrderBy(x=>x.Position)
            .ToList();
        // A dead heat for 1st used to be rejected outright here, by BetsApiResultClient and
        // by the Sporting Life finality check, so no source could settle the race and it sat
        // until the 24-hour stale close shut it with NULL results. Admit it: the per-market
        // dead-heat check below decides what can still be settled.
        if(resultWinners.Count<1||matchedWinners.Count!=resultWinners.Count||
           matchedRunnerCount!=positionedRunners.Count)
        {
            _logger.LogError(
                "Rejected unmatched result for event {EventId}: result winners={ResultWinners}, matched winners={MatchedWinners}, matched positioned runners={MatchedRunners}/{PositionedRunners}, final={IsFinal}, source={SourceUrl}",
                resultPage.EventId,resultWinners.Count,matchedWinners.Count,matchedRunnerCount,
                positionedRunners.Count,resultPage.IsFinal,resultPage.SourceUrl);
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        if(resultPage.IsFinal)
        {
            var fingerprint=RaceRunnerFingerprint.Compare(
                // Sporting Life's full-result rows contain the declared
                // starters; non-runners are listed separately. Compare that
                // starter set with our non-scratched runners so legitimate
                // late non-runners do not make an otherwise exact result fail.
                participantRaceWinnerSelections.Where(x=>!x.Scratch).Select(x=>x.Name),
                resultPage.Runners.Select(x=>x.Name));
            // BetsAPI's greyhound result payload guarantees the placed traps but
            // does not always repeat the complete field. A complete, unique top
            // three is sufficient for every enabled greyhound settlement market.
            var topThreeTrapFinal = resultPage.SportId == 4 &&
                resultPage.ExpectedRunnerCount is null &&
                resultPage.Runners.Count >= 3 &&
                resultPage.Runners.Count(x => x.Position == 1) == 1 &&
                resultPage.Runners.All(x => x.Position is >= 1 and <= 3) &&
                (resultPage.Runners.Any(x => x.Position == 3) ||
                 resultPage.Runners.Count(x => x.Position == 2) >= 2) &&
                resultPage.Runners.Select(x => x.Identifier).Distinct().Count() == 3;
            var participantIdentityMatches = resultPage.SportId == 4
                ? topThreeTrapFinal || TrapIdentifiersMatch(participantRaceWinnerSelections, resultPage.Runners)
                : fingerprint.IsMatch;
            // A full greyhound result names the declared starters, while our
            // stored card can still contain a late non-runner that was never
            // flagged by the price feed. Accept the final when every declared
            // starter and positioned runner matched uniquely; participation is
            // updated immediately below and the omitted runner is then voided.
            var declaredStartersMatch = resultPage.SportId == 4 &&
                resultPage.ExpectedRunnerCount is > 0 &&
                positionedRunners.Count == resultPage.ExpectedRunnerCount.Value &&
                matchedRunnerCount == positionedRunners.Count &&
                matchedParticipantCount == resultPage.ExpectedRunnerCount.Value;
            participantIdentityMatches |= declaredStartersMatch;
            var singleRunnerFinal=resultPage.ExpectedRunnerCount==1&&matchedParticipantCount==1;
            if(!participantIdentityMatches&&!singleRunnerFinal||
               resultPage.ExpectedRunnerCount is>0&&matchedParticipantCount<resultPage.ExpectedRunnerCount.Value)
            {
                _logger.LogError(
                    "Rejected incomplete final result for event {EventId}: fingerprint={FingerprintMatches}/{FingerprintLargest} ({FingerprintCoverage:P0}), matched participants={MatchedParticipants}, positioned finishers={PositionedFinishers}, expected starters={ExpectedRunners}, source={SourceUrl}",
                    resultPage.EventId,fingerprint.MatchedCount,Math.Max(fingerprint.ExpectedCount,fingerprint.ActualCount),
                    fingerprint.Coverage,matchedParticipantCount,matchedRunnerCount,
                    resultPage.ExpectedRunnerCount,resultPage.SourceUrl);
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }
        }
        // A trustworthy result is itself terminal evidence. Requiring an earlier
        // provider OFF flag made one missed/transient suspension signal also block
        // settlement indefinitely. The client has already matched the dated race
        // card by meeting and off time, and the checks above require the winner and
        // every reported finisher to match this event's stored runners before an
        // active event is allowed through.
        if(!wasSuspended)
            _logger.LogWarning(
                "Result is closing previously unsuspended event {EventId}: final={IsFinal}, source={SourceUrl}",
                resultPage.EventId,resultPage.IsFinal,resultPage.SourceUrl);
        var storedResults=await LoadStoredRunnerResultsAsync(connection,transaction,resultPage.EventId,cancellationToken);
        var incomingDepth=positionedRunners.Max(x=>x.Position!.Value);
        var storedDepth=storedResults.Where(x=>x.Position is>0).Select(x=>x.Position!.Value).DefaultIfEmpty(0).Max();
        if(incomingDepth<storedDepth)
        {
            _logger.LogInformation(
                "Ignored regressive provisional result for event {EventId}: incoming depth {IncomingDepth}, stored depth {StoredDepth}.",
                resultPage.EventId,incomingDepth,storedDepth);
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        var previousState=selections.ToDictionary(
            x=>(x.MarketId,x.SelectionId),
            x=>new SelectionResultState(x.Result,x.Active,x.Display,x.Scratch));
        // Arbitrary among joint winners. Only Greyhound Groups (9003) consumes it, and that
        // market is always voided when the tie reaches position 1, so it is never read for an
        // ambiguous race.
        var winner=matchedWinners[0];
        var winnerIdentifier=int.TryParse(winner.Identifier,out var wi)?wi:0;
        var knownDepth=incomingDepth;
        ApplyRunnerParticipation(selections,raceWinnerSelections,resultPage);
        var settledMarkets=new List<List<PricingRunner>>();
        foreach(var market in selections.GroupBy(x=>x.MarketId))
        {
            var suffix=Suffix(market.Key);if(!IsResultMarket(suffix))continue;
            var list=market.ToList();
            if(!CanSettleMarket(suffix,list,positions,resultPage.IsFinal))continue;
            if(deadHeats.Count>0&&DeadHeatBlocks(suffix,deadHeats,list,positions))
            {
                var tieText=string.Join(", ",deadHeats.Select(tie=>$"{tie.Count} runners on position {tie.Position}"));
                var blockedName=list[0].MarketName??MarketName(market.Key,"Market");
                // Never void off a provisional read - hold the market and wait for the final
                // result, which may resolve or confirm the tie.
                if(!resultPage.IsFinal)
                {
                    _logger.LogWarning(
                        "Dead heat on event {EventId} ({DeadHeats}) leaves market {MarketId} ({MarketName}) unsettleable; holding it until the result is final. Source={SourceUrl}",
                        resultPage.EventId,tieText,market.Key,blockedName,resultPage.SourceUrl);
                    continue;
                }
                // Correct settlement is a half (or third) stake at full odds, and result_text
                // carries no stake fraction, so a void is the only outcome this schema can
                // express. It is wrong but bounded, and it is now deliberate and logged
                // rather than the market silently paying two winners or none.
                _logger.LogError(
                    "Dead heat on event {EventId} ({DeadHeats}) makes market {MarketId} ({MarketName}) unsettleable; voiding it. Source={SourceUrl}",
                    resultPage.EventId,tieText,market.Key,blockedName,resultPage.SourceUrl);
                foreach(var selection in list)
                {selection.Active=false;selection.Display=false;selection.Result=RacingResultCodes.Void;}
                settledMarkets.Add(list);
                continue;
            }
            if(suffix is 9400 or 9500)
            {
                if(!SettleTrapProductLine(list,resultPage.Runners,suffix==9400?2:3))continue;
                settledMarkets.Add(list);
                continue;
            }
            if(suffix is>=9300 and<=9319)
            {
                if(!SettleForecast(list,resultPage.Runners,selections,resultPage.IsFinal))continue;
                settledMarkets.Add(list);
                continue;
            }
            if(suffix is>=9100 and<=9199)
            {
                if(!SettleMatch(list,positions))continue;
                settledMarkets.Add(list);
                continue;
            }
            PricingRunner? withoutWinner=suffix is>=9200 and<=9299?list.Where(x=>!x.Scratch&&Position(x,positions)>0).OrderBy(x=>Position(x,positions)).FirstOrDefault():null;
            foreach(var selection in list)
            {
                selection.Active=false;selection.Display=false;
                if(selection.Scratch){selection.Result=RacingResultCodes.Void;continue;}
                var position=Position(selection,positions);
                selection.Result=suffix switch
                {
                    9001=>position==1?"1":"0",
                    9002=>PlaceResult(position,2,knownDepth,resultPage.IsFinal),
                    9004=>PlaceResult(position,3,knownDepth,resultPage.IsFinal),
                    9011=>PlaceResult(position,4,knownDepth,resultPage.IsFinal),
                    >=9200 and<=9299=>ReferenceEquals(selection,withoutWinner)?"1":"0",
                    9006=>InsureResult(position,2,knownDepth,resultPage.IsFinal),
                    9007=>InsureResult(position,3,knownDepth,resultPage.IsFinal),
                    9008=>InsureResult(position,4,knownDepth,resultPage.IsFinal),
                    9009=>IndexResult(position,knownDepth,resultPage.IsFinal,50,25,10),
                    9010=>IndexResult(position,knownDepth,resultPage.IsFinal,50,30,20,10),
                    9003=>WinningGroup(selection.Name,winnerIdentifier)?"1":"0",
                    _=>resultPage.IsFinal?"0":null
                };
            }
            settledMarkets.Add(list);
        }
        var runnerResultsChanged=!RunnerResultsEqual(storedResults,resultPage.Runners);
        var selectionResultsChanged=settledMarkets.SelectMany(x=>x).Any(selection=>
        {
            var previous=previousState[(selection.MarketId,selection.SelectionId)];
            return previous.Result!=selection.Result||previous.Active!=selection.Active||
                   previous.Display!=selection.Display||previous.Scratch!=selection.Scratch;
        });
        var targetStatus=resultPage.IsFinal?"3":"2";
        var eventStatusChanged=!StatusEquals(ev.Status,targetStatus);
        if(!runnerResultsChanged&&!selectionResultsChanged&&!eventStatusChanged)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        foreach(var selection in settledMarkets.SelectMany(x=>x))
        {
            await using var update=new MySqlCommand("UPDATE spuno_selections SET result_text=@result,active=0,display=0,scratch=@scratch,rule4_time=IF(@scratch=0,NULL,rule4_time),rule4_deduction=IF(@scratch=0,0,rule4_deduction) WHERE event_id=@eventId AND market_id=@marketId AND selection_id=@selectionId",connection,transaction);
            update.Parameters.AddWithValue("@result",selection.Result is null?DBNull.Value:selection.Result);update.Parameters.AddWithValue("@eventId",resultPage.EventId);
            update.Parameters.AddWithValue("@marketId",selection.MarketId);update.Parameters.AddWithValue("@selectionId",selection.SelectionId);update.Parameters.AddWithValue("@scratch",selection.Scratch?1:0);await update.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach(var marketId in settledMarkets.Select(x=>x[0].MarketId).Distinct())
        {
            await using var closeMarket=new MySqlCommand("UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId AND market_id=@marketId",connection,transaction);
            closeMarket.Parameters.AddWithValue("@eventId",resultPage.EventId);
            closeMarket.Parameters.AddWithValue("@marketId",marketId);
            await closeMarket.ExecuteNonQueryAsync(cancellationToken);
        }
        if(resultPage.IsFinal)
        {
            // A final result used to close every market on the event, including the ones
            // CanSettleMarket declined - left active=0 with NULL results, so the race was
            // published as resulted with those liabilities neither paid nor refunded, and
            // CloseUnresultedAsync could not reach them afterwards because the event was
            // already status 3. BetsAPI only reports 1st-3rd and a three-row Betfred page
            // counts as final, so Top 4, Insure 4 and 50-30-20-10 land here routinely.
            // Void them instead: wrong, but bounded, and it matches the no-race path.
            var settledMarketIds=settledMarkets.Select(x=>x[0].MarketId).ToHashSet();
            foreach(var market in selections.GroupBy(x=>x.MarketId).ToList())
            {
                if(!IsResultMarket(Suffix(market.Key))||settledMarketIds.Contains(market.Key))continue;
                var unsettled=market.ToList();
                if(unsettled.All(x=>x.Result is not null))continue;
                _logger.LogError(
                    "Voiding unsettleable market {MarketId} ({MarketName}) on the final result for event {EventId}; the result did not carry enough placings to settle it. Source={SourceUrl}",
                    market.Key,unsettled[0].MarketName??MarketName(market.Key,"Market"),
                    resultPage.EventId,resultPage.SourceUrl);
                foreach(var selection in unsettled)
                {
                    selection.Active=false;selection.Display=false;selection.Result=RacingResultCodes.Void;
                    await using var voidSelection=new MySqlCommand("UPDATE spuno_selections SET result_text=@voidResult,active=0,display=0 WHERE event_id=@eventId AND market_id=@marketId AND selection_id=@selectionId",connection,transaction);
                    voidSelection.Parameters.AddWithValue("@voidResult",RacingResultCodes.Void);
                    voidSelection.Parameters.AddWithValue("@eventId",resultPage.EventId);
                    voidSelection.Parameters.AddWithValue("@marketId",selection.MarketId);
                    voidSelection.Parameters.AddWithValue("@selectionId",selection.SelectionId);
                    await voidSelection.ExecuteNonQueryAsync(cancellationToken);
                }
                // Publish the void as well, otherwise Sportuno keeps the open liability.
                settledMarkets.Add(unsettled);
            }
            await using var closeMarkets=new MySqlCommand("UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId; UPDATE spuno_selections SET active=0,display=0 WHERE event_id=@eventId AND MOD(market_id,10000)=9600;",connection,transaction);
            closeMarkets.Parameters.AddWithValue("@eventId",resultPage.EventId);
            await closeMarkets.ExecuteNonQueryAsync(cancellationToken);
        }
        await using(var closeEvent=new MySqlCommand("""
            UPDATE spuno_events SET status=@status,active=0,display=0,in_progress=0 WHERE event_id=@eventId;
            UPDATE em_events_raw SET time_status=@status,status=@status,active=0,display=0 WHERE betsapi_event_id=@eventId;
            """,connection,transaction))
        {
            closeEvent.Parameters.AddWithValue("@eventId",resultPage.EventId);
            closeEvent.Parameters.AddWithValue("@status",targetStatus);
            await closeEvent.ExecuteNonQueryAsync(cancellationToken);
        }
        await ReplaceRunnerResultsAsync(connection,transaction,resultPage,selections,cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var markets=settledMarkets.Select(group=>
        {
            var first=group[0];var suffix=Suffix(first.MarketId);var marketName=first.MarketName??MarketName(first.MarketId,"Market");
            var generated=group.Select(x=>new GeneratedSelection
            {EventId=x.EventId,MarketId=x.MarketId,SelectionId=x.SelectionId,CompetitorId=x.CompetitorId,MarketName=marketName,Name=x.Name,Identifier=x.Identifier,
             PriceBack=x.Price,PriceBackLast=x.PreviousPrice,PriceLay=0,Active=0,Display=0,Scratch=x.Scratch?1:0,
             Rule4Time=x.Rule4Time,Rule4Deduction=x.Rule4Deduction,AssetUrl=x.AssetUrl,Result=x.Result}).ToList();
            return new GeneratedMarket(first.MarketId,marketName,MarketType(suffix),DisplayOrder(suffix),false,false,generated);
        }).OrderBy(x=>x.DisplayOrder).ToList();
        return new GeneratedEventSnapshot(ev with{Status=targetStatus},markets);
    }

    public async Task<PricingEvent?> RevertFalseResultToSuspendedAsync(
        long eventId,CancellationToken cancellationToken)
    {
        await using var connection=OpenConnection();
        await connection.OpenAsync(cancellationToken);
        PricingEvent? ev=null;
        await using(var load=new MySqlCommand("""
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix,status,0
            FROM spuno_events WHERE event_id=@eventId;
            """,connection))
        {
            load.Parameters.AddWithValue("@eventId",eventId);
            await using var reader=await load.ExecuteReaderAsync(cancellationToken);
            if(await reader.ReadAsync(cancellationToken))
                ev=new(reader.GetInt64(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),
                    reader.GetInt64(4),reader.GetString(5),reader.GetInt64(6));
        }
        if(ev is null)return null;
        if(StatusEquals(ev.Status,"2"))
        {
            await using var hasResults=new MySqlCommand("""
                SELECT EXISTS(SELECT 1 FROM web_runner_results WHERE event_id=@eventId)
                    OR EXISTS(SELECT 1 FROM spuno_selections WHERE event_id=@eventId AND result_text IS NOT NULL);
                """,connection);
            hasResults.Parameters.AddWithValue("@eventId",eventId);
            if(!Convert.ToBoolean(await hasResults.ExecuteScalarAsync(cancellationToken)))
                return ev;
        }

        await using(var clearResults=new MySqlCommand("""
            DELETE FROM web_runner_results WHERE event_id=@eventId;
            UPDATE spuno_selections
            SET result_text=NULL,active=0,display=0,scratch=0
            WHERE event_id=@eventId;
            UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId;
            UPDATE spuno_events SET status='2',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
            UPDATE em_events_raw SET time_status=2,status=2,active=0,display=0 WHERE betsapi_event_id=@eventId;
            """,connection))
        {
            clearResults.CommandTimeout=90;
            clearResults.Parameters.AddWithValue("@eventId",eventId);
            await clearResults.ExecuteNonQueryAsync(cancellationToken);
        }

        // Restore non-runners recorded before the false result. Settlement had
        // treated every runner on the wrong result card as a participant.
        try
        {
          await using var restoreWithdrawals=new MySqlCommand("""
            UPDATE spuno_selections target
            JOIN (
                SELECT DISTINCT withdrawn.event_id,withdrawn.competitor_id
                FROM spuno_selections withdrawn
                JOIN web_market_adjustments adjustment
                  ON adjustment.event_id=withdrawn.event_id
                 AND adjustment.withdrawn_selection_id=withdrawn.selection_id
                 AND adjustment.adjustment_type='RULE4'
                WHERE withdrawn.event_id=@eventId
            ) withdrawn_runner
              ON withdrawn_runner.event_id=target.event_id
             AND withdrawn_runner.competitor_id=target.competitor_id
            SET target.scratch=1
            WHERE target.event_id=@eventId;
            """,connection);
          restoreWithdrawals.CommandTimeout=90;
          restoreWithdrawals.Parameters.AddWithValue("@eventId",eventId);
          await restoreWithdrawals.ExecuteNonQueryAsync(cancellationToken);
        }
        catch(MySqlException ex)
        {
            _logger.LogWarning(ex,
                "False result was removed for event {EventId}, but restoring historic non-runner flags will retry separately.",
                eventId);
        }
        return ev with{Status="2"};
    }

    public async Task<GeneratedEventSnapshot?> SettleForecastsAsync(
        ResultPage resultPage,CancellationToken cancellationToken)
    {
        if(!TryGetForecastOrder(resultPage.Runners,out _,out _))return null;
        await using var connection=OpenConnection();await connection.OpenAsync(cancellationToken);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
        var ev=await LoadEventAsync(connection,transaction,resultPage.EventId,cancellationToken);
        if(ev is null)return null;
        var selections=await LoadSelectionsAsync(connection,transaction,resultPage.EventId,cancellationToken);
        var raceWinnerSelections=selections.Where(x=>Suffix(x.MarketId)==9001).ToList();
        var fingerprint=RaceRunnerFingerprint.Compare(
            raceWinnerSelections.Select(x=>x.Name),
            resultPage.Runners.Select(x=>x.Name));
        if(!fingerprint.IsMatch)
        {
            _logger.LogError(
                "Rejected unmatched forecast result for event {EventId}: fingerprint={FingerprintMatches}/{FingerprintLargest} ({FingerprintCoverage:P0}), source={SourceUrl}",
                resultPage.EventId,fingerprint.MatchedCount,Math.Max(fingerprint.ExpectedCount,fingerprint.ActualCount),
                fingerprint.Coverage,resultPage.SourceUrl);
            return null;
        }
        var settledMarkets=new List<List<PricingRunner>>();
        foreach(var group in selections
                    .Where(x=>Suffix(x.MarketId) is>=9300 and<=9319)
                    .GroupBy(x=>x.MarketId))
        {
            var list=group.OrderBy(x=>x.SelectionId).ToList();
            if(list.Count==0)continue;
            if(!SettleForecast(list,resultPage.Runners,selections))continue;
            settledMarkets.Add(list);
        }
        if(settledMarkets.Count==0)return null;
        foreach(var list in settledMarkets)
        {
            foreach(var selection in list)
            {
                await using var update=new MySqlCommand("""
                    UPDATE spuno_selections
                    SET result_text=@result,active=0,display=0
                    WHERE event_id=@eventId AND market_id=@marketId AND selection_id=@selectionId;
                    """,connection,transaction);
                update.Parameters.AddWithValue("@result",selection.Result is null?DBNull.Value:selection.Result);
                update.Parameters.AddWithValue("@eventId",resultPage.EventId);
                update.Parameters.AddWithValue("@marketId",selection.MarketId);
                update.Parameters.AddWithValue("@selectionId",selection.SelectionId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var closeMarket=new MySqlCommand("""
                UPDATE spuno_markets SET active=0,display=0
                WHERE event_id=@eventId AND market_id=@marketId;
                """,connection,transaction);
            closeMarket.Parameters.AddWithValue("@eventId",resultPage.EventId);
            closeMarket.Parameters.AddWithValue("@marketId",list[0].MarketId);
            await closeMarket.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var markets=settledMarkets.Select(list=>
        {
            var first=list[0];var marketName=first.MarketName??MarketName(first.MarketId,"Forecast");
            var generated=list.Select(x=>new GeneratedSelection
            {
                EventId=x.EventId,MarketId=x.MarketId,SelectionId=x.SelectionId,CompetitorId=x.CompetitorId,
                MarketName=marketName,Name=x.Name,Identifier=x.Identifier,PriceBack=x.Price,
                PriceBackLast=x.PreviousPrice,PriceLay=0,Active=0,Display=0,Scratch=x.Scratch?1:0,
                Rule4Time=x.Rule4Time,Rule4Deduction=x.Rule4Deduction,AssetUrl=x.AssetUrl,Result=x.Result
            }).ToList();
            return new GeneratedMarket(first.MarketId,marketName,RacingMarketCalculator.ForecastMarketType,
                DisplayOrder(Suffix(first.MarketId)),false,false,generated);
        }).OrderBy(x=>x.DisplayOrder).ToList();
        return new GeneratedEventSnapshot(ev,markets);
    }

    public async Task<IReadOnlyList<long>> GetRecentForecastRepairEventIdsAsync(
        CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT DISTINCT e.event_id
            FROM spuno_events e
            JOIN web_forecast_pairs pair
              ON pair.event_id=e.event_id AND pair.enabled=1 AND pair.voided=0
            WHERE e.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 2 DAY)
              AND COALESCE(NULLIF(e.status,''),'0') IN('3','resulted','post game')
              AND EXISTS(
                  SELECT 1 FROM web_runner_results first_place
                  WHERE first_place.event_id=e.event_id AND first_place.finishing_position=1)
              AND EXISTS(
                  SELECT 1 FROM web_runner_results second_place
                  WHERE second_place.event_id=e.event_id AND second_place.finishing_position=2)
              AND EXISTS(
                  SELECT 1 FROM spuno_selections forecast_selection
                  WHERE forecast_selection.event_id=e.event_id
                    AND MOD(forecast_selection.market_id,10000) BETWEEN 9300 AND 9319
                    AND forecast_selection.result_text IS NULL)
            ORDER BY e.event_id;
            """;
        var eventIds=new List<long>();
        await using var connection=OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command=new MySqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))eventIds.Add(reader.GetInt64(0));
        return eventIds;
    }

    public async Task<IReadOnlyList<long>> GetRecentStoredResultEventIdsAsync(
        CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT DISTINCT e.event_id
            FROM spuno_events e
            JOIN web_runner_results rr ON rr.event_id=e.event_id
            WHERE e.send_to_sp<>0
              AND e.sport_id=4
              AND e.start_time_unix>=UNIX_TIMESTAMP(UTC_TIMESTAMP()-INTERVAL 12 HOUR)
              AND COALESCE(NULLIF(e.status,''),'0') IN('3','resulted','post game')
              AND EXISTS(
                  SELECT 1 FROM spuno_selections s
                  WHERE s.event_id=e.event_id AND s.result_text IS NOT NULL)
            ORDER BY e.event_id;
            """;
        var eventIds=new List<long>();
        await using var connection=OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command=new MySqlCommand(sql,connection);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))eventIds.Add(reader.GetInt64(0));
        return eventIds;
    }

    public async Task<GeneratedEventSnapshot?> BuildStoredResultSnapshotAsync(
        long eventId,CancellationToken cancellationToken)
        => await BuildStoredResultSnapshotAsync(eventId,true,cancellationToken);

    public async Task<GeneratedEventSnapshot?> BuildStoredProvisionalSnapshotAsync(
        long eventId,CancellationToken cancellationToken)
        => await BuildStoredResultSnapshotAsync(eventId,false,cancellationToken);

    private async Task<GeneratedEventSnapshot?> BuildStoredResultSnapshotAsync(
        long eventId,bool finalOnly,CancellationToken cancellationToken)
    {
        await using var connection=OpenConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
        var ev=await LoadEventAsync(connection,transaction,eventId,cancellationToken);
        if(ev is null||finalOnly&&!StatusEquals(ev.Status,"3")||
           !finalOnly&&!StatusEquals(ev.Status,"2")&&!StatusEquals(ev.Status,"3"))return null;
        var selections=await LoadSelectionsAsync(connection,transaction,eventId,cancellationToken);
        var markets=selections
            .GroupBy(x=>x.MarketId)
            .Where(group=>IsResultMarket(Suffix(group.Key))&&group.Any(x=>x.Result is not null))
            .Select(group=>
            {
                var first=group.First();var suffix=Suffix(group.Key);
                var marketName=first.MarketName??MarketName(group.Key,"Market");
                var generated=group.Select(x=>new GeneratedSelection
                {
                    EventId=x.EventId,MarketId=x.MarketId,SelectionId=x.SelectionId,
                    CompetitorId=x.CompetitorId,MarketName=marketName,Name=x.Name,
                    Identifier=x.Identifier,PriceBack=x.Price,PriceBackLast=x.PreviousPrice,
                    PriceLay=0,Active=0,Display=0,Scratch=x.Scratch?1:0,
                    Rule4Time=x.Rule4Time,Rule4Deduction=x.Rule4Deduction,AssetUrl=x.AssetUrl,Result=x.Result
                }).ToList();
                return new GeneratedMarket(group.Key,marketName,MarketType(suffix),
                    DisplayOrder(suffix),false,false,generated);
            })
            .OrderBy(x=>x.DisplayOrder).ToList();
        await transaction.CommitAsync(cancellationToken);
        return markets.Count==0?null:new GeneratedEventSnapshot(ev with{Status=StatusEquals(ev.Status,"3")?"3":"2"},markets);
    }

    public async Task<GeneratedEventSnapshot?> RepairForecastsAsync(
        long eventId,CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT e.sport_id,rr.runner_name,rr.finishing_position,rr.result_status,
                   rr.identifier,rr.source_url
            FROM spuno_events e
            JOIN web_runner_results rr ON rr.event_id=e.event_id
            WHERE e.event_id=@eventId
            ORDER BY rr.finishing_position IS NULL,rr.finishing_position,rr.runner_name;
            """;
        var runners=new List<ResultRunner>();
        var sportId=0;
        var sourceUrl="stored-result";
        await using(var connection=OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command=new MySqlCommand(sql,connection);
            command.Parameters.AddWithValue("@eventId",eventId);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken))
            {
                sportId=reader.GetInt32(0);
                if(!reader.IsDBNull(5))sourceUrl=reader.GetString(5);
                runners.Add(new ResultRunner(
                    reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetInt32(2),
                    reader.GetString(3),
                    reader.IsDBNull(4)?null:reader.GetString(4)));
            }
        }
        if(sportId==0||!TryGetForecastOrder(runners,out _,out _))return null;
        return await SettleForecastsAsync(
            new ResultPage(eventId,sportId,sourceUrl,runners,runners.Count,true),
            cancellationToken);
    }

    public async Task<GeneratedEventSnapshot?> RecomputeStoredResultAsync(
        long eventId,CancellationToken cancellationToken)
    {
        const string sql="""
            SELECT e.sport_id,rr.runner_name,rr.finishing_position,rr.result_status,
                   rr.identifier,rr.source_url
            FROM spuno_events e
            JOIN web_runner_results rr ON rr.event_id=e.event_id
            WHERE e.event_id=@eventId
            ORDER BY rr.finishing_position IS NULL,rr.finishing_position,rr.runner_name;
            """;
        var runners=new List<ResultRunner>();
        var sportId=0;
        var sourceUrl="stored-result";
        await using(var connection=OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command=new MySqlCommand(sql,connection);
            command.Parameters.AddWithValue("@eventId",eventId);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken))
            {
                sportId=reader.GetInt32(0);
                if(!reader.IsDBNull(5))sourceUrl=reader.GetString(5);
                runners.Add(new ResultRunner(reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetInt32(2),reader.GetString(3),
                    reader.IsDBNull(4)?null:reader.GetString(4)));
            }
        }
        var positioned=runners.Count(x=>x.Position is>0);
        if(sportId==0||positioned==0)return null;
        return await SettleAsync(new ResultPage(
            eventId,sportId,sourceUrl,runners,positioned,true),cancellationToken);
    }

    public async Task<GeneratedEventSnapshot?> PromoteStoredResultAsync(
        ResultPage confirmation,CancellationToken cancellationToken)
    {
        if(!confirmation.IsFinal||confirmation.Runners.Count<3)return null;
        const string sql="""
            SELECT e.sport_id,rr.runner_name,rr.finishing_position,rr.result_status,
                   rr.identifier
            FROM spuno_events e
            JOIN web_runner_results rr ON rr.event_id=e.event_id
            WHERE e.event_id=@eventId
            ORDER BY rr.finishing_position IS NULL,rr.finishing_position,rr.runner_name;
            """;
        var stored=new List<ResultRunner>();
        var sportId=0;
        await using(var connection=OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command=new MySqlCommand(sql,connection);
            command.Parameters.AddWithValue("@eventId",confirmation.EventId);
            await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken))
            {
                sportId=reader.GetInt32(0);
                stored.Add(new ResultRunner(reader.GetString(1),
                    reader.IsDBNull(2)?null:reader.GetInt32(2),reader.GetString(3),
                    reader.IsDBNull(4)?null:reader.GetString(4)));
            }
        }
        if(sportId==0||stored.Count==0)return null;
        var confirmsStoredPodium=confirmation.Runners.All(incoming=>stored.Any(existing=>
            existing.Position==incoming.Position &&
            ((!string.IsNullOrWhiteSpace(incoming.Identifier)&&
              string.Equals(existing.Identifier,incoming.Identifier,StringComparison.OrdinalIgnoreCase))||
             string.Equals(existing.Name,incoming.Name,StringComparison.OrdinalIgnoreCase))));
        if(!confirmsStoredPodium)return null;
        var promoted=await SettleAsync(new ResultPage(confirmation.EventId,sportId,
            confirmation.SourceUrl,stored,stored.Count(x=>x.Position is>0),true),cancellationToken);
        if(promoted is not null)return promoted;

        // The stored full card may contain a dead-heat rank sequence such as
        // 1,2,2,4,5,6 which some derived race markets cannot recompute. The
        // independently confirmed podium is still sufficient to make the
        // meeting winner count final, so promote the stored card atomically.
        await using(var connection=OpenConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command=new MySqlCommand("""
                UPDATE spuno_events SET status='3',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
                UPDATE em_events_raw SET time_status='3',status='3',active=0,display=0 WHERE betsapi_event_id=@eventId;
                UPDATE web_runner_results SET result_status='Final',source_url=@sourceUrl WHERE event_id=@eventId;
                """,connection);
            command.Parameters.AddWithValue("@eventId",confirmation.EventId);
            command.Parameters.AddWithValue("@sourceUrl",confirmation.SourceUrl);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return await BuildStoredProvisionalSnapshotAsync(confirmation.EventId,cancellationToken);
    }

    public async Task<GeneratedEventSnapshot?> CloseUnresultedAsync(long eventId,CancellationToken cancellationToken)
    {
        await using var connection=OpenConnection();await connection.OpenAsync(cancellationToken);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
        var ev=await LoadEventAsync(connection,transaction,eventId,cancellationToken);
        // Every other site compares status with StatusEquals, which also accepts "resulted"
        // and "post game". The raw compare here let a correctly settled race fall through and
        // be re-closed, republishing it as closed-without-result.
        if(ev is null||StatusEquals(ev.Status,"3"))return null;
        var selections=await LoadSelectionsAsync(connection,transaction,eventId,cancellationToken);
        if(selections.Count==0)return null;
        // This runs when no source could be matched inside the stale window. It used to set
        // active=0,display=0 and never touch result_text, so the race was published as
        // resulted with NULL results - liabilities neither paid nor refunded - and nothing
        // could reach it afterwards because the event was already status 3.
        //
        // "We could not match a result" and "there was no result" are different facts, and
        // only the second justifies a void. A void is the only one of the two this schema can
        // express, so take it and make the alert loud enough to act on.
        var unresulted=selections.Where(x=>x.Result is null).ToList();
        foreach(var selection in unresulted) selection.Result=RacingResultCodes.Void;
        _logger.LogError(
            "Closing event {EventId} ({EventName}) with no matched result; voiding {VoidedCount} of {SelectionCount} selections that were never settled.",
            eventId,ev.Name,unresulted.Count,selections.Count);
        await using(var closeSelections=new MySqlCommand("UPDATE spuno_selections SET active=0,display=0,result_text=COALESCE(result_text,@voidResult) WHERE event_id=@eventId",connection,transaction))
        {
            closeSelections.Parameters.AddWithValue("@eventId",eventId);
            closeSelections.Parameters.AddWithValue("@voidResult",RacingResultCodes.Void);
            await closeSelections.ExecuteNonQueryAsync(cancellationToken);
        }
        await using(var closeMarkets=new MySqlCommand("UPDATE spuno_markets SET active=0,display=0 WHERE event_id=@eventId",connection,transaction))
        {closeMarkets.Parameters.AddWithValue("@eventId",eventId);await closeMarkets.ExecuteNonQueryAsync(cancellationToken);}
        await using(var closeEvent=new MySqlCommand("""
            UPDATE spuno_events SET status='3',active=0,display=0,in_progress=0 WHERE event_id=@eventId;
            UPDATE em_events_raw SET time_status=3,status=3,active=0,display=0 WHERE betsapi_event_id=@eventId;
            """,connection,transaction))
        {closeEvent.Parameters.AddWithValue("@eventId",eventId);await closeEvent.ExecuteNonQueryAsync(cancellationToken);}
        await transaction.CommitAsync(cancellationToken);
        var markets=selections.GroupBy(x=>x.MarketId).Select(group=>
        {
            var suffix=Suffix(group.Key);var first=group.First();var marketName=first.MarketName??MarketName(group.Key,"Market");
            var generated=group.Select(x=>new GeneratedSelection
            {EventId=x.EventId,MarketId=x.MarketId,SelectionId=x.SelectionId,CompetitorId=x.CompetitorId,MarketName=marketName,Name=x.Name,Identifier=x.Identifier,
             PriceBack=x.Price,PriceBackLast=x.PreviousPrice,PriceLay=0,Active=0,Display=0,Scratch=x.Scratch?1:0,
             Rule4Time=x.Rule4Time,Rule4Deduction=x.Rule4Deduction,AssetUrl=x.AssetUrl,Result=x.Result}).ToList();
            return new GeneratedMarket(group.Key,marketName,MarketType(suffix),DisplayOrder(suffix),false,false,generated);
        }).OrderBy(x=>x.DisplayOrder).ToList();
        return new GeneratedEventSnapshot(ev with{Status="3"},markets);
    }

    private static async Task<PricingEvent?> LoadEventAsync(MySqlConnection c,MySqlTransaction t,long eventId,CancellationToken ct)
    {await using var cmd=new MySqlCommand("SELECT event_id,sport_id,meeting,ev_name,start_time_unix,status,0 FROM spuno_events WHERE event_id=@id FOR UPDATE",c,t);cmd.Parameters.AddWithValue("@id",eventId);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?new(r.GetInt64(0),r.GetInt32(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetString(5),0):null;}
    private static async Task<List<PricingRunner>> LoadSelectionsAsync(MySqlConnection c,MySqlTransaction t,long eventId,CancellationToken ct)
    {var o=new List<PricingRunner>();await using var cmd=new MySqlCommand("SELECT event_id,market_id,selection_id,competitor_id,name,identifier,price_back,price_back_last,active,display,scratch,asset_url,result_text,market_name,rule4_time,rule4_deduction,line FROM spuno_selections WHERE event_id=@id ORDER BY market_id,selection_id FOR UPDATE",c,t);cmd.Parameters.AddWithValue("@id",eventId);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))o.Add(new(){EventId=r.GetInt64(0),MarketId=r.GetInt64(1),SelectionId=r.GetInt64(2),CompetitorId=r.IsDBNull(3)?null:r.GetInt64(3),Name=r.GetString(4),Identifier=r.GetString(5),Price=r.GetDouble(6),PreviousPrice=r.GetDouble(7),Active=r.GetBoolean(8),Display=r.GetBoolean(9),Scratch=r.GetBoolean(10),AssetUrl=r.IsDBNull(11)?null:r.GetString(11),Result=r.IsDBNull(12)?null:r.GetString(12),MarketName=r.IsDBNull(13)?null:r.GetString(13),Rule4Time=r.IsDBNull(14)?null:r.GetValue(14).ToString(),Rule4Deduction=r.IsDBNull(15)?0:r.GetDouble(15),Line=r.IsDBNull(16)?0:r.GetDouble(16)});return o;}

    private static async Task<List<StoredRunnerResult>> LoadStoredRunnerResultsAsync(
        MySqlConnection c,MySqlTransaction t,long eventId,CancellationToken ct)
    {
        var output=new List<StoredRunnerResult>();
        await using var cmd=new MySqlCommand("""
            SELECT selection_id,runner_name,finishing_position,result_status
            FROM web_runner_results WHERE event_id=@eventId
            ORDER BY finishing_position IS NULL,finishing_position,selection_id
            FOR UPDATE
            """,c,t);
        cmd.Parameters.AddWithValue("@eventId",eventId);
        await using var reader=await cmd.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            output.Add(new(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2)?null:reader.GetInt32(2),
                reader.GetString(3)));
        return output;
    }

    public async Task<ResultCandidate?> GetCandidateAsync(long eventId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id,sport_id,meeting,ev_name,start_time_unix
            FROM spuno_events WHERE event_id=@eventId AND sport_id=4 LIMIT 1;
            """;
        await using var connection=OpenConnection();await connection.OpenAsync(cancellationToken);
        await using var command=new MySqlCommand(sql,connection);
        command.Parameters.AddWithValue("@eventId",eventId);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetInt64(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4))
            : null;
    }

    private static async Task ReplaceRunnerResultsAsync(MySqlConnection c,MySqlTransaction t,ResultPage page,List<PricingRunner> selections,CancellationToken ct)
    {
        await using(var delete=new MySqlCommand(
                        "DELETE FROM web_runner_results WHERE event_id=@eventId",c,t))
        {
            delete.Parameters.AddWithValue("@eventId",page.EventId);
            await delete.ExecuteNonQueryAsync(ct);
        }
        var winner=selections.Where(x=>Suffix(x.MarketId)==9001).ToList();
        var storedSelections=new HashSet<long>();
        foreach(var row in page.Runners)
        {
            var s=winner.FirstOrDefault(x=>Matches(x,row));
            // Sporting Life can include a replacement and the withdrawn dog as
            // separate rows carrying the same trap. Keep the first (finishing)
            // row; otherwise the later duplicate overwrites the winner position
            // in web_runner_results and makes recovery replay forever.
            if(s is null||!storedSelections.Add(s.SelectionId))continue;
            await using var cmd=new MySqlCommand("""
        INSERT INTO web_runner_results(event_id,selection_id,competitor_id,identifier,runner_name,finishing_position,result_status,source_url,resulted_at_utc)
        VALUES(@event,@selection,@competitor,@identifier,@name,@position,@status,@url,UTC_TIMESTAMP(6))
        ON DUPLICATE KEY UPDATE finishing_position=VALUES(finishing_position),result_status=VALUES(result_status),source_url=VALUES(source_url),resulted_at_utc=VALUES(resulted_at_utc)
        """,c,t);
            cmd.Parameters.AddWithValue("@event",page.EventId);
            cmd.Parameters.AddWithValue("@selection",s.SelectionId);
            cmd.Parameters.AddWithValue("@competitor",s.CompetitorId is null?DBNull.Value:s.CompetitorId);
            cmd.Parameters.AddWithValue("@identifier",row.Identifier is null?s.Identifier:row.Identifier);
            cmd.Parameters.AddWithValue("@name",row.Name);
            cmd.Parameters.AddWithValue("@position",row.Position is null?DBNull.Value:row.Position);
            cmd.Parameters.AddWithValue("@status",page.IsFinal?row.Status:"Provisional");
            cmd.Parameters.AddWithValue("@url",page.SourceUrl);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static bool SettleMatch(List<PricingRunner> list,Dictionary<string,int> p)
    {
        var ordered=list.Where(x=>Position(x,p)>0).OrderBy(x=>Position(x,p)).ToList();
        if(ordered.Count==0)return false;
        var voided=list.Count!=2||list.Any(x=>x.Scratch);
        foreach(var s in list)
        {
            s.Active=false;
            s.Display=false;
            s.Result=voided?RacingResultCodes.Void:ReferenceEquals(s,ordered[0])?"1":"0";
        }
        return true;
    }
    private static bool SettleForecast(
        List<PricingRunner> list,
        IReadOnlyList<ResultRunner> runners,
        IReadOnlyList<PricingRunner> allSelections,
        bool allowFinalVoid=false)
    {
        var hasOrder=TryGetForecastOrder(runners,out var first,out var second);
        if(!hasOrder&&!allowFinalVoid)return false;
        list=list.OrderBy(x=>x.SelectionId).ToList();
        var runnerA=list.Count>0?ResolveForecastRunner(list[0],allSelections):null;
        var runnerB=list.Count>1?ResolveForecastRunner(list[1],allSelections):null;
        var voided=!hasOrder||list.Count!=3||runnerA is null||runnerB is null||
                   runnerA?.Scratch==true||runnerB?.Scratch==true;
        var aThenB=!voided&&Matches(runnerA!,first)&&Matches(runnerB!,second);
        var bThenA=!voided&&Matches(runnerB!,first)&&Matches(runnerA!,second);
        foreach(var s in list){s.Active=false;s.Display=false;s.Result=voided?RacingResultCodes.Void:"0";}
        if(!voided)
        {
            if(aThenB)list[0].Result="1";
            if(bThenA)list[1].Result="1";
            if(aThenB||bThenA)list[2].Result="1";
        }
        return true;
    }

    private static bool CanSettleMarket(
        int suffix,
        IReadOnlyList<PricingRunner> selections,
        Dictionary<string,int> positions,
        bool isFinal)
    {
        // A fast top-three result does not establish fourth place.
        if (suffix is 9008 or 9011 && !positions.Values.Contains(4)) return false;
        if (suffix == 9010 && !positions.Values.Contains(4)) return false;
        if(isFinal)return true;
        bool HasPlaces(int places)=>Enumerable.Range(1,places).All(
            place=>positions.Values.Contains(place));
        return suffix switch
        {
            9001 or 9003=>HasPlaces(1),
            // Place, insurance and index markets can be published progressively.
            // A known placed runner has a deterministic result immediately; the
            // unplaced runners remain null until the required depth is known.
            9002 or 9006=>HasPlaces(2),
            9004 or 9007 or 9009=>HasPlaces(3),
            9008 or 9010 or 9011=>HasPlaces(4),
            >=9100 and<=9199=>selections.Any(x=>Position(x,positions)>0),
            >=9200 and<=9299=>selections.Any(x=>!x.Scratch&&Position(x,positions)>0),
            >=9300 and<=9319=>HasPlaces(2),
            9400=>HasPlaces(2),
            9500=>HasPlaces(3),
            _=>false
        };
    }

    private static bool SettleTrapProductLine(
        IReadOnlyList<PricingRunner> selections,IReadOnlyList<ResultRunner> runners,int places)
    {
        var traps=runners.Where(x=>x.Position is>0&&x.Position<=places)
            .OrderBy(x=>x.Position)
            .Select(x=>int.TryParse(x.Identifier,out var trap)?trap:0).ToList();
        if(traps.Count!=places||traps.Any(x=>x<=0))return false;
        var product=traps.Aggregate(1,(value,trap)=>value*trap);
        // The line comes from spuno_selections.line, not from parsing the display name back
        // out. The name is written with the current culture (RacingMarketCalculator) and was
        // parsed here with InvariantCulture, so on a host with a comma decimal separator the
        // parse failed and the line silently became 0 - every Under settled as a loser and
        // every Over as a winner, for every race, with no error. A line we cannot trust is a
        // refusal to settle, not a zero. Validate every selection before mutating any of
        // them: the caller treats false as "leave this market alone".
        var lines=new double[selections.Count];
        for(var index=0;index<selections.Count;index++)
        {
            var line=selections[index].Line;
            if(!double.IsFinite(line)||line<=0)return false;
            lines[index]=line;
        }
        for(var index=0;index<selections.Count;index++)
        {
            var selection=selections[index];
            selection.Active=false;selection.Display=false;
            selection.Result=selection.Name.StartsWith("Under",StringComparison.OrdinalIgnoreCase)
                ? product<lines[index]?"1":"0"
                : product>lines[index]?"1":"0";
        }
        return true;
    }

    private static void ApplyRunnerParticipation(
        IReadOnlyList<PricingRunner> selections,
        IReadOnlyList<PricingRunner> raceWinnerSelections,
        ResultPage page)
    {
        foreach(var raceRunner in raceWinnerSelections)
        {
            // A vacant trap is always void, even when a partial provider result
            // does not include an expected-runner count from which to infer it.
            if(IsVacantTrap(raceRunner.Name))
            {
                raceRunner.Scratch=true;
                raceRunner.Result=RacingResultCodes.Void;
                continue;
            }
            // A pulled-up, unseated or otherwise unplaced runner still took
            // part in the race. Presence anywhere in the final result is
            // participation evidence; a finishing position is not required.
            var appearsInResult=page.Runners.Any(result=>Matches(raceRunner,result));
            if(appearsInResult)raceRunner.Scratch=false;
            else if(page.IsFinal&&page.ExpectedRunnerCount is>0)raceRunner.Scratch=true;
        }
        foreach(var selection in selections)
        {
            var suffix=Suffix(selection.MarketId);
            // These selections describe trap groups or calculated lines. Their
            // identifiers are selection ordinals (for example 05 for
            // "Traps 1 or 2"), not trap numbers. Never inherit a scratch from
            // the race runner with the same number.
            if(suffix is 9003 or 9400 or 9500 or GreyhoundDistanceMarkets.Suffix)
            {
                selection.Scratch=false;
                continue;
            }
            var raceRunner=ResolveRaceRunner(selection,raceWinnerSelections);
            if(raceRunner is not null)selection.Scratch=raceRunner.Scratch;
        }
    }

    private static PricingRunner? ResolveRaceRunner(
        PricingRunner selection,
        IReadOnlyList<PricingRunner> raceWinnerSelections)
    {
        if(Suffix(selection.MarketId)==GreyhoundDistanceMarkets.Suffix)return null;
        if(Suffix(selection.MarketId)==9001)return selection;
        if(selection.CompetitorId is not null)
        {
            var byCompetitor=raceWinnerSelections.FirstOrDefault(
                x=>x.CompetitorId==selection.CompetitorId);
            if(byCompetitor is not null)return byCompetitor;
        }
        var byIdentifier=raceWinnerSelections.FirstOrDefault(
            x=>NormalizeIdentifier(x.Identifier)==NormalizeIdentifier(selection.Identifier));
        if(byIdentifier is not null)return byIdentifier;
        return raceWinnerSelections.FirstOrDefault(
            x=>Normalize(x.Name)==Normalize(selection.Name));
    }

    private static bool RunnerResultsEqual(
        IReadOnlyList<StoredRunnerResult> stored,
        IReadOnlyList<ResultRunner> incoming)
    {
        var storedPositions=stored.Where(x=>x.Position is>0)
            .Select(x=>(Name:Normalize(x.Name),Position:x.Position!.Value))
            .OrderBy(x=>x.Position).ThenBy(x=>x.Name,StringComparer.Ordinal)
            .ToList();
        var incomingPositions=incoming.Where(x=>x.Position is>0)
            .Select(x=>(Name:Normalize(x.Name),Position:x.Position!.Value))
            .OrderBy(x=>x.Position).ThenBy(x=>x.Name,StringComparer.Ordinal)
            .ToList();
        return storedPositions.SequenceEqual(incomingPositions);
    }

    private static bool StatusEquals(string value,string expected)=>
        expected=="3"
            ?value is "3" or "resulted" or "post game"
            :value==expected;
    private static PricingRunner? ResolveForecastRunner(
        PricingRunner forecastSelection,
        IReadOnlyList<PricingRunner> allSelections)
    {
        var raceWinnerSelections=allSelections.Where(x=>Suffix(x.MarketId)==9001);
        if(forecastSelection.CompetitorId is not null)
        {
            var byCompetitor=raceWinnerSelections.FirstOrDefault(
                x=>x.CompetitorId==forecastSelection.CompetitorId);
            if(byCompetitor is not null)return byCompetitor;
        }
        return raceWinnerSelections.FirstOrDefault(
            x=>NormalizeIdentifier(x.Identifier)==NormalizeIdentifier(forecastSelection.Identifier));
    }
    private static bool TryGetForecastOrder(IReadOnlyList<ResultRunner> runners,out ResultRunner first,out ResultRunner second)
    {
        var firsts=runners.Where(x=>x.Position==1).ToList();
        var seconds=runners.Where(x=>x.Position==2).ToList();
        first=firsts.FirstOrDefault()!;
        second=seconds.FirstOrDefault()!;
        return firsts.Count==1&&seconds.Count==1;
    }
    private static Dictionary<string,int> BuildPositions(IEnumerable<ResultRunner> runners)
    {
        var positions=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        foreach(var runner in runners.Where(x=>x.Position.HasValue))
        {
            AddPosition(positions,"name:"+Normalize(runner.Name),runner.Position!.Value);
            if(!string.IsNullOrWhiteSpace(runner.Identifier))
                AddPosition(positions,"trap:"+NormalizeIdentifier(runner.Identifier),runner.Position!.Value);
        }
        return positions;
    }
    private static void AddPosition(Dictionary<string,int> positions,string key,int position)
    {if(!positions.TryGetValue(key,out var existing)||position<existing)positions[key]=position;}
    private static int Position(PricingRunner r,Dictionary<string,int> p)
    {
        if(!string.IsNullOrWhiteSpace(r.Identifier)&&
           p.TryGetValue("trap:"+NormalizeIdentifier(r.Identifier),out var byTrap))return byTrap;
        return p.TryGetValue("name:"+Normalize(r.Name),out var byName)?byName:0;
    }
    // && binds tighter than ||, so the original form read
    //   (bothTrapsPresent && trapsEqual) || namesEqual
    // which left the name arm unguarded and unconditional: a name match alone was enough
    // even when the traps disagreed. Greyhounds carry a trap and a name, and every result
    // source here (BetsAPI PN, Sporting Life cloth_number, Betfred trap) supplies the trap,
    // so when both sides have one the trap is authoritative. The name is only consulted
    // when one side has no trap at all.
    private static bool Matches(PricingRunner selection,ResultRunner runner)=>
        TrapsComparable(selection,runner)
            ?NormalizeIdentifier(selection.Identifier)==NormalizeIdentifier(runner.Identifier!)
            :Normalize(selection.Name)==Normalize(runner.Name);
    private static bool TrapsComparable(PricingRunner selection,ResultRunner runner)=>
        !string.IsNullOrWhiteSpace(selection.Identifier)&&
        !string.IsNullOrWhiteSpace(runner.Identifier);
    private static bool TrapsAgree(PricingRunner selection,ResultRunner runner)=>
        TrapsComparable(selection,runner)&&
        NormalizeIdentifier(selection.Identifier)==NormalizeIdentifier(runner.Identifier!);
    // Tied runners occupy consecutive finishing slots: `count` of them starting at
    // `position` fill positions `position` .. `position + count - 1`. A market paying
    // `places` is only made ambiguous when the payout line falls INSIDE that group.
    //   1,2,2 -> Top 2 is ambiguous (three runners, two places) but Top 3 is not: positions
    //            1, 2 and 3 are all filled and every one of those runners is a clear winner.
    //   1,1,3 -> the Race Winner is ambiguous but Top 2 is not: both joint winners are in
    //            the first two places whichever way the tie is read.
    private static bool StraddlesPayoutLine(int position,int count,int places)=>
        position<=places&&places<position+count-1;

    // The payout lines each market family depends on. A market is blocked when any dead heat
    // straddles any of its lines. An empty set means the market does not turn on finishing
    // order at all.
    private static int[] DeadHeatPayoutLines(int suffix)=>suffix switch
    {
        9001 or 9003=>[1],
        9002=>[2],
        9004=>[3],
        9011=>[4],
        // Insurance pays the winner and refunds the other places, so both lines matter.
        9006=>[1,2],
        9007=>[1,3],
        9008=>[1,4],
        // Index markets award different points per position, so any tie inside the scoring
        // depth changes what each tied runner is owed, not merely who is in the frame.
        9009=>[1,2,3],
        9010=>[1,2,3,4],
        // A forecast needs an unambiguous 1st and 2nd.
        >=9300 and<=9319=>[1,2],
        // Betting Without pays the best-placed runner outside the excluded set; removing the
        // excluded runner can move the decisive line up, so guard the front of the field.
        >=9200 and<=9299=>[1,2],
        9400=>[2],
        9500=>[3],
        _=>[]
    };

    private static bool DeadHeatBlocks(
        int suffix,IReadOnlyList<(int Position,int Count)> deadHeats,
        IReadOnlyList<PricingRunner> selections,Dictionary<string,int> positions)
    {
        if(suffix is>=9100 and<=9199)
        {
            // A match bet turns only on the relative order of its own two runners, so a tie
            // elsewhere in the field is irrelevant - and a tie between them is fatal. The
            // generic settlement path breaks that tie by database row order.
            var placings=selections.Where(x=>!x.Scratch).Select(x=>Position(x,positions))
                .Where(position=>position>0).ToList();
            return placings.Count!=placings.Distinct().Count();
        }
        var lines=DeadHeatPayoutLines(suffix);
        return lines.Length>0&&deadHeats.Any(
            tie=>lines.Any(places=>StraddlesPayoutLine(tie.Position,tie.Count,places)));
    }

    private static bool TrapIdentifiersMatch(
        IEnumerable<PricingRunner> selections,IEnumerable<ResultRunner> runners)
    {
        var expected=selections.Where(x=>!x.Scratch&&!IsVacantTrap(x.Name)&&!string.IsNullOrWhiteSpace(x.Identifier))
            .Select(x=>NormalizeIdentifier(x.Identifier)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual=runners.Where(x=>!string.IsNullOrWhiteSpace(x.Identifier))
            .Select(x=>NormalizeIdentifier(x.Identifier!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.SetEquals(actual);
    }
    private static string? PlaceResult(int position,int places,int knownDepth,bool isFinal)=>
        position is>0&&position<=places?"1":isFinal||knownDepth>=places?"0":null;
    private static string? InsureResult(int position,int places,int knownDepth,bool isFinal)=>
        position==1?"1":position>1&&position<=places?"-1":isFinal||knownDepth>=places?"0":null;
    private static string? IndexResult(int position,int knownDepth,bool isFinal,params int[] points)=>
        position>0&&position<=points.Length?points[position-1].ToString():isFinal||knownDepth>=points.Length?"0":null;
    private static bool WinningGroup(string n,int t)=>n switch{"Odd"=>new[]{1,3,5}.Contains(t),"Even"=>new[]{2,4,6}.Contains(t),"Traps 1 or 2 or 3"=>t is 1 or 2 or 3,"Traps 4 or 5 or 6"=>t is 4 or 5 or 6,"Traps 1 or 2"=>t is 1 or 2,"Traps 3 or 4"=>t is 3 or 4,"Traps 5 or 6"=>t is 5 or 6,_=>false};
    private static int Suffix(long id)=>(int)(id%10000);private static bool IsResultMarket(int s)=>s is>=9001 and<=9011||s is>=9100 and<=9319||s is 9400 or 9500;
    private static string Normalize(string s)=>Regex.Replace(s.Trim().ToLowerInvariant().Replace("&","and").Replace("’","'").Replace("-"," "),@"\s+"," ");
    private static bool IsVacantTrap(string name)=>Normalize(name).StartsWith("vacant trap",StringComparison.Ordinal);
    private static string NormalizeIdentifier(string value)=>int.TryParse(value.Trim(),out var number)?number.ToString():value.Trim();
    private static int MarketType(int s)=>s switch{9001=>1001,9002=>1002,9004=>1003,9006=>1005,9007=>1006,9008=>1009,9009=>1010,9010=>1011,9011=>1008,>=9100 and<=9199=>1001,>=9200 and<=9219=>1004,>=9300 and<=9319=>RacingMarketCalculator.ForecastMarketType,9400=>RacingMarketCalculator.MultiTrapsMarketType,9500=>RacingMarketCalculator.TripleTrapsMarketType,_=>1001};
    private static int DisplayOrder(int s)=>s switch{9001=>10,9002=>20,9004=>30,9011=>35,9003=>35,9009=>40,9010=>45,9006=>160,9007=>170,9008=>180,>=9200 and<=9219=>50+s-9200,>=9100 and<=9119=>70+s-9100,>=9300 and<=9319=>90+s-9300,9400=>200,9500=>210,_=>300};
    private static string MarketName(long marketId,string fallback)=>Suffix(marketId) switch{9001=>"Race Winner",9002=>"Top 2",9003=>"Inside-Middle-Outside-Odd-Even",9004=>"Top 3",9006=>"Insure Bet 2 Places",9007=>"Insure Bet 3 Places",9008=>"Insure Bet 4 Places",9009=>"50-25-10 Index",9010=>"50-30-20-10 Index",9011=>"Top 4",>=9300 and<=9319=>"Forecast",9400=>"Multi-Traps",9500=>"Triple-Traps",_=>fallback};
}
