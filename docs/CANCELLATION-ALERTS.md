# Cancellation Telegram alerts

The confirmed-meeting cancellation implementation is live in both traders.
It voids only unsettled selections, preserves decided results, closes/hides the
affected events and uses a durable PROD/DEMO broker outbox. DEMO is the UAT target.
Automatic confirmation requires explicit structured Sporting Life abandonment or
complete source-card cancellation evidence. Missing races, stale prices and a
merely suspended card never establish cancellation. US-only meetings absent from
Sporting Life still need official evidence and operator confirmation.

`cancellation_alerts.py` is a separate, read-only observer for both local databases.
It adds no remote dependency to trading and requires no trader restart. It polls
committed cancellation records approximately every ten seconds plus query time:

- **DETECTED / LOCAL VOID COMPLETE:** confirmation and local void committed. These
  are one message because the existing cancellation transaction commits them
  together. This does not announce uncommitted detection or failed transactions.
- **BROKER ACKNOWLEDGED:** all current PROD/UAT outbox jobs acknowledged. This is
  not proof of downstream Sportuno application receipt, which is not exposed.
- **DELIVERY NEEDS ATTENTION:** no delivery jobs, or a job pending over two minutes.
  Sent once per payload revision, followed by acknowledgement when recovered.
- Late-created selections that produce revised cancellation payloads generate an
  additional closure notice. Counts are current totals, not the original action's
  number of newly voided selections.

Private config: `D:\Sportuno\CancellationAlerts\config.json`. The existing private
`SPRacing.Telegram.env` supplies `SPRACING_TELEGRAM_BOT_TOKEN`; the new destination
must be explicitly set as `SPORTUNO_ALERT_TELEGRAM_CHAT_ID`. No fallback to the
existing channel. Add `@JarlonRacingTraderAlertsBot` to the new private SPORTUNO
ALERT channel as an administrator with permission to post, verify the channel
identity and bot permission, then send a clearly labelled test.

No credentials are stored in Git. Database password goes in the MariaDB child's
environment, not its command line. Errors retain exception types only, never
Telegram URLs, tokens, or raw exceptions. The database observer uses SELECT and
session configuration only; it does not modify any trader table.

Runtime: `D:\Sportuno\CancellationAlerts\cancellation_alerts.py`.
State/status: `D:\CodexData\cancellation-alerts\alerts.sqlite` / `status.json`.
SQLite stores notices before delivery; only a successful Telegram response marks
them sent. Retries survive restart and use bounded exponential backoff. Ordering
keeps the local notice before broker acknowledgement. Telegram has no sendMessage
idempotency key: a successful send with a lost response can produce a duplicate.
One exclusive process lock prevents concurrent observers. Sent notices roll off
after 31 days; source monitoring covers the previous 30 days and configured
activation time, so initial deployment does not replay historical cancellations.
Unsent notices remain until acknowledged. Status records per-source read failures;
it is not an independent external machine-health monitor.

Install/update with `Install-CancellationAlerts.ps1`. Task:
`Sportuno - Cancellation Telegram Alerts`; starts hidden now and on user sign-in,
restarts on process failure. Neither trader is restarted. This cannot report while
the machine or its internet connection is down. Retain SQLite/config when updating.

Validation:

```
python -m unittest test_cancellation_alerts.py -v
python cancellation_alerts.py --config D:\Sportuno\CancellationAlerts\config.json --read-only
```

Seven tests cover duplicate suppression, restart/retry and send ordering, separate
broker acknowledgement, delayed delivery, revised payloads, missing channel config
and zero-job failure. Live read-only validation covered both schemas and yesterday's
Laurel Park confirmation: ten events, twenty acknowledged jobs, zero pending.
The separate Telegram destination must be connected and a live test acknowledged
before calling notification delivery operational.

## Live activation — 27 November 2026

The new private channel is named **SPORTUNO ALERT**. The existing bot was added
as administrator with Post Messages enabled and other optional administrator
permissions disabled. Bot API getChat/getChatMember verified channel identity
and posting permission. The destination is configured privately; the old alert
channel configuration was preserved. A clearly labelled setup test was queued
through the live SQLite monitor and acknowledged at **11:17:10 UTC**, with zero
retries; its arrival was also verified visually in Telegram. Both database
sources are healthy, the channel is configured and there are no pending notices.
The observer's MariaDB subprocesses run with CREATE_NO_WINDOW on Windows.
