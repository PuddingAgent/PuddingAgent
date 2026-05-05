import json, sys
data = json.loads(sys.stdin.buffer.read())
for col in data['kanban']['columns']:
    print(f"\n[{col['key']}]: {col['count']} tasks")
    for t in col['tasks']:
        print(f"  {t['id']} | {t['priority']} | {t['title'][:70]}")
