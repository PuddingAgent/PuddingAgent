const AGENT_CLIENT_ARCHITECTURE_KEY = 'pudding-agent-client-arch';

export const isAgentClientArchitectureEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(AGENT_CLIENT_ARCHITECTURE_KEY) !== '0';
};

// ── CU-11: 执行流单一数据源切换开关（TurnContentStream 内容块流默认开启）───
// AgentTurnCard 内容块流（TurnContentStream）以 canonical 投影为主数据源：
// 正文段 ⇄ 行为组按 sequence 交错；历史/无投影 turn 回退 processItems 适配
// （同一块结构）。逃生门：localStorage 值 === '0' 关闭。
const EXEC_FLOW_PROJ_KEY = 'pudding-exec-flow-proj';

export const isExecutionFlowProjectionEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(EXEC_FLOW_PROJ_KEY) !== '0';
};
