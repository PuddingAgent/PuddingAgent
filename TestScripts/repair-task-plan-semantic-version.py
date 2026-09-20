"""One-time offline repair for the 2026-09-20 PlanVersion/replan incident.

Stop Core before applying. Only explicitly selected, provable v2 plans are repaired.
The backup is a before-image of affected rows and evidence, not a full DB backup.
No task, goal, budget, command, node or binding state is rewritten.
"""
import argparse
import json
import os
from pathlib import Path
import sqlite3
import subprocess


def inspect_plan(db, plan_id):
    plan_row = db.execute("SELECT * FROM task_plan_runs WHERE plan_id=?", (plan_id,)).fetchone()
    if plan_row is None:
        raise ValueError(f"Plan not found: {plan_id}")
    plan = dict(plan_row)
    if plan['plan_version'] == 2:
        return {'planId': plan_id, 'alreadyCurrent': True}
    if (plan['plan_version'] < 3 or plan['schema_version'] != 1
            or plan['plan_kind'] != 'workspace-task-v1' or not plan['plan_fingerprint']
            or plan.get('plan_revision', 1) != 1):
        raise ValueError(f"Unproven semantics or already revised: {plan_id}")
    bindings = [dict(r) for r in db.execute(
        "SELECT * FROM task_goal_bindings WHERE task_plan_id=?", (plan_id,))]
    if len(bindings) != 1:
        raise ValueError(f"Expected exactly one frozen binding: {plan_id}")
    binding = bindings[0]
    frozen = json.loads(binding['execution_window_snapshot_json'])
    expected_key = f"task-goal:{plan['workspace_id']}:{plan['workspace_task_id']}:{plan['workspace_task_version']}"
    if (binding['workspace_id'] != plan['workspace_id']
            or binding['task_id'] != plan['workspace_task_id']
            or binding['idempotency_key'] != expected_key
            or binding['plan_fingerprint'] != plan['plan_fingerprint']
            or frozen.get('executionPlanVersion') != 2
            or frozen.get('executionPlanSchemaVersion') != 1
            or frozen.get('taskPlanId') != plan_id
            or frozen.get('executionPlanFingerprint') != plan['plan_fingerprint']):
        raise ValueError(f"Frozen compiler identity mismatch: {plan_id}")
    goal = dict(db.execute("SELECT * FROM goal_runs WHERE goal_run_id=?",
                           (binding['goal_run_id'],)).fetchone())
    if goal['status'] == 1:
        raise ValueError(f"Pause active goal through its control API first: {goal['goal_run_id']}")
    if db.execute("""SELECT COUNT(*) FROM chat_execution_commands c
        JOIN goal_iterations i ON i.command_id=c.command_id
        WHERE i.goal_run_id=? AND c.status NOT IN ('succeeded','failed','cancelled','lease_lost')""",
                  (goal['goal_run_id'],)).fetchone()[0]:
        raise ValueError(f"Unsettled execution command: {plan_id}")
    nodes = [dict(r) for r in db.execute("SELECT * FROM task_nodes WHERE plan_id=?", (plan_id,))]
    leaves = [n for n in nodes if n['depth'] == 1]
    budgets = ('max_rounds', 'max_tool_calls', 'max_duration_seconds',
               'max_input_tokens', 'max_output_tokens', 'max_cost')
    if not leaves or any(not n['work_unit_kind'] or any(float(n[k] or 0) <= 0 for k in budgets)
                         for n in leaves):
        raise ValueError(f"Missing typed v2 work-unit budget: {plan_id}")
    evidence = []
    for row in db.execute("""SELECT event_id,payload FROM conversation_events
            WHERE workspace_id=? AND conversation_id=? AND type='goal.circuit_opened'
            ORDER BY sequence""", (plan['workspace_id'], goal['current_conversation_id'])):
        payload = json.loads(row['payload'])
        if payload.get('planId') == plan_id and payload.get('kind') == 'replan':
            if payload.get('goalRunId') != goal['goal_run_id'] or 'planRevision' in payload:
                raise ValueError(f"Mixed replan history: {plan_id}")
            evidence.append({'eventId': row['event_id'], 'version': payload['planVersion']})
    if [e['version'] for e in evidence] != list(range(3, plan['plan_version'] + 1)):
        raise ValueError(f"Replan history does not prove every semantic increment: {plan_id}")
    if db.execute("""SELECT COUNT(*) FROM task_plan_runs WHERE workspace_id=?
            AND workspace_task_id=? AND workspace_task_version=? AND plan_version=2 AND plan_id<>?""",
                  (plan['workspace_id'], plan['workspace_task_id'], plan['workspace_task_version'], plan_id)).fetchone()[0]:
        raise ValueError(f"Compiled plan identity collision: {plan_id}")
    return {'planId': plan_id, 'before': plan, 'binding': binding, 'goal': goal,
            'nodes': nodes, 'evidence': evidence, 'newVersion': 2, 'newRevision': 1 + len(evidence)}


def apply_repair(db, repair):
    if repair.get('alreadyCurrent'):
        return
    before = repair['before']
    count = db.execute("""UPDATE task_plan_runs SET plan_version=2, plan_revision=?
            WHERE plan_id=? AND plan_version=? AND plan_revision=1 AND plan_fingerprint=?""",
                       (repair['newRevision'], repair['planId'], before['plan_version'],
                        before['plan_fingerprint'])).rowcount
    if count != 1:
        raise ValueError('Plan changed during repair')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True)
    parser.add_argument('--plan-id', action='append', required=True)
    parser.add_argument('--backup', help='New JSON file for before-images; required on apply')
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    if not args.dry_run:
        if os.name != 'nt':
            raise ValueError('Offline Core check requires Windows')
        stopped = subprocess.run(['pwsh', '-NoProfile', '-Command',
            "if (Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'PuddingAgent.exe' -or "
            "($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'PuddingAgent\\.dll') }) { exit 1 }"],
            capture_output=True, check=False)
        if stopped.returncode != 0:
            raise ValueError('Core must be stopped before repair')
        if not args.backup:
            raise ValueError('--backup is required')
    uri = Path(args.database).resolve().as_uri() + ('?mode=ro' if args.dry_run else '?mode=rw')
    with sqlite3.connect(uri, uri=True, timeout=5) as db:
        db.row_factory = sqlite3.Row
        db.execute('BEGIN' if args.dry_run else 'BEGIN IMMEDIATE')
        repairs = [inspect_plan(db, plan_id) for plan_id in dict.fromkeys(args.plan_id)]
        if not args.dry_run:
            # Refuse to overwrite an earlier recovery record. Flush the evidence
            # before touching data; the separate receipt reports committed state.
            with open(args.backup, 'x', encoding='utf-8') as backup:
                json.dump(repairs, backup, ensure_ascii=False, indent=2)
                backup.flush()
                os.fsync(backup.fileno())
            columns = {r['name'] for r in db.execute('PRAGMA table_info(task_plan_runs)')}
            if 'plan_revision' not in columns:
                db.execute('ALTER TABLE task_plan_runs ADD COLUMN plan_revision INTEGER NOT NULL DEFAULT 1')
            for repair in repairs:
                apply_repair(db, repair)
        db.commit()
    print(json.dumps({'dryRun': args.dry_run, 'plans': [
        {k: r[k] for k in ('planId', 'alreadyCurrent', 'newVersion', 'newRevision') if k in r}
        for r in repairs]}))


if __name__ == '__main__':
    main()
