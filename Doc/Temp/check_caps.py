import sqlite3
conn = sqlite3.connect(r"E:\github\AgentNetworkPlan\PuddingAgent\Doc\Temp\pudding_platform_new.db")
conn.row_factory = sqlite3.Row
print("=== Capabilities (all) ===")
for r in conn.execute("SELECT CapabilityId, Name, ToolName, IsEnabled FROM Capabilities ORDER BY SortOrder"):
    print(f"  {r['CapabilityId']:30s} {r['ToolName']:20s} {r['Name']} (enabled={r['IsEnabled']})")
conn.close()
