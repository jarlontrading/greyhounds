using RacingTrader.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;

var assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
foreach (var active in new[] {0,1})
{
    var challengeEvent = new Dictionary<string,object?> { ["messageType"]="event", ["sportId"]=4, ["eventName"]="Monmore Trap Challenge", ["active"]=active };
    RacingPublisher.AddTrapChallengeSinglesOnly(challengeEvent);
    Check(JsonSerializer.SerializeToElement(challengeEvent).GetProperty("singlesOnly").ValueKind==JsonValueKind.True,
        "Open and terminal trap-challenge events carry a boolean singlesOnly restriction");
    var singlesChallengeMarket = new Dictionary<string,object?> { ["messageType"]="market", ["marketTypeId"]=2002, ["active"]=active };
    RacingPublisher.AddTrapChallengeSinglesOnly(singlesChallengeMarket);
    Check(singlesChallengeMarket.GetValueOrDefault("singlesOnly") is true,"Trap-challenge markets carry singlesOnly in every state");
}
foreach (var ordinary in new[]{
    new Dictionary<string,object?> { ["messageType"]="event", ["sportId"]=4, ["eventName"]="Monmore 18:00" },
    new Dictionary<string,object?> { ["messageType"]="market", ["marketTypeId"]=1 },
    new Dictionary<string,object?> { ["messageType"]="selection", ["marketTypeId"]=2002 }})
{
    RacingPublisher.AddTrapChallengeSinglesOnly(ordinary);
    Check(!ordinary.ContainsKey("singlesOnly"),"Ordinary races, other markets and selections remain unchanged");
}
var directBookRunners = Enumerable.Range(1, 6).Select(trap =>
    new FeedRunner(trap, $"Direct dog {trap}", trap, "5/1", 6, true, false, $"silk-{trap}", "{}"))
    .ToList();
var partialApiRunners = directBookRunners.Select(runner => runner.Identifier == 6
    ? runner with { PriceText = "", Price = 0, HasPrice = false }
    : runner).ToList();
var sourceNow = DateTime.UtcNow;
var directBook = new FeedPriceSnapshot("BET365_DIRECT", sourceNow, sourceNow, directBookRunners, []);
var partialApiBook = new FeedPriceSnapshot("BETSAPI", sourceNow, sourceNow, partialApiRunners, []);
var selectedDirectBook = RacingFeedClient.SelectBetsApiPricingOrMetadata(directBook, partialApiBook);
Check(selectedDirectBook?.Source == "BET365_DIRECT" &&
      selectedDirectBook.Runners.All(runner => runner.PricingSource == "BET365_DIRECT"),
    "A complete Bet365 direct book replaces an incomplete BetsAPI snapshot as a separately labelled source");
var incompleteDirectBook = directBook with
{
    Runners = directBookRunners.Select(runner => runner.Identifier == 6
        ? runner with { PriceText = "", Price = 0, HasPrice = false }
        : runner).ToList()
};
var noCompleteBook = RacingFeedClient.SelectBetsApiPricingOrMetadata(incompleteDirectBook, partialApiBook);
Check(noCompleteBook?.Source == "BETSAPI_METADATA" && noCompleteBook.Runners.All(runner => runner.PricingPrice is null),
    "Partial provider books remain metadata and cannot price Trap Challenge");
var uniformTrapRace = new TrapChallengeRaceInput(1, 1_800_000_000, "0", true, false,
    Enumerable.Range(1, 6).Select(trap =>
        new TrapChallengeRunnerInput(trap, $"Dog {trap}", 6, 6, false, null, null, null)).ToList());
var uniformTrapChallenge = TrapChallengeService.Calculate("Test", [uniformTrapRace]);
Check(uniformTrapChallenge.Selections.All(x => Math.Abs(x.Probability!.Value - 1d / 6) < 1e-12),
    "Uniform race gives every trap one-sixth of Trap Challenge settlement probability");
Check(uniformTrapChallenge.Selections.All(x => Math.Abs(x.FairPrice!.Value - 6) < 1e-10),
    "Uniform Trap Challenge fair prices are 6.0");
var vacantTrapRace = uniformTrapRace with
{
    Runners = uniformTrapRace.Runners.Select(runner => runner.Trap == 5
        ? runner with { Name = "Vacant Trap 5", CurrentPrice = 0, PreviousPrice = 0, Scratch = true }
        : runner).ToList()
};
var vacantTrapChallenge = TrapChallengeService.Calculate("Test", [vacantTrapRace]);
Check(vacantTrapChallenge.Selections.Single(x => x.Trap == 5).Probability == 0,
    "A vacant trap stays in the meeting market but has zero chance in that race");
var suspendedTrapChallenge = TrapChallengeService.Calculate("Test", [uniformTrapRace with { Status = "2", Active = false }]);
Check(suspendedTrapChallenge.Status == "suspended" && suspendedTrapChallenge.Selections.All(x => x.FairPrice.HasValue),
    "Suspension retains calculated prices while marking the meeting market suspended");
var resultRunners = uniformTrapRace.Runners.Select(runner => runner.Trap == 3
    ? runner with { Result = "1", FinishingPosition = 1 }
    : runner with { Result = "0", FinishingPosition = null }).ToList();
var resultedTrapChallenge = TrapChallengeService.Calculate("Test", [uniformTrapRace with { Status = "3", Active = false, Runners = resultRunners }]);
Check(resultedTrapChallenge.Selections.Single(x => x.Trap == 3).Winners == 1 &&
      resultedTrapChallenge.Selections.Single(x => x.Trap == 3).Probability == 1,
    "Confirmed result fixes the winning trap and updates its winner count");
var challengeSnapshot = TrapChallengeService.BuildPublishingSnapshot(
    "Monmore EVE", [uniformTrapRace, uniformTrapRace with { EventId = 2, StartTimeUnix = 1_800_000_600 }])!;
Check(challengeSnapshot.Event.EventId == TrapChallengeService.EventNamespace + uniformTrapRace.EventId &&
      challengeSnapshot.Event.Name == "Monmore Trap Challenge" &&
      challengeSnapshot.Event.StartTimeUnix == uniformTrapRace.StartTimeUnix,
    "Trap Challenge Sportuno event has a stable unique ID, clean name and first-race start time");
var eveningChallengeSnapshot = TrapChallengeService.BuildPublishingSnapshot(
    "Harlow EVE", [uniformTrapRace, uniformTrapRace with { EventId = 2, StartTimeUnix = 1_800_000_600 }],
    isEveningSession: true)!;
Check(eveningChallengeSnapshot.Event.Meeting == "Harlow Evening" &&
      eveningChallengeSnapshot.Event.Name == "Harlow Evening Trap Challenge",
    "A second same-track meeting is identified as Evening in Sportuno");
var persistedChallengeId = 1_234_567_890L;
var earlierRaceSnapshot = TrapChallengeService.BuildPublishingSnapshot(
    "Monmore EVE",
    [uniformTrapRace with { EventId = 99, StartTimeUnix = uniformTrapRace.StartTimeUnix - 600 }, uniformTrapRace],
    persistedChallengeId)!;
Check(earlierRaceSnapshot.Event.EventId == persistedChallengeId &&
      earlierRaceSnapshot.Event.StartTimeUnix == uniformTrapRace.StartTimeUnix - 600,
    "Persisted meeting event ID survives a newly discovered earlier first race");
var challengeMarket = challengeSnapshot.Markets.Single();
Check(challengeMarket.Name == "Trap Challenge" && challengeMarket.MarketTypeId == TrapChallengeService.MarketTypeId &&
      challengeMarket.Selections.Select(x => x.SelectionId).Distinct().Count() == 6,
    "Trap Challenge publishes one market with six stable selection IDs");
Check(challengeMarket.Selections.Select(x => x.Name).SequenceEqual(Enumerable.Range(1, 6).Select(x => $"Trap {x} (0 winners)")),
    "Open selection names include the current meeting winner count");
Check(challengeMarket.Selections.All(selection =>
        selection.AssetUrl == RacingFeedRepository.GreyhoundTrapAssetUrl(int.Parse(selection.Identifier))),
    "Trap Challenge selections publish the same trap icon URLs as individual greyhound races");
Check(challengeMarket.Selections.Sum(x => 1d / x.PriceBack) >= 1d - 1e-12 &&
      challengeMarket.Selections.All(x => RacingMarketCalculator.SnapToOddsLadder(x.PriceBack) == x.PriceBack),
    "Trap Challenge Sportuno prices use the bookmaker ladder and do not form an underround book");
var suspendedChallengeSnapshot = TrapChallengeService.BuildPublishingSnapshot(
    "Monmore", [uniformTrapRace with { Status = "2", Active = false },
        uniformTrapRace with { EventId = 2, StartTimeUnix = 1_800_000_600 }])!;
Check(suspendedChallengeSnapshot.Event.Status == "2" &&
      !suspendedChallengeSnapshot.Markets.Single().Active &&
      RacingPublisher.ShouldDisplayMarket(true, suspendedChallengeSnapshot.Markets.Single()) &&
      suspendedChallengeSnapshot.Markets.Single().Selections.All(x => x.Active == 0 && x.Display == 1),
    "Suspended Trap Challenge publishes event status 2 and remains visible with inactive market and selections");
var unpricedFutureRace = uniformTrapRace with
{
    EventId = 3,
    StartTimeUnix = 1_800_001_200,
    Runners = uniformTrapRace.Runners.Select(runner => runner with
        { CurrentPrice = 0, PreviousPrice = 0 }).ToList()
};
var suspendedWithMissingPrices = TrapChallengeService.BuildPublishingSnapshot(
    "Monmore", [uniformTrapRace with { Status = "2", Active = false }, unpricedFutureRace])!;
Check(suspendedWithMissingPrices.Event.Status == "2" &&
      !suspendedWithMissingPrices.Markets.Single().Active,
    "A race-off suspension takes priority over missing prices in a later race");
TrapChallengeRaceInput WonBy(int eventId, int trap) => uniformTrapRace with
{
    EventId = eventId,
    StartTimeUnix = uniformTrapRace.StartTimeUnix + (eventId - 10) * 600,
    Status = "3",
    Active = false,
    Runners = uniformTrapRace.Runners.Select(runner => runner with
    {
        Result = runner.Trap == trap ? "1" : "0",
        FinishingPosition = runner.Trap == trap ? 1 : null
    }).ToList()
};
var tiedChallenge = TrapChallengeService.BuildPublishingSnapshot("Monmore", [WonBy(10, 1), WonBy(11, 2)])!;
Check(tiedChallenge.Event.Status == "3" && !tiedChallenge.Markets.Single().Active &&
      tiedChallenge.Markets.Single().Selections.Where(x => x.Result == "1").Select(x => x.Identifier).SequenceEqual(["1", "2"]) &&
      tiedChallenge.Markets.Single().Selections.Single(x => x.Identifier == "1").Name == "Trap 1 (1 winner)",
    "End-of-meeting dead heat results every joint-leading trap as a winner");
var tableText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "fixed-margin-markets-min50.txt"));
var tables = GreyhoundDistanceMarkets.ParseTables(tableText).ToArray();
Check(tables.Length == 125, "Load all 125 track/distance price tables");
var calculator = new RacingMarketCalculator();
var vacantEvent = new PricingEvent(999001, 4, "Test", "Test 12:00", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(), "0", 0);
var vacantRunners = Enumerable.Range(1, 6).Select(trap => new PricingRunner
{
    EventId = vacantEvent.EventId, MarketId = vacantEvent.EventId * 10000 + 9001,
    SelectionId = (vacantEvent.EventId * 10000 + 9001) * 100 + trap, CompetitorId = trap,
    Name = trap == 2 ? "Vacant Trap 2" : $"Runner {trap}", Identifier = trap.ToString("00"),
    Price = trap == 2 ? 1000 : 5, PreviousPrice = trap == 2 ? 1000 : 5,
    Active = trap != 2, Display = true, Scratch = trap == 2
}).ToList();
var vacantConfig = new PricingConfiguration(true, true, true, false, true, true, null, null, [], [], []);
var vacantMarkets = calculator.Build(vacantEvent, vacantRunners, vacantConfig);
var vacantSelections = vacantMarkets.SelectMany(market => market.Selections)
    .Where(selection => selection.Name.StartsWith("Vacant Trap", StringComparison.OrdinalIgnoreCase)).ToList();
Check(vacantSelections.Count >= 4 && vacantSelections.All(selection => selection.Result == RacingResultCodes.Void),
    "Vacant traps are sent as explicit void results in every runner market");
var maximumThreeWayBook = 0d;
foreach (var table in tables)
{
    Check(table.Bands.Length == 5, "Five selections");
    Check(string.Join(',', table.Bands.Select(b => b.Label.Split(' ')[0])) == "OVER,UNDER,OVER,UNDER,MIDDLE", "Selection order");
    Check(Math.Abs(table.Bands.Take(2).Sum(b => 1d / b.Price) - 1d) < .0000005, "Two-way fractional book is exactly 100%");
    var threeWayBook = table.Bands.Skip(2).Sum(b => 1d / b.Price);
    Check(threeWayBook >= 1d - .0000005, "Three-way fractional book is not under 100%");
    maximumThreeWayBook = Math.Max(maximumThreeWayBook, threeWayBook);
    Check(table.Bands.All(b => RacingMarketCalculator.SnapToOddsLadder(b.Price) == b.Price), "Every distance price is on the fractional ladder");
    var boundaries = table.Bands.SelectMany(b => new[] { b.Lower, b.Upper }).Where(b => b.HasValue).Select(b => b!.Value).Distinct();
    foreach (var value in boundaries.SelectMany(b => new[] { b - .001m, b, b + .001m }).Append(0m).Append(100m))
    {
        Check(table.Bands.Take(2).Count(b => b.Wins(value)) == 1, $"Exactly one 2-way winner at {value}");
        Check(table.Bands.Skip(2).Count(b => b.Wins(value)) == 1, $"Exactly one 3-way winner at {value}");
    }
    Check(table.Bands[1].Wins(table.Bands[1].Upper!.Value), "2-way equality in UNDER");
    Check(table.Bands[3].Wins(table.Bands[3].Upper!.Value), "3-way lower equality in UNDER");
    Check(table.Bands[4].Wins(table.Bands[4].Upper!.Value), "3-way upper equality in MIDDLE");
}
Console.WriteLine($"Maximum three-way book: {maximumThreeWayBook:P6}");
foreach (var (raw, expected) in new (string, decimal?)[] { ("½", .5m), ("1 ¼", 1.25m), ("2¾", 2.75m),
    ("1/2", .5m), ("2 3/4", 2.75m), ("nose", .05m), ("sh", .1m), ("hd", .2m), ("neck", .3m),
    ("dead heat", 0m), ("", null), ("-1", null), ("unknown", null) })
    Check(GreyhoundDistanceMarkets.ParseMargin(raw) == expected, $"Margin {raw}");

var root = Path.Combine(Path.GetTempPath(), "greyhound-distance-tests-" + Guid.NewGuid());
Directory.CreateDirectory(Path.Combine(root, "Data"));
File.WriteAllText(Path.Combine(root, "Data", "fixed-margin-markets-min50.txt"), tableText);
try
{
    var env = new TestEnvironment(root);
    var store = new GreyhoundDistanceMarkets(env);
    Check(store.Find("Harlow EVE", 415) is not null, "Known EVE alias");
    Check(store.Find("Harlow", 416) is null && store.Find("Unknown", 415) is null, "Exact track/metres required");
    var ev = new PricingEvent(123456, 4, "Harlow", "10:32 Harlow", new DateTimeOffset(2026,9,7,9,32,0,TimeSpan.Zero).ToUnixTimeSeconds(), "0", 0);
    Check(store.Build(ev) is null, "No market before source distance known");
    var now = DateTime.UtcNow;
    store.Put(new(ev.EventId,ev.Meeting,ev.StartTimeUnix,"https://www.sportinglife.com/example",415,now,now,null,null,null,null,null,false,store.Find("Harlow",415)));
    var market = store.Build(ev)!;
    Check(market.MarketTypeId == 2001 && market.MarketId == ev.EventId * 10000 + 9600, "IDs");
    Check(market.Selections.Select(s => s.SelectionId).SequenceEqual(Enumerable.Range(1,5).Select(i => market.MarketId*100+i)), "Stable selection IDs");
    Check(market.Active && market.Selections.All(s => s.Result is null), "Scheduled time alone does not suspend or settle");
    Check(!store.Build(ev with {Status="2"})!.Active, "Explicit OFF suspends");
    var observation = store.Get(ev.EventId)!;
    store.Put(observation with { Margin = 1.5m, RawMargin="1½", MarginSeenUtc=now });
    var result = store.Build(ev with {Status="3"})!;
    Check(!result.Active && !result.Display && result.Selections.Count(s => s.Result == "1") == 2, "Two winning outcomes, event remains closed");
    await store.SaveAsync(CancellationToken.None);
    var restored = new GreyhoundDistanceMarkets(env);
    Check(restored.Get(ev.EventId)?.Margin == 1.5m, "Restart preserves result");
    Check(restored.Build(ev with {Status="3"})!.Selections.Select(s => s.Result).SequenceEqual(result.Selections.Select(s => s.Result)), "Restart settlement stable");

    string Data(string stage="Final Result", string distance="415", string time="09:32", string course="Harlow", string margin="½") => JsonSerializer.Serialize(new {
        props = new { pageProps = new { race = new { race_summary = new { course_name=course, date="2026-09-07", time, distance, off_time="09:32:11", race_stage=stage },
            runs = new[] { new { run_status="RUNNER",cloth_number=5,finish_position=1,finish_distance="",greyhound=new{name="Mikeys Pick"}},
                new {run_status="RUNNER",cloth_number=3,finish_position=2,finish_distance=margin,greyhound=new{name="Rosstemple Cody"}} } } } } });
    var card = GreyhoundDistanceClient.Parse(Data(),"fixture",ev)!;
    Check(card.Metres == 415 && card.Margin == .5m && card.OffUtc == new DateTime(2026,9,7,9,32,11,DateTimeKind.Utc), "Source UTC off and second runner margin");
    Check(GreyhoundDistanceClient.Parse(Data(time:"10:32"),"fixture",ev) is null, "Reject adjacent/BST-offset race");
    Check(GreyhoundDistanceClient.Parse(Data(course:"Romford"),"fixture",ev) is null, "Reject different track");
    Check(GreyhoundDistanceClient.Parse(Data(distance:""),"fixture",ev) is null, "Reject missing metres");
    Check(GreyhoundDistanceClient.Parse(Data(stage:"Racecard"),"fixture",ev)!.Margin is null, "Never result pre-race trap order");
    Check(GreyhoundDistanceClient.Parse(Data(margin:""),"fixture",ev)!.Margin is null, "Final order without margin remains pending");
    Check(GreyhoundDistanceClient.Parse(Data(),"fixture",ev with {StartTimeUnix=ev.StartTimeUnix+86400}) is null, "Reject previous day");
    if (args.Contains("--live-source"))
    {
        using var http = new HttpClient { Timeout=TimeSpan.FromSeconds(15) };
        var live = await new GreyhoundDistanceClient(http).FindAsync(ev, null, CancellationToken.None);
        Check(live?.Metres == 415 && live.Margin == .5m, "Live index discovery and confirmed Harlow result");
        Console.WriteLine("Live source: exact Harlow race, 415m, winning margin 0.5 lengths.");
    }
    if (args.Contains("--read-db"))
    {
        var repo = new RacingFeedRepository(new RacingTrader.Web.Configuration.ExternalSettings(), new RacingMarketCalculator(store),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RacingFeedRepository>.Instance);
        var candidates = await repo.GetDistanceEventsAsync(CancellationToken.None);
        Console.WriteLine($"Read-only database check: {candidates.Count} eligible races.");
        using var http = new HttpClient { Timeout=TimeSpan.FromSeconds(15) };
        var client = new GreyhoundDistanceClient(http);
        foreach (var candidate in candidates.Where(e => e.StartTimeUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds()).OrderBy(e=>e.StartTimeUnix).Take(3))
        {
            var source = await client.FindAsync(candidate, null, CancellationToken.None);
            var matched = source is not null && await repo.DistanceRunnerNamesMatchAsync(candidate.EventId,source.RunnerNames,CancellationToken.None);
            Console.WriteLine($"Read-only match: {candidate.Meeting}, event {candidate.EventId}: source={source is not null}, runners={matched}, metres={source?.Metres}, prices={(source is not null && store.Find(candidate.Meeting,source.Metres) is not null)}");
        }
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"PASS: {assertions} distance-market checks");

sealed class TestEnvironment(string root) : IWebHostEnvironment
{
    public string ContentRootPath { get; set; } = root;
    public string WebRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "GreyhoundTrader.DistanceTests";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
