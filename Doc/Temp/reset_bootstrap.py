import json
with open(r'E:\github\AgentNetworkPlan\PuddingAgent\data\bootstrap-state.json', 'r') as f:
    data = json.load(f)
print("Before:", data)
data['Bootstrap']['Initialized'] = False
with open(r'E:\github\AgentNetworkPlan\PuddingAgent\data\bootstrap-state.json', 'w') as f:
    json.dump(data, f, separators=(',', ':'))
print("After:", data)
