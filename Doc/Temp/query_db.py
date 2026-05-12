import sqlite3
import json

conn = sqlite3.connect(r'E:\github\AgentNetworkPlan\PuddingAgent\Doc\Temp\pudding_platform.db')
conn.row_factory = sqlite3.Row

print("=== GlobalAgentTemplates ===")
rows = conn.execute("SELECT TemplateId, Name, Role, SelectedCapabilityIdsJson FROM GlobalAgentTemplates").fetchall()
for r in rows:
    print(dict(r))

print("\n=== WorkspaceAgentTemplates (first 5) ===")
rows = conn.execute("SELECT TemplateId, Name, Role, SelectedCapabilityIdsJson FROM WorkspaceAgentTemplates LIMIT 5").fetchall()
for r in rows:
    print(dict(r))

print("\n=== Capabilities (first 20) ===")
rows = conn.execute("SELECT CapabilityId, Name, ToolName, IsEnabled, Description FROM Capabilities LIMIT 20").fetchall()
for r in rows:
    print(dict(r))

conn.close()
