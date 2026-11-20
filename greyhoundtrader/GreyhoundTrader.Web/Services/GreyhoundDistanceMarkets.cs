using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RacingTrader.Web.Services;

internal sealed record DistanceBand(string Label, double Price, decimal? Lower, decimal? Upper)
{
    public bool Wins(decimal margin)
    {
        // A band with neither bound used to win on every margin, so both sides of a market
        // paid out. ParseTables now rejects such a band, but a band restored from older
        // saved state can still reach here: refuse to settle rather than guess.
        if (!Lower.HasValue && !Upper.HasValue)
            throw new InvalidDataException(
                $"Price band '{Label}' has no bounds and cannot settle a {margin} length margin.");
        return (!Lower.HasValue || margin > Lower) && (!Upper.HasValue || margin <= Upper);
    }
}
internal sealed record DistancePriceTable(string Track, int Metres, int SampleSize, DistanceBand[] Bands)
{
    // Identifies the table by its contents. The publication signature carries this, so a
    // regenerated fixed-margin file is visible as a different table instead of silently
    // re-settling bets struck against the previous bands and labels.
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', Bands.Select(band => string.Join(',',
            band.Label,
            band.Price.ToString("0.########", CultureInfo.InvariantCulture),
            band.Lower?.ToString(CultureInfo.InvariantCulture) ?? "",
            band.Upper?.ToString(CultureInfo.InvariantCulture) ?? "")))))).ToLowerInvariant()[..12];
}
internal sealed record DistanceObservation(long EventId, string Track, long StartTimeUnix,
    string SourceUrl, int Metres, DateTime FirstSeenUtc, DateTime CheckedUtc,
    DateTime? ActualOffUtc, DateTime? LastMissingUtc, DateTime? MarginSeenUtc,
    string? RawMargin, decimal? Margin, bool TimedSample, DistancePriceTable? Prices,
    string? PublishedSignature = null);

internal sealed class GreyhoundDistanceMarkets
{
    public const int Suffix = 9600;
    public const int MarketType = 2001;
    private readonly ConcurrentDictionary<long, DistanceObservation> _races = new();
    private readonly Dictionary<string, DistancePriceTable> _tables = new();
    private readonly string _statePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    public string? LastError { get; set; }
    public DateTime? LastPollUtc { get; set; }
    public int TableCount => _tables.Count;

    public GreyhoundDistanceMarkets(IWebHostEnvironment environment, ILogger<GreyhoundDistanceMarkets>? logger = null)
    {
        var data = Path.Combine(environment.ContentRootPath, "Data");
        Directory.CreateDirectory(data);
        // Dropbox can briefly lock a file while syncing, which makes the atomic
        // replace fail and stalls publication for subsequent races. Keep runtime
        // state with the service's other writable operational data instead.
        var stateDirectory = environment.IsEnvironment("Testing")
            ? data
            : Path.Combine(@"D:\Temp", "greyhoundtrader");
        Directory.CreateDirectory(stateDirectory);
        _statePath = Path.Combine(stateDirectory, "distance-observations.json");
        var legacyStatePath = Path.Combine(data, "distance-observations.json");
        if (!File.Exists(_statePath) && File.Exists(legacyStatePath))
            File.Copy(legacyStatePath, _statePath);
        foreach (var table in ParseTables(File.ReadAllText(Path.Combine(data, "fixed-margin-markets-min50.txt"))))
            _tables.Add(Key(table.Track, table.Metres), table);
        if (File.Exists(_statePath))
        {
            try
            {
                foreach (var race in JsonSerializer.Deserialize<DistanceObservation[]>(File.ReadAllText(_statePath)) ?? [])
                    _races[race.EventId] = race;
            }
            catch (JsonException ex)
            {
                var quarantined = _statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                try { File.Move(_statePath, quarantined); }
                catch (IOException moveError)
                {
                    logger?.LogWarning(moveError, "Could not preserve corrupt distance cache at {Path}.", quarantined);
                }
                catch (UnauthorizedAccessException moveError)
                {
                    logger?.LogWarning(moveError, "Could not preserve corrupt distance cache at {Path}.", quarantined);
                }
                logger?.LogWarning(ex, "Distance cache was unreadable. Results will start and distance observations will rebuild.");
            }
        }
    }

    public static string TrackKey(string value) => Regex.Replace(
        Regex.Replace(value.Trim(), @"\s+EVE$", "", RegexOptions.IgnoreCase).ToLowerInvariant(), @"[^a-z0-9]", "");
    private static string Key(string track, int metres) => $"{TrackKey(track)}:{metres}";
    public DistancePriceTable? Find(string track, int metres) => _tables.GetValueOrDefault(Key(track, metres));
    public DistanceObservation? Get(long id) => _races.GetValueOrDefault(id);
    public void Put(DistanceObservation observation) => _races[observation.EventId] = observation;
    public IReadOnlyList<DistanceObservation> Races => _races.Values.OrderByDescending(x => x.StartTimeUnix).ToList();

    public async Task SaveAsync(CancellationToken ct)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            var temp = _statePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Races), ct);
            File.Move(temp, _statePath, true);
        }
        finally { _saveGate.Release(); }
    }

    public object Review() => new
    {
        tableCount = TableCount, lastPollUtc = LastPollUtc, lastError = LastError,
        timingNote = "Seconds from official off to first observed margin. Only races observed before the off and with a post-off missing-margin check count in averages. Backfills are excluded; polling adds observation delay.",
        tracks = Races.GroupBy(x => x.Track).OrderBy(x => x.Key).Select(group =>
        {
            var timed = group.Where(x => x.TimedSample && x.MarginSeenUtc.HasValue && x.ActualOffUtc.HasValue)
                .Select(x => (x.MarginSeenUtc!.Value - x.ActualOffUtc!.Value).TotalSeconds).Order().ToArray();
            return new { track = group.Key, observed = group.Count(), margins = group.Count(x => x.Margin.HasValue),
                pending = group.Count(x => !x.Margin.HasValue && x.StartTimeUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                timedSamples = timed.Length, averageSeconds = timed.Length > 0 ? (double?)timed.Average() : null,
                maxSeconds = timed.Length > 0 ? (double?)timed.Max() : null };
        }), races = Races
    };

    public GeneratedMarket? Build(PricingEvent ev)
    {
        var race = Get(ev.EventId);
        if (ev.SportId != 4 || race?.Prices is null) return null;
        var active = ev.Status is "0" or "not started";
        var finished = race.Margin.HasValue;
        var marketId = ev.EventId * 10000L + Suffix;
        var name = $"Winning Distance ({race.Metres}m)";
        var selections = race.Prices.Bands.Select((band, index) => new GeneratedSelection
        {
            EventId = ev.EventId, MarketId = marketId, SelectionId = marketId * 100 + index + 1,
            MarketName = name, Name = band.Label, Identifier = (index + 1).ToString("00"),
            PriceBack = band.Price, PriceBackLast = band.Price, Active = active && !finished ? 1 : 0,
            Display = finished ? 0 : 1,
            Result = finished ? band.Wins(race.Margin!.Value) ? "1" : "0" : null
        }).ToList();
        return new(marketId, name, MarketType, 220, active && !finished, !finished, selections);
    }

    public static decimal? ParseMargin(string? raw)
    {
        var s = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(s)) return null;
        var named = s switch { "nse" or "nose" => .05m, "sh" or "short head" => .1m,
            "hd" or "head" => .2m, "nk" or "neck" => .3m, "dh" or "dead heat" => 0m, _ => (decimal?)null };
        if (named.HasValue) return named;
        s = s.Replace("¼", " 0.25").Replace("½", " 0.5").Replace("¾", " 0.75");
        s = Regex.Replace(s, @"\b([123])/4\b", m => (decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 4).ToString(CultureInfo.InvariantCulture));
        s = s.Replace("1/2", "0.5");
        decimal result = 0;
        foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!decimal.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
            result += value;
        }
        return result;
    }

    public static IEnumerable<DistancePriceTable> ParseTables(string text)
    {
        var blocks = Regex.Matches(text, @"(?m)^(?<track>[^\r\n|]+) \| (?<metres>\d+) metres \| (?<count>[\d,]+) races\r?$");
        for (int i = 0; i < blocks.Count; i++)
        {
            var header = blocks[i];
            var body = text[header.Index..(i + 1 < blocks.Count ? blocks[i + 1].Index : text.Length)];
            var rows = Regex.Matches(body, @"(?m)^\s+(?<label>[^:\r\n]+): (?<price>\d+\.\d+)\s+\(");
            if (rows.Count != 5) throw new InvalidDataException("Expected five price bands: " + header.Value);
            DistanceBand Read(int index, string side, string family)
            {
                var row = rows[index];
                var label = row.Groups["label"].Value.Trim();
                var numbers = Regex.Matches(label, @"\d+(?:\.\d+)?").Select(m => decimal.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
                if (numbers.Length == 0)
                    throw new InvalidDataException($"No bound found in price band label '{label}' in: {header.Value}");
                // Case-insensitive: the bounds used to be inferred with an Ordinal
                // "over " prefix test, so a label written "Over 2.5" produced a band with
                // no bounds at all, which then won on every margin.
                decimal? lower = label.StartsWith("over ", StringComparison.OrdinalIgnoreCase) ? numbers[0] : null;
                decimal? upper = label.Contains("or less", StringComparison.OrdinalIgnoreCase) ? numbers[0]
                    : numbers.Length == 2 ? numbers[1] : null;
                if (lower is null && upper is null)
                    throw new InvalidDataException(
                        $"Price band label '{label}' in {header.Value} yields no bounds; such a band wins on every margin.");
                return new($"{side} ({family}: {label} lengths)", double.Parse(row.Groups["price"].Value, CultureInfo.InvariantCulture), lower, upper);
            }
            DistanceBand[] bands = [Read(1, "OVER", "2-way"), Read(0, "UNDER", "2-way"),
                Read(4, "OVER", "3-way"), Read(2, "UNDER", "3-way"), Read(3, "MIDDLE", "3-way")];
            // The bands must partition the margin range, otherwise a margin either pays two
            // selections in the same family or none of them. Nothing checked this before.
            RequirePartition(header.Value, "2-way", bands[1], null, bands[0]);
            RequirePartition(header.Value, "3-way", bands[3], bands[4], bands[2]);
            var twoWay = RacingMarketCalculator.SnapMarketToMinimumFractionalBook(bands.Take(2).Select(x => x.Price).ToArray());
            var threeWay = RacingMarketCalculator.SnapMarketToMinimumFractionalBook(bands.Skip(2).Select(x => x.Price).ToArray());
            for (var index = 0; index < 2; index++) bands[index] = bands[index] with { Price = twoWay[index] };
            for (var index = 0; index < 3; index++) bands[index + 2] = bands[index + 2] with { Price = threeWay[index] };
            yield return new(header.Groups["track"].Value, int.Parse(header.Groups["metres"].Value),
                int.Parse(header.Groups["count"].Value.Replace(",", "")), bands);
        }
    }

    // under: bounded above only. middle (optional): bounded both sides. over: bounded below
    // only. Adjacent bounds must meet exactly, so every margin falls in exactly one band.
    private static void RequirePartition(
        string header, string family, DistanceBand under, DistanceBand? middle, DistanceBand over)
    {
        void Require(bool condition, string detail)
        {
            if (!condition)
                throw new InvalidDataException($"{family} price bands do not partition in {header}: {detail}");
        }
        Require(under.Lower is null && under.Upper.HasValue, $"'{under.Label}' must be bounded above only.");
        Require(over.Upper is null && over.Lower.HasValue, $"'{over.Label}' must be bounded below only.");
        if (middle is null)
        {
            Require(under.Upper == over.Lower,
                $"'{under.Label}' ends at {under.Upper} but '{over.Label}' starts at {over.Lower}.");
            return;
        }
        Require(middle.Lower.HasValue && middle.Upper.HasValue, $"'{middle.Label}' must be bounded on both sides.");
        Require(under.Upper == middle.Lower,
            $"'{under.Label}' ends at {under.Upper} but '{middle.Label}' starts at {middle.Lower}.");
        Require(middle.Upper == over.Lower,
            $"'{middle.Label}' ends at {middle.Upper} but '{over.Label}' starts at {over.Lower}.");
    }
}
