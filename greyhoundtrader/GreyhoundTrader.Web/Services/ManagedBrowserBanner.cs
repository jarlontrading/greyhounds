using Microsoft.Playwright;

namespace RacingTrader.Web.Services;

internal static class ManagedBrowserBanner
{
    public const string MarkerArgumentPrefix = "--racingtrader-managed=";

    public static string MarkerArgument(string source) =>
        MarkerArgumentPrefix + source.Trim().ToLowerInvariant();

    public static Task InstallAsync(IPage page, string source) => page.AddInitScriptAsync(Script(source));

    public static async Task ShowAsync(IPage page, string source)
    {
        if (page.IsClosed) return;
        try { await page.EvaluateAsync(Script(source)); }
        catch (PlaywrightException) { }
    }

    private static string Script(string source)
    {
        var safeSource = source.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $$"""
            (() => {
              const source = '{{safeSource}}';
              const state = window.__racingTraderManaged ||= {
                source,
                samples: [],
                lastRequestMs: 0,
                observer: null,
                timer: null
              };
              state.source = source;

              const record = entry => {
                const when = performance.timeOrigin + (entry?.startTime || performance.now());
                state.samples.push(when);
                state.lastRequestMs = Math.max(state.lastRequestMs || 0, when);
              };
              if (!state.observer && window.PerformanceObserver) {
                try {
                  state.observer = new PerformanceObserver(list => list.getEntries().forEach(record));
                  state.observer.observe({ type: 'resource', buffered: true });
                } catch (_) { }
              }

              const render = () => {
                if (!document.documentElement) return;
                const now = Date.now();
                state.samples = state.samples.filter(value => now - value <= 60000);
                let banner = document.getElementById('__racingTraderManagedBanner');
                if (!banner) {
                  banner = document.createElement('div');
                  banner.id = '__racingTraderManagedBanner';
                  banner.setAttribute('data-racingtrader-managed', source);
                  banner.style.cssText = [
                    'position:fixed','z-index:2147483647','top:4px','right:4px',
                    'padding:7px 10px','border:2px solid #32d583','border-radius:6px',
                    'background:rgba(5,20,28,.94)','color:#fff','font:700 12px/1.25 Arial,sans-serif',
                    'box-shadow:0 2px 10px rgba(0,0,0,.55)','pointer-events:none','white-space:nowrap'
                  ].join(';');
                  document.documentElement.appendChild(banner);
                }
                const last = state.lastRequestMs
                  ? new Date(state.lastRequestMs).toLocaleTimeString('en-GB', { hour12: false })
                  : 'waiting';
                banner.textContent = `RACINGTRADER MANAGED · ${source} · net ${state.samples.length}/min · last ${last}`;
                const prefix = `[RT MANAGED ${source}] `;
                if (!document.title.startsWith(prefix)) document.title = prefix + document.title.replace(/^\[RT MANAGED [^\]]+\]\s*/, '');
              };
              render();
              if (!state.timer) state.timer = setInterval(render, 1000);
              if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', render, { once: true });
            })();
            """;
    }
}
