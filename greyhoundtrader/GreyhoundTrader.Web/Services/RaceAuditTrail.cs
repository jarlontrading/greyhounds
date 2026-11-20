using System.Text.Json;

namespace RacingTrader.Web.Services;

internal sealed class RaceAuditTrail
{
    private const string Root = @"D:\Temp\racingtrader-audit";
    private static readonly TimeZoneInfo UkZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ILogger<RaceAuditTrail> _logger;

    public RaceAuditTrail(ILogger<RaceAuditTrail> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(Root);
        // Retention is maintenance work and must never delay construction of the
        // suspension worker. Run cleanup separately from the live startup path.
    }

    public async Task WriteAsync(
        string stage, long? eventId, int? sportId, string? meeting, string? eventName,
        long startTimeUnix, string provider, string method, string message,
        object? details = null, CancellationToken cancellationToken = default)
    {
        var utc = DateTime.UtcNow;
        var raceDate = startTimeUnix > 0
            ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(startTimeUnix), UkZone).ToString("yyyy-MM-dd")
            : TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), UkZone).ToString("yyyy-MM-dd");
        var entry = JsonSerializer.Serialize(new
        {
            utc, raceDate, stage, eventId, sportId, meeting, eventName, startTimeUnix,
            scheduledUtc = startTimeUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(startTimeUnix).UtcDateTime
                : (DateTime?)null,
            provider, method, message, details
        }, JsonOptions);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(Root, raceDate + ".jsonl");
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await File.AppendAllTextAsync(path, entry + Environment.NewLine, cancellationToken);
                    return;
                }
                catch (IOException ex) when (attempt < 5)
                {
                    _logger.LogDebug(ex, "Race audit file {Path} is busy; retry {Attempt}/5.", path, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
                }
                catch (IOException ex)
                {
                    // Audit telemetry must never prevent OFF detection, pricing,
                    // or result settlement. The activity/database records remain
                    // authoritative if an external tray reader holds this file.
                    _logger.LogWarning(ex, "Skipped race audit entry because {Path} remained busy.", path);
                    return;
                }
            }
        }
        finally { Gate.Release(); }
    }

    public void Write(
        string stage, long? eventId, int? sportId, string? meeting, string? eventName,
        long startTimeUnix, string provider, string method, string message, object? details = null) =>
        WriteAsync(stage, eventId, sportId, meeting, eventName, startTimeUnix,
            provider, method, message, details).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<JsonElement>> ReadAsync(
        DateOnly date, long? eventId, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Root, date.ToString("yyyy-MM-dd") + ".jsonl");
        if (!File.Exists(path)) return [];
        var output = new List<JsonElement>();
        await Gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (eventId is not null &&
                    (!root.TryGetProperty("eventId", out var idNode) || idNode.ValueKind == JsonValueKind.Null ||
                     idNode.GetInt64() != eventId.Value)) continue;
                output.Add(root.Clone());
            }
        }
        finally { Gate.Release(); }
        return output.OrderBy(item => item.GetProperty("utc").GetDateTime()).ToList();
    }
}
