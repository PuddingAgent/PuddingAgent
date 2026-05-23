import sqlite3
conn = sqlite3.connect(r'E:\github\AgentNetworkPlan\PuddingAgent\data\pudding_platform.db')
c = conn.cursor()
# List all tables
c.execute("SELECT name FROM sqlite_master WHERE type='table'")
tables = c.fetchall()
print("Tables:", tables)
# Try AppUsers
for tbl in ['AppUsers', '__AppUsers', 'App_Users', 'Users']:
    try:
        c.execute(f"SELECT * FROM {tbl}")
        print(f"\n{tbl}:")
        print([desc[0] for desc in c.description])
        for r in c.fetchall():
            print(r)
    except Exception as e:
        print(f"\n{tbl}: {e}")
conn.close()
