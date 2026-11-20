using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using RacingTrader.Web.Configuration;

namespace RacingTrader.Web.Services;

internal sealed class BetfairSessionManager(
    ExternalSettings settings,
    ILogger<BetfairSessionManager> logger) : IDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _sessionToken = settings.BetfairSessionToken;
    private DateTime _lastKeepAliveUtc = DateTime.UtcNow;

    public async Task<string> GetSessionTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sessionToken) &&
            DateTime.UtcNow - _lastKeepAliveUtc < KeepAliveInterval)
            return _sessionToken;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrWhiteSpace(_sessionToken))
                return await CertificateLoginAsync(cancellationToken);

            if (DateTime.UtcNow - _lastKeepAliveUtc >= KeepAliveInterval)
            {
                try
                {
                    await KeepAliveAsync(_sessionToken, cancellationToken);
                    _lastKeepAliveUtc = DateTime.UtcNow;
                }
                catch when (settings.BetfairCertificateConfigured)
                {
                    _sessionToken = null;
                    return await CertificateLoginAsync(cancellationToken);
                }
            }

            return _sessionToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void InvalidateSession() => _sessionToken = null;

    private async Task<string> CertificateLoginAsync(CancellationToken cancellationToken)
    {
        if (!settings.BetfairCertificateConfigured)
            throw new InvalidOperationException(
                "The Betfair session has expired and certificate login is not configured.");

        using var certificate = new X509Certificate2(
            settings.BetfairCertificatePath!,
            settings.BetfairCertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(certificate);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://identitysso-cert.betfair.com/api/certlogin");
        request.Headers.TryAddWithoutValidation("X-Application", settings.BetfairAppKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = settings.BetfairUsername!,
            ["password"] = settings.BetfairPassword!
        });

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken) as JsonObject;
        var status = root?["loginStatus"]?.ToString();
        var token = root?["sessionToken"]?.ToString();
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Betfair certificate login failed: {status ?? "UNKNOWN"}.");

        _sessionToken = token;
        _lastKeepAliveUtc = DateTime.UtcNow;
        logger.LogInformation("Betfair certificate login succeeded; a new session is active.");
        return token;
    }

    private async Task KeepAliveAsync(string token, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://identitysso.betfair.com/api/keepAlive");
        request.Headers.TryAddWithoutValidation("X-Application", settings.BetfairAppKey);
        request.Headers.TryAddWithoutValidation("X-Authentication", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var root = await JsonNode.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken) as JsonObject;
        if (!string.Equals(root?["status"]?.ToString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Betfair keep-alive failed: {root?["error"]?.ToString() ?? "UNKNOWN"}.");
    }

    public void Dispose() => _gate.Dispose();
}
