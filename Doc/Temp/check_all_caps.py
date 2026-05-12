import sqlite3
conn = sqlite3.connect(r"E:\github\AgentNetworkPlan\PuddingAgent\Doc\Temp\pudding_platform_latest.db")
conn.row_factory = sqlite3.Row
rows = conn.execute("SELECT CapabilityId, Name, ToolName, SortOrder FROM Capabilities ORDER BY SortOrder").fetchall()
print(f"Total capabilities: {len(rows)}")
for r in rows:
    print(f"  {r['CapabilityId']:30s} {r['ToolName']:20s} sort={r['SortOrder']} {r['Name']}")
conn.close()
