const AGENT_CLIENT_ARCHITECTURE_KEY = 'pudding-agent-client-arch';

export const isAgentClientArchitectureEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(AGENT_CLIENT_ARCHITECTURE_KEY) !== '0';
};

// ── CU-11: 执行流单一数据源切换开关（行为链升级 P2 转正：默认开启）──────────
// 行为链交错时间线（ExecutionFlowTimeline）以 canonical 投影为主数据源：
// live turn 直接消费投影 nodes 的 sequence 顺序；历史/无投影 turn 回退
// processItems adapter（同一渲染结构）。逃生门：localStorage 值 === '0' 关闭。
const EXEC_FLOW_PROJ_KEY = 'pudding-exec-flow-proj';

export const isExecutionFlowProjectionEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(EXEC_FLOW_PROJ_KEY) !== '0';
};
