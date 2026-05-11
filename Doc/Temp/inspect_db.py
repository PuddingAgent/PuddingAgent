import sqlite3, sys
db = sys.argv[1] if len(sys.argv) > 1 else "pudding_platform.db"
c = sqlite3.connect(db)
cur = c.cursor()
print("=== TABLES ===")
for (name,) in cur.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
    print(name)
print()
candidates = [
    "platform.GlobalAgentTemplates",
    "platform_GlobalAgentTemplates",
    "GlobalAgentTemplates",
    "platform.WorkspaceAgentTemplates",
    "platform_WorkspaceAgentTemplates",
    "WorkspaceAgentTemplates",
]
for t in candidates:
    try:
        cols = list(cur.execute(f'PRAGMA table_info("{t}")'))
    except Exception as e:
        cols = [("ERR", str(e))]
    if cols:
        print(f"--- {t} ---")
        for col in cols:
            print(col)
        print()
