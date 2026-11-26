# Confirmed meeting cancellation policy

The same policy applies to horse racing and greyhounds: when an exact meeting/date is confirmed cancelled, void every unsettled selection and close/hide the affected markets and events. Preserve existing winners, losers and voids, including previously decided meeting Over/Under lines. A result of `0` is settled; only null/blank results are unsettled.

## Confirmation and scope

Automatic monitoring checks yesterday, today and tomorrow every two minutes, using UK calendar boundaries including DST. It accepts structured Sporting Life meeting abandonment with the exact date, course and expected race times. An individual abandoned race is not a meeting cancellation. Without an explicit meeting status, the complete racecard must be covered by explicit abandonment, official per-race No Race evidence, or exact provider cancellation/abandonment statuses. Existing discovery alone does not prove complete-card coverage.

Missing cards, disconnected browsers, ordinary suspension, ended/postponed/removed statuses and provider error/challenge pages do not trigger voiding. Unavailable evidence is exposed at `/api/cancellations/status`. Greyhound racecards use the supported current-day index, with dated results indexes for other days. Supplier/alias matching never drops the date or race-time check.

Primary source references: [BetsAPI status glossary](https://sk.betsapi.com/docs/GLOSSARY.html), [the dated Fakenham abandonment example](https://www.sportinglife.com/racing/results/2026-11-22), and [the greyhound racecard index](https://www.sportinglife.com/greyhounds/racecards). Unknown/new supplier formats fail closed for settlement and need review.

## Manual confirmation

Authenticated POST endpoints `/api/admin/cancellations/preview` and `/api/admin/cancellations/confirm` accept:

```json
{"operationId":"<new GUID>","sportId":2,"meeting":"<exact local meeting name>","date":"yyyy-MM-dd","source":"OPERATOR","reason":"<why cancellation is definitely confirmed>","sourceUrl":"<official evidence URL, when available>","confirmation":"VOID_UNSETTLED"}
```

Use sport 4 for greyhounds. Supply `X-Cancellation-Admin-Key` from the existing private settings file, key `SPRACING_CANCELLATION_ADMIN_KEY` or `GREYHOUNDTRADER_CANCELLATION_ADMIN_KEY`. Values must never enter Git, URLs or command arguments. API clients cannot pretend to be an internally validated provider adapter. Preview makes no business-data changes. Confirm commits settlement and queues delivery; it does not claim that Sportuno has already applied the messages. Retired unauthenticated blanket-void routes return 410.

## Persistence and delivery

Four small local InnoDB tables support the workflow:

- `web_confirmed_meeting_cancellations`: exact scope and confirmation evidence.
- `web_cancelled_events`: terminal event payloads and replay protection.
- `web_cancellation_outbox`: per-event/per-environment delivery, failures and acknowledgements.
- `web_meeting_market_frames`: current published selection identities and sticky decided outcomes for non-SQL meeting products. Price-only changes do not create history rows.

Race selection updates, cancellation records and delivery jobs commit atomically. Per-event publishing locks and persistent terminal fences stop stale feeds/results from reopening a cancellation. Horse meeting lines, greyhound trap challenges and non-SQL winning-distance markets are included. An in-flight result transaction is allowed to finish and its decided outcome is preserved/replayed.

A separate delivery loop checks pending work every second; slow evidence HTTP requests cannot delay it. Delivery uses publisher confirmations and mandatory routing, retains failed work with bounded retry backoff, and tracks PROD/DEMO independently. Repeats are idempotent by scope/event/selection identity. A stale acknowledgement cannot clear a newer late-selection payload. The reconciliation pass catches selections created after confirmation. Pending jobs never expire; acknowledged delivery receipts roll off after 30 days. Terminal cancellation and outcome records remain.

Broker acknowledgement is not a downstream settlement receipt. Queue status explicitly reports that distinction. No cloud database is required for OFF, confirmation, settlement or retry. The Ocean background mirror copies permanent cancellation/outcome records individually by primary key, avoiding an unbounded whole-table document. Pending delivery stays local; a disaster restore must reconcile/rebuild delivery from terminal payloads before changing ownership.

## Release and validation

Run `dotnet run --project CancellationTests/CancellationTests.csproj -c Release`. Set `RG_CANCELLATION_TEST_MYSQL` to an isolated localhost administrator connection for SQL, concurrency, restart/retry and terminal-guard checks. Test schemas are generated `cxltest_<GUID>` names; cleanup is restricted to that pattern. Both repositories run the tests against MySQL 8.4 in release CI before packaging. Existing racing result and greyhound distance suites remain required.

A read-only supplier preflight is available through the same executable with `--preview-live 2` or `--preview-live 4`, using `RG_CANCELLATION_SOURCE_FILE` to name the private source file. It does not initialise schemas, settle anything or publish messages. Historical bulk cleanup is not a side effect of installing this code; older exact meetings can be confirmed separately with recorded evidence.
