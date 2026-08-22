const AGENT_CLIENT_ARCHITECTURE_KEY = 'pudding-agent-client-arch';

export const isAgentClientArchitectureEnabled = () => {
  if (typeof localStorage === 'undefined') return true;
  return localStorage.getItem(AGENT_CLIENT_ARCHITECTURE_KEY) !== '0';
};

// ── CU-11: 执行流单一数据源切换灰度开关 ─────────────────────────────
// 默认「关闭」（保守）：CU-11 是切换存量渲染路径的最高风险片，需人工
// 显式开启（localStorage 值 === '1'）灰度过关后再翻转默认开启。
// 与 isAgentClientArchitectureEnabled（默认开）刻意相反，新路径必须 opt-in。
const EXEC_FLOW_PROJ_KEY = 'pudding-exec-flow-proj';

export const isExecutionFlowProjectionEnabled = () => {
  if (typeof localStorage === 'undefined') return false;
  return localStorage.getItem(EXEC_FLOW_PROJ_KEY) === '1';
};
