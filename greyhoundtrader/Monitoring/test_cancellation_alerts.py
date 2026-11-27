import tempfile
import unittest
from pathlib import Path

import cancellation_alerts as alerts


class CancellationAlertsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.path = Path(self.directory.name) / "state.sqlite"
        self.db = alerts.open_state(self.path)
        self.row = dict(sport=2, meeting_key="FAKENHAM", meeting="Fakenham",
                        date="2026-11-27", operation="test-operation", source="SPORTINGLIFE",
                        confirmed_utc="2026-11-27 10:00:00", events=7, jobs=14,
                        pending=14, revision="payload-v1", voided=140,
                        oldest_pending_seconds=0)

    def tearDown(self):
        self.db.close()
        self.directory.cleanup()

    def pending(self):
        return self.db.execute("SELECT body FROM notices WHERE sent_utc IS NULL ORDER BY id").fetchall()

    def test_only_committed_confirmation_is_observed_and_polls_do_not_repeat(self):
        for _ in range(10):
            alerts.observe(self.db, "Racing", self.row)
        self.assertEqual(len(self.pending()), 1)
        self.assertIn("LOCAL VOID COMPLETE", self.pending()[0][0])
        self.assertNotIn("BROKER ACKNOWLEDGED", self.pending()[0][0])

    def test_delivery_ack_is_separate_and_deduplicated(self):
        alerts.observe(self.db, "Racing", self.row)
        self.row["pending"] = 0
        alerts.observe(self.db, "Racing", self.row)
        alerts.observe(self.db, "Racing", self.row)
        self.assertEqual(len(self.pending()), 2)
        self.assertIn("downstream application receipt is not exposed", self.pending()[1][0])

    def test_failed_send_retries_after_restart_and_keeps_notice_order(self):
        self.row["pending"] = 0
        alerts.observe(self.db, "Racing", self.row)

        def fail(text):
            raise TimeoutError("Do not store exception message or token")

        alerts.deliver(self.db, fail, now=100)
        self.db.close()
        self.db = alerts.open_state(self.path)
        delivered = []
        alerts.deliver(self.db, delivered.append, now=101)
        self.assertEqual(delivered, [])
        self.assertEqual(self.db.execute("SELECT last_error FROM notices ORDER BY id LIMIT 1").fetchone()[0], "TimeoutError")
        alerts.deliver(self.db, delivered.append, now=115)
        alerts.deliver(self.db, delivered.append, now=116)
        self.assertEqual(len(delivered), 2)
        self.assertIn("LOCAL VOID COMPLETE", delivered[0])
        self.assertIn("BROKER ACKNOWLEDGED", delivered[1])
        self.assertEqual(self.pending(), [])

    def test_pending_delivery_warns_once_then_reports_recovery(self):
        self.row["oldest_pending_seconds"] = 121
        for _ in range(10):
            alerts.observe(self.db, "Greyhounds", self.row | {"sport": 4})
        self.assertEqual(len(self.pending()), 2)
        self.assertIn("NEEDS ATTENTION", self.pending()[1][0])
        alerts.observe(self.db, "Greyhounds", self.row | {"sport": 4, "pending": 0})
        self.assertEqual(len(self.pending()), 3)

    def test_late_selections_create_new_revision_notice(self):
        alerts.observe(self.db, "Racing", self.row)
        alerts.observe(self.db, "Racing", self.row | {"revision": "payload-v2"})
        self.assertEqual(len(self.pending()), 2)
        self.assertIn("ADDITIONAL CLOSURE WORK", self.pending()[1][0])

    def test_old_channel_is_never_used_as_fallback(self):
        with self.assertRaises(ValueError):
            alerts.send_telegram({"SPRACING_TELEGRAM_BOT_TOKEN": "test",
                                  "SPRACING_TELEGRAM_CHAT_ID": "old-channel"}, "test")

    def test_zero_jobs_is_not_delivery_success(self):
        alerts.observe(self.db, "Racing", self.row | {"jobs": 0, "pending": 0})
        self.assertEqual(len(self.pending()), 2)
        self.assertIn("NEEDS ATTENTION", self.pending()[1][0])


if __name__ == "__main__":
    unittest.main()
