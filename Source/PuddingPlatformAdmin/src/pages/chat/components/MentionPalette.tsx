// ── MentionPalette：输入 @ 触发 Agent 候选面板 ────────────────
// 与 CommandPalette 同构（同一浮层定位/视觉语言），差别只在于候选源是
// 「可 @ 的 Agent」而不是系统指令。存在的理由：@ 早有解析（hooks/chatRouting.ts
// 的 resolveChatRoute），却没有任何发现入口 —— 用户知道有该功能却猜不出该 @ 谁，
// 且打错名字会被静默回落到默认 Agent（不报错、不提示）。
import React, { useEffect, useRef } from 'react';

export interface MentionCandidate {
  /** 候选展示名（用户可读）。 */
  label: string;
  /**
   * 实际插入输入框的 @token。
   * ⚠️ 必须不含空格与 @ —— 解析器为正则 `^@([^\s@]+)`（chatRouting.ts），
   * 含空格的 token 会被截断，导致 @ 失败并静默回落。
   */
  token: string;
  /** 次要说明（通常为 agentId，便于区分同名展示名）。 */
  hint?: string;
}

/** 过滤候选：按 label / token 子串匹配（大小写不敏感）。 */
export function filterMentionCandidates(
  candidates: MentionCandidate[],
  filterText: string,
): MentionCandidate[] {
  const q = filterText.trim().toLowerCase();
  if (!q) return candidates;
  return candidates.filter(
    (c) =>
      c.label.toLowerCase().includes(q) || c.token.toLowerCase().includes(q),
  );
}

/** @ 候选的输入形态（只需这些字段，故用结构化类型而非直接依赖 WorkspaceAgentDto）。 */
export interface MentionAgentInput {
  agentId: string;
  name?: string | null;
  displayName?: string | null;
  isEnabled?: boolean;
  isFrozen?: boolean;
}

/** 把 Agent 列表映射为候选；不可 @ 的项（token 含空格）被剔除而非降级。 */
export function toMentionCandidates(
  agents: ReadonlyArray<MentionAgentInput>,
): MentionCandidate[] {
  const list: MentionCandidate[] = [
    { label: 'all（广播给全部 Agent）', token: 'all', hint: '@all' },
  ];
  for (const agent of agents) {
    if (agent.isEnabled === false || agent.isFrozen === true) continue;
    const label = agent.displayName || agent.name || agent.agentId;
    // 展示名含空格时改用 agentId（解析器不接受含空格的 token）。
    const token =
      label.includes(' ') || label.includes('@') ? agent.agentId : label;
    if (!token || token.includes(' ') || token.includes('@')) continue;
    list.push({ label, token, hint: agent.agentId });
  }
  return list;
}

interface MentionPaletteProps {
  visible: boolean;
  filterText: string;
  candidates: MentionCandidate[];
  selectedIdx: number;
  onSelectIndex: (idx: number) => void;
  onSelect: (candidate: MentionCandidate) => void;
}

const containerStyle: React.CSSProperties = {
  position: 'absolute',
  bottom: '100%',
  left: 0,
  marginBottom: 8,
  width: 'min(360px, calc(100vw - 32px))',
  maxHeight: 240,
  overflowY: 'auto',
  background: 'color-mix(in srgb, var(--soft-white) 95%, transparent)',
  backdropFilter: 'blur(20px)',
  WebkitBackdropFilter: 'blur(20px)',
  border: '1px solid color-mix(in srgb, var(--earth-brown) 12%, transparent)',
  borderRadius: 8,
  boxShadow: '0 4px 24px rgba(0,0,0,0.08)',
  padding: 4,
  zIndex: 20,
};

const titleStyle: React.CSSProperties = {
  padding: '4px 10px 6px',
  fontSize: 11,
  color: 'color-mix(in srgb, var(--text-primary) 55%, transparent)',
};

function rowStyle(active: boolean): React.CSSProperties {
  return {
    padding: '7px 10px',
    borderRadius: 6,
    display: 'flex',
    alignItems: 'baseline',
    gap: 8,
    cursor: 'pointer',
    fontSize: 13,
    color: 'var(--text-primary)',
    background: active
      ? 'rgb(from var(--misty-blue) r g b / 0.45)'
      : 'transparent',
  };
}

const MentionPalette: React.FC<MentionPaletteProps> = ({
  visible,
  candidates,
  selectedIdx,
  onSelectIndex,
  onSelect,
}) => {
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!visible || !listRef.current) return;
    const items =
      listRef.current.querySelectorAll<HTMLDivElement>('[data-mention-item]');
    const selected = items[selectedIdx];
    if (typeof selected?.scrollIntoView === 'function') {
      selected.scrollIntoView({ block: 'nearest' });
    }
  }, [visible, selectedIdx]);

  if (!visible) return null;

  return (
    <div style={containerStyle} ref={listRef} data-testid="mention-palette">
      <div style={titleStyle}>@ 指派给…（↑↓ 选择，Enter 确认，Esc 关闭）</div>
      {candidates.map((c, idx) => (
        <div
          key={`${c.token}-${idx}`}
          data-mention-item
          style={rowStyle(idx === selectedIdx)}
          onMouseEnter={() => onSelectIndex(idx)}
          onMouseDown={(e) => {
            // 用 mousedown 抢在 textarea blur 之前提交，避免先失焦关闭面板。
            e.preventDefault();
            onSelect(c);
          }}
        >
          <span style={{ fontWeight: 500 }}>@{c.token}</span>
          {c.label !== c.token && (
            <span
              style={{
                fontSize: 11,
                color: 'color-mix(in srgb, var(--text-primary) 55%, transparent)',
              }}
            >
              {c.label}
            </span>
          )}
        </div>
      ))}
    </div>
  );
};

export default MentionPalette;
