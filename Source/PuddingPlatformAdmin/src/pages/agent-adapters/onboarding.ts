import type { AgentAdapterRow } from '@/services/platform/agentAdapters';

export const agentApiDocumentation = (baseUrl: string) =>
  `${baseUrl.replace(/\/+$/, '')}/agent-adapters/docs/agent-adapters-v1.json`;

export function agentAdapterPrompt(
  baseUrl: string,
  binding: Pick<AgentAdapterRow, 'agentId' | 'workspaceId' | 'ak'>,
) {
  const root = baseUrl.replace(/\/+$/, '');
  return [
    '请把本工具接入 PuddingAgent 的外部 Agent HTTP 接口。',
    `服务地址 ${root}`,
    `工作区 ${binding.workspaceId}`,
    `Agent ID ${binding.agentId}`,
    `AK ${binding.ak}`,
    '从本工具的安全配置读取 SK，或请我通过安全输入提供。请勿把 SK 写入聊天、日志或项目文件。',
    '使用 Authorization: Bearer pdt_v1_<AK>.<SK>。',
    '向 /api/external/v1/agent-adapters/register 发送 POST，JSON 请求体为 {}。',
    '确认返回的工作区与 Agent ID，保存 leaseId，在 expiresAtUtc 之前 POST /api/external/v1/agent-adapters/renew 并提交 {"leaseId":"..."}。',
    '通过 GET /api/external/v1/agent-adapters/inbox?leaseId=<leaseId>&after=0 轮询订阅。处理成功后保存 nextCursor 作为下次 after，用 messageId 去重。',
    '通过 GET /api/external/v1/agent-adapters/agents?leaseId=<leaseId> 查找同一工作区的外部 Agent。',
    '向同一工作区的其他外部 Agent 发文本时，调用 POST /api/external/v1/agent-adapters/messages，并传 leaseId、operationId（UUID）、targetAgentId 和 text。',
    '委派任务时，把相同字段发到 POST /api/external/v1/agent-adapters/delegations。保存返回的 messageId，用 GET /api/external/v1/agent-adapters/delegations/<messageId>?leaseId=<leaseId> 查询结果。',
    '收件箱以 kind 区分 message 和 delegation。委派处理结束后，用 PUT /api/external/v1/agent-adapters/delegations/<messageId>/result 提交 leaseId、state、turnId、text 或 reason，再推进收件箱游标。',
    '结束连接时用同一 leaseId 调用 POST /api/external/v1/agent-adapters/disconnect。',
    `接口文档 ${agentApiDocumentation(root)}`,
  ].join('\n');
}
