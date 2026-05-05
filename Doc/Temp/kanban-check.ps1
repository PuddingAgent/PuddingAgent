cd D:/WangXianQiang/github/hyfree/PuddingCode
python .github/skills/todo-api/todo_api.py kanban --group-by stage --project Pudding > temp/kanban.json 2>nul
python -c "import json; d=json.load(open('temp/kanban.json','r',encoding='utf-8')); [print(f'{c[\"key\"]}: {c[\"count\"]} tasks') for c in d['kanban']['columns']]; print(); [print(f'{t[\"id\"]} | {t[\"priority\"]} | {t[\"title\"][:70]}') for c in d['kanban']['columns'] for t in c['tasks'] if c['key'] not in ('done','cancelled')]"
