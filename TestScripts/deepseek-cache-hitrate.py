# -*- coding: utf-8 -*-
"""DeepSeek 缓存命中率日报（设计方案《上下文Token效率缓存命中与分级压缩优化设计方案》§15.3 验收口径）。

用法:
    python TestScripts/deepseek-cache-hitrate.py                    # 最近 7 天
    python TestScripts/deepseek-cache-hitrate.py --days 3           # 最近 3 天
    python TestScripts/deepseek-cache-hitrate.py --db D:/data/databases/pudding_platform.db

口径说明:
    - 权威数据源 = llm_gateway_usage_events（与服务商账单对账，覆盖率 ~99.8%）。
      TokenUsageEvents 仅用于归因（PrefixChangeReason），历史上有投影双计，勿用于总量。
    - 自然日 = 北京时间（与服务商账单一致），occurred_at_utc + 8h。
    - 验收: 连续 7 个完整自然日 Token 加权总命中率 > 99%；
      单日输入 >= 10M 的模型分组各自 > 99%；session_rehydrated / prefix 变化单独展示。
"""
import argparse
import sqlite3
import sys
from pathlib import Path

DEFAULT_DB = r"D:/data/databases/pudding_platform.db"


def connect(db_path: str) -> sqlite3.Connection:
    p = Path(db_path)
    if not p.exists():
        sys.exit(f"database not found: {db_path}")
    conn = sqlite3.connect(f"file:{p.as_posix()}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    return conn


def daily_report(conn: sqlite3.Connection, days: int) -> None:
    print("=" * 96)
    print("1) 逐日 Token 加权命中率（deepseek，北京时间自然日，权威口径 llm_gateway_usage_events）")
    print("=" * 96)
    rows = conn.execute(
        """
        SELECT substr(datetime(occurred_at_utc, '+8 hours'), 1, 10) AS day,
               COUNT(*) AS reqs,
               SUM(prompt_tokens) AS pt,
               SUM(cache_hit_tokens) AS hit,
               SUM(cache_miss_tokens) AS miss,
               SUM(total_cost) AS cost
        FROM llm_gateway_usage_events
        WHERE provider_id = 'deepseek'
          AND occurred_at_utc >= datetime('now', 'localtime', ?)
        GROUP BY day ORDER BY day
        """,
        (f"-{days - 1} days",),
    ).fetchall()
    if not rows:
        print("  (无数据)")
        return
    for r in rows:
        rate = r["hit"] / r["pt"] * 100 if r["pt"] else 0.0
        verdict = "PASS" if rate > 99.0 else "FAIL"
        print(f"  {r['day']}  req={r['reqs']:>7,}  in={r['pt']:>13,}  miss={r['miss']:>12,}"
              f"  rate={rate:7.3f}%  cost~{r['cost'] or 0:8.2f}  [{verdict}]")

    print()
    print("=" * 96)
    print("2) 分模型（单日输入 >= 10M 的分组须各自 > 99%，小样本单独展示）")
    print("=" * 96)
    rows = conn.execute(
        """
        SELECT model_id,
               substr(datetime(occurred_at_utc, '+8 hours'), 1, 10) AS day,
               COUNT(*) AS reqs, SUM(prompt_tokens) AS pt,
               SUM(cache_hit_tokens) AS hit, SUM(cache_miss_tokens) AS miss
        FROM llm_gateway_usage_events
        WHERE provider_id = 'deepseek'
          AND occurred_at_utc >= datetime('now', 'localtime', ?)
        GROUP BY model_id, day ORDER BY day, model_id
        """,
        (f"-{days - 1} days",),
    ).fetchall()
    for r in rows:
        rate = r["hit"] / r["pt"] * 100 if r["pt"] else 0.0
        sample = "full" if r["pt"] >= 10_000_000 else "small"
        verdict = "PASS" if (rate > 99.0 or sample == "small") else "FAIL"
        print(f"  {r['day']}  {r['model_id']:<30s} req={r['reqs']:>6,}  in={r['pt']:>13,}"
              f"  miss={r['miss']:>11,}  rate={rate:7.3f}%  [{sample}/{verdict}]")


def attribution_report(conn: sqlite3.Connection, days: int) -> None:
    print()
    print("=" * 96)
    print("3) miss 归因（TokenUsageEvents；usage 桶按 miss/输入比例分类）")
    print("=" * 96)
    rows = conn.execute(
        """
        SELECT COALESCE(PrefixChangeReason, CASE
                   WHEN CacheMissTokens >= 0.8 * PromptTokens THEN '(unattributed: full-rebuild)'
                   WHEN CacheMissTokens >= 0.4 * PromptTokens THEN '(unattributed: half-rebuild)'
                   WHEN CacheMissTokens >= 0.1 * PromptTokens THEN '(unattributed: partial)'
                   ELSE '(unattributed: incremental)' END) AS reason,
               COUNT(*) AS n, SUM(PromptTokens) AS pt, SUM(CacheMissTokens) AS miss
        FROM TokenUsageEvents
        WHERE ProviderId = 'deepseek'
          AND OccurredAtUtc >= datetime('now', 'localtime', ?)
        GROUP BY reason ORDER BY SUM(CacheMissTokens) DESC
        """,
        (f"-{days - 1} days",),
    ).fetchall()
    total_miss = sum(r["miss"] for r in rows) or 1
    for r in rows:
        print(f"  {r['reason']:<38s} n={r['n']:>7,}  in={r['pt']:>13,}  miss={r['miss']:>12,}"
              f"  ({r['miss'] / total_miss * 100:5.1f}% of miss)")


def top_misses(conn: sqlite3.Connection, days: int, limit: int = 10) -> None:
    print()
    print("=" * 96)
    print(f"4) Top-{limit} 单请求 miss（定位重水合/前缀漂移的具体会话）")
    print("=" * 96)
    rows = conn.execute(
        """
        SELECT SessionId, ModelId, datetime(OccurredAtUtc, '+8 hours') AS t,
               PromptTokens AS pt, CacheMissTokens AS miss, PrefixChangeReason
        FROM TokenUsageEvents
        WHERE ProviderId = 'deepseek'
          AND OccurredAtUtc >= datetime('now', 'localtime', ?)
        ORDER BY CacheMissTokens DESC LIMIT ?
        """,
        (f"-{days - 1} days", limit),
    ).fetchall()
    for r in rows:
        print(f"  miss={r['miss']:>10,}  in={r['pt']:>10,}  {r['t']}  {str(r['SessionId'])[:24]:<24s}"
              f"  {r['ModelId']:<28s}  {r['PrefixChangeReason'] or '-'}")


def main() -> None:
    ap = argparse.ArgumentParser(description="DeepSeek cache hit-rate daily report")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--days", type=int, default=7)
    ap.add_argument("--top", type=int, default=10)
    args = ap.parse_args()
    conn = connect(args.db)
    try:
        daily_report(conn, args.days)
        attribution_report(conn, args.days)
        top_misses(conn, args.days, args.top)
    finally:
        conn.close()
    print()
    print("验收提示: 连续 7 个完整自然日 >99% 才算达成（设计方案 §15.3）；当天为部分日，仅供参考。")


if __name__ == "__main__":
    main()
