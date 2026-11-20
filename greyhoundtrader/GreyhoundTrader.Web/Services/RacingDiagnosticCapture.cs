using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace RacingTrader.Web.Services;

internal sealed class RacingDiagnosticCapture(ILogger<RacingDiagnosticCapture> logger) : BackgroundService
{
    private const string Root = @"D:\Temp\racingtrader-diagnostics";
    private static readonly TimeSpan RawSampleInterval = TimeSpan.FromMinutes(1);
    private const int SampledPayloadLimit = 250_000;
    private const long FileSizeLimit = 50_000_000;
    private readonly bool _captureEveryRawPayload = string.Equals(
        Environment.GetEnvironmentVariable("SPGREYHOUND_RAW_DIAGNOSTICS_ENABLED"), "true",
        StringComparison.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastRawSampleUtc = new(StringComparer.Ordinal);
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(
        new BoundedChannelOptions(5000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public void Write(string source, string kind, object? metadata = null, string? payload = null)
    {
        if (!_captureEveryRawPayload && payload is not null)
        {
            var now = DateTime.UtcNow;
            var sampleKey = $"{source}|{kind}";
            var last = _lastRawSampleUtc.GetOrAdd(sampleKey, DateTime.MinValue);
            if (now - last < RawSampleInterval)
                payload = null;
            else
                _lastRawSampleUtc[sampleKey] = now;
        }
        var payloadLimit = _captureEveryRawPayload ? 2_000_000 : SampledPayloadLimit;
        _queue.Writer.TryWrite(new Entry(DateTime.UtcNow, source, kind, metadata,
            payload is { Length: var length } && length > payloadLimit ? payload[..payloadLimit] : payload));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Diagnostic directory maintenance can be slow on a busy disk; it must
        // never delay the web listener or suspension controls becoming available.
        await Task.Yield();
        Directory.CreateDirectory(Root);
        Cleanup();
        await foreach (var entry in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var folder = Path.Combine(Root, entry.Utc.ToString("yyyy-MM-dd"), entry.Utc.ToString("HH"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, Safe(entry.Source) + ".jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= FileSizeLimit) continue;
                var line = JsonSerializer.Serialize(new
                {
                    utc = entry.Utc,
                    source = entry.Source,
                    kind = entry.Kind,
                    metadata = entry.Metadata,
                    payload = entry.Payload
                });
                await File.AppendAllTextAsync(path, line + Environment.NewLine, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogDebug(ex, "Diagnostic capture write failed."); }
        }
    }

    private static string Safe(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    private static void Cleanup()
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed record Entry(DateTime Utc, string Source, string Kind, object? Metadata, string? Payload);
}
