"""Deterministic SQLite checks for the offline recovery; no live runtime access."""
import importlib.util
import json
from pathlib import Path
import sqlite3
import unittest

spec = importlib.util.spec_from_file_location('repair', Path(__file__).with_name('repair-task-plan-semantic-version.py'))
repair = importlib.util.module_from_spec(spec)
spec.loader.exec_module(repair)


class RepairTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(':memory:')
        self.db.row_factory = sqlite3.Row
        self.db.executescript('''
            CREATE TABLE task_plan_runs(plan_id,plan_version,plan_revision,schema_version,plan_kind,
                plan_fingerprint,workspace_id,workspace_task_id,workspace_task_version,status);
            INSERT INTO task_plan_runs VALUES('plan',4,1,1,'workspace-task-v1','fp','ws','task',18,'Active');
            CREATE TABLE task_goal_bindings(task_plan_id,workspace_id,task_id,idempotency_key,
                plan_fingerprint,execution_window_snapshot_json,goal_run_id);
            CREATE TABLE goal_runs(goal_run_id,status,current_conversation_id,iterations_started,cost);
            INSERT INTO goal_runs VALUES('goal',2,'conv',25,12.5);
            CREATE TABLE chat_execution_commands(command_id,status);
            CREATE TABLE goal_iterations(command_id,goal_run_id);
            CREATE TABLE task_nodes(plan_id,depth,work_unit_kind,max_rounds,max_tool_calls,
                max_duration_seconds,max_input_tokens,max_output_tokens,max_cost,status);
            INSERT INTO task_nodes VALUES('plan',1,'change',25,60,1800,150000,16000,1,'Running');
            CREATE TABLE conversation_events(event_id,workspace_id,conversation_id,type,sequence,payload);
        ''')
        self.frozen = dict(executionPlanVersion=2, executionPlanSchemaVersion=1,
                           taskPlanId='plan', executionPlanFingerprint='fp')
        self.db.execute('INSERT INTO task_goal_bindings VALUES(?,?,?,?,?,?,?)',
                        ('plan','ws','task','task-goal:ws:task:18','fp',json.dumps(self.frozen),'goal'))
        for v in (3, 4):
            self.db.execute('INSERT INTO conversation_events VALUES(?,?,?,?,?,?)',
                (str(v),'ws','conv','goal.circuit_opened',v,
                 json.dumps(dict(kind='replan',goalRunId='goal',planId='plan',planVersion=v))))

    def tearDown(self):
        self.db.close()

    def test_repairs_only_version_fields_and_is_idempotent(self):
        untouched = ('task_goal_bindings','goal_runs','task_nodes','conversation_events')
        before = {t: list(self.db.execute('SELECT * FROM '+t)) for t in untouched}
        result = repair.inspect_plan(self.db, 'plan')
        repair.apply_repair(self.db, result)
        row = self.db.execute('SELECT * FROM task_plan_runs').fetchone()
        self.assertEqual((row['plan_version'], row['plan_revision'], row['status']), (2,3,'Active'))
        self.assertEqual(before, {t: list(self.db.execute('SELECT * FROM '+t)) for t in untouched})
        again = repair.inspect_plan(self.db, 'plan')
        self.assertTrue(again['alreadyCurrent'])
        repair.apply_repair(self.db, again)

    def test_rejects_unproved_versions_and_revision(self):
        for mutation in ("plan_version=1", "plan_version=99", "plan_revision=2", "schema_version=2"):
            with self.subTest(mutation=mutation):
                self.db.execute('SAVEPOINT sample')
                self.db.execute('UPDATE task_plan_runs SET '+mutation)
                with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')
                self.db.execute('ROLLBACK TO sample')
                self.db.execute('RELEASE sample')

    def test_rejects_missing_replan_evidence(self):
        self.db.execute('DELETE FROM conversation_events WHERE sequence=3')
        with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')

    def test_rejects_frozen_semantic_or_identity_mismatch(self):
        for key, value in (('executionPlanVersion',1),('executionPlanFingerprint','changed'),('taskPlanId','other')):
            self.db.execute('UPDATE task_goal_bindings SET execution_window_snapshot_json=?',
                            (json.dumps(self.frozen | {key:value}),))
            with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')

    def test_rejects_active_goal_or_command(self):
        self.db.execute('UPDATE goal_runs SET status=1')
        with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')
        self.db.execute('UPDATE goal_runs SET status=2')
        self.db.execute("INSERT INTO chat_execution_commands VALUES('cmd','running')")
        self.db.execute("INSERT INTO goal_iterations VALUES('cmd','goal')")
        with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')

    def test_rejects_invalid_v2_budget(self):
        self.db.execute('UPDATE task_nodes SET max_input_tokens=NULL')
        with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')

    def test_rejects_compiled_identity_collision(self):
        self.db.execute("INSERT INTO task_plan_runs SELECT 'other',2,1,schema_version,plan_kind,"
                        "plan_fingerprint,workspace_id,workspace_task_id,workspace_task_version,status FROM task_plan_runs")
        with self.assertRaises(ValueError): repair.inspect_plan(self.db, 'plan')

    def test_cas_rejects_plan_changed_after_inspection(self):
        candidate = repair.inspect_plan(self.db, 'plan')
        self.db.execute('UPDATE task_plan_runs SET plan_version=5')
        with self.assertRaises(ValueError): repair.apply_repair(self.db, candidate)


if __name__ == '__main__':
    unittest.main()
