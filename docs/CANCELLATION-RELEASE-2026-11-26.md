# Cancellation release — 26 November 2026

Released the same confirmed-meeting cancellation policy to racing and greyhounds. Existing nonblank results, including losing `0`, remain unchanged; only unsettled selections become `99`. Race and meeting products are closed/hidden. Missing feeds, ordinary suspensions and late results are not cancellation evidence.

## Verified releases

| Application | Source revision | Successful release CI |
| --- | --- | --- |
| Racing | `e6b9f88e2f4baec296a52b58173da58b453012a1` | [45812755664](https://github.com/jarlontrading/racing/actions/runs/45812755664) |
| Greyhounds | `30580756f5acd52577dd65a971e00327e16fd3f4` | [45815546603](https://github.com/jarlontrading/greyhounds/actions/runs/45815546603) |

CI application DLL/PDB hashes were checked against clean-source manifests and then against installed files. Both applications restarted with PROD and UAT publishing enabled. Their cancellation evidence/delivery loops completed without errors; Betfred browser pricing and the separate OFF services reconnected. Greyhounds does not use Spreadex.

## Validation

Cancellation policy, isolated SQL transaction/retry/restart, concurrent-result preservation and terminal replay checks passed (117 local checks, with the SQL suite also required in release CI). Existing racing result, greyhound distance and persistence suites passed. Isolated HTTP hosts proved authenticated preview/confirmation, paid-result preservation, idempotence and recovery across restart without publishing any trading messages. Greyhound HTTP checks also covered retained meeting products on an exact historical/future fixture day and no allocation of new meeting events during cancellation.

Four new InnoDB tables were prepared in each local database; existing schema and race results were not rewritten. Distinct administrator keys were added to private settings, with the original settings backed up through Windows DPAPI. No credentials are stored in Git.

Racing's current/future meeting outcome ledger was seeded from 210 acknowledged selection frames in the current-day audit: 28 market records and 66 previously decided selection outcomes were retained. Concurrent new ledger records were merged by compare-and-swap, preserving their published identities and decided results. Existing historical audit files were not deleted or bulk replayed.

The independent persistence worker was updated to source revision `e6b9f88`, preserving its configuration, writer identity, checkpoints and durable queue. It now mirrors permanent cancellation/outcome records individually to Ocean. Source verification passed for both namespaces; cloud acknowledgement resumed without errors/conflicts. Live trading and cancellation retry remain local.

At release verification there were no confirmed cancellations or pending cancellation delivery jobs. No historical meeting was bulk voided and no synthetic prices or cancellation messages were published to PROD/UAT. Broker confirmation is not proof of a downstream Sportuno settlement receipt.

Runtime evidence and rollback binaries are retained locally under `D:\TraderReleases\cancellations-20261126-*` and `D:\TraderReleases\cancellation-preparation-20261126`. The operational contract and authenticated confirmation endpoints are documented in [CANCELLED-MEETINGS.md](CANCELLED-MEETINGS.md).
