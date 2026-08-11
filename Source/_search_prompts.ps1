$ErrorActionPreference = 'SilentlyContinue'
Get-ChildItem -Path 'E:\github\AgentNetworkPlan\PuddingAgent\Source' -Recurse -Include *.cs,*.json,*.md |
  Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } |
  Select-String -Pattern 'SOUL','AGENTS','Voice','voice 代码块','save_preference','session-context-recovery','workspace-task-agent','general-assistant' -List |
  Select-Object -ExpandProperty Path
