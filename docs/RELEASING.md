# Release process

## Change and verify

1. Branch from `main`, make the change and open a pull request. Require the **Build and release / verify** check and review before merging. Configure branch protection in GitHub repository Settings; this is not enabled merely by committing this document.
2. Racing CI runs arithmetic/consistency and result tests and packages Racing Trader plus the shared Betfred OFF service. Greyhounds CI runs 3,806 distance checks and packages Greyhound Trader. Each repository has independent version tags and releases. These checks do not connect to the live feeds or publish prices.
3. After merge, tag the exact reviewed commit, for example `git tag -a v0.1.0 -m "First validated release"` followed by `git push origin v0.1.0`. Never move an existing release tag.
4. The tag workflow creates a **draft** GitHub release containing a single ZIP. Its manifest records version, commit and SHA256 for every packaged file. Download it and record its ZIP SHA256. UAT and production must use this same ZIP, without rebuilding.

A local build is `pwsh ./scripts/Build-Release.ps1 -Version v0.1.0`. Local dirty builds are marked in the manifest and the staging script rejects them. The script does not restart or deploy anything.

## Stage and validate in UAT

Run `pwsh ./scripts/Stage-Release.ps1 -Package <zip> -Sha256 <expected SHA256>`. This verifies and extracts to `D:\TraderReleases\<version>` without changing running services.

Supply external configuration through `SPRACING_SETTINGS_FILE` and `GREYHOUNDTRADER_SETTINGS_FILE`. These point to credential files, not files in Git. For UAT use independent databases, UAT RabbitMQ credentials, different HTTP/debug ports and separate browser profiles. Set `RacingTrader__PublishTargets=DEMO` and explicitly enable `RacingTrader__DisableHostedWorkers=false` only after checking those settings. Use an external ASP.NET configuration file or environment variables for operational options; Git defaults intentionally disable hosted workers.

Validate: event shells, markets and runner selections; prices and provider grid; silks; non-runners; Betfred and BetAhoy OFF suspensions; terminal events remaining suspended; results; publishing delivery. Check both horse and greyhound traders. A broker ACK alone does not prove Sportuno applied a message. Record observed event IDs, times and Sportuno state in the release notes.

The Betfred OFF service needs its existing sport-specific ports and Chrome profile configuration. Never share a browser profile or debugging port between UAT and production. Existing external Betfred pricing bridges must remain available; this package does not install them.

## Production deployment

Choose a quiet gap between races and confirm OFF cover before stopping anything. Record current process command lines, startup-task/watchdog configuration and the previous working release. Take a database backup before any schema change; application rollback does not reverse a migration. There are no automatic database migrations in this release process.

The current machine starts horses from `D:\Dropbox\SportunoPF\RacingTrader\RacingTrader.Web` and greyhounds from the source tree `GreyhoundTrader.Web\bin\Debug\net8.0`. Startup scripts and watchdogs still reference these legacy locations. They are included for reference and are not rewritten by the package. Do not assume staging switches either app.

For the first deployment, update the relevant launchers and watchdogs together to use the staged, versioned DLL paths and matching working directories, preserving all external configuration. Disable duplicate startup triggers during the switch. Keep one process per service/port. Set production publishing targets explicitly to `PROD,DEMO` only on production; verify the credential files before enabling hosted workers. Restart one trader at a time, retaining OFF cover. Change the two Betfred OFF instances separately only if their service changed. Launch background processes with hidden windows and separate log files outside the release directory.

Confirm `/api/status` and `/api/feed-diagnostics` on horse port 5187 and greyhound port 5287. Check provider freshness, BetAhoy grid prices, Betfred OFF `/api/status` on 5088/5388, and the greyhound `/api/suspension-sentinel/status`. Verify an actual suspension and result in Sportuno when the next races run. Complete the release notes with commit, deploy time, operator, previous version and evidence, then publish the draft release.

## Rollback and retention

Stop the affected new process and point its launcher/watchdog back to the recorded previous version, preserving external settings and its browser profile. Restart and repeat health and Sportuno checks. Do not undo settled races or reopen terminal events. If a database migration was made, follow its separate recovery plan.

Keep the active release and two verified previous releases locally. Delete older release folders only after confirming no process, launcher or scheduled task references them. Keep tagged release ZIPs on GitHub. Do not archive logs, database backups or browser caches inside release folders.

## Initial import boundaries

The racing repository includes the horse web app, tests, launcher, monitoring and shared OFF service. The greyhounds repository includes its web app, tests, monitor and startup scripts. It excludes the legacy VB desktop trader, Excel workbooks, captured provider responses, database snapshots, credentials and external pricing/browser bridge implementations. Source was imported from the Dropbox working directories without modifying live processes. Betfred OFF refresh changes present in working source may not be in the running service; validate them in UAT before release.
