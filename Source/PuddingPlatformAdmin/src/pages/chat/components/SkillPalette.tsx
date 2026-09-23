// ── SkillPalette：`+` 菜单 →「技能」面板（搜索 + 选择）────────
// 交互定调（用户 2026-09-23）：选中技能后挂为 chip，**发送时**才转为文本附加到本轮。
//
// 视觉参照 WorkBuddy 的技能面板（用户提供截图）：顶部搜索框 + 「彩色字母徽标 +
// 两行（名称/描述截断）」列表 + 选中高亮 + 底部分隔线与次级操作。
// 但配色不照抄 —— 一律取本站设计变量，与 composer.styles.ts 的 composerMenuItem
// 同源：var(--pudding-text) / var(--pudding-text-muted) / var(--earth-brown) /
// var(--pudding-accent)，徽标色复用 stringToColor（与 Agent 头像同一套 --avatar-N）。
// 样式就地内联而非并入 composer.styles.ts：本组件是 React.lazy 的 async chunk，
// 内联可守住首屏 4.2KB 余量（chat chunk 预算硬上限 507904）。
//
// 数据源：SKILL Hub —— listHubSkills() → GET /api/skill-hub/skills
// ⚠️ 曾误用 listSkillPackages()（/api/skill-packages）：那是「上传的技能包」，本机
//    SkillPackages 表 0 行，而真正的技能库是 HubSkills（7 行）。误用导致面板显示
//    「暂无已启用的技能包」——不是没数据，是查错了表。
import { SearchOutlined } from '@ant-design/icons';
import { listHubSkills } from '@/services/platform/api';
import type { HubSkillSummaryDto } from '@/services/platform/api';
import React, { useEffect, useMemo, useState } from 'react';
import { stringToColor } from '../hooks/useChatState';

/** 按 skillId / name / summary 子串过滤（大小写不敏感）。 */
export function filterHubSkills(
  list: HubSkillSummaryDto[],
  filterText: string,
): HubSkillSummaryDto[] {
  const q = filterText.trim().toLowerCase();
  if (!q) return list;
  return list.filter(
    (s) =>
      s.skillId.toLowerCase().includes(q) ||
      s.name.toLowerCase().includes(q) ||
      (s.summary ?? '').toLowerCase().includes(q),
  );
}

interface SkillPaletteProps {
  open: boolean;
  /** 可选：作为级联子面板嵌入时无需「返回」（主菜单始终可见）。 */
  onClose?: () => void;
  onSelect: (skill: HubSkillSummaryDto) => void;
  /** 已挂 chip 的技能 id，用于列表高亮（避免重复添加看不出来）。 */
  selectedSkillIds?: string[];
}

const PUDDING_TEXT = 'var(--pudding-text, #1d1b24)';
const PUDDING_MUTED = 'var(--pudding-text-muted, #756b5f)';
const ACCENT = 'var(--pudding-accent, #8b5cf6)';

const wrapStyle: React.CSSProperties = {
  width: 320,
  display: 'flex',
  flexDirection: 'column',
};

const searchRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 6,
  height: 34,
  padding: '0 10px',
  borderRadius: 8,
  background: 'color-mix(in srgb, var(--earth-brown, #5c4a3a) 5%, transparent)',
  color: PUDDING_MUTED,
};

const searchInputStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  border: 'none',
  outline: 'none',
  background: 'transparent',
  fontSize: 13,
  color: PUDDING_TEXT,
};

const listStyle: React.CSSProperties = {
  maxHeight: 280,
  overflowY: 'auto',
  marginTop: 6,
  display: 'flex',
  flexDirection: 'column',
  gap: 2,
};

const noteStyle: React.CSSProperties = {
  padding: '6px 10px',
  fontSize: 11.5,
  lineHeight: 1.5,
  color: PUDDING_MUTED,
};

const itemStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: 9,
  width: '100%',
  padding: '7px 8px',
  border: 'none',
  borderRadius: 8,
  background: 'transparent',
  textAlign: 'left',
  cursor: 'pointer',
  transition: 'background 0.15s',
};

const badgeStyle = (skillId: string): React.CSSProperties => ({
  width: 22,
  height: 22,
  flexShrink: 0,
  borderRadius: 6,
  background: stringToColor(skillId),
  color: '#fff',
  fontSize: 12,
  fontWeight: 600,
  lineHeight: '22px',
  textAlign: 'center',
  userSelect: 'none',
});

const textColStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  display: 'flex',
  flexDirection: 'column',
  gap: 1,
};

const nameStyle: React.CSSProperties = {
  fontSize: 13,
  color: PUDDING_TEXT,
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
};

const descStyle: React.CSSProperties = {
  fontSize: 11.5,
  lineHeight: 1.45,
  color: PUDDING_MUTED,
  opacity: 0.8,
  display: '-webkit-box',
  WebkitLineClamp: 2,
  WebkitBoxOrient: 'vertical',
  overflow: 'hidden',
};

const footerStyle: React.CSSProperties = {
  marginTop: 6,
  paddingTop: 6,
  borderTop:
    '1px solid color-mix(in srgb, var(--earth-brown, #5c4a3a) 12%, transparent)',
  display: 'flex',
  flexDirection: 'column',
  gap: 1,
};

const footerItemStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  height: 32,
  padding: '0 8px',
  border: 'none',
  borderRadius: 6,
  background: 'transparent',
  color: PUDDING_TEXT,
  fontSize: 13,
  textAlign: 'left',
  cursor: 'pointer',
  textDecoration: 'none',
};

const SkillPalette: React.FC<SkillPaletteProps> = ({
  open,
  onClose,
  onSelect,
  selectedSkillIds,
}) => {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [skills, setSkills] = useState<HubSkillSummaryDto[]>([]);
  const [filterText, setFilterText] = useState('');
  const [hovered, setHovered] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return undefined;
    let cancelled = false;
    setLoading(true);
    setError(null);
    listHubSkills({ status: 'active', pageSize: 100 })
      .then((list) => {
        if (!cancelled) setSkills(Array.isArray(list) ? list : []);
      })
      .catch((e) => {
        if (!cancelled) {
          setError(e instanceof Error ? e.message : '技能库加载失败');
        }
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [open]);

  const filtered = useMemo(
    () => filterHubSkills(skills, filterText),
    [skills, filterText],
  );

  const selected = useMemo(
    () => new Set(selectedSkillIds ?? []),
    [selectedSkillIds],
  );

  if (!open) return null;

  return (
    <div style={wrapStyle} data-testid="skill-palette">
      <div style={searchRowStyle}>
        <SearchOutlined />
        <input
          style={searchInputStyle}
          value={filterText}
          onChange={(e) => setFilterText(e.target.value)}
          placeholder="搜索技能"
          aria-label="搜索技能"
          data-testid="skill-palette-search"
        />
      </div>

      <div style={listStyle}>
        {loading && <div style={noteStyle}>加载中…</div>}
        {!loading && error && <div style={noteStyle}>{error}</div>}
        {!loading && !error && filtered.length === 0 && (
          <div style={noteStyle}>
            {skills.length === 0 ? '技能库暂无技能' : '没有匹配的技能'}
          </div>
        )}
        {!loading &&
          !error &&
          filtered.map((s) => {
            const isSelected = selected.has(s.skillId);
            const isHovered = hovered === s.skillId;
            return (
              <button
                key={s.skillId}
                type="button"
                style={{
                  ...itemStyle,
                  background: isSelected
                    ? `color-mix(in srgb, ${ACCENT} 10%, transparent)`
                    : isHovered
                      ? 'color-mix(in srgb, var(--earth-brown, #5c4a3a) 6%, transparent)'
                      : 'transparent',
                }}
                title={s.skillId}
                data-testid={`skill-item-${s.skillId}`}
                aria-pressed={isSelected}
                onMouseEnter={() => setHovered(s.skillId)}
                onMouseLeave={() => setHovered((cur) => (cur === s.skillId ? null : cur))}
                onClick={() => onSelect(s)}
              >
                <span style={badgeStyle(s.skillId)} aria-hidden>
                  {s.name.slice(0, 1).toUpperCase()}
                </span>
                <span style={textColStyle}>
                  <span style={nameStyle}>{s.name}</span>
                  {(s.summary || s.description) && (
                    <span style={descStyle}>{s.summary || s.description}</span>
                  )}
                </span>
                {isSelected && (
                  <span style={{ color: ACCENT, fontSize: 12, flexShrink: 0 }}>
                    已选
                  </span>
                )}
              </button>
            );
          })}
      </div>

      {/* 诚实边界：本轮技能不能作为协议参数下发，只能以文本提示方式附加。 */}
      <div style={noteStyle}>
        选择后发送时会在消息末尾附加一行提示，由 Agent 据此使用该技能。
      </div>

      <div style={footerStyle}>
        {onClose && (
          <button
            type="button"
            style={footerItemStyle}
            onClick={onClose}
            data-testid="skill-palette-back"
          >
            返回
          </button>
        )}
        {/* 不提供「从本地添加技能」：本站没有本地上传技能的能力，不摆不能用的入口。
            只链接到真实存在的技能管理页。 */}
        <a
          style={footerItemStyle}
          href="/skill-management"
          target="_blank"
          rel="noreferrer"
        >
          管理技能
        </a>
      </div>
    </div>
  );
};

export default SkillPalette;
