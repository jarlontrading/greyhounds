# Ocean persistence update, 23 November 2026

The later instruction is to retain local MariaDB for all real-time trading and use Ocean only for asynchronous persistent storage. It supersedes the full remote SQL cutover proposal below. Neither trader depends on Ocean for pricing, OFF, results or publishing.

The shared storage API and Windows background worker live in [jarlontrading/racing](https://github.com/jarlontrading/racing/tree/migration/ocean-storage). See its `docs/OCEAN-MIGRATION.md` and `PersistenceSync/README.md` for the current deployment, data scope and recovery contract. One worker copies both databases using separate SELECT-only source users, namespace-specific keys, durable disk queues and revision-checked uploads. There is no additional greyhound trading publisher.

The new app is https://racing-greyhounds-storage-gr2xj.ondigitalocean.app, with separate `racing_uat`, `greyhounds_uat`, `racing_prod`, and `greyhounds_prod` schemas on the existing cluster. Confirmed extra app cost is US$5/month, excluding taxes and bandwidth overage. No cluster resize was performed.

Private worker configuration is `D:/CodexData/ocean-migration/sync/config.json`; loopback status is `http://127.0.0.1:5489/status`. Cards/results/configuration and current prices are copied; raw tick history and logs are not. Existing local 30-day history retention remains. No local data is deleted, and local MariaDB is still required. Cloud copying is not a completed SQL restore facility or permission to remove the local database.

## Historical preflight (20 November 2026)

Status: PREPARATION ONLY. Neither trader is switched. No cloud database was created, no production settings changed, and no additional Sportuno publisher started.

## Verified inputs

Read `D:/Dropbox/SingleSdxTrader/docs/OCEAN-HANDOVER.md` and `D:/Dropbox/Codex/Coupons/REMOTE_DATABASE_HANDOVER_2026-09-07.md`. The preferred architecture is Windows trader -> authenticated HTTPS storage service -> managed DigitalOcean MySQL. Database credentials stay server-side. The existing Coupons writer accepts current-state projections but provides no reads. The admin shared-state endpoint supports revision-controlled documents, not the traders' SQL/query model. Its event-create endpoint also publishes UAT messages and must NOT be used to import the existing race identities.

Both live traders report production publishing enabled. Local databases are MariaDB 12.3.2. Schema-only exports and manifests are outside Git:

- `D:/RacingTraderBackups/ocean-migration/20261120-racing-schema-v2`
- `D:/RacingTraderBackups/ocean-migration/20261120-greyhounds-schema-v2`

The first export attempts failed on an unsupported dump-client option; only the `-v2` directories have completed manifests. The option is corrected in the committed export script. No data was modified.

Racing has 33 tables; greyhounds has 57. The greyhound schema also contains legacy tables named for other sports/providers, several MyISAM tables, and MariaDB-specific `uca1400` collations. Do not clone those indiscriminately into the managed MySQL target. Assess actual consumers and rows, map collations explicitly, use InnoDB for the migrated operational tables and test all unique-key semantics. Racing also has two `spuno_events` triggers whose behavior must be reviewed and preserved where required.

The six direct SQL components in EACH web app are RacingRepository, RacingFeedRepository, RacingResultRepository, RacingActivityLog, BetfairRepository and CanonicalEventService. Their partial-class files must be included in the inventory. Current connections use TLS Preferred and do not configure Ocean's port/CA; replacing the hostname is not a supported cutover.

## Required implementation

1. Provision isolated racing/greyhound UAT and PROD stores (names to confirm against existing cloud resources), dedicated least-privilege runtime users and an authenticated HTTPS service. Preserve existing App Platform trusted sources. No public MySQL opening and no reuse of cricket/Coupons runtime identities.
2. Implement typed reads and writes for current cards, runners, markets, selections, settings, subscriptions, provider links, terminal state, results and retained history. Do not expose arbitrary SQL. Preserve all existing IDs; treat large identity values losslessly.
3. Add asynchronous cloud persistence with durable operation IDs, explicit per-entity revisions, idempotent retry and a terminal-state guard so an old open price cannot reopen an OFF/resulted race. Settings conflicts must be surfaced, not overwritten. Never drop pending terminal/results operations.
4. Refactor the six storage components to use the API while keeping the local schedule/cache and offline outbox. OFF -> RabbitMQ publication must not wait for a database/HTTPS persistence call. Cloud bootstrap needs a last-known local schedule and explicit stale-state handling.
5. Trial import in isolated UAT. Compare cards, runner identities, market/selection counts, non-runners, subscriptions, results and final/terminal states. Test disconnect, timeout-after-commit, replay after restart, stale updates and duplicate delivery.
6. Use one publishing owner throughout. Shadow persistence must not publish a second time. Cut over one trader at a time in a quiet interval after final reconciliation. A frozen local database is recovery material, not a second active writer. Rollback must reconcile operations committed in Ocean since cutover before selecting local storage again.
7. Keep the agreed 30-day rolling price/activity history; preserve cards and results. Do not create ongoing history archives. Central backups are a separate recovery policy. Retain browser profiles, bounded diagnostic logs, a cache and the durable local recovery queue. Retire local MariaDB only after proving all consumers have moved.

## Export tool

`scripts/Export-OceanMigration.ps1 -Trader racing|greyhounds -OutputDirectory <new external path>` exports schema and inventory by default, with SHA256 manifest. Credentials are read from the existing private settings file and passed through the dump child process environment, never command-line arguments or repository files. `-IncludeData` creates a one-time recovery/export snapshot; this is NOT a retention archive and is not a final cutover snapshot while writes continue. Nontransactional tables require separate handling or a write freeze. Never import an unreviewed MariaDB dump directly into the live cloud schema.

## Access verified, 20 November 2026

Located the configured client key in `D:/Dropbox/SingleSdxTrader/docs/OCEAN-API.txt` and the private portable credential handover. Credential values are not copied into this repository or migration report. Authenticated Ocean health succeeds: database `sportuno`, event publisher target `UAT / DEMOevents`. This publisher target does not cause shared-state writes to send trading messages.

Verified GET -> PUT -> GET round trips and rejection of stale revisions (409), using separate app-owned keys `racing-prod-migration-probe-v1` and `greyhounds-prod-migration-probe-v1`. Only these small probe documents were written; no cards, results, prices or trading events were migrated or published.

Saved separate CurrentUser-DPAPI connection documents under `%LOCALAPPDATA%/Sportuno/RacingTrader/ocean-connection.json` and `%LOCALAPPDATA%/Sportuno/GreyhoundTrader/ocean-connection.json`, each marked `PreflightOnly`. They are not portable to another user/machine. The existing applications do not yet load these files, so this does not switch storage.

Run `pwsh ./scripts/Test-OceanConnection.ps1 -Trader racing|greyhounds -TestWrite` to repeat authenticated health, document round-trip and stale-revision checks. The test compares JSON values rather than textual formatting because MySQL can normalize stored JSON.

The discovered credential authorizes the existing client API, not DigitalOcean infrastructure/deployment. No DigitalOcean deployment token or database administrator connection was found in these files. Full cutover still requires the cloud storage API and trader adapters described above, plus target capacity/grants/import validation. The client key alone does not authorize deploying/extending an App Platform service or provisioning isolated database schemas. Do not represent these probes as a completed migration or retire local MariaDB.
