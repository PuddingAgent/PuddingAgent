import sqlite3
conn = sqlite3.connect(r"E:\github\AgentNetworkPlan\PuddingAgent\Doc\Temp\pudding_memory_verify.db")
conn.row_factory = sqlite3.Row
print("=== MemoryFacts (last 5) ===")
for r in conn.execute("SELECT Statement, Confidence FROM MemoryFacts ORDER BY CreatedAt DESC LIMIT 5"):
    print(f"  {r['Statement'][:100]} ({r['Confidence']})")
print()
print("=== SubconsciousJobLogs (last 3) ===")
for r in conn.execute("SELECT Status, FactsExtracted, FactsMerged, ElapsedMs FROM SubconsciousJobLogs ORDER BY CreatedAt DESC LIMIT 3"):
    print(f"  {r['Status']} facts={r['FactsExtracted']} merged={r['FactsMerged']} ms={r['ElapsedMs']}")
conn.close()
