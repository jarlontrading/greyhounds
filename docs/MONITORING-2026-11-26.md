# Three-hour trader watch — 26 November 2026

Completed the requested active-chat monitoring from 16:26 to 19:26 Europe/London, with reports at 16:56, 17:26, 17:56, 18:26, 18:56 and 19:26. Additional read-only checks ran between reports and issues were reported immediately. No recurring background task remains.

Final checks confirmed both apps publishing in PROD/UAT with DiscountedHarville place pricing active. Sampled winner/Top 2/Top 3 grids had finite, active prices. Betfred pricing and OFF services were connected without reported errors; the horse Spreadex feed was fresh. No closed/OFF race in the audit had active winner selections. The final all-day scan found no unsettled non-runner-excluded winner selection more than 15 minutes past scheduled start. Recent races within that window remained safely suspended awaiting normal results where appropriate.

## Findings and actions

- Restored horse Betfred pricing by mapping Chelmsford to Chelmsford City, retaining date/time and runner-fingerprint checks. Released source `90a0854`.
- Repaired both Betfred OFF clients to join provider event IDs to dated metadata instead of requiring a missing DOM course heading. Racing `b79a174` and greyhounds `5ab9a06` passed CI and were deployed. Actual Betfred SERVICE suspensions were verified for Chelmsford 17:00 (17:02:37 observation, 17:02:38 suspension), Chelmsford 17:30, Valley 17:39 (~0.42 seconds) and Monmore 19:06 (~0.37 seconds). Existing BetAhoy/BetsAPI/horse Spreadex alternatives remained available.
- Investigated Laurel Park 16:52 when it lost provider coverage. An administrative suspension first prevented stale open betting. The live [official Equibase cancellation list](https://tvg.equibase.com/static/latechanges/html/cancellations.html) then confirmed Laurel races 2–9 cancelled for weather. Operator-confirmed cancellation `0b6b6e12-299a-485a-82a7-a330d6362c2e` voided 461 unsettled selections across the nine race events and meeting product, preserving 161 previously settled results unchanged. All nine race events were verified closed/hidden. All 20 PROD/DEMO delivery jobs were broker-acknowledged, with no pending work or delivery errors. A downstream Sportuno receipt is not exposed by this interface.
- Valley 17:54 used the existing 90-second scheduled-off safeguard when provider OFF evidence was missing; it stayed suspended and its official final result was published at 18:04:29.

## Follow-up

The automatic cancellation evidence monitor currently uses Sporting Life/complete provider cards. The US-only Equibase cancellation notice required operator confirmation during this watch. An explicit Equibase cancellation adapter is a coverage improvement still to implement; disappearing cards alone must not trigger refunds.

Local snapshots, six checkpoint records, incident requests/receipts and result-preservation evidence are retained under `D:\Temp\trader-health-20261126-1626`. Runtime release hashes and rollback files are under the corresponding `D:\TraderReleases\places-20261126-*` folders. The asynchronous Ocean mirror was also checked: no pending snapshots, conflicts or errors at 18:15.
