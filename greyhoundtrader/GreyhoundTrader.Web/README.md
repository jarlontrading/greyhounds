# Greyhound Trader Web

Isolated .NET 8 scaffold cloned from RacingTrader for greyhound development. It is safe by default: port 5287, hosted workers disabled, startup schema work skipped, and all RabbitMQ publishing disabled.

Read `..\HANDOVER.md` before enabling any provider or lifecycle worker. Configuration uses a separate `GreyhoundTrader.env`; see `..\GreyhoundTrader.env.example`. Never reuse or copy `SPRacing.env`.

Build with `dotnet build ..\GreyhoundTrader.sln`.

## Legacy implementation notes

The browser-based operator screen for horse racing and greyhounds. It uses the existing `randg` database and external `SPRacing.env` settings.

## Start

From the solution folder:

```powershell
dotnet run --project RacingTrader.Web\RacingTrader.Web.csproj
```

Then open <http://127.0.0.1:5187/>. The application binds to localhost only.

The `Start-RacingTrader-Web.cmd` file in the solution folder is a double-click alternative.

## Current scope

- PROD publishing for subscribed horse and greyhound meetings.
- Horse and greyhound meeting subscriptions.
- Saved meeting subscriptions automatically book new races as they arrive in the next 48-hour feed window.
- All-meetings and subscribed-competitions navigation.
- Subscribed events grouped within each meeting.
- Runner price matrices, with silks/trap images and markets in `displayOrder` order.
- Race Winner and derived-market prices use the exact fractional ladder from `sdxSpunoRacing11.xlsm` sheet `LU`; balanced ladder walks target 100%/200%/300% books with a one-point lower tolerance.
- Event lifecycle, active/suspended and displayed/hidden state badges.
- Published winner/loser results for resulted selections.
- Full Sporting Life finishing order for horses and greyhounds, including non-completion status where supplied.
- Greyhound Odd/Even, Inside/Middle/Outside and half-field derived prices.
- Workbook-compatible Top 2 and Top 3 prices, plus trader-controlled Betting Without and Insure Bet 2/3/4 markets.
- Any number of trader-selected Match Bet pairs; a non-runner automatically voids the affected pair.
- Horse withdrawals with a timestamped Tattersalls Rule 4 deduction.
- Per-market Rule 4 calculations and audit history across the additional markets.
- Automatic Rule 4 detection: explicit `NR` immediately, or a sustained isolated suspension confirmed over three observations and at least 20 seconds while other runners remain active.
- Race Winner price-change history retained for seven days and rendered as compact runner graphs.
- Delayed Betfair WIN back/lay/LTP prices are shown before the internal market columns, with stale-price protection and Race Winner odds/probability differences.
- Greyhound replacement while retaining the original trap/selection.
- Audited operator and automatic runner actions.

The web service discovers and prices horses and greyhounds concurrently, builds all configured markets, stores their snapshots and publishes them to the PROD exchanges. The legacy Forms executable is not required for this live path.

- Pre-live event and odds deltas are checked every 10 seconds.
- In-play events are checked every second; unchanged runner snapshots are not republished.
- Sporting Life fast results are checked in the web service and complete finishing orders settle every configured market.

## Configuration

Settings are loaded in this order:

1. `D:\Dropbox\projectvariables\SPRacing.env`
2. `C:\Dropbox\projectvariables\SPRacing.env`

`SPRACING_RACING_DATABASE` is optional and defaults to `randg`.

### Betfair delayed-feed integration

The Betfair layer is disabled until credentials are supplied. It only discovers WIN markets for meetings already subscribed in Racing Trader. A link is trusted only when the meeting, off-time and every active internal runner match; partial links are retained for diagnostics but cannot bias prices.

```text
SPRACING_BETFAIR_ENABLED=false
SPRACING_BETFAIR_APP_KEY=
SPRACING_BETFAIR_SESSION_TOKEN=
SPRACING_BETFAIR_USERNAME=
SPRACING_BETFAIR_PASSWORD=
SPRACING_BETFAIR_CERTIFICATE_PATH=
SPRACING_BETFAIR_CERTIFICATE_PASSWORD=
SPRACING_BETFAIR_BIAS_ENABLED=false
SPRACING_BETFAIR_BIAS_WEIGHT=0.15
SPRACING_BETFAIR_MIN_PROBABILITY_DIFFERENCE=0.01
SPRACING_BETFAIR_MAX_SPREAD_PERCENT=0.20
SPRACING_BETFAIR_POLL_SECONDS=5
SPRACING_BETFAIR_CATALOGUE_SECONDS=300
SPRACING_BETFAIR_MAX_PRICE_AGE_SECONDS=240
SPRACING_BETFAIR_ALLOW_INPLAY_BIAS=false
SPRACING_BETFAIR_ORDER_MODE=disabled
```

Use the current 16-character delayed App Key during development. A manual session token remains supported, but the preferred autonomous configuration supplies the username, password and path to a Betfair-linked PKCS#12 (`.p12`/`.pfx`) client certificate. The service obtains a session through Betfair's certificate-login endpoint, sends keep-alive calls every six hours, and renews an invalid session automatically. Delayed in-play data is excluded from price bias by default. `SPRACING_BETFAIR_ORDER_MODE` is diagnostic only in this build: no endpoint or background process can place orders.

Diagnostics are available at `/api/betfair/status` and `/api/betfair/matches`.

The web project creates its own support tables on startup:

- `web_meeting_subscriptions`
- `web_runner_actions`
- `web_runner_state`
- `web_non_runner_candidates`
- `web_price_history`
- `web_event_market_config`
- `web_match_bet_pairs`
- `web_market_adjustments`
- `web_runner_results`
- `web_betfair_event_links`
- `web_betfair_runner_links`
- `web_betfair_prices`
- `web_betfair_state`
