using System.Globalization;

namespace RacingTrader.Web.Configuration;

public sealed class ExternalSettings
{
    private const string FileName = "GreyhoundTrader.env";
    private readonly IReadOnlyDictionary<string, string> _values;

    public ExternalSettings()
    {
        var configuredPath = Environment.GetEnvironmentVariable("GREYHOUNDTRADER_SETTINGS_FILE");
        var candidates = new[]
        {
            configuredPath,
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "projectvariables", FileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "projectvariables", FileName)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Sportuno", "GreyhoundTrader", FileName)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        SourcePath = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Greyhound Trader settings were not found. Expected {string.Join(" or ", candidates)}.");

        _values = File.ReadLines(SourcePath)
            .Select((raw, index) => (Line: raw.Trim(), Number: index + 1))
            .Where(x => x.Line.Length > 0 && !x.Line.StartsWith('#'))
            .Select(x =>
            {
                var separator = x.Line.IndexOf('=');
                if (separator <= 0)
                    throw new FormatException($"Invalid setting at {SourcePath}, line {x.Number}.");

                var value = x.Line[(separator + 1)..].Trim();
                if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                    value = value[1..^1];

                return new KeyValuePair<string, string>(x.Line[..separator].Trim(), value);
            })
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        MySqlServer = Required("GREYHOUNDTRADER_MYSQL_SERVER");
        MySqlUser = Required("GREYHOUNDTRADER_MYSQL_USER");
        MySqlPassword = Required("GREYHOUNDTRADER_MYSQL_PASSWORD");
        RacingDatabase = Environment.GetEnvironmentVariable("GREYHOUNDTRADER_DATABASE")
            ?? Optional("GREYHOUNDTRADER_DATABASE")
            ?? "greyhoundtrader";
        B365ApiToken = Required("GREYHOUNDTRADER_B365_API_TOKEN");
        RabbitMqUrlSp = Required("GREYHOUNDTRADER_RABBITMQ_URL_SP");

        R2AccountId = Optional("SPRACING_R2_ACCOUNT_ID");
        R2AccessKeyId = Optional("SPRACING_R2_ACCESS_KEY_ID");
        R2SecretAccessKey = Optional("SPRACING_R2_SECRET_ACCESS_KEY");
        R2BucketName = Optional("SPRACING_R2_BUCKET_NAME") ?? "racingtrader-images";
        R2PublicBaseUrl = (Optional("SPRACING_R2_PUBLIC_BASE_URL") ??
            "https://images.jarlontrading.co.uk").TrimEnd('/');

        BetfairEnabled = OptionalBoolean("SPRACING_BETFAIR_ENABLED", false);
        BetfairAppKey = Optional("SPRACING_BETFAIR_APP_KEY");
        BetfairSessionToken = Optional("SPRACING_BETFAIR_SESSION_TOKEN");
        BetfairUsername = Optional("SPRACING_BETFAIR_USERNAME");
        BetfairPassword = Optional("SPRACING_BETFAIR_PASSWORD");
        BetfairCertificatePath = Optional("SPRACING_BETFAIR_CERTIFICATE_PATH");
        BetfairCertificatePassword = Optional("SPRACING_BETFAIR_CERTIFICATE_PASSWORD");
        BetfairBiasEnabled = OptionalBoolean("SPRACING_BETFAIR_BIAS_ENABLED", false);
        BetfairBiasWeight = Math.Clamp(OptionalDouble("SPRACING_BETFAIR_BIAS_WEIGHT", .15), 0, .5);
        BetfairMinimumProbabilityDifference = Math.Clamp(OptionalDouble("SPRACING_BETFAIR_MIN_PROBABILITY_DIFFERENCE", .01), 0, .25);
        BetfairMaximumSpreadPercent = Math.Clamp(OptionalDouble("SPRACING_BETFAIR_MAX_SPREAD_PERCENT", .20), .01, 1);
        BetfairPollSeconds = Math.Clamp(OptionalInt32("SPRACING_BETFAIR_POLL_SECONDS", 5), 1, 60);
        BetfairCatalogueSeconds = Math.Clamp(OptionalInt32("SPRACING_BETFAIR_CATALOGUE_SECONDS", 300), 30, 3600);
        BetfairMaximumPriceAgeSeconds = Math.Clamp(OptionalInt32("SPRACING_BETFAIR_MAX_PRICE_AGE_SECONDS", 240), 10, 900);
        BetfairAllowInPlayBias = OptionalBoolean("SPRACING_BETFAIR_ALLOW_INPLAY_BIAS", false);
        BetfairOrderMode = (Optional("SPRACING_BETFAIR_ORDER_MODE") ?? "disabled").Trim().ToLowerInvariant();
    }

    public string SourcePath { get; }
    public string MySqlServer { get; }
    public string MySqlUser { get; }
    public string MySqlPassword { get; }
    public string RacingDatabase { get; }
    public string B365ApiToken { get; }
    public string RabbitMqUrlSp { get; }
    public string? R2AccountId { get; }
    public string? R2AccessKeyId { get; }
    public string? R2SecretAccessKey { get; }
    public string R2BucketName { get; }
    public string R2PublicBaseUrl { get; }
    public bool R2SilkStorageConfigured =>
        !string.IsNullOrWhiteSpace(R2AccountId) &&
        !string.IsNullOrWhiteSpace(R2AccessKeyId) &&
        !string.IsNullOrWhiteSpace(R2SecretAccessKey);
    public bool BetfairEnabled { get; }
    public string? BetfairAppKey { get; }
    public string? BetfairSessionToken { get; }
    public string? BetfairUsername { get; }
    public string? BetfairPassword { get; }
    public string? BetfairCertificatePath { get; }
    public string? BetfairCertificatePassword { get; }
    public bool BetfairCertificateConfigured =>
        !string.IsNullOrWhiteSpace(BetfairUsername) &&
        !string.IsNullOrWhiteSpace(BetfairPassword) &&
        !string.IsNullOrWhiteSpace(BetfairCertificatePath) &&
        File.Exists(BetfairCertificatePath);
    public string BetfairAuthenticationMode => BetfairCertificateConfigured
        ? "certificate"
        : !string.IsNullOrWhiteSpace(BetfairSessionToken) ? "manual-session-token" : "unconfigured";
    public bool BetfairConfigured =>
        BetfairEnabled &&
        BetfairAppKey?.Length == 16 &&
        (BetfairCertificateConfigured || !string.IsNullOrWhiteSpace(BetfairSessionToken));
    public bool BetfairBiasEnabled { get; }
    public double BetfairBiasWeight { get; }
    public double BetfairMinimumProbabilityDifference { get; }
    public double BetfairMaximumSpreadPercent { get; }
    public int BetfairPollSeconds { get; }
    public int BetfairCatalogueSeconds { get; }
    public int BetfairMaximumPriceAgeSeconds { get; }
    public bool BetfairAllowInPlayBias { get; }
    public string BetfairOrderMode { get; }

    private string Required(string key) =>
        Optional(key) ?? throw new InvalidOperationException($"Required setting '{key}' is missing from {SourcePath}.");

    private string? Optional(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private bool OptionalBoolean(string key, bool fallback) =>
        bool.TryParse(Optional(key), out var value) ? value : fallback;

    private int OptionalInt32(string key, int fallback) =>
        int.TryParse(Optional(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private double OptionalDouble(string key, double fallback) =>
        double.TryParse(Optional(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
