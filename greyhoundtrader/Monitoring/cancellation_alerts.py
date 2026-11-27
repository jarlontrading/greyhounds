"""Read-only cancellation observer. Telegram failures never block either trader.

Only committed confirmations are announced; a missing card is not evidence.
SQLite retains notices across process restarts. A lost Telegram response can
produce a duplicate: Telegram sendMessage has no idempotency key.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import time
import urllib.request


def utcnow():
    return datetime.now(timezone.utc).isoformat()


def env_file(path):
    values = {}
    for line in Path(path).read_text(encoding="utf-8-sig").splitlines():
        if "=" in line and not line.lstrip().startswith("#"):
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"')
    return values


def open_state(path):
    db = sqlite3.connect(path)
    db.executescript("""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS notices (
            id INTEGER PRIMARY KEY, notice_key TEXT UNIQUE NOT NULL,
            body TEXT NOT NULL, created_utc TEXT NOT NULL,
            sent_utc TEXT, attempts INTEGER NOT NULL DEFAULT 0,
            next_attempt REAL NOT NULL DEFAULT 0, last_error TEXT);
        CREATE TABLE IF NOT EXISTS observed (
            scope TEXT PRIMARY KEY, revision TEXT NOT NULL, updated_utc TEXT NOT NULL);
    """)
    return db


def enqueue(db, key, text):
    db.execute("INSERT OR IGNORE INTO notices(notice_key,body,created_utc) VALUES(?,?,?)",
               (key, text, utcnow()))


def observe(db, app, row):
    """Persist local enactment and broker acknowledgement as distinct notices."""
    scope = f"{app}/{row['sport']}/{row['meeting_key']}/{row['date']}"
    # Revision describes the actual cancellation payload, never acknowledgement timing.
    revision = row["revision"]
    previous = db.execute("SELECT revision FROM observed WHERE scope=?", (scope,)).fetchone()
    prefix = f"{app}: {row['meeting']} — {row['date']}"
    key = f"{scope}/{revision}"
    with db:
        if previous is None or previous[0] != revision:
            heading = "CANCELLATION DETECTED / LOCAL VOID COMPLETE" if previous is None else "CANCELLATION: ADDITIONAL CLOSURE WORK"
            text = (f"{heading}\n{prefix}\nEvidence: {row['source']}\n"
                    f"{row['events']} affected events; {row['voided']} selections currently void. "
                    "Existing decided results preserved.\n"
                    "Local events closed/hidden; PROD/UAT broker delivery tracked separately.\n"
                    f"Recorded: {row['confirmed_utc']} UTC\nReference: {row['operation']}")
            enqueue(db, key + "/local", text)
            db.execute("INSERT INTO observed VALUES(?,?,?) ON CONFLICT(scope) DO UPDATE SET revision=excluded.revision,updated_utc=excluded.updated_utc",
                       (scope, revision, utcnow()))
        if row["jobs"] > 0 and row["pending"] == 0:
            enqueue(db, key + "/broker", f"CANCELLATION: BROKER ACKNOWLEDGED\n{prefix}\n"
                    f"All {row['jobs']} PROD/UAT delivery jobs acknowledged.\n"
                    "Sportuno downstream application receipt is not exposed.\n"
                    f"Reference: {row['operation']}")
        elif row["jobs"] == 0 or row["oldest_pending_seconds"] >= 120:
            # One persisted notice per revision; no five-second repeated warning.
            enqueue(db, key + "/pending", f"CANCELLATION: DELIVERY NEEDS ATTENTION\n{prefix}\n"
                    f"{row['pending']} of {row['jobs']} jobs pending broker acknowledgement.\n"
                    "Local void is committed; check cancellation delivery before assuming Sportuno is updated.\n"
                    f"Reference: {row['operation']}")


def query_rows(config, app):
    values = env_file(app["settingsFile"])
    prefix = app["prefix"]
    # This observer is intentionally restricted to the existing local hot database.
    host = values[prefix + "_MYSQL_SERVER"]
    if host not in ("localhost", "127.0.0.1"):
        raise ValueError("Observer requires local database")
    database = values.get(app["databaseKey"], app["defaultDatabase"])
    since = datetime.fromisoformat(config["startUtc"].replace("Z", "+00:00"))
    if since.tzinfo is None:
        raise ValueError("startUtc requires an explicit timezone")
    since_sql = since.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
    sql = """
    SET SESSION max_statement_time=10;
    SET SESSION group_concat_max_len=1048576;
    SELECT JSON_OBJECT(
      'sport',c.sport_id,'meeting_key',c.meeting_key,'meeting',c.meeting_name,
      'date',DATE_FORMAT(c.meeting_date,'%Y-%m-%d'),'operation',c.operation_id,
      'source',JSON_UNQUOTE(JSON_EXTRACT(c.evidence_json,'$.source')),
      'confirmed_utc',DATE_FORMAT(c.confirmed_utc,'%Y-%m-%d %H:%i:%s'),
      'events',COUNT(DISTINCT e.event_id),'jobs',COUNT(o.id),
      'pending',COALESCE(SUM(o.acknowledged_utc IS NULL AND o.id IS NOT NULL),0),
      'revision',SHA2(COALESCE(GROUP_CONCAT(CONCAT(o.id,':',o.payload_sha256)
                     ORDER BY o.id SEPARATOR '|'),'no-jobs'),256),
      'oldest_pending_seconds',COALESCE(MAX(IF(o.acknowledged_utc IS NULL,
                     TIMESTAMPDIFF(SECOND,o.created_utc,UTC_TIMESTAMP()),0)),0),
      'voided',(SELECT COUNT(*) FROM spuno_selections s
        JOIN web_cancelled_events ce ON ce.event_id=s.event_id
        WHERE ce.sport_id=c.sport_id AND ce.meeting_key=c.meeting_key
          AND ce.meeting_date=c.meeting_date AND s.result_text='99'))
    FROM web_confirmed_meeting_cancellations c
    LEFT JOIN web_cancelled_events e ON e.sport_id=c.sport_id
      AND e.meeting_key=c.meeting_key AND e.meeting_date=c.meeting_date
    LEFT JOIN web_cancellation_outbox o ON o.event_id=e.event_id
    WHERE c.confirmed_utc >= 'SINCE'
      AND c.confirmed_utc >= UTC_TIMESTAMP()-INTERVAL 30 DAY
    GROUP BY c.sport_id,c.meeting_key,c.meeting_date,c.meeting_name,
             c.operation_id,c.evidence_json,c.confirmed_utc;
    """.replace("SINCE", since_sql)
    child_env = os.environ.copy()
    child_env["MYSQL_PWD"] = values[prefix + "_MYSQL_PASSWORD"]
    result = subprocess.run([config["mariaClient"], "--connect-timeout=3", "--batch",
                             "--skip-column-names", "--raw", "--host=" + host,
                             "--user=" + values[prefix + "_MYSQL_USER"],
                             "--database=" + database, "--execute=" + sql],
                            env=child_env, capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise RuntimeError("Cancellation query failed")  # Never expose SQL credentials/stderr.
    return [json.loads(line) for line in result.stdout.splitlines() if line.strip()]


def send_telegram(secrets, text):
    token = secrets.get("SPRACING_TELEGRAM_BOT_TOKEN", "")
    # Deliberately never fall back to the old channel.
    chat = secrets.get("SPORTUNO_ALERT_TELEGRAM_CHAT_ID", "")
    if not token or not chat:
        raise ValueError("SPORTUNO ALERT channel not configured")
    request = urllib.request.Request("https://api.telegram.org/bot" + token + "/sendMessage",
                                     data=json.dumps({"chat_id": chat, "text": text}).encode(),
                                     headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        result = json.load(response)
    if not result.get("ok") or not result.get("result", {}).get("message_id"):
        raise RuntimeError("Telegram did not acknowledge message")


def deliver(db, sender, now=None):
    now = time.time() if now is None else now
    # Preserve local-before-broker ordering, including after a failed send.
    row = db.execute("SELECT id,body,attempts,next_attempt FROM notices WHERE sent_utc IS NULL ORDER BY id LIMIT 1").fetchone()
    if row is None or row[3] > now:
        return
    try:
        sender(row[1])
    except Exception as error:
        with db:
            db.execute("UPDATE notices SET attempts=attempts+1,last_error=?,next_attempt=? WHERE id=?",
                       (type(error).__name__, now + min(900, 15 * 2 ** min(row[2], 6)), row[0]))
    else:
        with db:
            db.execute("UPDATE notices SET sent_utc=?,last_error=NULL WHERE id=?", (utcnow(), row[0]))


def run(config, once=False, read_only=False):
    root = Path(config["stateDirectory"])
    if read_only:
        for app in config["apps"]:
            rows = query_rows(config, app)
            print(json.dumps({"app": app["name"], "confirmations": rows, "readOnly": True}))
        return
    root.mkdir(parents=True, exist_ok=True)
    # Windows exclusive lock persists until this process exits, including crashes.
    import msvcrt
    lock = (root / "monitor.lock").open("a+b")
    lock.seek(0)
    try:
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
    except OSError:
        return
    db = open_state(root / "alerts.sqlite")
    try:
        while True:
            status = {"checkedUtc": utcnow(), "sources": {}}
            for app in config["apps"]:
                try:
                    rows = query_rows(config, app)
                    for row in rows:
                        observe(db, app["name"], row)
                    status["sources"][app["name"]] = {"ok": True, "confirmations": len(rows)}
                except Exception as error:
                    status["sources"][app["name"]] = {"ok": False, "error": type(error).__name__}
            try:
                secrets = env_file(config["telegramFile"])
                status["channelConfigured"] = bool(secrets.get("SPORTUNO_ALERT_TELEGRAM_CHAT_ID"))
                deliver(db, lambda text: send_telegram(secrets, text))
            except Exception as error:
                status["telegramError"] = type(error).__name__
            with db:
                db.execute("DELETE FROM notices WHERE sent_utc IS NOT NULL AND sent_utc < ?",
                           (datetime.fromtimestamp(time.time() - 31 * 86400, timezone.utc).isoformat(),))
            status["pendingNotices"] = db.execute("SELECT COUNT(*) FROM notices WHERE sent_utc IS NULL").fetchone()[0]
            status["lastDeliveryError"] = db.execute("SELECT last_error FROM notices WHERE sent_utc IS NULL ORDER BY id LIMIT 1").fetchone()
            temporary = root / "status.tmp"
            temporary.write_text(json.dumps(status, indent=2), encoding="utf-8")
            temporary.replace(root / "status.json")
            if once:
                print(json.dumps(status))
                return
            time.sleep(10)
    finally:
        db.close()
        lock.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--read-only", action="store_true")
    arguments = parser.parse_args()
    try:
        run(json.loads(Path(arguments.config).read_text(encoding="utf-8-sig")), arguments.once, arguments.read_only)
    except Exception as error:
        print(json.dumps({"fatalError": type(error).__name__}))
        raise SystemExit(1)
