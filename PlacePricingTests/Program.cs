using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RacingTrader.Web.Services;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Near(double actual, double expected, string message, double tolerance = 1e-10)
    => Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: {actual:R} vs {expected:R}");
void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception("Invalid place configuration/input was accepted");
}
var parameters = new PlacePricingParameters { Model = "DiscountedHarville" };
using (var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures.json"))))
    foreach (var fixture in fixtures.RootElement.EnumerateArray())
    {
        var weights = fixture.GetProperty("weights").EnumerateArray().Select(p => p.GetDouble()).ToArray();
        var actual = DiscountedHarville.Calculate(weights, parameters);
        var expected = fixture.GetProperty("top").EnumerateArray().ToArray();
        for (var k = 1; k <= 4; k++)
            for (var i = 0; i < weights.Length; i++)
                Near(actual[k][i], expected[k - 1][i].GetDouble(), "Independent subset-state reference");
    }

foreach (var count in new[] { 0, 1, 2, 5, 6, 8, 12, 24, 32 })
{
    var weights = Enumerable.Range(1, count).Select(i => 1d / (i * i)).ToArray();
    var books = DiscountedHarville.Calculate(weights, parameters);
    var scaled = DiscountedHarville.Calculate(weights.Select(w => w * 1e200).ToArray(), parameters);
    var reversed = DiscountedHarville.Calculate(weights.Reverse().ToArray(), parameters);
    var equal = DiscountedHarville.Calculate(Enumerable.Repeat(1d, count).ToArray(), parameters);
    for (var k = 1; k <= 4; k++)
    {
        Near(books[k].Sum(), Math.Min(k, count), "Place book conserves paid positions", 1e-9);
        for (var i = 0; i < count; i++)
        {
            Check(books[k][i] >= books[k - 1][i] - 1e-12 && books[k][i] <= 1, "Place probability is bounded and cumulative");
            Near(books[k][i], scaled[k][i], "Scale invariance");
            Near(books[k][i], reversed[k][count - i - 1], "Runner-order invariance");
            Near(equal[k][i], Math.Min(k, count) / (double)count, "Symmetric field");
        }
    }
}
var original = new[] { .6, .3, .1, 0 };
var ordinary = DiscountedHarville.Calculate(original, new PlacePricingParameters { SecondExponent = 1, ThirdExponent = 1, FourthExponent = 1 });
for (var i = 0; i < 3; i++)
{
    var expectedSecond = Enumerable.Range(0, 3).Where(j => j != i).Sum(j => original[j] * original[i] / (1 - original[j]));
    Near(ordinary[2][i], original[i] + expectedSecond, "Unit exponents recover ordinary Harville");
}
for (var k = 1; k <= 4; k++) Near(ordinary[k][3], 0, "Zero-weight runner stays excluded");
var dominant = DiscountedHarville.Calculate(new[] { 1d, 1e-200, 1e-250, 0 }, parameters);
for (var k = 1; k <= 4; k++) Near(dominant[k].Sum(), Math.Min(k, 3), "Extreme favourite remains numerically stable");
Check(DiscountedHarville.Calculate([0, 0], parameters).All(b => b.All(p => p == 0)), "No probability invented for an empty book");
foreach (var value in new[] { 0d, -1d, double.NaN, double.PositiveInfinity, 1.01 })
    Reject(() => new PlacePricingOptions { Horses = new() { SecondExponent = value } }.Validate());
Reject(() => new PlacePricingOptions { Greyhounds = new() { Model = "Typo" } }.Validate());
Reject(() => DiscountedHarville.Calculate([double.NaN, 1], parameters));
Reject(() => DiscountedHarville.Calculate([-1, 1], parameters));

var options = new PlacePricingOptions { Horses = new() { Model = "DiscountedHarville" }, Greyhounds = new() { Model = "DiscountedHarville" } };
// The two applications have different existing constructor dependencies.
var constructor = typeof(RacingMarketCalculator).GetConstructors().Single();
RacingMarketCalculator Calculator(PlacePricingOptions? configuration = null)
    => (RacingMarketCalculator)constructor.Invoke(constructor.GetParameters().Select(p => p.ParameterType == typeof(PlacePricingOptions) ? configuration : null).ToArray());
var legacy = Calculator();
var candidate = Calculator(options);
var configConstructor = typeof(PricingConfiguration).GetConstructors().Single();
var configuration = (PricingConfiguration)configConstructor.Invoke(configConstructor.GetParameters().Select(p =>
    p.ParameterType == typeof(bool) ? (object)true :
    p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        ? Array.CreateInstance(p.ParameterType.GetGenericArguments()[0], 0) : null).ToArray());
var oldFormula = typeof(RacingMarketCalculator).GetMethod("PlaceProbabilities", BindingFlags.Static | BindingFlags.NonPublic)!;
PricingRunner[] Runners(int count, bool scratch = false) => Enumerable.Range(1, count).Select(i => new PricingRunner
{
    EventId = 123, SelectionId = i, CompetitorId = i, Name = $"Runner {i}", Identifier = i.ToString(),
    Price = 1.5 + i * i * .75, Active = true, Display = true, Scratch = scratch && i == count
}).ToArray();
foreach (var sport in new[] { 2, 4 })
{
    var race = new PricingEvent(123, sport, "Test", "Test", 0, "0", 0);
    var field = Runners(8, scratch: true);
    var before = legacy.Build(race, field, configuration);
    var after = candidate.Build(race, field, configuration);
    var beforeById = before.ToDictionary(m => m.MarketId);
    foreach (var market in after.Where(m => m.MarketId % 10000 is not (9002 or 9004 or 9011 or 9006 or 9007 or 9008)))
        Check(JsonSerializer.Serialize(market) == JsonSerializer.Serialize(beforeById[market.MarketId]), "Non-place markets unchanged");
    foreach (var market in after)
    {
        var previous = beforeById[market.MarketId];
        Check(market.MarketId == previous.MarketId && market.Active == previous.Active && market.Display == previous.Display, "Market identity/visibility preserved");
        Check(market.Selections.Select(s => s.SelectionId).SequenceEqual(previous.Selections.Select(s => s.SelectionId)), "Selection identities preserved");
    }
    Check(after.Where(m => m.MarketId % 10000 == 9004).SelectMany(m => m.Selections).Zip(beforeById[1239004].Selections).Any(pair => pair.First.PriceBack != pair.Second.PriceBack), "Candidate actually changes quoted places");
    var books = candidate.PlaceProbabilityBooks(sport, field);
    for (var k = 2; k <= 4; k++)
    {
        Near(books[k][^1], 0, "Scratched runner receives exactly zero place probability");
        Near(books[k].Sum(), k, "Integrated unsnapped book sums to place count");
        var baseline = legacy.PlaceProbabilityBooks(sport, field)[k];
        var expected = (double[])oldFormula.Invoke(null, [field, k])!;
        Check(baseline.SequenceEqual(expected), "Default preserves exact legacy formula");
    }
    var suspendedBuilder = typeof(RacingMarketCalculator).GetMethod("BuildSuspendedCard", BindingFlags.Instance | BindingFlags.NonPublic);
    if (suspendedBuilder is not null)
    {
        var suspended = (IReadOnlyList<GeneratedMarket>)suspendedBuilder.Invoke(candidate, [race, field, configuration])!;
        Check(suspended.SelectMany(m => m.Selections).All(s => s.Active == 0 && s.PriceBack == 1), "SP-only cards remain unpriced and suspended");
    }
    foreach (var count in new[] { 3, 4, 5 })
    {
        var small = candidate.Build(race, Runners(count), configuration).ToDictionary(m => m.MarketId % 10000);
        Check(small[9002].Active == (count >= 4), "Top-2 field-size rule");
        Check(small[9004].Active == (count >= 5), "Top-3 field-size rule");
        Check(!small.TryGetValue(9011, out var top4) || !top4.Active, "Top-4 requires six runners");
    }
}
var independent = new PlacePricingOptions { Horses = new() { Model = "DiscountedHarville" }, Greyhounds = new() { Model = "Legacy" } };
var split = Calculator(independent);
Check(split.PlaceProbabilityBooks(2, Runners(8))[3].SequenceEqual(candidate.PlaceProbabilityBooks(2, Runners(8))[3]), "Horse switch independent");
Check(split.PlaceProbabilityBooks(4, Runners(8))[3].SequenceEqual(legacy.PlaceProbabilityBooks(4, Runners(8))[3]), "Greyhound switch independent");
var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["RacingTrader:PlacePricing:Horses:Model"] = "DiscountedHarville",
    ["RacingTrader:PlacePricing:Greyhounds:SecondExponent"] = ".9"
}).Build().GetSection("RacingTrader:PlacePricing").Get<PlacePricingOptions>()!.Validate();
Check(bound.Horses.UsesDiscountedHarville && !bound.Greyhounds.UsesDiscountedHarville && bound.Greyhounds.SecondExponent == .9, "Configuration binding and independent defaults");
var altered = new PlacePricingParameters { Model = "DiscountedHarville", FourthExponent = .9 };
var baseBooks = DiscountedHarville.Calculate([.4, .25, .15, .1, .06, .04], parameters);
var alteredBooks = DiscountedHarville.Calculate([.4, .25, .15, .1, .06, .04], altered);
for (var k = 1; k <= 3; k++)
    Check(baseBooks[k].SequenceEqual(alteredBooks[k]), "Fourth-place parameter cannot change earlier positions");
Check(baseBooks[4].Where((p, i) => Math.Abs(p - alteredBooks[4][i]) > 1e-6).Any(), "Fourth-place parameter changes top-4");
var samples = new List<double>();
foreach (var count in new[] { 6, 12, 24 })
{
    var field = Runners(count);
    candidate.PlaceProbabilityBooks(2, field); // JIT warmup
    var timer = Stopwatch.StartNew();
    for (var i = 0; i < 30; i++) candidate.PlaceProbabilityBooks(2, field);
    var milliseconds = timer.Elapsed.TotalMilliseconds / 30;
    samples.Add(milliseconds);
    Console.WriteLine($"Discounted Harville all top-1/2/3/4 books: {count} runners, {milliseconds:F3} ms/call (30 iterations)");
}
Console.WriteLine($"PASS: {checks} place-pricing checks; no database, browsers, or broker connections used.");
