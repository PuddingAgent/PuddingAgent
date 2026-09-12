import importlib.util
import unittest
import sqlite3
import io
from contextlib import redirect_stdout
from datetime import datetime, timezone
from pathlib import Path

spec = importlib.util.spec_from_file_location("hitrate", Path(__file__).with_name("deepseek-cache-hitrate.py"))
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)


class CompleteDayWindowTests(unittest.TestCase):
    def test_excludes_current_partial_beijing_day(self):
        self.assertEqual(("2026-08-28 16:00:00", "2026-09-04 16:00:00"),
                         report.complete_day_bounds(7, datetime(2026, 9, 5, 3, tzinfo=timezone.utc)))

    def test_before_beijing_midnight(self):
        self.assertEqual(("2026-09-02 16:00:00", "2026-09-03 16:00:00"),
                         report.complete_day_bounds(1, datetime(2026, 9, 4, 15, 59, tzinfo=timezone.utc)))

    def test_rejects_nonpositive_window(self):
        with self.assertRaises(ValueError):
            report.complete_day_bounds(0)

    def test_report_uses_half_open_utc_bounds_and_weighted_denominator(self):
        with sqlite3.connect(":memory:") as conn:
            conn.row_factory = sqlite3.Row
            conn.execute("CREATE TABLE llm_gateway_usage_events(provider_id,model_id,occurred_at_utc,prompt_tokens,cache_hit_tokens,cache_miss_tokens,total_cost)")
            conn.executemany("INSERT INTO llm_gateway_usage_events VALUES ('deepseek','flash',?,?,?,?,0)", [
                ("2026-08-28 15:59:59+00:00", 999, 0, 999),
                ("2026-08-28 16:00:00+00:00", 100, 90, 10),
                ("2026-09-04 15:59:59+00:00", 100, 100, 0),
                ("2026-09-04 16:00:00+00:00", 999, 0, 999),
            ])
            output = io.StringIO()
            with redirect_stdout(output):
                report.daily_report(conn, 7, datetime(2026, 9, 5, 3, tzinfo=timezone.utc))
            self.assertIn("weighted=95.000%", output.getvalue())
            self.assertIn("observed_days=2/7", output.getvalue())


if __name__ == "__main__":
    unittest.main()
