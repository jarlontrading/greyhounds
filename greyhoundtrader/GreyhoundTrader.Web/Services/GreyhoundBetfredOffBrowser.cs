using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace RacingTrader.Web.Services;

internal sealed record GreyhoundBetfredOffStatus(
    bool Enabled, bool BrowserConnected, bool PageAvailable, string ManagedBrowserId, string BrowserProfile,
    string CouponUrl, DateTime? StartedAtUtc, DateTime? LastObservedAtUtc,
    int RaceCardsObserved, int RunningSignals, string? LastError);

internal sealed record GreyhoundBetfredRunningSignal(
    long BetfredEventId, string MeetingText, string CardText, DateTime ObservedAtUtc);

internal sealed class GreyhoundBetfredOffBrowser(
    RacingDiagnosticCapture diagnostics,
    ILogger<GreyhoundBetfredOffBrowser> logger) : BackgroundService, IAsyncDisposable
{
    private const string ManagedBrowserId = "GreyhoundTrader/Betfred-OFF";
    private const string CouponUrl = "https://www.betfred.com/sports/greyhound-racing?tab=today";
    private const string BrowserProfile = @"D:\Temp\greyhoundtrader\betfred-off-browser-profile-v3";
    private readonly ConcurrentDictionary<long, GreyhoundBetfredRunningSignal> _signals = new();
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private volatile bool _connected;
    private volatile bool _pageAvailable;
    private DateTime? _startedAtUtc;
    private DateTime? _lastObservedAtUtc;
    private int _raceCardsObserved;
    private string? _lastError;

    public GreyhoundBetfredOffStatus Status => new(
        true, _connected, _pageAvailable, ManagedBrowserId, BrowserProfile, CouponUrl,
        _startedAtUtc, _lastObservedAtUtc, _raceCardsObserved, _signals.Count, _lastError);

    public bool TryGetRaceOffSignal(PricingEvent racingEvent, out string reason)
    {
        var meeting = RacingEventNames.MeetingKey(racingEvent.Meeting);
        var ukStart = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
            DateTimeOffset.FromUnixTimeSeconds(racingEvent.StartTimeUnix), "GMT Standard Time");
        var time = ukStart.ToString("HH:mm");
        foreach (var signal in _signals.Values.OrderByDescending(item => item.ObservedAtUtc))
        {
            if (DateTime.UtcNow - signal.ObservedAtUtc > TimeSpan.FromMinutes(10)) continue;
            var textKey = RacingEventNames.MeetingKey(signal.MeetingText);
            if (!textKey.Contains(meeting, StringComparison.Ordinal) ||
                !signal.CardText.Contains(time, StringComparison.OrdinalIgnoreCase)) continue;
            reason = $"Betfred greyhound coupon reported Race is Running " +
                     $"(event {signal.BetfredEventId}, observed {signal.ObservedAtUtc:O}).";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not delay the trader HTTP listener while the visible Chrome profile starts.
        await Task.Yield();
        Directory.CreateDirectory(BrowserProfile);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureBrowserAsync(stoppingToken);
                await ObserveAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _connected = false;
                _pageAvailable = false;
                _lastError = ex.Message;
                logger.LogWarning(ex, "Greyhound Betfred OFF page check failed; retrying without relaunching an open browser.");
                // A transient SPA/navigation error must not create a Chrome relaunch
                // storm. Only discard the context when Chrome has actually closed.
                if (_page is null || _page.IsClosed)
                    await CloseBrowserAsync();
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task EnsureBrowserAsync(CancellationToken ct)
    {
        if (_page is { IsClosed: false }) return;
        _playwright ??= await Playwright.CreateAsync();
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(BrowserProfile,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                Channel = "chrome",
                Timeout = 30000,
                Args =
                [
                    "--no-sandbox",
                    "--start-maximized",
                    "--disable-blink-features=AutomationControlled",
                    "--greyhound-trader-managed=betfred-off"
                ],
                ViewportSize = null
            });
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        await _page.AddInitScriptAsync("""
            (() => {
              const add = () => {
                if (document.getElementById('greyhound-trader-managed-banner')) return;
                const b = document.createElement('div');
                b.id = 'greyhound-trader-managed-banner';
                b.textContent = 'GREYHOUND TRADER · BETFRED OFF MONITOR · managed browser · 1s status check';
                Object.assign(b.style, {position:'fixed',left:'0',right:'0',top:'0',zIndex:'2147483647',
                  background:'#4b136f',color:'#fff',font:'bold 14px Arial',padding:'8px 12px',
                  textAlign:'center',boxShadow:'0 2px 8px #0008'});
                document.documentElement.appendChild(b);
              };
              add(); document.addEventListener('DOMContentLoaded', add);
            })();
            """);
        try
        {
            // Betfred is a long-lived SPA and does not reliably reach
            // DOMContentLoaded. The committed document is enough; ObserveAsync
            // can wait for coupon links on its normal one-second cycle.
            await _page.GotoAsync(CouponUrl,
                new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 10000 });
        }
        catch (PlaywrightException ex)
        {
            // Keep the successfully launched browser. A slow navigation is not
            // a reason to kill Chrome and immediately create another process tree.
            _lastError = $"Initial Betfred navigation is still loading: {ex.Message}";
            logger.LogWarning(ex, "Betfred OFF browser navigation did not settle; keeping the browser open.");
        }
        _startedAtUtc ??= DateTime.UtcNow;
        _connected = true;
        logger.LogInformation("Started managed Greyhound Trader Betfred OFF browser at {Url}.", CouponUrl);
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        if (_page is null) return;
        var json = await _page.EvaluateAsync<string>("""
            () => JSON.stringify([...document.querySelectorAll("a[href*='/sports/greyhound-racing/event/']")]
              .map(a => {
                // Keep the largest ancestor which still belongs to this one
                // event. Never promote the card to the meeting container: one
                // RUNNING badge there would otherwise mark every race RUNNING.
                let card = a;
                let node = a.parentElement;
                for (let i=0; i<8 && node; i++, node=node.parentElement) {
                  const links = node.querySelectorAll("a[href*='/sports/greyhound-racing/event/']");
                  if (links.length !== 1) break;
                  card = node;
                }
                // Meeting identity may live in the surrounding coupon group,
                // but its other race times must remain separate from cardText.
                let group = card.parentElement || card;
                for (let i=0; i<8 && group; i++) {
                  const groupText = group.innerText || '';
                  const links = group.querySelectorAll("a[href*='/sports/greyhound-racing/event/']");
                  if (links.length > 1 && /\b\d+\s+races?\b/i.test(groupText)) break;
                  if (!group.parentElement) break;
                  group = group.parentElement;
                }
                const id = Number((a.getAttribute('href') || '').match(/\/event\/(\d+)/)?.[1] || 0);
                return {
                  id,
                  meetingText: (group.innerText || '').replace(/\s+/g,' ').trim(),
                  text: (card.innerText || '').replace(/\s+/g,' ').trim()
                };
              }).filter(x => x.id > 0))
            """);
        var regionBlocked = await _page.EvaluateAsync<bool>("""
            () => /(?:unavailable|not available) in your region/i.test(document.body?.innerText || '')
            """);
        var cards = JsonSerializer.Deserialize<List<Card>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        _raceCardsObserved = cards.Count;
        _lastObservedAtUtc = DateTime.UtcNow;
        _connected = true;
        _pageAvailable = !regionBlocked;
        _lastError = regionBlocked ? "Betfred is unavailable in this region; BetsAPI OFF fallback is active." : null;
        foreach (var card in cards.Where(item =>
                     Regex.IsMatch(item.Text, @"\bRUNNING\b", RegexOptions.IgnoreCase)))
        {
            var signal = new GreyhoundBetfredRunningSignal(
                card.Id, card.MeetingText, card.Text, DateTime.UtcNow);
            _signals[card.Id] = signal;
            diagnostics.Write("betfred-greyhound-off", "race-running", new
            {
                betfredEventId = card.Id, observedAtUtc = signal.ObservedAtUtc,
                card.MeetingText, card.Text
            });
        }
        foreach (var stale in _signals.Where(item => DateTime.UtcNow - item.Value.ObservedAtUtc > TimeSpan.FromMinutes(10)).ToList())
            _signals.TryRemove(stale.Key, out _);
    }

    private async Task CloseBrowserAsync()
    {
        if (_context is not null) { try { await _context.CloseAsync(); } catch { } }
        _context = null; _page = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseBrowserAsync();
        _playwright?.Dispose();
    }

    private sealed record Card(long Id, string MeetingText, string Text);
}
