using Amazon.S3;
using Amazon.S3.Model;
using RacingTrader.Web.Configuration;
using System.Collections.Concurrent;

namespace RacingTrader.Web.Services;

internal sealed class R2SilkStorage : IDisposable
{
    private const int MaximumSilkBytes = 2 * 1024 * 1024;
    private readonly ExternalSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<R2SilkStorage> _logger;
    private readonly Lazy<AmazonS3Client?> _client;
    private readonly ConcurrentDictionary<string, string> _knownObjects = new(StringComparer.Ordinal);

    public R2SilkStorage(
        ExternalSettings settings,
        IHttpClientFactory httpClientFactory,
        ILogger<R2SilkStorage> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        // AWS client construction is not part of the web/worker startup path.
        // On a saturated host it has previously held singleton resolution and
        // prevented the suspension sentinel from being constructed.
        _client = new Lazy<AmazonS3Client?>(() =>
        {
            if (!settings.R2SilkStorageConfigured) return null;
            return new AmazonS3Client(settings.R2AccessKeyId, settings.R2SecretAccessKey,
                new AmazonS3Config
            {
                ServiceURL = $"https://{settings.R2AccountId}.r2.cloudflarestorage.com",
                AuthenticationRegion = "auto",
                ForcePathStyle = true,
                // R2 does not implement the AWS streaming checksum-trailer mode
                // enabled by newer SDK defaults.
                RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED
            });
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string?> CacheBetfredSilkAsync(
        long eventId,
        long competitorId,
        string sourceUrl,
        CancellationToken cancellationToken)
    {
        var client = _client.Value;
        if (client is null || eventId <= 0 || competitorId <= 0) return null;
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps ||
            !source.Host.Equals("assets.oddsking.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = $"silks/{eventId}/{competitorId}.png";
        var publicUrl = $"{_settings.R2PublicBaseUrl}/{key}";
        if (_knownObjects.TryGetValue(key, out var known)) return known;

        try
        {
            try
            {
                await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = _settings.R2BucketName,
                    Key = key
                }, cancellationToken);
                _knownObjects[key] = publicUrl;
                return publicUrl;
            }
            catch (AmazonS3Exception ex) when ((int)ex.StatusCode == 404) { }

            using var response = await _httpClientFactory.CreateClient("SilkImages")
                .GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumSilkBytes)
                throw new InvalidDataException("Betfred silk exceeds the 2 MB safety limit.");

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaximumSilkBytes || !IsPng(bytes))
                throw new InvalidDataException("Betfred silk response was not a valid PNG image.");

            await using var stream = new MemoryStream(bytes, writable: false);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _settings.R2BucketName,
                Key = key,
                InputStream = stream,
                ContentType = "image/png",
                AutoCloseStream = false,
                DisablePayloadSigning = true,
                DisableDefaultChecksumValidation = true,
                Headers = { CacheControl = "public, max-age=31536000, immutable" }
            }, cancellationToken);
            _knownObjects[key] = publicUrl;
            return publicUrl;
        }
        catch (Exception ex) when (ex is HttpRequestException or AmazonS3Exception or InvalidDataException)
        {
            _logger.LogWarning(ex,
                "Could not cache Betfred silk for event {EventId}, competitor {CompetitorId}.",
                eventId, competitorId);
            return null;
        }
    }

    private static bool IsPng(byte[] bytes) => bytes.Length >= 8 &&
        bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 &&
        bytes[4] == 0x0d && bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value?.Dispose();
    }
}
