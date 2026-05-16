import sqlite3
import os

# Check both data directories
dirs = [
    r'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingAgent\data',
    r'E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingAgent\bin\Debug\net10.0\data',
    r'E:\github\AgentNetworkPlan\PuddingAgent\data',
]

for d in dirs:
    db_path = os.path.join(d, 'pudding_platform.db')
    if not os.path.exists(db_path):
        print(f'NOT FOUND: {db_path}')
        continue
    conn = sqlite3.connect(db_path)
    c = conn.cursor()
    c.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
    tables = [t[0] for t in c.fetchall()]
    targets = ['session_event_log','session_sub_agents','session_diagnostic_log']
    print(f'\n=== {db_path} ===')
    for t in targets:
        exists = t in tables
        cnt = -1
        if exists:
            c.execute(f'SELECT COUNT(*) FROM "{t}"')
            cnt = c.fetchone()[0]
        print(f'  {t}: exists={exists} rows={cnt}')
    conn.close()
