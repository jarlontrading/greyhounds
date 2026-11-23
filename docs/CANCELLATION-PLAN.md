# Cancellation detection — queued after Ocean migration

Priority: next feature after the racing/greyhound database cutover and recovery validation. Planning only: no new cancellation detector has been deployed and Fakenham has not been voided by this investigation.

## Evidence from Fakenham, 22 November 2026

Six races (12:43–15:13), event IDs 204944486, 204944488, 204944499, 204944504, 204944509 and 204944515, have 391 stored selections and no void results at investigation time. Sporting Life's dated results identify the meeting as Abandoned: Frozen track and mark individual races Abd. The racecourse's dated announcement independently confirms abandonment.

Audit records show Spreadex suspending every runner across the six races at 09:22:02–09:22:10 UTC. Discovery continued observing the races as pending individually until shortly before their scheduled times. Whole-card disappearance alone would not have caught the original announcement. A later BetsAPI result query returned one ended and five to-be-fixed states, not explicit cancelled/abandoned statuses.

## Implementation order

1. Add a source-evidence model scoped by sport, exact track, meeting date and race identity. Record observed time, source event ID, status, reason and source URL/raw evidence. Keep observed cancellation separate from OFF, suspension, removal and completed results.
2. For horses, parse meeting abandonment reasons and per-race Abd markers from dated Sporting Life card/results content. Inspect the structured page payload where available; do not search unrelated page-wide text. Cover fully and partially abandoned meetings and stale/wrong-date pages.
3. Add explicit cancellation status handling to pricing adapters only after validating provider contracts and real messages. BetsAPI documents cancelled=5 and abandoned=8; removed=99, postponed, ended and missing results must not be treated as an unconditional void. Check individual existing events after they leave discovery. Validate the correct supplier/internal IDs.
4. Keep a persisted expected schedule and compare only complete, successful, date-scoped provider snapshots. Repeated disappearance of future races or all-runner suspension marks a suspected cancellation, suspends affected markets and triggers confirmation/alerts. Preserve last-known cards during feed failure. Never infer absence from BetAhoy's rolling next-races window or a capped/incompletely paginated list. Record snapshot coverage and connection health separately.
5. Verify Sporting Life greyhound cancellation labels against actual abandoned/no-race examples before enabling automatic voiding for dogs. Reuse the evidence/workflow framework; use explicit provider or official confirmation where Sporting Life is insufficient. No Spreadex greyhound OFF dependency.
6. Add one idempotent cancellation coordinator: persist terminal cancellation, block stale reopening, void only unrun/affected races under applicable settlement rules, hide markets/selections/event and reliably publish to configured PROD/UAT targets. Keep completed results. Handle meeting specials, trap challenges and distance markets separately under their rules; do not blanket-void already determined outcomes.
7. Put terminal publishing/retry on a durable outbox with delivery evidence and an operator view. Failures must alert and remain retryable across restarts. Distinguish broker ACK from confirmed downstream application. Reuse and harden the existing exact-date/meeting manual void route; it is not itself an automatic detector.
8. Add an operator action showing matched races, evidence and intended effects, plus an unresolved-cancellation queue. Run a recent-days sweep for suspended/unresulted races and a pre-racing/current-day monitor. Missing-card suspicion never becomes a void merely because a timeout expires.

## Acceptance checks and rollout

Replay Fakenham's six-race card, a partially abandoned card, completed races plus cancelled remainder, an individual greyhound no-race, a temporary suspension, a disconnected browser, partial pagination, rolling-window removal, event-ID changes and a wrong-date announcement. Verify preserved IDs, no duplicate settlement, no accidental reopening and correct parent/child hiding. Test outage/retry/restart and both publishing targets.

Deploy in observation-only mode first. Verify real greyhound evidence before arming its automatic path. Then enable explicit-confirmation processing, check Sportuno outcomes and retain the prior release for rollback. Historical cleanup is a separately enumerated operation, not a side effect of deploying the detector.

## Database-work handover pointer

On 23 November the user designated `D:/Dropbox/ocean` for the consolidated Ocean handover/configuration. Treat it as the next source to inspect for `cricket-single-trader-test` and deployment access. Do not modify the cricket service or reuse its database credentials for racing without establishing the appropriate separate resources. Never copy secrets into this plan or Git.
