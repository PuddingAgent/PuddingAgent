import sqlite3
conn = sqlite3.connect(r'E:\github\AgentNetworkPlan\PuddingAgent\data\pudding_platform.db')
c = conn.cursor()
c.execute("SELECT * FROM AppUsers")
rows = c.fetchall()
print(f"Rows: {len(rows)}")
for r in rows:
    print(r)
c.execute("SELECT COUNT(*) FROM AppUsers")
cnt = c.fetchone()
print(f"Count: {cnt}")
conn.close()
