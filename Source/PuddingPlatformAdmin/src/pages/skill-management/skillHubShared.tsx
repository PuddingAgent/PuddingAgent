import { Badge, Empty, Space, Tag, Typography } from 'antd';
import React from 'react';
import type { EvoMapDto, EvoMapNodeDto } from '@/services/platform/api';

const { Text } = Typography;

/** 字节数友好格式化 */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

/** ISO 时间 → 本地化展示（无效值返回 '-'） */
export function formatDateTime(iso?: string | null): string {
  if (!iso) return '-';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '-';
  return d.toLocaleString('zh-CN', { hour12: false });
}

/** 未知异常 → 可展示文本 */
export function errText(e: unknown): string {
  if (e instanceof Error) return e.message;
  if (typeof e === 'string') return e;
  try {
    return JSON.stringify(e);
  } catch {
    return String(e);
  }
}

/** 技能主档状态 → antd 预设色（可辨识但不刺眼，不自造 hex） */
export const SKILL_STATUS_META: Record<string, { color: string; text: string }> = {
  active: { color: 'green', text: '活跃' },
  deprecated: { color: 'orange', text: '已停用' },
  retired: { color: 'default', text: '已退役' },
};

export function renderSkillStatusTag(status: string): React.ReactNode {
  const meta = SKILL_STATUS_META[status] ?? { color: 'default', text: status };
  return <Tag color={meta.color}>{meta.text}</Tag>;
}

/** 进化动作 → 颜色（Brief §五.6：antd 预设 token） */
export const EVO_ACTION_COLOR: Record<string, string> = {
  create: 'green',
  patch: 'blue',
  split: 'purple',
  compress: 'cyan',
  retire: 'default',
  merge: 'orange',
  fork: 'magenta',
};

export const EVO_ACTION_TEXT: Record<string, string> = {
  create: '创建',
  patch: '修补',
  split: '拆分',
  compress: '压缩',
  retire: '退役',
  merge: '合并',
  fork: '分叉',
};

/** 全部 7 种进化动作（固定展示顺序） */
export const ALL_EVO_ACTIONS = ['create', 'patch', 'split', 'compress', 'retire', 'merge', 'fork'];

export function evoActionTag(action: string): React.ReactNode {
  return (
    <Tag color={EVO_ACTION_COLOR[action] ?? 'default'} style={{ marginRight: 0 }}>
      {EVO_ACTION_TEXT[action] ?? action}
    </Tag>
  );
}

/** 操作者徽标（agent·user·system） */
export function renderActor(actorKind?: string | null, actorId?: string | null): React.ReactNode {
  const kind = actorKind || 'system';
  const color = kind === 'agent' ? 'blue' : kind === 'user' ? 'green' : 'default';
  return (
    <Space size={4}>
      <Badge status={kind === 'agent' ? 'processing' : kind === 'user' ? 'success' : 'default'} />
      <Text style={{ fontSize: 12 }}>
        {kind}
        {actorId ? `·${actorId}` : ''}
      </Text>
    </Space>
  );
}

/**
 * 语义化版本比较：a>b → 1；a<b → -1；相等 → 0。
 * 数值段比较（1.10.0 > 1.9.0），非数值段按字典序，缺失段按 0 处理。
 */
export function compareVersions(a: string, b: string): number {
  const pa = String(a ?? '')
    .split('.')
    .map((s) => (/^\d+$/.test(s) ? Number(s) : Number.NaN));
  const pb = String(b ?? '')
    .split('.')
    .map((s) => (/^\d+$/.test(s) ? Number(s) : Number.NaN));
  const len = Math.max(pa.length, pb.length);
  for (let i = 0; i < len; i += 1) {
    const va = Number.isNaN(pa[i]) ? 0 : pa[i];
    const vb = Number.isNaN(pb[i]) ? 0 : pb[i];
    if (va !== vb) {
      if (Number.isNaN(va)) return -1;
      if (Number.isNaN(vb)) return 1;
      return va > vb ? 1 : -1;
    }
  }
  return 0;
}

/** 友好空态（V3 红线：每个 Tab 均不得白屏） */
export function HubEmpty({ description }: { description: string }): React.ReactNode {
  return (
    <Empty
      image={Empty.PRESENTED_IMAGE_SIMPLE}
      description={description}
      style={{ padding: '40px 0' }}
    />
  );
}

// ── EVO MAP → antd Tree ────────────────────────────────────────

export interface EvoTreeDataNode {
  key: string;
  title: React.ReactNode;
  children?: EvoTreeDataNode[];
}

export interface EvoTreeResult {
  treeData: EvoTreeDataNode[];
  nodeByKey: Map<string, EvoMapNodeDto>;
  rootCount: number;
}

function evoNodeTitle(node: EvoMapNodeDto): React.ReactNode {
  return (
    <Space size={6} wrap>
      {evoActionTag(node.evolutionAction)}
      <Text strong>{node.name}</Text>
      <Text code style={{ fontSize: 12 }}>
        {node.nodeId}
      </Text>
      <Text type="secondary" style={{ fontSize: 12 }}>
        装机 {node.installCount} · {formatBytes(node.contentBytes)}
      </Text>
    </Space>
  );
}

/**
 * 将 EvoMapDto（节点 + 边）构建为 antd Tree 的 treeData。
 * 层级：边 fromNodeId → toNodeId；无入边的节点为根；父节点缺失的孤儿节点也按根展示。
 */
export function buildEvoTreeData(map: EvoMapDto | null | undefined): EvoTreeResult {
  const nodeByKey = new Map<string, EvoMapNodeDto>();
  const treeData: EvoTreeDataNode[] = [];
  if (!map || !map.nodes || map.nodes.length === 0) {
    return { treeData, nodeByKey, rootCount: 0 };
  }

  for (const n of map.nodes) nodeByKey.set(n.nodeId, n);

  const childrenOf = new Map<string, string[]>();
  const hasIncoming = new Set<string>();
  for (const e of map.edges ?? []) {
    if (!nodeByKey.has(e.fromNodeId) || !nodeByKey.has(e.toNodeId)) continue;
    const list = childrenOf.get(e.fromNodeId) ?? [];
    list.push(e.toNodeId);
    childrenOf.set(e.fromNodeId, list);
    hasIncoming.add(e.toNodeId);
  }

  const makeNode = (id: string): EvoTreeDataNode => {
    const n = nodeByKey.get(id)!;
    const childIds = (childrenOf.get(id) ?? []).slice();
    childIds.sort((x, y) => {
      const a = nodeByKey.get(x)!;
      const b = nodeByKey.get(y)!;
      if (a.skillId !== b.skillId) return a.skillId.localeCompare(b.skillId);
      return compareVersions(a.version, b.version);
    });
    return {
      key: id,
      title: evoNodeTitle(n),
      children: childIds.length > 0 ? childIds.map(makeNode) : undefined,
    };
  };

  const rootIds = map.nodes
    .filter((n) => !hasIncoming.has(n.nodeId))
    .map((n) => n.nodeId)
    .sort((x, y) => x.localeCompare(y));

  for (const id of rootIds) treeData.push(makeNode(id));

  return { treeData, nodeByKey, rootCount: rootIds.length };
}
